# Issue #869 progress

As of **October 14, 2026**. The [approved execution plan](plans/issue-869-transaction-bound-handoff-fence.md) is implemented on `codex/make-it-so-869-handoff-fence`. Strict native PostgreSQL and fresh packed-package verification have passed. The acceptance-matrix source audit and implementation enhancement review are complete. Final native/packed verification, a clean baseline, solution coverage, compliant documentation/browser QA receipts, and a draft pull request remain pending.

## Current snapshot

| Area | State | Evidence boundary |
| --- | --- | --- |
| Public contract and implementation | Scoped API, transaction helper, locked facts, savepoints, invocation binding, and API snapshot are implemented. | The strict suite includes the normal API assertion, all nine handoff classes, and existing pump regressions. |
| Discovery documentation | Handoff reference, package/repository links, harvested guide, compiled canonical consumer, test plan, and release note are authored. | Harvested guide/snippet/link check passed 1/1; final docs audit and browser QA remain pending. |
| Current main baseline | Physical-path retry passed 840 tests with one historical compatibility skip. | Source `2f805e60d7aba9249fe7ffe848f826629dc67869`. Strict candidate verification supplies the historical package pin and runs without skips. |
| Fresh packed packages | **311 passed, 0 failed, 0 skipped**, exit 0. | `CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.3 ./Durable/verify-packed-consumers.sh`; includes 12 handoff cases within 15 PostgreSQL consumer cases, plus eight expected negative compilation fixtures. |
| Strict PostgreSQL acceptance | **966 passed, 0 failed, 0 skipped**, exit 0. | `./Durable/verify-postgresql.sh --ci`; partitions 1/1 historical, 86/86 execution fixtures, 879/879 remaining. Includes 125 native handoff cases. |
| Host idle abandonment | Passed in both focused and strict runs. | Proves ledger rollback, epoch/native/application lock release, and replacement of a terminated pooled connection. Rotation's try-lock refusal is observed through the schema-manager seam and shared advisory-lock ownership; PostgreSQL row blockers are observed directly. |
| Formatting | Production, native-test, and packed-consumer formatting completed. | Final diff/style verification remains required before push. |
| Solution coverage | Final run will start after the clean baseline commit. | `BUILD_CONFIGURATION=Release COVERAGE_PARALLELISM=2 ./scripts/coverage-solution.sh`; unchanged aggregate and patch gates at 95% line / 85% branch, patch base `origin/main`. |
| Publication and adopter feasibility | Separate milestones. | No publication, downstream package upgrade, writer audit, or adoption claim is included in this framework proof. |

The restrictive-role public-only consumer proves both application race orders, exact transaction identity, callback-write rollback, preservation of an unrelated earlier caller marker, and provider resource release. The [44-ID test plan and supplements](plans/issue-869-handoff-test-plan.md) are mapped explicitly in the [completion audit](plans/issue-869-plan-completion-audit.md); final terminal results remain separate from static source mapping.

Raw generated receipts remain in ignored `artifacts/issue-869/` and are not release documents. Relevant files are `strictfullfinal.log` (terminal `STRICT_EXIT_CODE=0`), `packed-final-summary.json`, `packed-final.log`, `idle-abandonment-final.log`, and `docs-focused.log`. These runs used the dirty implementation tree on the baseline above; production or consumer-test changes require fresh verification.

The PostgreSQL test image is `postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877`. The historical compatibility harness uses package `0.2.0-preview.8`, SHA-256 `62a48f6b7ec299ad608f3714a49a39f18c5cb1fe45293d53915e5549422d311e`.

The fresh `ForgeTrust.AppSurface.Durable.PostgreSql` package version is `0.2.0-issue869.3`, SHA-256 `fc3b307c484fb076f3bc388d1c0f5b1e3e9f8e9acb534efb8841edf98c207d49`. Coordinated package hashes were captured before the isolated feed/cache was deleted. The packed run's dirty-source fingerprint is `ee1273ea1c4e300106a65a67023a5f269e487a401c19060eb30d88f5c4ff524b`; later documentation-only changes are outside that receipt's exact tree.

Coverage attempt 1 measured **96.27% line / 88.95% branch**, but exited 1 (`ASCOV120`) on an older source/test build, so its patch gate never ran. That build expected epoch rotation to appear as a PostgreSQL blocking waiter, although it polls a try-lock; the current observation above passed strict verification. Its documentation test also relied on a `language-csharp` CSS class the harvester does not emit; the corrected test verifies the actual rendered canonical snippet and passed focused verification. Two final boundary cases and removal of an unreachable immutable-connection branch also postdate that build. Logs/raw report are retained under `coverage-attempt1-*`; they do not satisfy the final gate. The rerun uses the same command and thresholds, with no test skip, timeout increase, exclusion, or gate relaxation.

## Remaining verification commands

When the consumer is ready, use the supported commands exactly as written:

```bash
./Durable/verify-postgresql.sh --ci
CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.3 ./Durable/verify-packed-consumers.sh
BUILD_CONFIGURATION=Release COVERAGE_PARALLELISM=2 ./scripts/coverage-solution.sh
```

The packed verifier takes no positional arguments. Preserve the source revision, fresh package version and SHA-256, database/test-image identity, effective host settings and owner, executed/skipped counts, exits, and coverage summary. The thresholds remain the script defaults. Host settings in the idle-abandonment proof are test-owned: 1s lock / 20s statement / 5s idle for the abandoned session, with separate finite budgets for competitors. These are proof fixtures, not deployment defaults; use the [transaction and host contract](../Durable/handoff-fence.md#transaction-and-lock-sequence) to select real workload budgets.

## State rules

`HandoffPrepared=true` remains provisional until caller commit. Provider I/O follows transaction and connection release inside the active invocation and uses the existing provider-safety policy. A failed or ambiguous commit suppresses sending until the application ledger and existing native/provider rules resolve it. No `PLANNED / GAP` test-plan row becomes passed solely because its source file exists or a narrower baseline/pump/API check is green.

## Final candidate verification

The new misuse test proves that callback denial/exception removes the supplied-transaction write while separately committed writes and a local fake-provider call survive. The final cancellation test observes a competing native transition finish before the caller closes its outer transaction; the unsupported-isolation cases preserve isolation and the pre-canceled call never enters the database callback. No new public binder or reflective test access was added.

Enhancement cycle 1 completed without a supported production defect; the independent bounded adversarial pass likewise found none. Neither review substitutes for terminal acceptance or coverage. The package failure section now distinguishes transactional acceptance from the handoff fence's savepoint cleanup. New discovery links use harvested guides or explicit repository URLs for non-harvested source/planning files.

Coverage attempt 2 was interrupted during context recovery before merge/gates; its exact exit cannot be recovered and it is not passing evidence. Partial reports are retained. The final unchanged command will run against the committed complete test set. Documentation browser observations verified guide/package navigation and zero console errors, but their exploration checkpoint receipt was incomplete; a fresh bounded post-commit verification will close that procedural gap.
