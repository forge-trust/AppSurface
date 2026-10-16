# Issue #869 progress

As of **October 15, 2026**. The [approved execution plan](plans/issue-869-transaction-bound-handoff-fence.md) is implemented on `codex/make-it-so-869-handoff-fence` in [PR #877](https://github.com/forge-trust/AppSurface/pull/877). Verification below is tied to specific revisions; subsequent local changes require fresh affected checks.

## Latest published verification

Published source [`861ad6132fa0c694f6b50a8c811d37cde29e268a`](https://github.com/forge-trust/AppSurface/commit/861ad6132fa0c694f6b50a8c811d37cde29e268a) passed [the full CI build and coverage gate](https://github.com/forge-trust/AppSurface/actions/runs/38674845508/job/116225993972): **55 projects, 16,927 passed, 0 failed, 2 existing conditional skips**. Aggregate coverage was **96.2301% line / 88.8863% branch**; patch coverage was **99.6835% line / 99.3243% branch** against the merge checkout's `HEAD^1`. Thresholds remain 95% line / 85% branch for aggregate and patch, with the existing 0.5-point tolerance and Codecov patch mode.

The [strict PostgreSQL lane](https://github.com/forge-trust/AppSurface/actions/runs/38674845508/job/116225993738) passed **971 tests, 0 failed, 0 skipped**, including 130 native handoff cases and the exact previous-package proof skipped by ordinary coverage. The second ordinary skip is the manual candidate-publication rehearsal. [Strict documentation export](https://github.com/forge-trust/AppSurface/actions/runs/38674845508/job/116234612895), package gates, formatting, security analyses, and the Codecov patch check passed. The PR remains a draft requiring maintainer review; publication and adopter feasibility remain separate milestones.

## Historical implementation snapshot (October 14)

The following table and local receipts describe source `092730aa3742aad2de94e2c724c11b92e5b98627`, before later review fixes and the published CI run above. Its counts and package hashes are historical evidence, not current-candidate verification. At that stage, release review, the final documentation audit, and the draft PR were still pending.

| Area | State | Evidence boundary |
| --- | --- | --- |
| Public contract and implementation | Scoped API, transaction helper, locked facts, savepoints, invocation binding, and API snapshot are implemented. | The strict suite includes the normal API assertion, all nine handoff classes, and existing pump regressions. |
| Discovery documentation | Handoff reference, package/repository links, harvested guide, compiled canonical consumer, test plan, and release note are authored. | Harvested guide/snippet/link check passed 1/1; live guide/package round trip passed with zero console warnings/errors. First-time human comprehension/timing remains unmeasured. |
| Current main baseline | Physical-path retry passed 840 tests with one historical compatibility skip. | Source `2f805e60d7aba9249fe7ffe848f826629dc67869`. Strict candidate verification supplies the historical package pin and runs without skips. |
| Fresh packed packages | **311 passed, 0 failed, 0 skipped**, exit 0. | `CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.4 ./Durable/verify-packed-consumers.sh`; includes 12 handoff cases within 15 PostgreSQL consumer cases, plus eight expected negative compilation fixtures. |
| Strict PostgreSQL acceptance | **969 passed, 0 failed, 0 skipped**, exit 0. | `./Durable/verify-postgresql.sh --ci`; partitions 1/1 historical, 86/86 execution fixtures, 882/882 remaining. Includes 128 native handoff cases. |
| Host idle abandonment | Passed in both focused and strict runs. | Proves ledger rollback, epoch/native/application lock release, and replacement of a terminated pooled connection. Rotation's try-lock refusal is observed through the schema-manager seam and shared advisory-lock ownership; PostgreSQL row blockers are observed directly. |
| Formatting | Production, native-test, and packed-consumer formatting completed and verified. | No formatting changes remained; final diff check is required before push. |
| Solution coverage | **PASS**, exit 0; 55 projects, **16,920 passed, 0 failed, 4 existing conditional skips**. Release build: 0 warnings/errors. | `BUILD_CONFIGURATION=Release COVERAGE_PARALLELISM=2 ./scripts/coverage-solution.sh`; aggregate **96.2728% line / 88.9468% branch**, patch **99.3671% line / 98.6486% branch**. Unchanged thresholds: 95% / 85%, including the gate's built-in 0.5-point tolerance; patch base `origin/main`. |
| Publication and adopter feasibility | Separate milestones. | No publication, downstream package upgrade, writer audit, or adoption claim is included in this framework proof. |

The restrictive-role public-only consumer proves both application race orders, exact transaction identity, callback-write rollback, preservation of an unrelated earlier caller marker, and provider resource release. The [44-ID test plan and supplements](plans/issue-869-handoff-test-plan.md) are mapped explicitly in the [completion audit](plans/issue-869-plan-completion-audit.md); final terminal results remain separate from static source mapping.

Raw generated receipts remain in ignored `artifacts/issue-869/` and are not release documents. Final baseline receipts are `strict-native-frozen.log`, `pregate-native.log`, `packed-issue869.4-final-summary.json`, `packed-issue869.4-qa-receipt.json`, and `coverage-attempt3-receipt.json`. They identify the committed source, exact commands, terminal exits and input hashes. Earlier dirty-tree runs remain historical evidence only; production or consumer-test changes require fresh verification.

The PostgreSQL test image is `postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877`. The historical compatibility harness uses package `0.2.0-preview.8`, SHA-256 `62a48f6b7ec299ad608f3714a49a39f18c5cb1fe45293d53915e5549422d311e`.

The fresh `ForgeTrust.AppSurface.Durable.PostgreSql` package version is `0.2.0-issue869.4`, SHA-256 `c791a6df5a4b18af6365de55fde7c6bbbda36d7cfb0849d85103b8217bb8381e`. All eight coordinated package hashes were captured before the isolated feed/cache was deleted. Source, dependency, configuration and verifier hashes match before/after manifests at the committed baseline; the five transitive Flow generator inputs are retained separately. Later authored progress/test-plan updates do not change those package inputs.

Coverage attempt 1 measured **96.27% line / 88.95% branch**, but exited 1 (`ASCOV120`) on an older source/test build, so its patch gate never ran. That build expected epoch rotation to appear as a PostgreSQL blocking waiter, although it polls a try-lock; the current observation above passed strict verification. Its documentation test also relied on a `language-csharp` CSS class the harvester does not emit; the corrected test verifies the actual rendered canonical snippet and passed focused verification. Two final boundary cases and removal of an unreachable immutable-connection branch also postdate that build. Logs/raw report are retained under `coverage-attempt1-*`; they do not satisfy the final gate. The rerun uses the same command and thresholds, with no test skip, timeout increase, exclusion, or gate relaxation.

## Reproduction commands

When the consumer is ready, use the supported commands exactly as written:

```bash
./Durable/verify-postgresql.sh --ci
CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.4 ./Durable/verify-packed-consumers.sh
BUILD_CONFIGURATION=Release COVERAGE_PARALLELISM=2 ./scripts/coverage-solution.sh
```

The packed verifier takes no positional arguments. Preserve the source revision, fresh package version and SHA-256, database/test-image identity, effective host settings and owner, executed/skipped counts, exits, and coverage summary. The thresholds remain the script defaults. Host settings in the idle-abandonment proof are test-owned: 1s lock / 20s statement / 5s idle for the abandoned session, with separate finite budgets for competitors. These are proof fixtures, not deployment defaults; use the [transaction and host contract](../Durable/handoff-fence.md#transaction-and-lock-sequence) to select real workload budgets.

## State rules

`HandoffPrepared=true` remains provisional until caller commit. Provider I/O follows transaction and connection release inside the active invocation and uses the existing provider-safety policy. A failed or ambiguous commit suppresses sending until the application ledger and existing native/provider rules resolve it. No `PLANNED / GAP` test-plan row becomes passed solely because its source file exists or a narrower baseline/pump/API check is green.

## Historical candidate verification (October 14)

The new misuse test proves that callback denial/exception removes the supplied-transaction write while separately committed writes and a local fake-provider call survive. The final cancellation test observes a competing native transition finish before the caller closes its outer transaction; the unsupported-isolation cases preserve isolation and the pre-canceled call never enters the database callback. No new public binder or reflective test access was added.

Enhancement cycle 1 completed without a supported production defect; the independent bounded adversarial pass likewise found none. Neither review substitutes for terminal acceptance or coverage. The package failure section now distinguishes transactional acceptance from the handoff fence's savepoint cleanup. New discovery links use harvested guides or explicit repository URLs for non-harvested source/planning files.

Coverage attempt 2 was interrupted during context recovery before merge/gates; its exact exit cannot be recovered and it is not passing evidence. Attempt 3 completed the exact unchanged command with passing aggregate and patch gates; full logs, project reports and input hashes are retained. Its four conditional skips are the historical-package test (CI unset for this local command), a manual downloaded-candidate rehearsal and two filesystem case-collision cases. The separate strict PostgreSQL verifier supplies the historical package pin and passed with zero skips.

The fresh post-commit browser verification completed three live guide/package probes on the same source, with checkpoints saved before follow-up navigation and zero console warnings/errors. The package-only functional capture completed with exit 0, all 311 tests passed, zero skips, all eight negative fixtures verified, and 21 build sections reporting zero warnings. Commit-uncertainty variants use a consumer-owned adapter; the unresolved case cleans up its fixture and does not reproduce a wire-level PostgreSQL fault. Provider execution is synthetic; publication and real adopter feasibility remain separately owned milestones.
