# Durable contract diagnostics

For schema-11 runtime heartbeat maintenance, use the [retention operations guide](../Durable/heartbeat-retention-operations.md#recover-and-diagnose) and its [complete runtime-set receipt checklist](../Durable/heartbeat-retention-operations.md#complete-runtime-set-preflight-and-proof-checklist). A pending migration 0011 preflight means the downtime migration has not been applied; drain workers, apply it as migration owner, reconcile the complete reviewed manifest, then require one passing preflight under each distinct runtime credential. A migration advisory-lock, runtime-fence, or index table-lock timeout keeps activation closed until the blocker is resolved and the entire gate is rerun. The role recipe may refuse observed drift; follow the category-specific direction below and use a reviewed repair. Repeated prune failures mean inspect database reachability and exact grants, then retry or pause via a new provider registration. Cleanup failures alone do not change Work readiness.

AppSurface Durable uses append-only `ASDURxxx` codes. Messages and operator history must contain safe Problem, Cause,
Fix, and Docs guidance and must never include credentials, provider response bodies, tokenized URLs, email content, or
child-sensitive data.

The Durable contract and PostgreSQL public-preview packages emit the codes below. Hosted-runtime diagnostics use only
fixed, low-cardinality codes and never expose connection targets, notification payloads, scopes, aggregates, or trace
context.

## Canonical runtime diagnostic descriptors

These entries provide shared problem, cause, fix, and documentation wording for provider health/admission codes and
the non-mutating Durable runtime doctor. Health producers retain their existing snapshots, codes, and classification
rules; the catalog does not change their factories. The catalog intentionally covers this affected set; other Durable
and application-owned codes keep their existing owners and meanings.

### ASDUR103 Store unavailable

Problem: Store unavailable

Cause: A PostgreSQL transport, permission, session-affinity, timeout, or cleanup failure prevented the bounded durable operation from completing.

Fix: Restore PostgreSQL connectivity and read permissions, establish session affinity, or unblock the cooperative maintenance fence; retry only under application policy.

### ASDUR108 Recovery epoch required

Problem: Recovery epoch required

Cause: The configured runtime epoch differs from the active store epoch.

Fix: Perform authorized epoch initialization or rotation before enabling the worker host.

### ASDUR400 Durable schema is missing

Problem: Durable schema is missing

Cause: The Durable schema or migration history is not installed.

Fix: Apply reviewed forward-only migrations with a migration-owner connection.

### ASDUR401 Durable schema upgrade is required

Problem: Durable schema upgrade is required

Cause: Known migrations are pending in the installed Durable schema.

Fix: Apply every known pending migration before this reader or writer.

### ASDUR402 Durable schema version is too new or unsupported

Problem: Durable schema version is too new or unsupported

Cause: The installed reader or writer range excludes this package.

Fix: Deploy compatible package code; do not bypass supported ranges.

### ASDUR403 Durable schema history is inconsistent

Problem: Durable schema history is inconsistent

Cause: Recorded migration names, checksums, order, or metadata do not match the expected schema history.

Fix: Compare ordered names and checksums; never rewrite applied history.

### ASDUR404 Initial heartbeat not observed or activator stale

Problem: Initial heartbeat not observed or activator stale

Cause: NotStarted may mean the first worker heartbeat is absent; Stale means the observed heartbeat or sweep exceeded HeartbeatStaleAfter.

Fix: For NotStarted, treat it as a compatible initial assessment and follow the host's activation policy. For Stale, inspect the configured runtime and role/schema prerequisites.

### ASDUR408 Restricted runtime credential required

Problem: Restricted runtime credential required

Cause: The connected role has a prohibited attribute, membership, ownership, grant option, or heartbeat-table privilege.

Fix: Use a dedicated restricted LOGIN runtime credential and complete the reviewed runtime-role preflight; do not use a migration owner.

### ASDUR409 Heartbeat-retention capability unavailable

Problem: Heartbeat-retention capability unavailable

Cause: The retention function, its required permissions or configuration, or the retention index is missing or does not meet the schema 0011 contract.

Fix: Review the schema 0011 retention function and index contract, apply an authorized repair, then rerun doctor and complete runtime preflight.

### ASDUR410 No retained heartbeat for the selected worker

Problem: No retained heartbeat for the selected worker

Cause: No retained heartbeat matches the selected worker ID; the worker may not have started, the ID may differ, or retention may have removed the row.

Fix: Verify the configured worker ID and host activation policy; rerun doctor after the worker records a heartbeat.

### ASDUR411 Selected worker is draining

Problem: Selected worker is draining

Cause: The selected worker heartbeat records that the worker is refusing new passes while existing work drains.

Fix: Let in-flight work finish and follow the host's drain or deployment procedure before expecting new claims.

### ASDUR412 Selected heartbeat belongs to another runtime epoch

Problem: Selected heartbeat belongs to another runtime epoch

Cause: The retained heartbeat's runtime epoch does not match the configured and active store epoch.

Fix: Resolve the authorized runtime-epoch mismatch, then rerun doctor with the host's configured epoch.

### ASDUR413 Doctor input is invalid

Problem: Doctor input is invalid

Cause: A command option or selected environment value is absent or malformed.

Fix: Provide required values through the selected environment variables, use a restricted runtime credential, and check supported options with appsurface durable doctor --help.

### ASDUR414 Doctor was canceled by its caller

Problem: Doctor was canceled by its caller

Cause: The caller canceled the valid doctor request before it completed.

Fix: Retry only when the caller intends a fresh diagnostic attempt.

### ASDUR415 Doctor encountered an unexpected contract failure

Problem: Doctor encountered an unexpected contract failure

Cause: The CLI or provider returned malformed, contradictory, or otherwise unexpected doctor evidence.

Fix: Verify the matching CLI and provider packages, investigate their contract compatibility and the canonical troubleshooting diagnostics, and retry only after resolving the mismatch.

## Schema-11 complete runtime preflight

The CLI preflight requires `--role-pairs-file` and `--migration-owner-role` for one or many pairs. See the [CLI contract](../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands) for exact input limits and checked catalog predicates and the [canonical staged workflow](../Durable/heartbeat-retention-operations.md#complete-runtime-set-preflight-and-proof-checklist) for candidate, published, and deployment gates. The fixed categories below report only validated pair index/role identifiers. Unknown results use no raw server text. Never paste JSON, SQL, ACL text, connection values, provider exceptions, or secret values into an issue.

| Category | Problem and likely cause | Safe action |
| --- | --- | --- |
| `manifest_input` | The file is unreadable or violates strict UTF-8/no-BOM, 64 KiB, depth-8, exact-shape, duplicate-property, type, pair-count, profile, or role-name limits. | Use the complete reviewed file (1–32 pairs), fix it through review, and pass the same exact file to the recipe and every runtime command. |
| `connection_input` | The environment-variable name, secret value, or PostgreSQL connection configuration is invalid. | Correct the named environment variable without printing its value, then rerun with the unchanged reviewed manifest. |
| `database_operation` | A read-only PostgreSQL operation failed. | Check server reachability, the reviewed role grants and session-affine package fence; verify owned resources released before repeating the complete gate. |
| `role_resolution` | An expected dispatcher, runtime, or owner name does not resolve exactly once. | Check the reviewed spelling and database role provisioning; do not infer a substitute from catalogs. |
| `role_alias` | Distinct manifest names or the owner resolve to an ambiguous or repeated identity. | Correct the reviewed role mapping so every expected name maps to a unique OID and the owner is disjoint. |
| `runtime_role` | A runtime is not a restricted LOGIN leaf or has a prohibited role attribute. | Review role attributes and repair through the approved identity/role process; rerun every pair. |
| `role_membership` | A runtime has an incoming or outgoing membership edge. | Review membership grants and remove only through an authorized reviewed change; rerun the full manifest. |
| `runtime_ownership` | A runtime owns a database or package schema, relation, sequence, or function. | Review ownership and transfer it through the approved owner workflow; rerun all credentials. |
| `caller_identity` | `session_user` and `current_user` cannot be resolved to the same identity. | Use a direct credential session without role switching; check the endpoint and connection configuration without exposing values. |
| `caller_role` | The caller is outside the manifest runtimes and independent owner, or the runtime result does not match its expected pair. | Select the correct environment-variable name for this pass; owner diagnostics do not count as runtime evidence. |
| `forced_rls` | Heartbeat row-level security is absent, disabled, or not forced. | Review schema/migration state and apply an authorized forward repair, then rerun complete preflight. |
| `function_signature` | The pruning function's four-argument signature, integer/non-set result, or function kind differs. | Compare against the released migration/catalog contract and use a reviewed forward repair; do not edit migration history. |
| `function_owner` | The schema, heartbeat table, retention function, due-health function, or owner policy does not match the independently reviewed owner. | Verify the owner input and perform only a reviewed ownership correction; do not accept a consistent but unexpected owner. |
| `security_definer` | The retention function is not `SECURITY DEFINER` as required. | Review the function definition from the matching package/migration; repair forward and rerun all checks. |
| `search_path` | The retention function has an unexpected search path. | Restore the exact reviewed function setting using the approved package procedure, then rerun. |
| `heartbeat_policies` | Required heartbeat policies, owner policy, expressions, command, permissiveness, or exact count differ. | Inspect through an authorized catalog review; reconcile using the complete manifest only if the recipe accepts the observed state. |
| `runtime_policy_set` | A managed runtime policy contains missing, extra, duplicate, PUBLIC, or unresolved targets, including on a noncurrent pair. | Compare raw targets with the reviewed complete manifest; never grant a wider set to make the check pass. |
| `function_acl` | Pruning EXECUTE is missing for a runtime, has grant option, or includes an unexpected grantee. | Review the exact function ACL and default ACL; apply a reviewed narrow repair and rerun every pair. |
| `due_health_acl` | Due-health EXECUTE does not match the expected owner-plus-runtime set. | Review the released role recipe and effective grants; do not infer authorization from one passing role. |
| `runtime_table_privileges` | A runtime has effective heartbeat DELETE or TRUNCATE, including inherited/PUBLIC rights. | Trace direct and inherited grants, then remove excess rights only with review; rerun complete preflight. |
| `retention_index` | The required index is absent, invalid/not ready, or differs in uniqueness, ordering, keys, opclass, predicate, or expression. | Verify migration 0011 and index state; use the downtime/forward-repair procedure and rerun. |
| `catalog_result` | The query returned null, incomplete, contradictory, oversized, or otherwise unexpected catalog evidence. | Keep activation closed; inspect safe server health and supported schema state. Do not treat partial evidence as success. |
| `cleanup` | Transaction rollback, explicit fence unlock, or physical connection disposal could not be confirmed. | Keep activation closed, allow owned resources to drain, inspect server/session health, then run every credential pass again. |
| `timeout` | Connection, lock wait, query work, or cleanup exceeded the 30-second total (28-second work plus at most 2-second cleanup) or was canceled. | Resolve the direct endpoint/load/lock cause; do not increase connection timeouts or claim partial success. Rerun the entire combined gate under a fresh continuous guard. |

The canonical recipe is idempotent for accepted state but can refuse drift it cannot safely reconcile. A recipe failure does not authorize ad-hoc grants or mean that rerunning it will repair every finding. The deployment owner must keep the full gate closed until a reviewed repair, all distinct runtime passes, both lane proofs, matching StoreId/nonempty epoch, and the continuous guard window are re-established.

## Available contract diagnostics

### Typed Work definition validation

Typed Work definitions fail locally before provider acceptance. These failures are bounded contract diagnostics and do
not use or impersonate provider-owned `ASDURxxx` codes:

| Failure boundary | Meaning | Safe response |
|---|---|---|
| Definition construction | Work identity, safety, retry default, or codec metadata/type is invalid | Correct the named contract and recreate the definition; no request or provider retry is appropriate |
| Request construction | Scope, command, duplicate key, input, retry override, or due time is invalid | Correct the caller choices and create a new request with coherent identities |
| Guarded codec encode/decode | Consumer codec output or input metadata differs from the captured contract | Repair the codec implementation or create a new Work version when semantics changed; never relabel payload bytes |
| Binding completion | Reconciliation is missing, safety does not match, or exit binding is used outside `ProviderKeyed` | Select the binding method matching the declared safety and replace duplicate registrations |
| Flow registration resolution | Flow activity does not use the exact globally registered Work registration or codec contract | Resolve by exact Work name/version from `IDurableWorkRegistry`; do not build a nested provider or second registration |

These local failures are distinct from `ASDUR109` (historical Work contract unavailable), `ASDUR100` (provider request
validation), and `ASDUR119` (PostgreSQL discovery registry snapshot unavailable). A passing passive proof means only that
the contracts, request parity, and registry construction succeeded; it does not mean Work was accepted or terminalized.

| Code | Problem | Typical cause | Safe action |
|---|---|---|---|
| `ASDUR100` | Request validation failed | Default/missing id, unregistered contract, unsafe payload, limit violation, or invalid policy | Correct the caller contract before retrying |
| `ASDUR102` | Command conflict | A command identity was reused with a different known-schema fingerprint | Reuse the original semantic request or allocate a new command id |
| `ASDUR106` | Ambiguous external outcome | Provider response was lost after an effect permit, or a post-permit executor/runtime failure occurred | Follow declared provider safety; reconcile or resolve rather than guessing |
| `ASDUR109` | Work contract unavailable | Historical codec/executor registration is absent | Restore that immutable registration or perform an explicit migration |
| `ASDUR119` | Work discovery contract selection unavailable | PostgreSQL worker activation could not snapshot the complete, exact custom registry contracts | Implement stable `RegisteredContracts`, correct default/duplicate/oversized values, then restart the host |
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

### ASDUR119

PostgreSQL could not form the in-memory Work discovery authority while resolving the worker host. A custom
`IDurableWorkRegistry` must expose one complete, exact `RegisteredContracts` snapshot; it must not throw, be null,
contain default or duplicate pairs, or contain more than 10,000 contracts. An empty snapshot is valid and leaves Work
passes quiescent. The provider deliberately snapshots that list
once, so registry mutation after activation does not change what the host may discover. Correct the registry and restart
the host. Migration, role, schema, or epoch failures use their own schema/runtime diagnostics instead.

### Exit-aware Work codes

An `IDurableWorkExitExecutor<TWork,TResult>` returns an application-owned code only with a non-success exit. Codes are
bounded to 120 characters and use Durable's identifier alphabet; a safe example is
`app.gmail.sender_list_transient`. They must never contain provider responses, payload values, credentials, URLs,
exception messages, or a code beginning with the reserved `ASDUR` prefix, case-insensitively.

`RetryBeforeEffect` is the sole exit that can enter PostgreSQL's existing `proven_no_effect` retry path. It means the
executor can prove it did not begin an external provider mutation; read-only provider I/O is allowed. It does not mean
that Durable did not issue an effect permit. The provider still evaluates cancellation, retry limits, deadline,
lease/scope/epoch/revision fences, and dispatch state. A
`FailedTerminal` exit after a permit is not evidence that no external effect occurred; V1's `ProviderKeyed` contract
therefore preserves ambiguity safety. `AmbiguousExternalOutcome` explicitly preserves the same safety path.

After the effect permit, executor exceptions, cancellation, lease loss, result-serialization failures, and a
non-success exit invoked through a legacy success-only provider boundary use `ASDUR106`, not an application retry
code. Registration or codec resolution failures before the permit use `ASDUR109`. Operators should use the
[typed-exit protocol](../Durable/work-protocol-v1.md#typed-executor-exits) to distinguish a user-returned fact from a
provider-owned diagnostic.

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
| [`ASDUR103`](#asdur103-store-unavailable) | Store unavailable | Restore PostgreSQL connectivity and read permissions, establish session affinity, or unblock the cooperative maintenance fence; retry only under application policy. |
| `ASDUR104` | Claim lost | Read current Work truth; never execute or complete with the stale claim. |
| `ASDUR105` | Lease lost | Stop the attempt; it cannot acquire a permit or change current Work. |
| `ASDUR107` | Scope disabled | Treat the scope as a permanent tombstone; do not recreate it. |
| [`ASDUR108`](#asdur108-recovery-epoch-required) | Recovery epoch required | Perform authorized epoch initialization or rotation before enabling the worker host. |
| `ASDUR119` | Work discovery contract selection unavailable | Worker activation could not snapshot complete custom registry contracts | Correct `RegisteredContracts` and restart the host |
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
| [`ASDUR400`](#asdur400-durable-schema-is-missing) | Durable schema is missing | Apply reviewed forward-only migrations with a migration-owner connection. |
| [`ASDUR401`](#asdur401-durable-schema-upgrade-is-required) | Durable schema upgrade is required | Apply every known pending migration before this reader or writer. |
| [`ASDUR402`](#asdur402-durable-schema-version-is-too-new-or-unsupported) | Durable schema version is too new or unsupported | Deploy compatible package code; do not bypass supported ranges. |
| [`ASDUR403`](#asdur403-durable-schema-history-is-inconsistent) | Durable schema history is inconsistent | Compare ordered names and checksums; never rewrite applied history. |

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
| [`ASDUR103`](#asdur103-store-unavailable) | Store unavailable | A PostgreSQL transport, permission, session-affinity, timeout, or cleanup failure prevented the bounded durable operation from completing. | Restore PostgreSQL connectivity and read permissions, establish session affinity, or unblock the cooperative maintenance fence; retry only under application policy. |
| `ASDUR406` | Wake listener retry | The advisory wake-listener connection disconnected or timed out | Polling remains authoritative; retry the listener after the configured bounded delay and alert separately from pass failures. |
| [`ASDUR404`](#asdur404-initial-heartbeat-not-observed-or-activator-stale) | Initial heartbeat not observed or activator stale | NotStarted may mean the first worker heartbeat is absent; Stale means the observed heartbeat or sweep exceeded HeartbeatStaleAfter. | For NotStarted, treat it as a compatible initial assessment and follow the host's activation policy. For Stale, inspect the configured runtime and role/schema prerequisites. The health probe preserves the observed code; activation results retain it only for Stale. |
| `ASDUR405` | Worker identity conflict | Another live process owns the configured `WorkerId`, an old generation updated after takeover, or the same runtime instance already has an active pass | Assign a unique worker ID per replica, wait for stale/drain takeover rules, avoid overlapping local activation, and never edit the heartbeat row manually. |
| [`ASDUR400`](#asdur400-durable-schema-is-missing)–[`ASDUR403`](#asdur403-durable-schema-history-is-inconsistent) | Incompatible runtime store | Missing, pending, unsupported, or inconsistent migration state | Apply reviewed migrations with the migration owner, rerun the role recipe, and deploy compatible code; startup intentionally performs no DDL. See each code's canonical entry for its specific action. |
| [`ASDUR108`](#asdur108-recovery-epoch-required) | Recovery epoch required | The configured runtime epoch differs from the active store epoch. | Perform authorized epoch initialization or rotation before enabling the worker host. |
| [`ASDUR408`](#asdur408-restricted-runtime-credential-required) | Restricted runtime credential required | The connected role has a prohibited attribute, membership, ownership, grant option, or heartbeat-table privilege. | Use a dedicated restricted LOGIN runtime credential and complete the reviewed runtime-role preflight; do not use a migration owner. |
| [`ASDUR409`](#asdur409-heartbeat-retention-capability-unavailable) | Heartbeat-retention capability unavailable | The retention function, its required permissions or configuration, or the retention index is missing or does not meet the schema 0011 contract. | Review the schema 0011 retention function and index contract, apply an authorized repair, then rerun doctor and complete runtime preflight. |
| [`ASDUR410`](#asdur410-no-retained-heartbeat-for-the-selected-worker) | No retained heartbeat for the selected worker | No retained heartbeat matches the selected worker ID; the worker may not have started, the ID may differ, or retention may have removed the row. | Verify the configured worker ID and host activation policy; rerun doctor after the worker records a heartbeat. |
| [`ASDUR411`](#asdur411-selected-worker-is-draining) | Selected worker is draining | The selected worker heartbeat records that the worker is refusing new passes while existing work drains. | Let in-flight work finish and follow the host's drain or deployment procedure before expecting new claims. |
| [`ASDUR412`](#asdur412-selected-heartbeat-belongs-to-another-runtime-epoch) | Selected heartbeat belongs to another runtime epoch | The retained heartbeat's runtime epoch does not match the configured and active store epoch. | Resolve the authorized runtime-epoch mismatch, then rerun doctor with the host's configured epoch. |
| [`ASDUR413`](#asdur413-doctor-input-is-invalid) | Doctor input is invalid | A command option or selected environment value is absent or malformed. | Provide required values through the selected environment variables, use a restricted runtime credential, and check supported options with appsurface durable doctor --help. |
| [`ASDUR414`](#asdur414-doctor-was-canceled-by-its-caller) | Doctor was canceled by its caller | The caller canceled the valid doctor request before it completed. | Retry only when the caller intends a fresh diagnostic attempt. |
| [`ASDUR415`](#asdur415-doctor-encountered-an-unexpected-contract-failure) | Doctor encountered an unexpected contract failure | The CLI or provider returned malformed, contradictory, or otherwise unexpected doctor evidence. | Verify the matching CLI and provider packages, investigate their contract compatibility and the canonical troubleshooting diagnostics, and retry only after resolving the mismatch. |
| `ASDUR407` | External activation failed | Unexpected nonfatal setup/health failure before admission, or provider/execution/bookkeeping failure after admission | Use the activation phase to distinguish zero admission calls from an invoked pass; inspect persisted Work/effect state before any caller retry. Never use exception text as a result code. |

The canonical activation path is the PostgreSQL package's [worker-host quickstart](../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#run-a-worker-host). It is a public-preview package; real PostgreSQL reference workloads and runtime tests remain the operational proof surface.

For the exact ten service outcomes and HTTP projection, see the canonical [external activation operator table](../Durable/external-activation-v1.md#operator-actions-and-recovery). A host probe's `ProbeFailed/ASDUR407` means no assessment was obtained; it does not synthesize an `Unavailable` assessment. `ProbeCanceled` records request/transport cancellation. Neither probe result establishes execution or effect truth.
