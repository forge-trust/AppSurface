# Issue #869 handoff fence test plan

Status: **PASSED** for the 44 minimum IDs and mandatory supplemental executions, using the final committed-source native and fresh packed receipts recorded below. Test source, a local build, or an earlier unrelated green suite does not mark a row passed. This plan preserves the approved 44 scenario IDs and all required parameterized and supplemental executions from the [approved implementation plan](issue-869-transaction-bound-handoff-fence.md).

The first proof is the public-only PostgreSQL path under restrictive runtime/application roles: exact caller transaction, both row-level security policies, two-savepoint behavior, prior unrelated caller marker preservation, and both application authority race orders. Stop and revise if that proof fails. Only then complete the full matrix. No private-type access or reflection is allowed in consumer tests.

## Test values

| Value card | Risk and controlled stimulus | Decisive observable result | Evidence that does not count |
| --- | --- | --- | --- |
| Invocation authority | Call outside the executor, after invocation exit, through a borrowed scoped instance, and from simultaneous async contexts. | Only the exact active invocation reaches the callback; refused calls have zero callback count. | A unit-only marker test does not prove pump binding or the real executor boundary. |
| Caller transaction and savepoints | Use the exact open `ReadCommitted` transaction, write an unrelated marker before the operation savepoint, then exercise callback true, false, throw, caller cancellation, and aborted SQL. Include an existing caller savepoint. | The outer transaction stays caller-owned; callback writes roll back on denial/failure; unrelated earlier writes remain; cleanup failure preserves both causes and requires outer rollback/disposal. A callback exception with the caller token still active rolls back callback writes but retains native locks until the test ends the outer transaction; observed caller-token cancellation rolls back the operation scope and may release those locks. | Returning `HandoffPrepared=true` does not prove commit visibility. A mock cannot prove PostgreSQL savepoint lock retention. |
| Fresh native facts and time | Mutate one native fact at a time and in approved multi-fault pairs. Hold real locks past lease/deadline using the controlled database clock and bounded barriers. | The first applicable refusal code wins; no refused handoff is visible to a fresh reader; time is sampled after waits and callback work. | Cached claim expiry/revision, sleeps-only races, or private predicate mirrors do not count. |
| Serialization and lock lifetime | Run both winner orders for native state changes and application revocation; leave the successful caller transaction open after method return. | The losing writer is observed blocked with `pg_blocking_pids`; it proceeds only after outer commit/rollback, then observes the serialized state. | A method-return assertion without observing the blocker does not prove locks remain held. |
| Roles, RLS, and pool state | Run the actual executor with restrictive native and application grants, both RLS policies, cross-scope data, prior/no scope settings, and pooled reuse. | Authorized same-scope path succeeds; missing grants/cross-scope access fail closed; prior effective scope is restored; no role escalation occurs. | Owner credentials or disabled RLS cannot prove the runtime path. |
| Public package and provider boundary | Build and execute through the packed PostgreSQL consumer with a fake provider that inspects transaction/connection state. Inject commit acknowledgement loss, established rollback, unresolved outcome, and post-commit cancellation. | Package-only consumer compiles; no provider call starts before transaction and connection release; unresolved commit and canceled executor suppress unsafe sends while preserving ledger facts. | Framework project references, private types, source-only compilation, or a fake provider acceptance claim do not count. |

## Branch map

Every branch below maps to executed scenarios through the [completion audit](issue-869-plan-completion-audit.md); terminal evidence is recorded under Current evidence.

| Implementation decision or branch | Planned scenario coverage | Primary evidence surface |
| --- | --- | --- |
| Null arguments; absent/inactive/mismatched invocation; same-instance overlap/reentry; exact active connection/physical target; `ReadCommitted`; caller cancellation before database work | U1–U5, N1, supplemental isolation/reentry | Public API and scoped contract tests; registration and pump-boundary regressions. |
| Scoped DI interface/concrete alias and per-scope separation; actual executor binding after permit/admission; deactivation in `finally` | U1–U3, U8, B7, K2 | DI/API snapshot, async-context contract tests, runtime-pump and packed consumer tests. |
| Operation savepoint creation, prior effective scope capture, shared epoch fence, schema/StoreId/epoch validation | N2–N4, R5–R6, B5–B6, CT3 | Real PostgreSQL tests with savepoint coexistence, coded exceptions, and caller-write visibility. |
| Scope missing/disabled/generation conflict; Work missing; identity mismatch; terminal/canceled state; absent/expired lease; opted-in deadline | U7, N5–N10, B1–B2, R1–R4, R7–R8 | Locked native facts, exact refusal code, fresh-reader assertion, controlled clock. |
| Dispatch/exact permit/admission validation, legacy and opted-in paths, no history scan, renewal-sensitive facts | N10, supplemental legacy/opt-in and renewal/history cases | Keyed real PostgreSQL reads and representative long permit history; existing renewal/admission regressions retained. |
| Callback savepoint and single invocation; true, false, exception, callback cancellation, SQL-aborted transaction; sequential calls and conditional duplicate protection | U6, B3–B6, B8, CT1–CT3, R9–R10 | Exact callback count, database visibility, original exception identity after successful cleanup, and app-row race barriers. For an active caller token and thrown callback, hold the caller transaction open after catching the exception, observe a competing native transition blocked, and release it only by ending that outer transaction. |
| Final decision after scope restoration and fresh database-time sample; active invocation and caller-token check; cleanup with fresh bounded token | B1–B3, B6–B7, supplemental before/after-last-check cases | Controlled final-check and cleanup seams; verify callback writes are absent on pre-decision cancellation/refusal. |
| Callback rollback retains native locks; operation rollback on pre-callback refusal can release operation locks; outer transaction remains caller-owned | CT1–CT3, R2/R4/R6/R8/R10, B5–B6 | Real lock-blocking observation through caller completion, plus fresh readers after commit and rollback. |
| Restrictive roles, both RLS policies, cross-scope filtering, scope restoration, pooled connection reuse | P1–P3 and scope/pool supplements | Real PostgreSQL role recipe and fresh connections; no migration-owner runtime. |
| Packed consumer commit/disposal order, stable application-ledger key, ambiguous commit and cancellation-to-provider pause | K1–K2 plus packed supplements | Fresh package version/bytes, public-only consumer, fake-provider call/resource observations. |
| Host-owned finite lock/statement/idle-transaction bounds and abandoned idle transaction release | Supplemental host-policy cases | Record effective settings and owner; observe terminated connection, rolled-back handoff, and released epoch/Work locks. No universal timeout values. |

## Minimum scenario matrix: 44 IDs

The `Status` for every row is **Passed** against the final source and package inputs below. Parameterized executions listed below are additional runs, not additional minimum IDs. Refused outcomes must be checked by a fresh database reader wherever a handoff row could have been written.

| ID | Setup | Required assertion | Status |
| --- | --- | --- | --- |
| U1 | Call with no active native invocation. | `ClaimLost`; callback count is zero. | Passed |
| U2 | Retain the service and call after its invocation exits. | `ClaimLost`; callback count is zero. | Passed |
| U3 | Invocation A borrows B's scoped service/reference. | A cannot use B's binding; callback count is zero. | Passed |
| U4 | Overlap or reenter a call on one scoped service. | `InvalidOperationException` before the second callback. | Passed |
| U5 | Null, closed, completed, wrong-target, or unsupported-isolation transaction. | Documented exception before callback; caller ownership and isolation stay unchanged. | Passed |
| U6 | Callback returns false or throws after entry. | False is successful denial with callback writes removed; thrown exception propagates after cleanup; callback runs once. | Passed |
| U7 | Supply a parameterized stale native fact. | Exact existing bounded Durable problem code; no unauthorized handoff. | Passed |
| U8 | Resolve interface and concrete service in one scope and separate scopes; inspect public API snapshot. | Interface/concrete resolve to the same scoped instance; scopes differ; signature is exact with no public binder. | Passed |
| CT1 | Callback succeeds and caller commits. | Fresh reader sees the handoff only after caller commit. | Passed |
| CT2 | Callback succeeds and caller rolls back. | Fresh reader never sees the handoff. | Passed |
| CT3 | Write unrelated marker before fence; callback stages ledger write then denies. | Caller may commit the earlier marker; callback write is absent. | Passed |
| R1 | Scope disable commits before handoff obtains its fence. | Handoff refuses; no ledger row survives. | Passed |
| R2 | Handoff takes native locks before scope disable. | Disable is observed blocked after method return until caller completion. | Passed |
| R3 | Native cancellation commits before handoff. | Handoff refuses; no ledger row survives. | Passed |
| R4 | Handoff takes native locks before native cancellation. | Cancellation is observed blocked until caller completion. | Passed |
| R5 | Epoch rotation commits before handoff. | Handoff receives existing epoch refusal; no ledger row survives. | Passed |
| R6 | Handoff takes shared epoch fence before rotation. | Rotation is observed blocked until caller completion. | Passed |
| R7 | Lease reclaim commits before handoff. | Stale claim is refused; no ledger row survives. | Passed |
| R8 | Handoff locks the claim before reclaim. | Reclaim is observed blocked until caller completion. | Passed |
| R9 | Application revocation commits before callback can lock authority. | Callback observes denial; no ledger row survives. | Passed |
| R10 | Handoff callback locks authority before revocation. | Revocation is observed blocked until caller completion; handoff state follows the winning transaction. | Passed |
| N1 | Transaction targets another physical PostgreSQL host, port, or database. | `InvalidOperationException` before callback. | Passed |
| N2 | Configured StoreId does not match. | Existing coded exception; callback is not run. | Passed |
| N3 | Runtime schema is unsupported. | Safe `DurableRuntimeSchemaException`; callback is not run. | Passed |
| N4 | Active epoch differs from the configured runtime epoch. | Existing coded exception; callback is not run. | Passed |
| N5 | Scope generation is stale. | `ScopeGenerationConflict`; callback is not run. | Passed |
| N6 | Activity identity differs. | `ClaimLost`; callback is not run. | Passed |
| N7 | Attempt number is stale. | `ClaimLost`; callback is not run. | Passed |
| N8 | Lease generation or owner differs. | `ClaimLost` for each parameterized mismatch; callback is not run. | Passed |
| N9 | Current lease is expired or absent. | `LeaseLost`; callback is not run or callback writes are removed. | Passed |
| N10 | Parameterize missing/noncurrent permit, terminal/canceled/missing Work, and missing scope. | Applicable `WorkNotFound`, `ScopeNotFound`, `AlreadyTerminal`, or `ClaimLost`; no handoff. | Passed |
| B1 | Lease expires during a native lock wait or callback. | Final `LeaseLost`; callback writes are removed. | Passed |
| B2 | Opted-in deadline is reached before or during callback/lock waits. | `ExecutionDeadlineReached`; callback count matches whether entry occurred; no handoff. | Passed |
| B3 | Caller token is canceled during callback or after callback return but before the final check. | `OperationCanceledException` after cleanup; no handoff. | Passed |
| B4 | Callback stages writes then throws while the caller token remains active. | Original callback exception after successful cleanup; callback writes do not survive; native locks remain held until caller ends the outer transaction, demonstrated by a competing transition blocked until then. | Passed |
| B5 | Parameterize SQL, permission, deadlock, lock/statement timeout, and serialization errors. | Original database exception after feasible cleanup; prior caller writes follow documented savepoint semantics. | Passed |
| B6 | Parameterize transport and rollback/release/scope-restore cleanup failure. | Throws; outer rollback/disposal required; retain both causes when cleanup follows another failure. | Passed |
| B7 | Escaped work attempts use after invocation exit. | Reject before callback or before final success; no handoff. | Passed |
| B8 | Use sequential calls and retry-prone failures. | Callback runs at most once per call, with no framework retry; application conditional write governs duplicate logical handoffs. | Passed |
| P1 | Actual executor uses required restrictive native/application grants and both RLS policies. | Authorized same-scope handoff commits. | Passed |
| P2 | Remove a required native or application grant, parameterized. | Permission failure; no handoff and no role escalation. | Passed |
| P3 | Callback attempts cross-scope application/native access. | RLS rejects access and prior effective scope is restored. | Passed |
| K1 | Run the actual executor from a fresh packed package-only consumer. | Exact transaction handoff works with no framework project reference or private-type access. | Passed |
| K2 | Exercise stale/unbound refusal and valid provider flow. | Refused call cannot send; valid flow releases transaction and connection before fake-provider invocation. | Passed |

## Mandatory supplemental executions

These are additional acceptance cases and do not change the 44-ID floor. Each has **Passed** against the same final inputs.

| Family | Required variants and decisive check | Status |
| --- | --- | --- |
| Isolation | Exercise every unsupported level the installed Npgsql version can create, including `ReadUncommitted`, `RepeatableRead`, `Serializable`, `Snapshot`, `Chaos`, and `Unspecified` where constructible. The fence rejects before callback and never changes the caller's level. | Passed |
| Invocation/context | Simultaneous borrowed references across async contexts; expired/retained binding; recursive and concurrent same-instance calls; sequential calls; pump binding occurs only after permit commit/admission and is deactivated in `finally`. | Passed |
| Savepoints | Unique operation/callback names; coexist with caller savepoints; SQL-aborted transaction recovery; callback false/throw/cancel; operation rollback removes callback writes even after callback-savepoint release; fresh cleanup token. Verify against repository-pinned Npgsql 10.0.3 that `NpgsqlTransaction.SaveAsync` completes synchronously without a round trip and queues each `SAVEPOINT` with the next command on that exact transaction; do not count `SaveAsync` completion alone as server acknowledgement. Prove lock retention for a callback exception with an active caller token separately from cancellation-driven operation rollback, which may release operation-acquired locks. | Passed |
| Scope/pooling | Restore a prior nonempty transaction-local scope and the no-scope state on success, denial, refusal, and error; prove pooled connection reuse does not leak scope. | Passed |
| Time/renewal | Renewal changes revision/expiry while preserving current identity; read fresh locked facts, not cached revision/expiry. Test expiry during lock wait/callback and expiry after final validation with delayed caller commit. | Passed |
| Final check | Controlled cancellation immediately before and after the final check; the former rolls back and throws, while a later signal cannot retroactively change the result and must follow caller pre-commit/ambiguous-commit handling. Invocation deactivation before the check refuses. | Passed |
| Native compatibility | Both legacy and opted-in Work; retain admission, renewal, completion, retry, reconciliation, and stable ActivityId/ProviderKey regressions. Opted-in Work requires existing admission facts; do not call mutating admission again. | Passed |
| Query shape | Representative long permit history proves constant-per-call exact permit lookup, with existing keyed access paths and no history scan or new schema/index. | Passed |
| Callback misuse | Callback attempts commit, rollback, dispose, replace resources, use another connection, provider I/O, or escaped work. Verify detection and documented limits; never claim an improper commit can be undone. | Passed |
| Host bounds | Record effective finite nonzero `lock_timeout`, `statement_timeout`, and `idle_in_transaction_session_timeout`; require lock timeout below statement timeout. Prove lock/statement timeout propagation and that terminating a returned-success idle transaction rolls back the handoff and releases epoch, native, and application locks. Verify the dead connection is not reused as healthy. | Passed |
| Commit/provider ambiguity | Committed-but-ack-lost, established rollback, and unresolved commit; use a stable application-ledger key and fresh scoped read. Absence is inconclusive while the original transaction may still commit. Provider calls stay suppressed until reconciled. Pause after commit, release resources, deliver executor cancellation, then resume; verify suppression and retained ledger without inferring `ProvenNoEffect`. | Passed |

The callback is database-only and must be short. Host timeouts bound waits/statements and idle abandonment, not arbitrary callback CPU or a cooperative transaction that remains actively extended. The API does not guarantee a universal maximum lock duration.

## Execution and evidence recipe

Run from the repository root with the .NET 10 SDK, Docker installed and running, and the checkout available. The packed verifier creates a fresh temporary package feed/cache, packs the coordinated packages, and runs the public-package PostgreSQL consumer against the repository-pinned PostgreSQL `16.5` Testcontainers image. NuGet restore requires package-feed access when its isolated cache is empty. The verifier has no positional test-case selector. Ready and cold image paths share the same supported first consumer command; their start states differ only in whether the pinned image is already present. Do not pre-pull on a cold timed run: Testcontainers fetches the image during execution, and image/package downloads and waits count toward the human timing.

Ready/equipped path (the exact pinned image is cached):

```bash
dotnet --version
docker info
docker image inspect 'postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877' >/dev/null
CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.4 ./Durable/verify-packed-consumers.sh
```

Cold-image path (same .NET/Docker/repository prerequisites, pinned image absent):

```bash
dotnet --version
docker info
CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.4 ./Durable/verify-packed-consumers.sh
```

There is no supported handoff-only command-line selector: this packed command includes the public consumer in the complete package rail. The strict PostgreSQL command below is the full provider suite, not a handoff-only selection. Run it after the first restrictive-role proof and consumer source are ready. Record actual image/database identity and the runtime role's effective timeout settings; do not invent verifier flags or expected output.

Run the strict PostgreSQL rail and the packed consumer rail only after the first restricted-role proof and consumer source are ready:

```bash
./Durable/verify-postgresql.sh --ci
CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.4 ./Durable/verify-packed-consumers.sh
```

The packed verifier takes no positional case selector. Keep its package version explicit and capture the generated package identity and bytes from the actual run. Then run the unchanged solution coverage workflow with its defaults, 95% line and 85% branch coverage:

```bash
./scripts/coverage-solution.sh
```

Capture for each run: source revision and dirty-tree status, exact command, start/end and terminal exit, package version plus package SHA-256, database/test-image identity, effective timeout values and owner, required-case executed/skipped counts, and the resulting API/documentation/format/coverage summaries. Required PostgreSQL rows must execute with no skips. Interrupted logs, stale output, source compilation, a green baseline, or a fake-provider assertion alone do not satisfy a row. The approved equipped-machine first-use target remains an **unmeasured 2–5 minute human aspiration**, not a release gate or agent elapsed-time claim. Start at the handoff entry point and stop only when a first-time reader can run the public consumer and correctly explain commit visibility and denial rollback. Include prerequisite checks, restore/build, image/cache state, downloads, and waits. Report cold SDK/Docker/database setup and the full verification suite separately; this plan does not claim a timing or comprehension result.

## Current evidence

The [progress record](../issue-869-progress.md) holds the current evidence snapshot and limitations. Final source: `092730aa3742aad2de94e2c724c11b92e5b98627`, tree `357177ded4e526d1c849286dd994157140c4fcb4`, with a clean worktree during acceptance. The strict `./Durable/verify-postgresql.sh --ci` completed with exit 0, 969 passed, zero skips/failures, including 128 native handoff executions. `CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.4 ./Durable/verify-packed-consumers.sh` completed with exit 0, 311 passed, zero skips/failures, including 12 handoff executions in 15 policy-consumer tests and all eight expected negative fixtures. The PostgreSQL package SHA-256 is `c791a6df5a4b18af6365de55fde7c6bbbda36d7cfb0849d85103b8217bb8381e`.

Ignored evidence includes `artifacts/issue-869/strict-native-frozen.log`, `pregate-native.log`, `packed-issue869.4-final-summary.json`, package hashes, before/after input manifests, and the public QA capture receipt. The [completion audit](issue-869-plan-completion-audit.md) maps all 44 IDs and supplements to named tests; the strict run covers all nine native handoff classes. The unchanged solution coverage command passed with 96.2728% aggregate line / 88.9468% aggregate branch and 99.3671% patch line / 98.6486% patch branch coverage. These runtime and coverage receipts, rather than authored source alone, support the updated status.

Commit-uncertainty scenarios use the documented consumer adapter, and the provider is synthetic; those limits remain part of the evidence. The human first-use timing/comprehension aspiration, package publication and downstream adopter feasibility are unmeasured or separately owned and are not marked passed here. Further production or test changes invalidate the affected receipts.
