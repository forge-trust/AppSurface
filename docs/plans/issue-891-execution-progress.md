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
| Durable goal | Full #891 objective preserved through a validated draft PR and passing CI. The prior missing-capture blocker is resolved. The runtime still reports the prior blocked status; work resumed under the owner's explicit continuation without replacing the objective. |
| Branch and source | Complete; current main integrated, pre-existing plan/TODO preserved. |
| Collision audit | At this source, no production `ASDEPLOY170`–`ASDEPLOY189` allocation and no existing observer command/package reference were found. The generic diagnostic test's `ASDEPLOY199` is outside the proposed range. Repeat the audit after any further main update. |
| Provider format | **Uncertified.** Local SDK `576.0.0` selects v1. Recovered archive readbacks demonstrate actual v1 labels/configuration, numeric project namespaces and no explicit Cloud SQL mount. Creator-version annotations do not establish the describing tool version. |
| Native feasibility review | Completed using the configured Luna fallback after five rejected `combo/sub` attempts. An initial region-absence hypothesis was challenged and corrected: SDK execution metadata labels are plausible location/parent carriers. It is not evidence that v1 is impossible. |
| Two worksheets / compatibility manifest | Neutral recovered-shape fixtures, negative controls, actual neutral bundle hashes, exact installed-SDK offline replay and executable nested hash checks added. The reviewed five-counter omission rule is scoped to v1, with primary Google generated-client and SDK evidence. V2 requires explicit counters. No live-read or invented human acceptance claim. |
| Observer, bounded reader and CLI | Implemented drafts with explicit project-number binding, strict full configuration classifier, immutable bundle admission, fixed read command, sequential polling and guarded CLI delivery. Local integration is ongoing; not yet accepted. |
| Tests, neutral consumers and package proof | Latest focused results before final snapshot admission refinement: GCP174/174 passed, CLI57/57 passed; packed proof workflow unit tests67/67 passed. Both neutral positive modes succeeded and both swapped modes returned Unknown. Full CLI and example builds succeeded with no warnings/errors. Full artifact/package proof and solution coverage still pending. |
| Enhance and functional QA | Two-cycle enhance review completed cleanly before its outside checkpoint; native only. See [cycle record](issue-891-enhance.md). Unsupported default mappings, timeout/settlement ownership and coordinated Terraform-expression tamper gaps were fixed. Functional CLI QA follows a clean baseline commit; no browser surface applies. |
| Draft PR | Not created. Coverage and implementation gates have not passed. |

Local inspection evidence is retained in the ignored directory `.gstack/tmp/make-it-so-891/`, including the tool-version result, exact SDK source hashes and a machine-readable feasibility record. It is not a provider fixture or a compatibility certificate.

## Required coverage gate

`COVERAGE_GATE=./scripts/coverage-solution.sh`

The unchanged script runs the solution and then enforces aggregate line coverage >=95% and branch coverage >=85%. With the default `origin/main` diff base it also enforces patch line coverage >=95% and branch coverage >=85%, in Codecov line mode. CI uses the same script with its documented Release/parallelism settings and PR merge-base selection. A failed test run is not cleared by a high coverage percentage.

The gate has **not run** yet: focused integration tests must pass first. No threshold, inherited test, CI or coverage policy has changed. Package verification and solution coverage must run sequentially in this checkout.

## CI ledger

- PR: none; published SHA: none.
- Repair rounds used: **0 / 3**.
- Cumulative CI waiting used: **0 / 60 minutes**.
- Runs/checks: not started. Required/task-relevant PR workflows include the solution build/coverage and package gates; final applicable checks must be enumerated against the published SHA.
- No readiness change, deployment or publication is authorized by the CI stage.

## Owner-directed continuation

On 2026-10-24 the owner instructed updating the plan from Skoolit evidence, reconciling both assumptions without weakening proof, and continuing make-it-so. The normative amendment is saved above the Implementation plan in the approved design. Native engineering review identified identity/compatibility/inventory regressions, now required. Exact-version SDK replay evidence is distinguished from creator annotations and new live-cloud reads.

## Next action

Finish focused regression repairs and the exact admitted default rules, then run fresh enhance review, CLI/neutral consumer QA, packed proof and the unchanged coverage gate. Complete the manifest from actual proof before any compatibility claim. If a required fact remains unavailable, preserve Unknown rather than substitute a current Job or ship an always-Unknown default reader. No cloud mutation or new provider read has been performed.
