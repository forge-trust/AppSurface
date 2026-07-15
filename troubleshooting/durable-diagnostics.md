# Durable contract diagnostics

AppSurface Durable uses append-only `ASDURxxx` codes. Messages and operator history must contain safe Problem, Cause,
Fix, and Docs guidance and must never include credentials, provider response bodies, tokenized URLs, email content, or
child-sensitive data.

This slice ships contracts only. The codes below distinguish diagnostics that contract implementations can emit now
from codes reserved for a future storage/runtime provider. A reserved code is part of the compatibility namespace, not
evidence that the corresponding provider behavior exists.

## Available contract diagnostics

| Code | Problem | Typical cause | Safe action |
|---|---|---|---|
| `ASDUR100` | Request validation failed | Default/missing id, unregistered contract, unsafe payload, limit violation, or invalid policy | Correct the caller contract before retrying |
| `ASDUR102` | Command conflict | A command identity was reused with a different known-schema fingerprint | Reuse the original semantic request or allocate a new command id |
| `ASDUR106` | Ambiguous external outcome | Provider response was lost after an effect permit | Follow declared provider safety; reconcile or resolve rather than guessing |
| `ASDUR109` | Work contract unavailable | Historical codec/executor registration is absent | Restore that immutable registration or perform an explicit migration |
| `ASDUR110` | Already terminal | A retry or operator request targets terminal Work | Return terminal truth; never repeat the executor |
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

## Reserved provider diagnostics

| Code | Reserved meaning | Provider prerequisite |
|---|---|---|
| `ASDUR101` | Active caller transaction required | Transactional storage writer |
| `ASDUR103` | Store unavailable | Concrete storage implementation and retry policy |
| `ASDUR104` | Claim lost | Atomic claim/revision implementation |
| `ASDUR105` | Lease lost | Lease, scope-generation, and runtime-epoch fencing |
| `ASDUR107` | Scope disabled | Authoritative scope lifecycle storage |
| `ASDUR108` | Recovery epoch required | Restore detection and explicit recovery release |
| `ASDUR400`-`ASDUR403` | Schema lifecycle failure | Numbered migrations and compatibility preflight |
| `ASDUR404` | Activator stale | Persisted heartbeat and configured stale bound |
| `ASDUR405` | Worker identity conflict | Persisted process ownership and drain/handoff behavior |

No CLI command, PostgreSQL example, migration, or provider runbook is linked here because those artifacts are outside
Durable slice 2. They become canonical only when the provider milestone implements and verifies them.
