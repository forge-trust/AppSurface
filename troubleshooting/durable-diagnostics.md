# Durable contract diagnostics

AppSurface Durable uses append-only `ASDURxxx` codes. Messages and operator history must contain safe Problem, Cause,
Fix, and Docs guidance and must never include credentials, provider response bodies, tokenized URLs, email content, or
child-sensitive data.

The Durable contract and PostgreSQL source-preview packages emit the codes below. Hosted runtime codes remain reserved.
A reserved code is part of the compatibility namespace, not evidence that the corresponding behavior exists.

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
| `ASDUR206` | Flow start conflict | Start identity was reused with different semantic bytes | Reuse the exact request or allocate new identities |
| `ASDUR207` | Flow command conflict | Command/event identity was reused with different semantic bytes | Reuse the exact request or allocate new identities |
| `ASDUR208` | Flow not found | No instance exists in the authorized scope | Verify scope and opaque instance id |
| `ASDUR209` | Event contract mismatch | Payload does not match the active typed wait | Send the exact declared payload and reuse the unconsumed event id |
| `ASDUR210` | Release manifest mismatch | Registration differs from recoverable history | Deploy a compatible registration or migrate explicitly |
| `ASDUR211` | Release state mismatch | Suspended state and wait/timer/child-work truth disagree | Reconcile authoritative truth before release |

Schedule contracts reserve `ASDUR301`-`ASDUR307` for invalid definition, missing schedule, revision conflict, command
conflict, access denial, evaluation incompatibility, and recovery-state mismatch. A provider must map these codes to its
tested implementation without changing their meanings.

## PostgreSQL Work provider diagnostics

| Code | Meaning | Safe response |
|---|---|---|
| `ASDUR101` | Active caller transaction required | Start and pass the intended transaction; the writer never creates one for this API. |
| `ASDUR103` | Store unavailable | Roll back after PostgreSQL/connection errors; retry only under application policy. |
| `ASDUR104` | Claim lost | Read current Work truth; never execute or complete with the stale claim. |
| `ASDUR105` | Lease lost | Stop the attempt; it cannot acquire a permit or change current Work. |
| `ASDUR107` | Scope disabled | Treat the scope as a permanent tombstone; do not recreate it. |
| `ASDUR108` | Recovery epoch required | Rotate the epoch through deployment tooling after restore before releasing Work. |
| `ASDUR400` | Durable schema is missing | Apply reviewed forward-only migrations with a migration-owner connection. |
| `ASDUR401` | Durable schema upgrade is required | Apply every known pending migration before this reader/writer. |
| `ASDUR402` | Durable schema version is too new or unsupported | Deploy compatible package code; do not bypass supported ranges. |
| `ASDUR403` | Durable schema history is inconsistent | Compare ordered names/checksums; never rewrite applied history. |

After an Npgsql exception, timeout, cancellation, connection loss, or server error, the caller must roll back.
Diagnostics retain exception type, stack, inner exception, and SQLSTATE, but omit connection strings, credentials,
parameter values, payloads, and provider responses from the safe outer durable message/status. The retained
`PostgresException` is server-controlled evidence, not a safe log projection. See the
[`Work protocol`](../Durable/work-protocol-v1.md#caller-owned-transaction-contract) and
[`reference workload`](../Durable/slice3-reference-workload.md#failure-interpretation).

Use the API method being called as the operation identifier. Ordinary provider failures keep their concrete
`NpgsqlException` or `PostgresException` type. `DurableRuntimeSchemaException.Status` is the safe schema-status snapshot;
if PostgreSQL exposed the missing schema during acceptance, its `InnerException` retains the original
`PostgresException` and SQLSTATE. Log only the API method, outer durable code/status, concrete exception type, and
five-character SQLSTATE. Never log or serialize inner message text, detail, hint, SQL text, object names, or parameters.

## Reserved hosted-runtime diagnostics

| Code | Reserved meaning | Hosted-runtime prerequisite |
|---|---|---|
| `ASDUR404` | Activator stale | Persisted heartbeat and configured stale bound |
| `ASDUR405` | Worker identity conflict | Persisted process ownership and drain/handoff behavior |

Slice 3 intentionally has no hosted worker runbook or production activation command. Its canonical proof is the
source-evaluator [reference workload](../Durable/slice3-reference-workload.md), which manually drives protocol operations
against real PostgreSQL.
