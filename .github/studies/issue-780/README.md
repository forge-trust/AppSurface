# Issue #780 paired package selection study

The study compares the ordinary package selection for [PR #960](https://github.com/forge-trust/AppSurface/pull/960) with two exact path rules. The rules opt the authored CI evidence guide and its matching release entry out of `package-checks`. The docs export and release contract run independently. The package-index, mixed, fallback, and ordinary authored-doc cases test selection boundaries. The [frozen definition](definition.json) pins the two policies, source and tree identities, the ordinary and untimed run references, the runner build, and the repetition count.

## Run and admission

The [study workflow](../../workflows/evidence-profile-study.yml) runs via push on `codex/issue-780-paired-study` with a workflow-path filter. The workflow also declares manual dispatch for a future installation on the default branch. Use one complete attempt. Re-run all jobs to regenerate that attempt's warm seed, untimed probes, qualification, and samples. A different fixture, policy, workflow or runner image requires new qualification.

1. The seed job restores the locked validator and web install into an archive of the NuGet global packages and pnpm store. Use the same SHA-256-checked bytes in both warm arms. Start both cold arms with empty restore directories. The separate NuGet HTTP cache and the package producer's private consumer caches start empty in both states. All caches and outputs are runner-local. Evidence receipts are never reused.
2. Four untimed probes check the planned warm and cold execution paths. The baseline must reproduce package, coverage, and native plan digests and close the ordinary package obligations. The receipt checks all three package stages, the four proof slots, and successful custody verification. Candidate and baseline use the same pinned tool and producer source, with timing instrumentation applied to the checkout after the Git identity checks. The probes discard their phase durations.
3. The successful qualification opens 24 pairs per state on separate runners. Odd pairs execute the baseline first. Even pairs execute the candidate first. A failed or mismatched arm excludes the pair. Use all admitted pairs, with a minimum of 20 per state.

The timed job boundary is the standalone package gate. Sibling coverage and native projections remain empty for the fixture. The independent docs export and release-contract results belong to selection quality. Each arm checks its package proof on the runner and retains a bounded study receipt. The ordinary terminal diagnostics archive has no downstream consumer for this fixture. Study occupancy includes the study checkout, restore-state setup, bounded receipt upload, and runner teardown. Use these measurements for the paired comparison under the study setup.

## Interpret the report

Phase measurements use a monotonic clock in the existing validator and host. The planner total includes plan writes and replanning during run and verification. The manifest total spans construction and canonical serialization/write. The zero-resource package fixture records zero startup. A separate disposable resource-backed host proof must exercise nonzero readiness and cleanup before promotion.

Use the local reporting script with the exact workflow run ID, attempt, and full workflow commit SHA. The report takes nearest-rank p50 and p95 from the same admitted pairs used to sum runner occupancy. Each state must have candidate planner-plus-manifest p95 below 30 seconds and candidate occupied runner minutes no more than 10% above baseline. A missed risk, insufficient sample count, failed verification, or missing resource proof leaves the candidate opt-in. Passing all checks makes it eligible for maintainer review.

Run the report from the repository root after the workflow attempt reaches a terminal state. Authenticate `gh` with read access to the run and artifacts.

```sh
python3 .github/scripts/report_evidence_profile_study.py \
  --run 46171759317 --attempt 1 \
  --workflow-sha 747b30b762adc6b7b58f4c82e081c6b98a22049f \
  --output /tmp/issue-780-report.json \
  --retained-dir /tmp/issue-780-observations \
  --resource-proof /path/to/resource-proof.json
```

Use fresh output paths. The resource proof input is optional for collecting cost and selection results. Eligibility requires a valid resource proof.

## First study result

Attempt 1 admitted all 24 warm and all 24 cold pairs. Both untimed baseline probes matched PR #960's package plan and completed its three package obligations. The study qualified for maintainer review. [Exact hosted attempt](https://github.com/forge-trust/AppSurface/actions/runs/46171759317/attempts/1)

| Restore state | Pairs | Baseline runner minutes | Candidate runner minutes | Matched change | Candidate planner + manifest p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Warm | 24 | 229.13 | 19.02 | −91.70% | 597.67 ms |
| Cold | 24 | 237.50 | 17.23 | −92.74% | 594.16 ms |

The five reviewed cases had zero missed risks out of 11 for both policies. Unnecessary package selections went from one of four to zero of three. The strict docs export reproduced `RWEXPORT004` for the two broken internal links on the historical source tree. The fixed source passed docs export and the release contract in ordinary CI. [Regression job](https://github.com/forge-trust/AppSurface/actions/runs/46166882315/job/141046375492) · [Fixed docs export](https://github.com/forge-trust/AppSurface/actions/runs/46167211798/job/141047376078) · [Release contract](https://github.com/forge-trust/AppSurface/actions/runs/46167211768/job/141047303258)

The separate [resource integration test](../../../examples/aspire-apphost.Tests/EvidenceReadinessMeasurementIntegrationTests.cs) verified 34.96 ms from creation to HTTP readiness and 1.32 ms from cleanup start to settlement with one disposal.
