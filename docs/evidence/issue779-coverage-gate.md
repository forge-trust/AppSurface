# #779 coverage gate audit

The unchanged `COVERAGE_GATE` is `./scripts/coverage-solution.sh`. See the [wrapper](../../scripts/coverage-solution.sh), [local verification prerequisites](../../CONTRIBUTING.md#local-verification) and [coverage gate reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate).

## Command and pass signal

The wrapper runs full solution coverage, then gates its generated `TestResults/coverage-merged/coverage.cobertura.xml` at 95% line and 85% branch coverage. The default local patch comparison is `origin/main`, with 95% patch line and 85% patch branch thresholds and `--patch-line-mode codecov`. The existing gate uses a 0.5 percentage point tolerance; that policy is preserved. The final command must exit zero. A below-threshold result is `ASCOV020`; neither a previous artifact nor focused tests establish a current gate pass.

The [build workflow](../../.github/workflows/build.yml) invokes the same wrapper in Release with locked restore already complete, parallelism 2, and `HEAD^1` as the PR merge-checkout comparison. Local execution preserves the default `origin/main` comparison. Do not change the script, clear the diff base, lower thresholds or disable its execution-environment requirement to produce a pass.

## Prerequisites and limits

The audit observed .NET 10.0.102 on macOS ARM64, a responding Docker engine, Node 24 and pnpm 11. The solution includes 54 matching test-project entries. CI additionally performs locked restore, package-consumer proofs and generated web asset validation before the full lane. CI runs on Ubuntu; a local macOS result cannot establish the separately required EvidenceHost provider/platform isolation proof.

The [packed coverage smoke script](../../scripts/coverage-run-package-smoke.sh) validates packaged coverage commands. It does not prove packaged EvidenceHost commands; #779 requires its own packed CLI/SDK caller fixture.

## Execution evidence

Audit revision: `fd0b124e16a35b85ee16acde799834af407e18de`, initially equal to `origin/main`. The initial audit was read-only. The first unchanged full lane subsequently ran on this revision with the task worktree dirty. All 54 project entries completed, but the command exited **1** with `ASCOV120` after four test projects failed. The wrapper did not run its threshold/patch gate. Raw merged Cobertura measured 95.07% line and 89.00% branch coverage; these figures are not a gate pass. See the [execution attempt record](../plans/issue-779-execution.md#staged-proof-route-approved-2026-10-01) for failed suites and focused repairs. Official patch coverage remains unavailable.

Fresh full-lane results are required after the final code/test changes and immediately before [Make It So shipping](../plans/issue-779-execution.md#required-checks). Store each attempt's revision, worktree state, exit status, uncovered paths and focused repair in that execution record.

Attempt 2 ran the committed candidate `2d27b0d5` against unchanged local `origin/main` at `fd0b124e`.
All 54 projects completed and the build reported zero warnings/errors, but four auth fixture deadline
failures made the wrapper exit **1 / ASCOV120**. Docs and visual suites passed. Raw merged coverage was
95.09% line / 89.01% branch; the official aggregate/patch gate was not reached. See the
[attempt 2 terminal record](../plans/issue-779-execution.md#attempt-2-terminal-result). A serialization
compatibility repair written after this run's build also requires new validation and committed coverage.
