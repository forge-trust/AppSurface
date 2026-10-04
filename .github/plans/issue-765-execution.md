# Issue 765 execution progress

Approved scope: [immutable policy, retries and deadlines](../../docs/designs/issue-765-deterministic-retry-plans-autoplan.md). Required proof: [A–X test matrix](../../docs/plans/issue-765-test-plan.md).

- Branch: `codex/make-it-so-765-retry-deadlines`, isolated from the unrelated #845 checkout, based on current `origin/main`.
- COVERAGE_GATE: `./scripts/coverage-solution.sh`, unchanged repository aggregate/patch thresholds 95% line and 85% branch with the existing gate tolerance; final fresh evidence required before shipping.
- Complete: approved plan copied; environment SDK/Docker/GitHub preflight succeeds.
- Active: final contract proof and review; every G1–G6 remains required.
- Remaining: A–X code/branch proof, enhance review, clean baseline commit, package/CLI user-facing QA, fresh full coverage, ship draft PR.
- Evidence: focused implementation suites are recorded below; final coverage and strict package/database gates remain required.

## Implementation checkpoint

- Main store drafts now cover accepted anchor/duplicates, acceptance-relative claims and recovery, permanent closure, exact permits, one-use invocation, deadline-capped renewal, circuit-only admitted success, late/stale metadata quarantine and atomic projections. No lifecycle facet is declared complete until branch proof passes.
- Shared Core timing evaluator is integrated into PostgreSQL; legacy discovery uses its direct clock and opted-in hints use the migration-owned clock.
- Added database regression cases for offsets, expiry, duplicate identity, all safety classes, one-use concurrency, late/stale evidence, backward clock, lock-wait timing and projection rollback, plus closed metric labels and listener failure.
- First integration build: `dotnet build Durable/ForgeTrust.AppSurface.Durable.PostgreSql/ForgeTrust.AppSurface.Durable.PostgreSql.csproj -c Release --no-incremental`, exit 0, zero warnings/errors. Log `/private/tmp/issue765-integration-build.log`.
- Remaining: complete all worker lanes, run/repair targeted tests and A–X missing branches, strict PostgreSQL/process/packed and historical ABI proof, docs/API/format checks, enhance, clean commit/package QA, fresh solution coverage, draft ship.

- Operator safety review: an initial separate mutator was rejected by automatic approval review as incomplete. Rejection and exact invariants are in `/private/tmp/issue765-operator-rejection.md` and `/private/tmp/issue765-operator-invariants.md`. A materially safer revision retains the original count-checked CTE, adds fixed due/sample/closure fields, and reuses atomic Flow/Schedule projectors; approval review accepted it. Pure decision tests passed 2/2 with zero skips, including a 240-case proof matrix plus 40 retry/recovery combinations. Integrated operator build passes zero warnings/errors. Database proof is still pending.
- Core initial focused run: 26/27 passed, one request codec-count assertion failed; Core owner is correcting it. First PostgreSQL test attempts were compile-only failures (new test fixture fixes applied; controlled clock helper now available; in-flight pump helper completion remains). These are not passing validation evidence.

- Database feedback: production store/operator tests ran against actual digest-pinned PostgreSQL with the isolated fixture. First 63 lifecycle/operator cases passed with zero skips. An expanded run passed 64/65; its only failure was an unsupported-version fixture that now correctly throws in the Core constructor, and it was corrected to retain the actual database timestamp-overflow/committable-transaction proof. Two selected discovery regressions were added. Final repository and strict gate evidence is still required.

- Repository focused DB run: `dotnet test Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj -c Release --filter 'FullyQualifiedName~ExecutionPolicy|FullyQualifiedName~ExecutionOperator|FullyQualifiedName~ExecutionMetric|FullyQualifiedName~ExecutionOperatorDecision'` passed 76/76, zero skips, `/private/tmp/issue765-db-matrix.log`. Includes delayed acceptance commit/domain rollback, bounded selected expiry discovery, ordinary lost-lease priority, stale dispatch/scope refusal, prior-effect uncertainty and actual metric-listener failure.
- Permanent deadline witness regression passed 1/1: a locally successful result remains quarantined after the authoritative clock moves backward.
- Full PostgreSQL (excluding separate historical-binary proof) and solution Debug build first attempts hit an in-progress process fixture enum/string compile error. Process owner notified; no broad passing-build claim.

- Core full suite passed 331/331, zero skips, normal additive API baseline validation; source frozen by owner. `/private/tmp/issue765-core-all.log`.
- Operator suite passed 20/20, zero skips after adding delayed read-only proof across the deadline and complete CTE/history rollback. `/private/tmp/issue765-db-operator-races3.log`.
- Derived Flow policy/projection suite passed 6/6, zero skips: inherited immutable policy plus cutoff/unknown-effect Flow suspension and deliberately missing parent-dispatch rollback. `/private/tmp/issue765-derived-projections2.log`.
- First broad PostgreSQL run (excluding separately gated historical binary) passed 645, failed 53, skipped 0, total698. Failures are assigned: stale schema11 expectations/role grants/clock fixture conversion (schema lane), snake-case state parsing and checkpoint admission-marker counts (process lane). `/private/tmp/issue765-postgresql-all2.log`.
- Provider initial suite83/85 and Testing84/85: owned API baselines and one provider fixture codec mismatch pending correction; no completion claim for these lanes.
- Markdown compiled-source chooser verification passed: `dotnet run --project tools/ForgeTrust.AppSurface.MarkdownSnippets/ForgeTrust.AppSurface.MarkdownSnippets.csproj -c Release -- verify --document Durable/execution-policies-v1.md`.
- Active remaining work: schema/process fixture repairs, nine-case derived matrix including Schedule, pump timing/exit branch proof, authorized inspection projection, packed five-slot/refusal/reconciliation counts and independently precompiled ABI consumer; then stable enhance review, baseline commit, functional package QA, unchanged fresh solution coverage hard gate, draft ship.

## Deadline observation repair and initial coverage

- New four-safety regression reproduced a deadline-completion bug: first circuit closure masked later observed deadline expiry after the fixture clock moved backward (4 failed, 0 passed, 0 skipped). The fix retains the first admission closure time/reason and adds independent internal `execution_deadline_reached_at` persistence. Claims/releases, renewal and completion honor the observed deadline permanently. The focused deadline/lock-wait suite passed 9/9, zero skips. Logs: `/private/tmp/issue765-deadline-witness-red3.log` and `/private/tmp/issue765-deadline-witness-green.log`.
- Provider and Testing normal suites each passed 85/85, zero skips after rebuilding API baseline inputs and fixing the typed codec fixture.
- Initial exact `./scripts/coverage-solution.sh` is executing all 55 test projects. PostgreSQL reported 687 passed / 24 failed / 1 skipped; assigned failures include schema-12 assertions/role grants, activation script sanitizer seams and a Schedule fixture. Separate strict historical proof must run with zero skips. Two Docs visual snapshots differ because release contents moved from preview.11 to preview.12; actual images were inspected and a deliberate refresh is running. No final coverage pass is claimed.
- Pending before review freezes: focused pump contract tests, derived/scale proof, schema/role/historical fixes, script repair, strict packed consumer and independent compiled ABI proof. Enhance review, clean baseline, functional QA, fresh exact coverage and draft PR still remain.

## Recovery and final proof checkpoint — 2026-10-04

- Integrated current main `7ae38084` (preview.13 release/provenance only) before final branch verification.
- Initial exact coverage attempt finished with ASCOV120 due failing tests: 94.78% lines / 88.88% branches, 55 reports. It is not a passing gate; final fresh unchanged command remains mandatory. Schema CLI/floor/roles, activation script seams, manifest classifications, and visual release fixtures were assigned/repaired.
- Epoch recovery red test reproduced pending rows surviving epoch rotation (two cases); opted-in claim now persists the canonical manual suspension and atomic projections, preserving policy/offsets/deadline/attempt/effect truth. Focused Release epoch+completion+invocation replay proof passes 7/7, zero skips (`/private/tmp/issue765-epoch-completion-green2.log`). A bounded native read-only review of this patch found no actionable finding; it does not substitute for the full branch review.
- Schema/role/clock focused current Debug no-build proof passes26/26, zero skips (`/private/tmp/issue765-schema-current.log`). Pump/inspection latest Release proof9/9 and Testing87/87, zero skips. Fresh-process proof48/48, zero skips (`/private/tmp/issue765-process-debug-nobuild.log`). CLI schema focused5/5 and Core value tests14/14, zero skips.
- Strict PostgreSQL CI attempt reached the exact historical proof then failed because the test assumed a role recipe archive path absent from preview.8. Historical owner must repair provenance/path while retaining pinned SHA/schema11 operation/schema12 refusal/two-host compatible rollback; no cohort is claimed green.
- Packed rerun proved current fresh-package binding to precompiled historical Core/Provider MVIDs, Testing4/4, source and package activation146/146 each, zero skips; then failed new policy consumer restore with NU1008 inherited central package management. Packed owner repairs the isolated template and reruns strict proof. No complete packed pass is claimed.
- Remaining: derived/100k scale proof; historical/strict and packed gate; focused coverage gaps, full enhance review and current required probes; formatting/warnings; clean baseline; functional package/CLI QA including frozen preflight artifact; fresh unchanged full coverage; draft PR only after all required gates.

## Enhance cycle 1 — current review repair

- Normal parent Release build is terminal; QA capture002 completed exit0,6 passed,0 failed,0 skipped after compiling PostgreSQL tests in Debug. Its guessed permit-replay filter selected no permit case, so full policy-class proof remains required.
- Formatting completed exit0 with no diagnostics before the current review fixes. Both strict verifier scripts retain activation sanitizer/runner seams; bash syntax and whitespace checks pass.
- Native contract review found two supported gaps: off-plan snapshot eligibility accepted, and bounded checkpoint history could evict a signaled observation before a waiter reads it. Gauss owns their narrow Core/Testing fixes and regressions; fresh focused validation remains required.
- Native adversarial review confirmed opt-in claim/renewal/deadline-only retry additions overflow with valid large durations before a deadline cap. Main added shared capped addition that compares remaining duration before adding, preserving exclusive immutable bounds. Boyle owns real-DB oversized lease/lifetime/retry regressions. No legacy arithmetic changes.
- Strict PostgreSQL CI is held until final review fixes/test class are ready. Previous historical proof failure was repaired by pinning the schema11 role recipe to immutable source bytes; current strict rerun remains pending.
- Packed final run is still active in isolated artifacts, previously passed historical ABI/Testing/basic consumers; source activation stage has not reached a terminal result. A fresh final run must include the current review fixes.
- Remaining hard gates: complete strict database/package proof, required documentation export/package-index/API/snippet checks, enhance re-review, clean baseline commit, package/CLI functional QA, fresh unchanged solution coverage and draft ship. No final gate pass or PR is claimed.

## Resumed validation coordination — 2026-10-04

- Confirmed feature work remains isolated; source checkout stays on unrelated #845. Normal Release build and previous strict run are terminal. Strict historical proof remains failed at the immutable preview.8 role recipe RLS check; schema fixture owner is diagnosing and repairing it before the next unchanged strict run.
- Review cycle 1 fixes: off-plan Core snapshot eligibility, retained checkpoint observation after bounded history eviction, and overflow-safe deadline-capped lease/lifetime/retry/renewal additions. Latest captured focused policy suite77/77 and overflow4/4, zero skips. Fresh final Core/Testing and full strict proof remain required.
- Derived/scale lane reports10/10 passing, zero skips:9 Flow/Schedule cases and real100k Work/dispatch rows over100 scopes, unprivileged dispatcher/RLS, selected/excluded contracts, actual indexed query plan, bounded16-row drain in3 pages and empty polls. Log receipt from lane retained in `/private/tmp/issue765-agent-01a104e3-6bed-7c90-ae88-8e0e77d9897e.md`.
- Fresh packed gate uses isolated artifacts; activation stage has not reached a terminal result. Package index generated outputs were stale and are being regenerated/verified; docs health/export and snippet verification are assigned.
- Previous coverage report is stale after code/test changes and a failed run, never final evidence. No PR/push or completed goal is claimed.

## Second recovery coordination — 2026-10-04

- Parent confirmed no active AppSurface normal Release build; reusable MSBuild nodes are idle and unrelated project runs are excluded. Bohr owns the unchanged strict PostgreSQL runner after historical source freezes.
- Historical schema-11 role recipe now applies. Latest final2 strict attempt still fails old preview.8 host with Discovered1/Claimed0/Deferred1; Boyle owns proving/fixing the possible two-host shared-candidate contention while retaining exact artifact, per-host processing/drain, schema12 refusal and compatible rollback. Prior schema owner remains frozen.
- Packed activation diagnostic isolates `Overlapping_payload_free_wakes_leave_receipt_bound_Work_independently_inspectable`; Sagan owns bounded diagnosis and a fresh complete packed gate. Main must integrate any production finding before source/package capture.
- Gauss owns the fresh CDN export/linkcheck after source-link repairs, using isolated CLI artifacts. Last complete CLI Release build has zero warnings/errors. Core334/334, Testing89/89, overflow4/4 and prior derived/scale10/10 pass with zero selected skips; these receipts are not final solution coverage.
- Enhance cycle2 core checklist reads and independent native adversarial review are in progress. Parent receipt `65c5ecb8-0825-4f8a-b860-f603a468068e` predates concurrent harness/docs fixes, so final completed review requires fresh frozen-candidate receipts. No review-completion record is claimed.
- Initial aggregate coverage is stale94.78%lines/88.88%branches from a failed run; current unchanged full command is still mandatory. Final implementation baseline commit, functional QA, fresh coverage and draft ship remain required. No push or PR exists.

## Third recovery and operator deadline proof — 2026-10-04

- Parent checked actual process state: normal #765 Release build is terminal; build-clear sent to strict verifier owner Bohr. PostgreSQL strict rerun remains coordinated with the historical fixture owner, not skipped.
- Supported enhance-cycle2 review finding: operator CTE retained only first admission closure, losing independent absolute deadline observation after an earlier circuit closure. Real-DB three-proof regression reproduced null witness (3 failed, 0 passed, 0 skipped), `/private/tmp/issue765-operator-witness-red.log`.
- Repair adds the independent first deadline witness to the same count-checked atomic Work/dispatch/permit/history/operator-command CTE, without changing first closure or effect truth. Green full operator class passes 23/23, zero skips, exit0; `/private/tmp/issue765-operator-witness-green.log`. Includes Applied/NotApplied/Unknown, earlier circuit closure, exact command replay after backward time, expired recovery and deliberate history-projection rollback of the witness.
- Activation diagnostic is terminal timeout124 inside `WebApplication.CreateBuilder()` before Durable services register. Sagan is testing content-root/file-watcher initialization with isolated artifacts; temporary trace instrumentation must be removed before source freeze.
- Historical lane owns the LIMIT-1 same-contract two-host discovery collision; each old host must still process/drain its own contract before schema12 refusal and compatible rollback. Bohr will run unchanged strict command after focused historical proof/source freeze.
- Docs CDN export terminal output shows992files and completed export; Gauss owns exit/linkcheck receipt. Existing coverage remains stale and initial untracked production paths were absent from its patch measurement: a clean intentional implementation commit and fresh unchanged `./scripts/coverage-solution.sh` are mandatory before ship.
- Active review sources: parent ordered critical/informational checklist, Hilbert fresh native adversarial pass (initial token f530ef98-4f7f-4c63-88b1-2ae69f8e439d), fixture/package/docs gates. Final review receipt must be captured after all fixes/freeze, never bound to stale code. No PR/push or final gate pass is claimed.

## Recovered current verification — 2026-10-04

- Normal Release build clearance confirmed from the actual local process inventory; Bohr retains strict verifier ownership pending Boyle's isolated historical focused proof. No unrelated project process was stopped.
- Fresh read-only native production pass found no further supported defects after the operator deadline repair. Receipt: `/private/tmp/issue765-enhance-cycle2-adversarial-current.md`. Final candidate binding remains pending source freeze, required probes, and exact full coverage.
- Operator functional QA capture003 independently reran the already-built current full real-DB class:23 passed,0 failed,0 skipped. Evidence `/private/tmp/issue765-review-qa/.qa-evidence/003`; checkpoint `/private/tmp/issue765-review-qa/exploration-003.json`.
- Docs final proof is green: isolated CLI Release build zero warnings/errors; health Healthy200; strict CDN export/link validation exit0,992files,no RWEXPORT errors. Receipt `/private/tmp/issue765-docs-final-status.md`. Two unchanged nonblocking docs-health warnings remain.
- Bounded activation comparison: default watcher timed out before builder creation even with empty shell cwd; polling watcher test passed1/1,zero skips and all assertions. Sagan owns an explicit fixture content-root confirmation, removal of temporary traces, and final fresh packed gate.
- Exact upstream main remains7ae380845ede06029d388232f49470ae34bef6e1. Feature changes are intentional; five release-docs image updates restore current preview13 baselines. No implementation commit/push/draft PR exists yet.
- Parent is auditing actual schema12 proof in the frozen preflight consumer: historical scenario/stage names remain a separate receipt compatibility contract; actual applied/current schema/floor must be proved without relabeling old evidence. Gauss owns read-only inventory before any additional source changes.

## Final strict database checkpoint — 2026-10-04

- Normal AppSurface Release build is terminal; unrelated Skoolit runs use separate checkouts.
- Strict `bash Durable/verify-postgresql.sh --ci` passes exit0: historical1/1, execution fixtures73/73 (48 process,23 operator), remainder688/688; total762 passed,0 failed,0 skipped. Log `/private/tmp/issue765-postgresql-ci-review-final4.log`.
- Corrected the deadline-only elapsed-cutoff test to supply an actual deadline beyond the earlier five-minute elapsed horizon. Focused real-DB proof1/1 passes with no deadline witness; no production change.
- Fixed current provider README role-recipe prerequisite: all migrations through0012 are required before invoking new function grants.
- Packed activation fixture remains under bounded diagnosis; full packed verifier has no passing terminal receipt. Frozen-artifact preflight requires explicit schema12/floor12 proof and a real committed-source bundle.
- Baseline commit makes all intentional new sources visible to patch coverage. Fresh unchanged `./scripts/coverage-solution.sh`, final review, package/CLI QA, artifact preflight, and draft ship remain required; no push/PR or coverage pass claimed.

## Operator permit recovery repair — 2026-10-04

- Enhance cycle 3 found a real orphaned-proof path: safe replay left a prior admitted effect unresolved while deadline closure proved only the current unadmitted permit absent. Operator authorization could not select the earlier permit.
- The locked execution row now carries the latest unresolved admitted permit identity and unresolved count separately from the current Work identity. The count-checked operator CTE updates only that exact admitted permit; proof and epoch-move history identify their distinct actions. NotApplied preserves suspension until every admitted effect is resolved; Applied retains existing logical Work result semantics. Reconciliation rechecks the selected permit and Work revision after asynchronous proof.
- Regression evidence: four pre-fix public-operator cases failed as expected; full current operator/decision suite passes 32/32, zero failures/skips, exit 0 (`/private/tmp/issue765-prior-permit-all-green.log`). Cases include Idempotent/ProviderKeyed, Applied/NotApplied, multiple prior effects, stale revision, duplicate command and atomic history-failure rollback.
- Formatting on the four changed C# files exits 0 (`/private/tmp/issue765-prior-proof-format.log`); current diff check passes. Schema/recipe docs now distinguish current schema 12 from historical schema-11 receipts and recipes.
- Prior strict PostgreSQL 762-test receipt, package bundle at af5c53dd, docs export and interrupted/stale coverage are not final evidence for this repair. Fresh strict PostgreSQL, full packed consumers, exact frozen artifacts/preflight, final review/QA and unchanged coverage gate remain mandatory before draft publication. Coverage gate is exactly `./scripts/coverage-solution.sh`: aggregate and origin/main patch lines >=95%, branches >=85%, with codecov patch-line mode.

## G6 gate-repair checkpoint — 2026-10-04

- Strict PostgreSQL on the prior-permit repair passed 769/769, zero skips, exit 0. Full fresh packed consumers passed after the fixture's required containing partial declaration: ABI, Testing 4, activation 146 source +146 packed, policy 3, and eight exact negative fixtures. Verifier bytes and required checks were unchanged.
- Exact 49-package source-stamped bundle/preflight on `980838f6` and current package-index/snippet/docs health/strict 992-file export passed. Polling file watchers resolved validation-host startup without product changes; the two existing docs diagnostics remain nonblocking.
- Unchanged coverage gate final3 exited 1: aggregate 95.16% lines /89.15% branches, patch 92.06% lines /89.31% branches. Its measured patch-line shortfall and two expected current-release outline captures are concrete remaining work, not waived gates.
- The original default-four-cycle enhance invocation is closed with incomplete native coverage and a separate read-only outside strategy checkpoint. A new scoped gate-repair invocation adds meaningful public/internal boundary tests for claim, invocation, renewal, completion, policy validation and checkpoint behavior. The pump's focused suite passes 11/11 after two deterministic deadline regressions; other scoped tests and deliberate release captures are in progress.
- Final candidate formatting, exact coverage rerun, complete review, fresh QA, source-stamped package/preflight evidence and draft ship remain pending. No remote push, PR, merge or release was performed. `COVERAGE_GATE` remains exactly `./scripts/coverage-solution.sh` with its unchanged aggregate/patch line95/branch85 settings and origin/main codecov patch mode.

## G6 consolidated validation — 2026-10-04

- New gate-repair tests cover real PostgreSQL claim revision/dispatch/scope/epoch fences, expired admitted attempts across all safety classes, current versus prior effect cancellation, exact-cutoff no-effect completion, wrong lease owners, permit replay after renewal, ordinary lease expiry, permanent circuit closure and deadline-bounded renewal. No product behavior changed in this pass.
- Strict unchanged `bash Durable/verify-postgresql.sh --ci` is terminal exit0: historical1/1, execution80/80, remainder711/711; total792 passed,0 failed,0 skipped (`/private/tmp/issue765-postgresql-ci-core-tests.log`). Core focused33/33 and Testing focused16/16 pass on rebuilt isolated artifacts.
- Exactly five release-outline PNGs were deliberately refreshed; no-update visual verification passes2/2,0 skipped. Packed gate previously passed with the owned required `partial` declaration. Full source-formatting include pass and identical verify-no-changes each exit0,62 C# paths including new classes (`/private/tmp/issue765-format-final-current*.log`); scripts syntax and whitespace checks pass.
- These intentional changes are committed before the next exact unchanged `./scripts/coverage-solution.sh`, so all new source is visible to the origin/main patch measurement. Last full gate remains failed; no final coverage/ship readiness claim. Full native review, final functional QA and ship documentation audit are still required.
