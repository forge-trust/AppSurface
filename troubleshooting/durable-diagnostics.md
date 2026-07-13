# Durable runtime diagnostics

AppSurface Durable diagnostics use append-only `ASDURxxx` codes. Logs, typed results, metrics, operator history, and
runbooks should use the same code. Messages must contain safe **Problem**, **Cause**, **Fix**, and **Docs** guidance and
must not persist credentials, provider response bodies, URLs containing tokens, email content, or child-sensitive data.

## Work and provider effects

| Code | Problem | Typical cause | Safe action |
|---|---|---|---|
| `ASDUR100` | Request validation failed | Missing id, unregistered contract, unsafe payload, or invalid policy | Correct the caller contract before retrying |
| `ASDUR101` | Active caller transaction required | Transaction was committed, disposed, closed, or belongs to another database | Pass the exact active domain `NpgsqlTransaction` |
| `ASDUR102` | Command conflict | Command or idempotency key was reused with different semantic bytes | Reuse the original request or create a new identity intentionally |
| `ASDUR103` | Store unavailable | PostgreSQL connection, transaction, or lock failed | Retry the whole caller transaction or bounded runtime pass |
| `ASDUR104` | Claim lost | Another worker or transition advanced the revision | Stop this observation and rescan |
| `ASDUR105` | Lease lost | Lease expired, scope generation changed, or runtime epoch rotated | Stop before another provider call; preserve any observed result as stale history |
| `ASDUR106` | Ambiguous external outcome | Provider response was lost after `EffectPermitRecorded` | Retry only for declared idempotent/keyed work; otherwise reconcile or resolve manually |
| `ASDUR107` | Scope disabled | Application lifecycle disabled the owning scope | Do not re-enable merely to run old work; follow the scope lifecycle policy |
| `ASDUR108` | Recovery epoch required | Database was restored or epoch configuration does not match | Stop workers, rotate epoch, reconcile, and explicitly release safe work |
| `ASDUR109` | Work contract unavailable | Historical codec/executor registration was removed | Restore the registration or run an explicit compatibility migration |
| `ASDUR110` | Already terminal | A retry or operator command targeted completed work | Read the terminal fact; never repeat the executor through projection repair |

## Flow

| Code | Problem | Typical cause | Safe action |
|---|---|---|---|
| `ASDUR200` | Definition unavailable | Flow id/version is not registered | Restore that immutable definition before resuming instances |
| `ASDUR201` | History incompatible | Node, callsite, context, or result codec changed incompatibly | Suspend and migrate explicitly; do not guess the transition |
| `ASDUR202` | Not waiting yet | Event arrived before its exact active wait | Retry later with the same unconsumed event id |
| `ASDUR203` | Flow race lost | Timeout, event, cancellation, or terminal transition won the revision | Read current state; do not deliver a second continuation |
| `ASDUR204` | Event duplicate | Single-use event id already has an outcome | Return the original outcome when request bytes match |
| `ASDUR205` | Flow access denied | Application authorization or trusted scope check failed | Correct application policy; instance ids are not authorization tokens |
| `ASDUR206` | Flow start conflict | Start identity was reused with different semantic content | Reuse the exact original request or allocate new start identities |
| `ASDUR207` | Flow command conflict | Command or event identity was reused with different semantic content | Reuse the exact original request or allocate a new command and event identity |
| `ASDUR208` | Flow not found | The instance does not exist in the authorized scope | Verify the authorized scope and opaque instance id |
| `ASDUR209` | Event contract mismatch | Payload is absent, unexpected, or encoded with a different active-wait contract | Send the exact typed payload declared by the active wait, then reuse the unconsumed event id |
| `ASDUR210` | Release manifest mismatch | Registered definition, authoring model, evaluator, codec, or command schema differs from the recoverable instance | Deploy the exact compatible registration or run an explicit migration before release |
| `ASDUR211` | Release state mismatch | Recorded suspended state does not match its active wait, timer, or child-work truth | Reconcile the persisted truth before retrying the audited release |

## Scheduling

Schedule APIs use the `ASDUR301`–`ASDUR307` codes documented beside `IDurableScheduleClient`. Invalid Cronos grammar,
unsupported dialects, missing schedules, revision conflicts, command conflicts, authorization failure, and changed
evaluator or time-zone fingerprints are rejected before occurrence generation. Use `ExplainNextOccurrencesAsync` to
inspect evaluated UTC times without starting work.

## Schema and activation

| Code | Problem | Typical cause | Safe action |
|---|---|---|---|
| `ASDUR400` | Schema missing | Numbered migrations were never applied | Generate and apply the script with the migration owner |
| `ASDUR401` | Upgrade required | Store is older than this runtime's required writer version | Apply pending expand migrations before starting claims |
| `ASDUR402` | Version unsupported | Runtime falls outside installed reader/writer ranges | Deploy a compatible package; do not bypass preflight |
| `ASDUR403` | Migration metadata inconsistent | Migration order, name, or hash was modified | Stop startup and repair through a reviewed forward migration |
| `ASDUR404` | Activator stale | No pass completed, the heartbeat exceeded `HeartbeatStaleAfter`, or a scale-to-zero host has no activator | Restore the hosted loop or external activator; keep the stale bound above its expected cadence |
| `ASDUR405` | Worker identity conflict | Another non-stale process owns the configured worker id, or one process attempted overlapping passes | Configure a unique id per live replica; drain and await the prior pass, or wait for an unclean owner to become stale |

Run [`appsurface durable schema status` and `preflight`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-durable-schema)
for deployment checks. Runtime retry, cancel, reconciliation, and restore release remain application-authorized APIs, not
raw database CLI commands.

For a runnable diagnosis loop, use the [durable PostgreSQL example](../examples/durable-postgresql/README.md). The
[PostgreSQL package operations reference](../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md) covers role
grants, [runtime health and graceful drain](../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#runtime-health-and-graceful-drain),
restore fencing, and real-database testing.
