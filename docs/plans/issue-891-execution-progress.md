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

Complete the explicit 4096-entry collection admission and quickstart gaps identified by the plan audit; validate the added boundary/state tests, refresh generated package docs, run packed proof sequentially with the unchanged coverage gate, and finish draft-only ship/CI. Complete the manifest from actual proof before any compatibility claim. If a required fact remains unavailable, preserve Unknown rather than substitute a current Job or ship an always-Unknown default reader. No cloud mutation or new provider read has been performed.

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
