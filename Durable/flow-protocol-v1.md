# PostgreSQL Flow protocol v1

This is the normative operation and lock manifest for Durable slice 4. It specifies observable behavior, transaction
boundaries, lock hierarchy, crash recovery boundaries, and execution state transitions for the PostgreSQL Flow engine.
The package starts no worker or background polling service; unit and integration tests and future hosted runtime code
drive each operation deterministically.

## Authoritative records

PostgreSQL is the sole durable truth for Flow state. The `appsurface_durable` schema owns flow instances, idempotency keys,
commands, event delivery tracking, execution history, active waits, timers, and RLS security policies alongside the Work/shared protocol tables.

- **`appsurface_durable.flow_instance`**: Primary state table keyed by `(scope_id, flow_instance_id)`. Tracks current node, execution state, context/resume payload envelopes, active epoch, scope generation, lease ownership, and suspension details.
- **`appsurface_durable.flow_command`**: Command log keyed by `(scope_id, command_id)`. Deduplicates incoming `start`, `event`, `cancel`, and `release` commands. Enforces start identity through `ix_flow_command_start_idempotency` and single-use event delivery through `ix_flow_command_event`.
- **`appsurface_durable.flow_history`**: Append-only sequence of execution events keyed by auto-incrementing `event_id`. Records state transitions, node entries/exits, inputs, outputs, context snapshots, and diagnostic details in `jsonb`.
- **`appsurface_durable.flow_wait`**: Retained wait lineage keyed by `wait_id`. Supports `event` and `activity` waits, permits at most one active/suspended wait per Flow, and links child activities through `(scope_id, child_work_id)`.
- **`appsurface_durable.flow_timer`**: Scheduled timers keyed by `timer_id`, tied to an exact wait and registered Flow revision.
- **`appsurface_durable.flow_dispatch`**: The payload-free global discovery surface for Flow and timer candidates. The dispatcher can select it but cannot mutate or read payload tables.

## Flow state machine

A Flow instance transitions through the following formal states:

- `ready`: Ready for step evaluation or initial execution.
- `evaluating`: Transition evaluation in progress under active lease/lock.
- `waiting_event`: Suspended awaiting an incoming external event or timer expiry.
- `waiting_timer`: Suspended awaiting timer expiry.
- `waiting_activity`: Suspended awaiting completion of an enqueued child Work activity.
- `cancel_pending`: Cancellation requested while an external activity or step is in progress.
- `completed`: Terminal state following successful execution to a completion node.
- `faulted`: Terminal state following an unhandled exception or failed condition.
- `canceled`: Terminal state following confirmed cancellation.
- `suspended`: Non-terminal safety state entered when definition mismatch, non-restorable child failure, or unexpected state occurs (`suspended_from_state` preserves prior state).

## Global lock order

To prevent deadlocks across concurrent flow steps, event deliveries, and child activity completions, all operations adhere to the canonical lock hierarchy:

**`scope` -> `child Work (when present)` -> `parent Flow` -> `Flow dispatch` -> `Flow wait` -> `Flow timer` -> `Work dispatch` -> `permit/operator command` -> `Work history` -> `Flow history`**

Rows within one class are locked in stable primary-key order. Activity acceptance resolves or inserts its deterministic
child Work before locking and conditionally advancing the parent; any parent-fence loss rolls the entire transaction
back.

Schema management and epoch rotation take the same exclusive session advisory lock as Work operations before altering schema or rotating active epoch credentials.

## Operation manifest

| Operation | Transaction and locks | Required validation | Result and durable effects |
| --- | --- | --- | --- |
| **Get schema status** | Read-only deployment connection | Migration hashes for `0001`, `0002`, `0003` | Reports compatibility (compatible, missing, inconsistent, old/new); includes `StoreId` and active epoch. |
| **Apply migrations** | Migration owner; session advisory lock | Pre/post migration hashes for `0001_work_shared`, `0002_forced_rls`, `0003_flow_protocol` | Applies pending known migrations in sequence under lock; fails closed on SHA-256 mismatch. |
| **Start Flow** | Caller/client transaction; scope -> flow_instance -> flow_command -> flow_history | Target, StoreId, active epoch, registry, definition fingerprint, `start_idempotency_key` | Atomically creates `flow_instance` (state `ready`), records `flow_command`, appends `flow_history` event, or returns exact duplicate. |
| **Deliver External Event** | Short transaction following the global order | Target, StoreId, active epoch, unique `event_id`, matching active `waiting_event` | Records command, resolves active wait (`event_won`), supersedes timer if scheduled, updates `flow_instance` to `ready`, appends history. Exact re-delivery returns the original duplicate-stable outcome. |
| **Fire Timer** | Payload-free `ix_flow_dispatch_due`; short transaction following the global order | StoreId, active epoch, `state = 'scheduled'`, `due_at <= clock_timestamp()` | Updates timer to `fired`, resolves event wait (`timer_won`), updates `flow_instance` to `ready`, appends history. |
| **Evaluate Step** | Short scoped transaction; scope -> flow_instance -> flow_history | Active lease/epoch, aggregate revision match, definition fingerprint matching registered code | Replays/evaluates transitions; advances node; creates new `flow_wait`/`flow_timer` or enqueues child Work; updates state and increments revision. |
| **Accept Child Activity Work** | Scoped transaction; child Work before parent Flow | Active Flow claim, registered Work contract, active epoch | Atomically inserts/resolves Work, dispatch/history, creates an activity wait, and sets Flow state to `waiting_activity`; parent-fence loss rolls everything back. |
| **Complete Activity Wait** | Scoped transaction; scope -> child Work -> parent Flow -> Flow dispatch -> wait -> Work dispatch/history -> Flow history | Terminal Work fact, matching `child_work_id` | Resolves the wait, records the typed activity result or suspension descriptor, transitions the parent, and appends both histories atomically. |
| **Cancel Flow** | Scoped transaction; scope -> flow_instance -> flow_command -> flow_wait -> flow_timer -> flow_history | Target, active instance, authorized actor/reason | If `ready`/`waiting`, cancels active waits/timers and transitions to `canceled`. If activity in-flight, transitions to `cancel_pending`. |
| **Suspend Flow** | Scoped transaction; scope -> flow_instance -> flow_history | Invalid transition, non-restorable child failure, code mismatch (`ASDUR200`/`ASDUR201`/`ASDUR211`) | Sets state to `suspended`, records `suspended_from_state` and terminal reason code, appends audit history. |
| **Release Suspended Flow** | Scoped transaction; scope -> flow_instance -> flow_command -> flow_history | Operator command, expected revision, valid epoch, authorized resolution | Clears suspension, restores original or target state, appends command and history record. |
| **Disable Scope** | Scoped transaction; canonical lock order | Active scope generation, actor/reason | Permanent tombstone on scope; atomically suspends non-terminal Flow instances and Work items in scope. |

## 11 Crash recovery boundaries

Flow persistence guarantees deterministic recovery across 11 explicit process crash points:

1. **Start Flow Pre-Commit**: Crash before SQL commit. PostgreSQL rolls back transaction; no `flow_instance`, `flow_command`, or `flow_history` row exists. Caller may safely retry.
2. **Start Flow Post-Commit (`ready`)**: Crash after commit. `flow_instance` is durable in `ready` state at revision 1. Recovery worker discovers `ready` instance and invokes evaluation.
3. **Evaluation In-Flight Before Child Activity Accept**: Crash during step calculation before Work enqueue transaction. Transaction rolls back; instance remains in `ready` state at current revision.
4. **Child Activity Accepted (`waiting_activity`)**: Crash after Work item and `flow_wait` commit. Work item is durable in `dispatch`; Flow is durable in `waiting_activity`. Work dispatcher processes child activity independently.
5. **Child Activity Execution Complete / Permit Acquired**: Crash after provider acquires Work effect permit and executes child activity. Work recovery or completion path handles Work item; Flow remains in `waiting_activity`.
6. **Child Activity Terminal Completion Committed, Flow Evaluation Pending**: Crash after child Work completion transaction commits (`flow_wait` set to `activity_completed`, Flow state set to `ready`), but before next Flow step evaluation completes. Flow recovery discovers instance in `ready` state with completed activity result payload and continues step evaluation.
7. **Event Delivery Pre-Commit**: Crash during external event delivery transaction. Transaction rolls back; `event_id` is unconsumed and `flow_wait` remains `active`. Caller may retry event delivery.
8. **Event Delivered (`event_won`), Flow Evaluation Pending**: Crash after event delivery commits (`flow_command` accepted, wait state `event_won`, timer superseded, Flow state `ready`). Discovery finds Flow in `ready` state with delivered event payload and evaluates next step.
9. **Timer Fired (`timer_won`), Flow Evaluation Pending**: Crash after timer fire transaction commits (`flow_timer` state `fired`, wait state `timer_won`, Flow state `ready`). Discovery finds Flow in `ready` state with timer-expiry signal and evaluates next step.
10. **Flow Suspended**: Crash after safety suspension commits (`state = 'suspended'`). Instance remains durable in `suspended` state with preserved `suspended_from_state`. Requires explicit operator release or reconciliation command.
11. **Flow Terminal State Committed (`completed` / `faulted` / `canceled`)**: Crash after terminal state commit. `flow_instance` is permanently terminal; subsequent commands or events are safely rejected with `already_terminal` or `ASDUR110`.

## Idempotency and race resolution matrix

| Identity / Key | Scope | Duplicate / Collision Behavior |
| --- | --- | --- |
| `(scope_id, start_idempotency_key)` | Scope-wide | Identical fingerprint returns original `FlowInstanceId` and `accepted_at` (`Duplicate`). Divergent definition or payload returns `ASDUR206` start conflict. |
| `(scope_id, flow_instance_id)` | Scope-wide | An instance is created by one coherent start only. Reusing it with different command or idempotency identities returns `ASDUR206`; an exact original start request returns the persisted outcome. |
| `(scope_id, command_id)` | Scope-wide | Identical command fingerprint returns original command outcome (`accepted`). Divergent command payload returns `ASDUR207` command conflict. |
| `(scope_id, event_id)` | Scope-wide | `ix_flow_command_event` prevents duplicate consumption. Exact retries return the original accepted/race-lost outcome; changed semantics fail `ASDUR207`. |
| Aggregate `revision` CAS | Instance-wide | State transitions validate `expected_revision`. Race condition (e.g. concurrent step evaluation or event delivery) causes loser to fail CAS and return `ASDUR203` race lost. |

## Options reuse and configuration sharing

`PostgreSqlDurableWorkOptions` is reused directly by `PostgreSqlDurableFlowClient` and `PostgreSqlDurableFlowStore`, or shared via compatible options types:

- **`ExpectedStoreId`**: Must match the deployment-time `StoreId` stored in `appsurface_durable.store_metadata`. Mismatch fails closed with `ASDUR115`.
- **`RuntimeEpoch`**: Must match the currently active epoch initialized or rotated via `IDurableRuntimeSchemaManager`. Stale epoch fails closed with `ASDUR108` or epoch fence violation.
- **`WakeNotificationMode`**: Flow and Work engines share PostgreSQL `LISTEN`/`NOTIFY` notification settings (default: `Disabled`).
- **Schema Compatibility**: Shared schema manager checks migrations `0001_work_shared.sql`, `0002_forced_rls.sql`, and `0003_flow_protocol.sql`.

## Migration order and rollback posture

PostgreSQL Flow schema requires applying migrations strictly in order:

1. `0001_work_shared.sql`: Defines `store_metadata`, `schema_migration`, `scope`, `work`, `dispatch`, `work_operator_command`, `effect_permit`, `scope_history`, `work_history`.
2. `0002_forced_rls.sql`: Enables and forces Row Level Security on Work entities.
3. `0003_flow_protocol.sql`: Defines the six Flow relations, indexes, constraints, and forced RLS policies.

After applying any migration that adds package relations, run [`configure-postgresql-roles.sql`](configure-postgresql-roles.sql) again: migrations must run first, then the role recipe grants the reviewed Flow privileges to existing dispatcher and scoped-runtime roles.

### Rollback posture

- Applied migrations are **forward-only**. The package provides no destructive down-migration scripts.
- Rolling back application binaries does **not** authorize rolling back database schema.
- Strict `./Durable/verify-postgresql.sh --ci --flow` builds the pinned v2 Work binary from
  `0e57477bab00b1951192c82ca28fdda977da2092` and runs it concurrently with current Work/Flow operations against v3.
  The rolling claim is limited to that Work-only path; v2 cannot process Flow.
- Manual DDL execution requires using `psql` with `-v ON_ERROR_STOP=1`.

## Security and Row-Level Security (RLS)

All six Flow tables, including payload-free `flow_dispatch`, have Row Level Security enabled and forced:

The dispatcher credential alone receives global `flow_dispatch` discovery. The scoped runtime credential retains
`SELECT` and column-scoped `UPDATE` privileges but sees Flow dispatch rows only after its transaction sets the matching
`appsurface_durable.scope_id`; rerun the role recipe after applying `0003` to normalize these role-targeted policies.

```sql
ALTER TABLE appsurface_durable.flow_instance ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_instance FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_instance_scope_isolation ON appsurface_durable.flow_instance
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));
```

Runtime connections must set `appsurface_durable.scope_id` transaction-locally. Unset or empty scope settings prevent access to all rows.

## Diagnostics catalog mapping

Flow operations emit append-only `ASDURxxx` codes. Safe error reporting excludes credentials, payloads, and parameter values.

| Diagnostic Code | Meaning | Safe Resolution Path |
| --- | --- | --- |
| `ASDUR200` | Flow definition unavailable | Register required `flow_id` and version before starting or resuming instance. |
| `ASDUR201` | Flow history incompatible | Definition fingerprint or step code changed; suspend instance and perform explicit migration. |
| `ASDUR202` | Not waiting yet | Event arrived before instance entered active `waiting_event` state; retry event delivery. |
| `ASDUR203` | Flow race lost | Optimistic aggregate revision CAS failed; reload instance state before retrying. |
| `ASDUR204` | Event duplicate | Single-use `event_id` was already consumed; return original delivery result. |
| `ASDUR205` | Flow access denied | Scope authorization check failed or scope setting missing. |
| `ASDUR206` | Flow start conflict | A start identity or target Flow instance conflicts with persisted Flow creation. |
| `ASDUR207` | Flow command conflict | `command_id` or `event_id` reused with different command semantics. |
| `ASDUR208` | Flow not found | Instance ID does not exist within the specified scope. |
| `ASDUR209` | Event contract mismatch | Payload schema version or contract ID does not match active wait registration. |
| `ASDUR210` | Release manifest mismatch | Recovery manifest registration disagrees with persisted history. |
| `ASDUR211` | Release state mismatch | Suspended state and active wait/timer/work records disagree; reconcile before release. |
| `ASDUR400`-`ASDUR403` | Schema manager errors | Apply pending schema migrations `0001`-`0003` using migration owner credentials. |

See the [diagnostics catalog](../troubleshooting/durable-diagnostics.md) for full error details.
