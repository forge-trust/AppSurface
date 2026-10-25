# Issue 891 execution progress

Plan: [approved execution-observation design](../designs/issue-891-cloud-run-execution-observation.md).
Evidence: [initial gcloud feasibility assessment](issue-891-gcloud-feasibility.md).
Recovered source evidence: [Skoolit archive assessment](issue-891-skoolit-evidence.md).

## Scope and branch

- Branch: `codex/make-it-so-cloud-run-execution`; base: `main`.
- Started at `486d68cdca85cbe33bda4f97dfe5cd3d0a674a55`. Fetched `origin/main` and fast-forwarded the feature branch to `c579bc7920e35c8c883891400ea3065319bf769e` before implementation. Git's autostash reapplied the approved TODO change successfully; the untracked design was preserved.
- The approved plan and its two owner recommendations were accepted with `Rec`. The current make-it-so invocation authorizes implementation, normal task commits/pushes and a **draft** PR, subject to the plan's evidence gate and all local checks.
- No cloud submission, mutation, credential inspection or new provider read has been performed. The subsequent user-directed local Skoolit search recovered representative execution readbacks and an intended Job pair; a new read is no longer needed merely to obtain an example.

## Facets and evidence

| Facet | Status and evidence |
| --- | --- |
| Durable goal | Full #891 objective preserved through a validated draft PR and passing CI. The prior missing-capture blocker is resolved. The durable goal is active under the owner's continuation; the objective is unchanged. |
| Branch and source | Complete; current main integrated, pre-existing plan/TODO preserved. |
| Collision audit | At this source, no production `ASDEPLOY170`–`ASDEPLOY189` allocation and no existing observer command/package reference were found. The generic diagnostic test's `ASDEPLOY199` is outside the proposed range. Repeat the audit after any further main update. |
| Provider format | Reviewed offline mapping; provider certification is unclaimed. Local SDK `576.0.0` selects v1. Recovered archive readbacks demonstrate actual v1 labels/configuration, numeric project namespaces and no explicit Cloud SQL mount. Creator-version annotations do not establish the describing tool version. |
| Native feasibility review | Completed using the configured Luna fallback after five rejected `combo/sub` attempts. An initial region-absence hypothesis was challenged and corrected: SDK execution metadata labels are plausible location/parent carriers. It is not evidence that v1 is impossible. |
| Two worksheets / compatibility manifest | Neutral recovered-shape fixtures, negative controls, actual neutral bundle hashes, exact installed-SDK offline replay and executable nested hash checks added. The reviewed five-counter omission rule is scoped to v1, with primary Google generated-client and SDK evidence. V2 requires explicit counters. No live-read or invented human acceptance claim. |
| Observer, bounded reader and CLI | Implemented with explicit project-number binding, strict full configuration classifier, immutable bundle admission, fixed read command, sequential polling and guarded CLI delivery. Focused integration passed. Final solution coverage and packed proof remain required. |
| Tests, neutral consumers and package proof | Latest focused results before final snapshot admission refinement: GCP174/174 passed, CLI57/57 passed; packed proof workflow unit tests67/67 passed. Both neutral positive modes succeeded and both swapped modes returned Unknown. Full CLI and example builds succeeded with no warnings/errors. Full artifact/package proof and solution coverage still pending. |
| Enhance and functional QA | Two-cycle enhance review completed cleanly before its outside checkpoint; native only. See [cycle record](issue-891-enhance.md). Unsupported default mappings, timeout/settlement ownership and coordinated Terraform-expression tamper gaps were fixed. Functional CLI QA follows a clean baseline commit; no browser surface applies. |
| Draft PR | Not created. Coverage and implementation gates have not passed. |

Local inspection evidence is retained in the ignored directory `.gstack/tmp/make-it-so-891/`, including the tool-version result, exact SDK source hashes and a machine-readable feasibility record. It is not a provider fixture or a compatibility certificate.

## Required coverage gate

`COVERAGE_GATE=./scripts/coverage-solution.sh`

The unchanged script runs the solution and then enforces aggregate line coverage >=95% and branch coverage >=85%. With the default `origin/main` diff base it also enforces patch line coverage >=95% and branch coverage >=85%, in Codecov line mode. CI uses the same script with its documented Release/parallelism settings and PR merge-base selection. A failed test run is not cleared by a high coverage percentage.

Attempt 1 completed on the prior compiled baseline with test failures and 94.72% aggregate line / 88.77% branch coverage. Docker became unresponsive and an installed-tool restore reported no space; the temporary raw log was later removed during host recovery. Retained project/JUnit results and the summary were copied under ignored `.gstack/tmp/make-it-so-891/validation-20261025/attempt1-*`. This attempt did not pass. The solution build passed with zero warnings/errors. Later collection-admission and test/documentation refinements are not covered by that baseline run; a fresh final gate remains required. No threshold, inherited test, CI or coverage policy has changed. Package verification and solution coverage must run sequentially in this checkout.

## CI ledger

- PR: none; published SHA: none.
- Repair rounds used: **0 / 3**.
- Cumulative CI waiting used: **0 / 60 minutes**.
- Runs/checks: not started. Required/task-relevant PR workflows include the solution build/coverage and package gates; final applicable checks must be enumerated against the published SHA.
- No readiness change, deployment or publication is authorized by the CI stage.

## Owner-directed continuation

On 2026-10-24 the owner instructed updating the plan from Skoolit evidence, reconciling both assumptions without weakening proof, and continuing make-it-so. The normative amendment is saved above the Implementation plan in the approved design. Native engineering review identified identity/compatibility/inventory regressions, now required. Exact-version SDK replay evidence is distinguished from creator annotations and new live-cloud reads.

## Next action

The 4096-entry collection admission and complete quickstart are implemented. Validate the final canonical/depth/configuration and CLI coverage cases on frozen inputs, run exact-artifact library/installed-tool proof sequentially with the unchanged coverage gate, then finish draft-only ship/CI. Complete the manifest from actual proof before any compatibility claim. If a required fact remains unavailable, preserve Unknown rather than substitute a current Job or ship an always-Unknown default reader. No cloud mutation or new provider read has been performed.

## Baseline and functional QA

- Commits: `92b5d9e9` implements the observer, CLI, neutral examples and proof wiring; `97136569` reconciles offline acceptance and exact SDK source provenance.
- The installed SDK replay and manifest tests passed (2/2); no credentials or network were used.
- Functional CLI probes used a frozen binary copy, owned synthetic `gcloud` stand-in and neutral rendered bundles. Both positive consumers returned matched Succeeded/exit 0; swapped responses returned Unknown/exit 2. Matched failure/cancellation returned exit 1, clean Running deadline exit 3, mismatch/stale/provider/local input failures exit 2, and direct managed SIGINT produced one safe ASDEPLOY186 envelope/exit 130. Text errors remained safe on stderr; fixed describe argv and unchanged bundle hashes were checked. Earlier wrong-project/catalog fixture and shell signal-delivery attempts were setup errors preserved in the ignored QA evidence, not product defects.
- Detailed captures/reports are under ignored `.gstack/qa-reports/issue-891-functional*`; these are offline process/contract probes, not provider or application-acceptance evidence. JSON collection admission changes require fresh affected probes after rebuilding.
- Plan audit found the explicit collection-count boundary and complete library quickstart missing. They are implemented within the approved scope. No proof field or existing gate was weakened. Shared test-path helper adoption corrects the repository's fixture policy; it adds no allowlist exception.

## Host recovery and final focused validation — 2026-10-25

The owner reported Docker healthy after the interrupted run. A bounded read-only `docker info` returned server `29.7.2` / exit 0; filesystem free space was 51 GiB. No Docker restart, prune or resource deletion was performed by this task.

- Current focused GCP suite: **266 passed, 0 failed, 0 skipped**. Current focused CLI suite: **88 passed, 0 failed, 0 skipped**. Logs and exact invocations are retained in ignored `validation-20261025`.
- The final classifier rejects malformed v2 label carriers and unsupported state/reason unions. Success has no v2 reason, workload failure requires recognized nonzero-exit evidence, and nonterminal reasons use their documented union member. V1 recovered `Completed` success is preserved.
- The new command-path tests join the existing process-state collection, preventing overlap with other real CLI pipelines. This fixes a failure observed in the full suite without changing its assertions or concurrency policy globally.
- Added alias, environment, resource-default, identity, polling/error and CLI text/command coverage. The shared finite collection helper has exact inclusive boundary tests. No coverage threshold or existing test was disabled or reduced.
- Generated package docs verify successfully. Formatting and a fresh full coverage run are the next required checks. The old run cannot clear the current code.

## Current run and remaining gate work

- Final affected functional QA on compiled `255b81c6` passed all scoped synthetic CLI contracts: both consumers, terminal failure/cancellation, one-read Running deadline, swapped/mismatched/stale evidence, provider/local failures, safe text, exact describe argv, unchanged bundle hashes and native caller SIGINT. The ignored report is `.gstack/qa-reports/issue-891-final/report.md`; its materialized verdict has no open items. This is agent-run offline QA, not a human onboarding or live-provider measurement.
- The independent native correctness/adversarial review of `255b81c6` returned no actionable findings. A later narrow review accepted the absolute-end anchor correction. Outside CLI/model review is unclaimed.
- Coverage attempt 2 is running against compiled `255b81c6`, after Docker recovery, in `validation-20261025/coverage-attempt2.log`. It passed the first eight projects, then reported one existing Docs source-route redirect test's HTTP 503 during initial harvest (301/302 integration tests passed). The assertion and timeout are unchanged. All subsequent project results and final percentages must still be collected. Later canonical-input/depth/validator tests require another full gate.
- The canonical-input regression has actual red proof (2/2 fail against prior source) and rebuilt green proof (2/2 pass). Exact JSON depth 64/65 boundary tests passed 2/2; added effective-setting/portable-intent matrix passed 13/13. These isolated builds use separate outputs and do not substitute for the final solution gate.
- Package metadata gate first found the legacy project name in a historical absolute artifact path in the plan. The prose now preserves the historical artifact filename without that private stale-brand path; no allowlist or gate changed. The current test-project lock adds only the existing shared test helper reference.

## Attempt 2 source-stability diagnosis

Read-only investigation mapped the installed-tool test's actual SHA-512 to the edited `GcpCloudRunBundleValidator.cs`; its protected-input assertion failed before package identity checks or the PostgreSQL diagnosis matrix. The expected hash is truncated in the log, so the starting bytes cannot be reconstructed conclusively. Separately, the docs 503 snapshot contains strict C# parse failures at the interpolation lines in three files that were briefly malformed during an edit, while the other source-route theory cases passed. Both failures are consistent with modifying the source tree during tests that inspect it. Replay both on quiescent source before the final unchanged full gate; no test, timeout, assertion or concurrency-policy change is justified.

A package-proof audit found that installed help plus the current observer-construction smoke does not prove both packed neutral consumers or the old custom-runner consumer. The remaining exact-artifact proof must compile the existing neutral example with exact package references, exercise both positive/swapped modes, compile the old `IGcloudCommandRunner` / render / verify surface, and run the installed candidate CLI against synthetic bundles and a controlled fake gcloud. Those results remain offline contract evidence, not cloud/provider certification.

## Patch-coverage enhancement and input freeze

Attempt 2 completed all 57 projects: two source-stability failures above, 55 successful projects, aggregate 95.34% line (154196/161728) and 89.42% branch (54558/61013). It failed ASCOV120 and does not clear the gate. An explicit advisory patch check against `origin/main` exposed 83.85% Codecov patch line and 81.87% patch branch coverage. Codecov line mode counts partially covered conditions as missed lines; aggregate coverage alone was insufficient.

Focused behavior matrices now cover malformed/unsupported provider carriers, identity/configuration/inventory conflicts, terminal counters/reason unions, coordinated Terraform/intent changes, old no-number profiles, parser timer precision, output routing/cancellation and package consumer failure phases. New test expectations were corrected where they predicted ASDEPLOY171/174 but the actual intended contract returned ASDEPLOY152/175; no product behavior, inherited assertion, timeout, gate or threshold changed. A new file-symlink fixture also required deleting its owned target file at teardown. Focused pass 4 was CLI111/111 and PackageIndex75/75; GCP436/437 had the subsequently corrected unsupported-shape expectation. Latest additional cases and final formatting await the final focused pass.

A diagnostic Cobertura formed by replacing whole changed-file classes with their latest focused reports, retaining the prior full baseline only for unrelated classes, measured aggregate95.26%/89.60% and patch95.05%/94.57% under the exact existing gate settings. This hybrid diagnostic guides work only: it is not the unchanged full solution gate, does not clear failed tests, and cannot authorize publishing. Retained evidence is in ignored `validation-20261025/pass4-advisory-replaced/`. The gate itself has an existing 0.5 percentage-point tolerance; this task has not changed it.

## Frozen final local baseline

Final focused pass 5 passed 453/453 GCP and 111/111 CLI observation tests with zero skips and no compiler warnings/errors. The package proof workflow tests passed75/75 in pass4 and have not changed since. The saved full-task formatter exited0 and `git diff --check` passed. Fresh package metadata gate passed64 entries/2990 scanned files. A native focused review confirmed canonical absolute-end regex escaping and no configuration-proof or gate weakening; task-local packed-proof script findings were fixed before execution. Source inputs will remain frozen during the subsequent packed proof and full gate; pending execution results are recorded separately and do not imply success.
