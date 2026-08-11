# Durable contract diagnostics

AppSurface Durable uses append-only `ASDURxxx` codes. Messages and operator history must contain safe Problem, Cause,
Fix, and Docs guidance and must never include credentials, provider response bodies, tokenized URLs, email content, or
child-sensitive data.

The Durable contract and PostgreSQL source-preview packages emit the codes below. Hosted-runtime diagnostics use only
fixed, low-cardinality codes and never expose connection targets, notification payloads, scopes, aggregates, or trace
context.

## Available contract diagnostics

| Code | Problem | Typical cause | Safe action |
|---|---|---|---|
| `ASDUR100` | Request validation failed | Default/missing id, unregistered contract, unsafe payload, limit violation, or invalid policy | Correct the caller contract before retrying |
| `ASDUR102` | Command conflict | A command identity was reused with a different known-schema fingerprint | Reuse the original semantic request or allocate a new command id |
| `ASDUR106` | Ambiguous external outcome | Provider response was lost after an effect permit | Follow declared provider safety; reconcile or resolve rather than guessing |
| `ASDUR109` | Work contract unavailable | Historical codec/executor registration is absent | Restore that immutable registration or perform an explicit migration |
| `ASDUR110` | Already terminal | A retry or operator request targets terminal Work | Return terminal truth; never repeat the executor |
| `ASDUR111` | Work not found | The authorized scope does not contain the requested Work identity | Verify the authorized scope and opaque Work identity |
| `ASDUR112` | Work revision conflict | Work changed after the operator read its revision | Reload authoritative Work truth before issuing another command |
| `ASDUR113` | Scope not found | The requested durable scope does not exist | Verify the trusted scope identity; do not create scope state implicitly |
| `ASDUR114` | Scope generation conflict | The scope lifecycle generation changed before mutation | Reload scope truth and do not reuse a stale generation |
| `ASDUR115` | Store identity mismatch | A caller-owned transaction targets a different durable store | Use the data source and StoreId validated for that transaction |
| `ASDUR116` | Operator transition rejected | Current Work state or immutable provider policy forbids the requested transition | Reload Work truth and select only the evidence-supported operation |
| `ASDUR117` | Operator proof required | An ambiguous effect permit prevents ordinary safe retry | Reconcile or submit authorized applied/not-applied proof |
| `ASDUR118` | Operator command in progress | The exact durable operator command has started but has no committed outcome | Wait and retry the exact same command identity and semantics |
| `ASDUR200` | Flow definition unavailable | Flow id/version is not registered | Restore the immutable definition before resuming |
| `ASDUR201` | Flow history incompatible | Definition, implementation, codec, or callsite identity changed | Suspend and migrate explicitly |
| `ASDUR202` | Not waiting yet | Event arrived before its exact wait | Retry with the same unconsumed event id |
| `ASDUR203` | Flow race lost | Another transition won the revision | Read current state; do not deliver another continuation |
| `ASDUR204` | Event duplicate | A single-use event id already has an outcome | Return original truth only when fingerprints match |
| `ASDUR205` | Flow access denied | Application authorization or trusted scope check failed | Correct application policy; opaque ids are not authorization |
| `ASDUR206` | Flow start conflict | A start identity or target Flow instance conflicts with persisted Flow creation | Reuse the exact request or allocate new identities |
| `ASDUR207` | Flow command conflict | Command/event identity was reused with different semantic bytes | Reuse the exact request or allocate new identities |
| `ASDUR208` | Flow not found | No instance exists in the authorized scope | Verify scope and opaque instance id |
| `ASDUR209` | Event contract mismatch | Payload does not match the active typed wait | Send the exact declared payload and reuse the unconsumed event id |
| `ASDUR210` | Release manifest mismatch | Registration differs from recoverable history | Deploy a compatible registration or migrate explicitly |
| `ASDUR211` | Release state mismatch | Suspended state and wait/timer/child-work truth disagree | Use Flow repair for a V1 child-effect descriptor; otherwise reconcile before release |
| `ASDUR212` | Trace context invalid | Persisted or ambient `traceparent` is malformed, unsupported, or unsafe | Drop context and continue the Flow without a causal link |
| `ASDUR213` | Trace state rejected | A valid parent carried malformed or oversized opaque `tracestate` | Retain the parent link and drop only `tracestate` |
| `ASDUR214` | Retention manifest not found | Manifest ID does not exist in the authorized scope | Verify the authorized scope and manifest ID, or assess and create a new manifest |
| `ASDUR215` | Retention source changed | Flow source items or closure digest changed after assessment/manifest creation | Assess and create a new manifest; do not archive or purge stale source state |
| `ASDUR216` | Retention lifecycle conflict | Expected lifecycle sequence is stale or another operation committed first | Read current manifest state and retry using its active lifecycle sequence |
| `ASDUR217` | Retention lifecycle rejected | Manifest is not in the required state, or a legal hold / active child prevents transition | Read manifest state and follow the lifecycle order. Release a legal hold only after an explicitly authorized legal or compliance decision; otherwise keep the hold and do not purge. |
| `ASDUR218` | Repair descriptor upgrade required | The suspension has no complete V1 child-effect descriptor identity | Existing suspensions remain unsupported; apply `0008` and compatible writers only for future V1 descriptors, then use the application-authorized recovery process |
| `ASDUR219` | Repair evidence mismatch | Locked Work, wait, result, history, or manual-resolution evidence differs | Reload the payload-free assessment; never substitute direct SQL evidence |
| `ASDUR220` | Repair action unsupported | The retained state is outside the two-action repair matrix | Preserve the suspension and use a documented recovery path |

### ASDUR202

The Flow has not committed a matching retained wait yet. This commonly occurs while its node is still evaluating.
Observe the current revision/wait, then retry the exact same request with the same unconsumed command and event
identities; do not change event semantics between retries.

### ASDUR206

A start command or start idempotency key resolves to different semantic content, or the target Flow instance is already
owned by another start. Retry the original start byte-for-byte, or use a new coherent command/idempotency/instance
triple. Never choose one conflicting identity as the winner.

### ASDUR207

A Flow command/event identity was reused with a changed or unsupported fingerprint, or the two identities resolve to
different command rows. Stop retrying changed semantics and inspect the original durable outcome.

### ASDUR209

The event name or encoded payload contract differs from the active wait’s exact contract, version, classification, or
retention identity. Encode the registered contract and retry with the same still-unconsumed identities.

### ASDUR210

The active Flow registration’s authoring model, implementation manifest, or definition fingerprint cannot interpret
the suspended instance safely. Deploy the exact compatible registration or perform an explicit migration before
release.

### ASDUR211

The persisted suspension descriptor, wait/timer lineage, or child-Work truth cannot be restored without guessing.
Reconcile authoritative Work and Flow facts first; cancellation or an explicit evidence-backed repair is safer than a
force-terminate shortcut.

### ASDUR218–ASDUR220

The Flow repair operator refuses rather than infers missing truth. `ASDUR218` means an older or mixed writer did not
persist the V1 descriptor schema and digest; existing suspensions without that identity remain unsupported because
`0008_flow_repair.sql` does not invent or backfill the missing evidence, and a fresh assessment cannot create it.
Apply `0008_flow_repair.sql` after
`0007_flow_retention.sql` and the role recipe, then deploy compatible writers for future V1 suspensions. Only assess
later suspensions that contain the V1 identity; retain pre-V1 suspensions and use the application-authorized recovery
process. `ASDUR219` means a fresh request no longer matches locked Work, wait, result, history, or manual-resolution
evidence. `ASDUR220` means neither supported assertion applies. Do not use `ReleaseSuspensionAsync` or direct SQL as
a workaround.

### ASDUR212 and ASDUR213

Trace diagnostics are value-free. `ASDUR212` drops both W3C fields and continues without a link; `ASDUR213` keeps a
valid W3C `traceparent` and drops only opaque `tracestate`. Neither diagnostic authorizes a retry, changes scope
authorization, or permits logging raw trace headers. See the [Durable trace-context contract](../Durable/flow-trace-context-v1.md).

### ASDUR214

The specified retention manifest ID was not found in the authorized scope. Verify that the manifest ID belongs to the authorized scope, or invoke assessment and manifest creation to obtain a valid manifest.

### ASDUR215

The underlying Flow source closure or state changed after the retention assessment or manifest was frozen. The current source items no longer match the immutable watermark. Create a new retention assessment and manifest before archiving or purging.

### ASDUR216

The expected lifecycle sequence supplied with the retention command does not match the manifest's current persisted sequence because a concurrent or previous operation committed first. Read current manifest state and retry the operation with its updated lifecycle sequence.

### ASDUR217

The retention operation cannot proceed because the manifest is in an invalid state for the operation (such as attempting purge before verification or recording receipt after purge), or because an active legal hold or child Work blocks execution. Verify lifecycle ordering and the blocking condition. Release a legal hold only after an explicitly authorized legal or compliance decision; otherwise keep the hold and do not purge.

Schedule contracts reserve `ASDUR301`-`ASDUR307` for invalid definition, missing schedule, revision conflict, command
conflict, access denial, evaluation incompatibility, and recovery-state mismatch. A provider must map these codes to its
tested implementation without changing their meanings.

## PostgreSQL Schedule provider diagnostics

| Code | Meaning | Safe response |
|---|---|---|
| `ASDUR301` | Schedule or target is invalid | Correct the definition, registration, policy, or target codec; do not retry changed content under the same command identity. |
| `ASDUR302` | Persisted Cron dialect or grammar is unsupported | Use `At`, `After`, or `Every` until the pinned Cron evaluator gate is complete; never reinterpret persisted Cron bytes. |
| `ASDUR303` | Schedule not found in the authorized scope | Reload the authorized Schedule inventory; opaque identity alone is not authorization. |
| `ASDUR304` | Schedule revision conflict | Reload the authoritative snapshot and retry the intended operation using its current revision. |
| `ASDUR305` | Schedule command conflict | Retry only the exact original command/idempotency semantics, or use a new command identity for changed intent. |
| `ASDUR306` | Schedule access or bridge-role denied | Use the authorized scoped client and exact configured runtime role; never set the RLS scope value manually. |
| `ASDUR307` | Schedule evaluation changed or clock safety suspended the Schedule | Correct the evaluator/time source, then update the definition or delete/recreate. Recovery release cannot move the cursor. |

The Work-first provider currently emits these codes for `At`, `After`, `Every`, and registered Work targets. Flow targets
and Cron evaluation are rejected until their separate transaction/evaluator gates have executable evidence. A Schedule
clock anomaly is not automatically retried: it records a suspension before any new occurrence or Work target is
accepted. After a PostgreSQL exception, timeout, disconnect, or SQLSTATE failure, roll back the whole transaction
before any bounded retry.

## PostgreSQL Work provider diagnostics

| Code | Meaning | Safe response |
|---|---|---|
| `ASDUR101` | Active caller transaction required | Start and pass the intended transaction; the writer never creates one for this API. |
| `ASDUR103` | Store unavailable | Roll back after PostgreSQL/connection errors; retry only under application policy. |
| `ASDUR104` | Claim lost | Read current Work truth; never execute or complete with the stale claim. |
| `ASDUR105` | Lease lost | Stop the attempt; it cannot acquire a permit or change current Work. |
| `ASDUR107` | Scope disabled | Treat the scope as a permanent tombstone; do not recreate it. |
| `ASDUR108` | Recovery epoch required | Rotate the epoch through deployment tooling after restore before releasing Work or Flow. |
| `ASDUR200` | Flow definition unavailable | Register required flow definition and version before starting or resuming instance. |
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
| `ASDUR211` | Release state mismatch | Suspended state and active wait/timer/work records disagree; V1 child-effect descriptors require repair, not release. |
| `ASDUR214` | Retention manifest not found | Verify scope and manifest ID; recreate manifest if necessary. |
| `ASDUR215` | Retention source changed | Re-assess Flow closure; do not purge with stale manifest. |
| `ASDUR216` | Retention lifecycle conflict | Reload manifest sequence and retry command. |
| `ASDUR217` | Retention lifecycle rejected | Verify lifecycle sequence and hold authorization. Release a legal hold only after an explicitly authorized decision; otherwise do not purge. |
| `ASDUR218` | Repair descriptor upgrade required | Existing suspensions without V1 identity remain unsupported; `0008` and compatible writers support only future descriptors. |
| `ASDUR219` | Repair evidence mismatch | Locked evidence changed or is incompatible; submit only a fresh assessment candidate. |
| `ASDUR220` | Repair action unsupported | The retained state is outside the two supported assertions; preserve evidence. |
| `ASDUR400` | Durable schema is missing | Apply reviewed forward-only migrations with a migration-owner connection. |
| `ASDUR401` | Durable schema upgrade is required | Apply every known pending migration before this reader/writer. |
| `ASDUR402` | Durable schema version is too new or unsupported | Deploy compatible package code; do not bypass supported ranges. |
| `ASDUR403` | Durable schema history is inconsistent | Compare ordered names/checksums; never rewrite applied history. |

After an Npgsql exception, timeout, cancellation, connection loss, or server error, the caller must roll back.
Diagnostics retain exception type, stack, inner exception, and SQLSTATE, but omit connection strings, credentials,
parameter values, payloads, and provider responses from the safe outer durable message/status. The retained
`PostgresException` is server-controlled evidence, not a safe log projection. See the
[`Work protocol`](../Durable/work-protocol-v1.md#caller-owned-transaction-contract),
[`Flow protocol`](../Durable/flow-protocol-v1.md), and
[`slice 4 reference workload`](../Durable/slice4-reference-workload.md#failure-interpretation).

Use the API method being called as the operation identifier. Ordinary provider failures keep their concrete
`NpgsqlException` or `PostgresException` type. `DurableRuntimeSchemaException.Status` is the safe schema-status snapshot;
if PostgreSQL exposed the missing schema during acceptance, its `InnerException` retains the original
`PostgresException` and SQLSTATE. Log only the API method, outer durable code/status, concrete exception type, and
five-character SQLSTATE. Never log or serialize inner message text, detail, hint, SQL text, object names, or parameters.

## PostgreSQL hosted-runtime diagnostics

| Code | Problem | Typical cause | Safe action |
|---|---|---|---|
| `ASDUR103` | Store unavailable | PostgreSQL transport or timeout blocks a bounded runtime pass | Retry after the configured bounded delay and inspect only safe infrastructure telemetry. |
| `ASDUR406` | Wake listener retry | The advisory wake-listener connection disconnected or timed out | Polling remains authoritative; retry the listener after the configured bounded delay and alert separately from pass failures. |
| `ASDUR404` | Activator stale | No current heartbeat or successful sweep inside `HeartbeatStaleAfter` | Check that exactly one compatible host or external activator is running, then inspect typed health and role/schema prerequisites. |
| `ASDUR405` | Worker identity conflict | Another live process owns the configured `WorkerId`, an old generation updated after takeover, or the same runtime instance already has an active pass | Assign a unique worker ID per replica, wait for stale/drain takeover rules, avoid overlapping local activation, and never edit the heartbeat row manually. |
| `ASDUR400`–`ASDUR403` | Incompatible runtime store | Missing, pending, unsupported, or inconsistent migration state | Apply reviewed migrations with the migration owner, rerun the role recipe, and deploy compatible code; startup intentionally performs no DDL. |
| `ASDUR108` | Recovery epoch required | The configured runtime epoch differs from the active store epoch | Perform authorized epoch initialization/rotation before enabling the worker host. |

The canonical activation path is the PostgreSQL package's [worker-host quickstart](../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#run-a-worker-host). Its source preview remains publication-held; real PostgreSQL reference workloads and runtime tests remain the operational proof surface.
