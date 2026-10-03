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

### Later passing runs

The [attempt 13 execution record](../plans/issue-779-execution.md#coverage-attempt-13-and-packed-depthexception-proof)
records a subsequent unchanged wrapper pass at `75f5f876adc1423357892f61057b2a4919180ee9`,
against `origin/main` at `4aa8329c76f4279ad319747f9f3c14b9b8de51ef`, on October 1, 2026.
It reports exit **0**, **15,032 passed / 0 failed / 4 existing skips**, zero build warnings/errors,
aggregate coverage **95.112% line / 89.0425% branch**, and committed-patch coverage
**98.419% line / 100% branch**. The earlier failed attempts above remain historical failures;
they do not describe the outcome of every later run.

A fresh recovery run on October 2, 2026 used the same source revision and comparison base,
with four authored documents modified. The exact `./scripts/coverage-solution.sh` started at
05:21:11 UTC and completed at 05:41:57 UTC with exit **0**: **54 successful projects**,
**15,032 passed / 0 failed / 4 existing skips**, zero build warnings/errors, aggregate coverage
**95.1057% line / 89.0287% branch**, and committed-patch coverage **98.419% line / 100% branch**.
All 288 archived file hashes and all 2,653 tracked input hashes were checked; inputs remained
unchanged during that run. Compiler process isolation resolved a local generated-XML access
failure without changing the wrapper, test selection, thresholds, tolerance, or environment policy.

These dated results establish their recorded inputs only. Subsequent documentation corrections
still require the final [verification gate](../plans/issue-779-execution.md#required-checks);
local coverage does not establish Ubuntu mechanism proof, protected admission, or completion
of the [full consumer acceptance plan](issue779-consumer-acceptance.md).

## Current frozen-source coverage checkpoint (2026-10-03)

[Native-v15 run 37101591528](https://github.com/forge-trust/AppSurface/actions/runs/37101591528)
tested source `5d325bb8c0f857eb37f0a736f39a0342b03e5c38` against comparison
base `416b30919e6c4d03b634d9d3fe558884b02f0b91`. Its aggregate gate passed,
but its patch line result **86.7302%** failed the unchanged requirement; patch
branch coverage was **85.109%**. This run exited **1 / ASCOV020**.

[Native-v16 run 37143380879](https://github.com/forge-trust/AppSurface/actions/runs/37143380879),
source `610489dbd1165ea7980faced55969cfe32c06b69` and the same comparison
base, produced raw merged measurements of **143,490 / 151,152 lines
(94.930930%)** and **48,765 / 54,890 branches (88.841319%)**. A test failure
made the unchanged wrapper exit **1 / ASCOV120** before numerical gating.
Official patch metrics for this run are unavailable; the raw percentages do not
establish a gate pass.

A source-matched advisory comparison of 599 previously identified gap locations
found **28 now fully covered**: 16 formerly zero-hit locations and 12 formerly
partial-condition locations. The other mapped locations remained 411 zero-hit
and 160 partial-condition. This historical location comparison does not measure
the complete current patch or supply official patch percentages.

[Native-v17 run 37147414478](https://github.com/forge-trust/AppSurface/actions/runs/37147414478)
tested source `085b302fedc37c30932122be27b3efc20ff9f5b0` against the same
base and completed every test selection. The job log reports aggregate
**94.94% line / 88.85% branch** and patch **87.44% line / 86.07% branch**.
Aggregate and patch branch gates passed; patch lines failed the unchanged
effective **94.5%** requirement. The wrapper exited **1 / ASCOV020**. Artifact
numerators and the latest per-file gaps remain under verification. The
[consumer checkpoint](issue779-consumer-acceptance.md#current-validation-checkpoint-2026-10-03-1958-utc)
records focused repairs and immutable run statuses. The current complete gate
has failed and remains required before the product PR can be updated.
