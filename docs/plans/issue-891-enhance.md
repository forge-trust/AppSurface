# Issue 891 enhancement record

Scope: the [approved amended execution plan](../designs/issue-891-cloud-run-execution-observation.md), current branch implementation, new files and adjacent legacy behavior. Base: `origin/main`. The canonical gstack review checklist was read from the installed review skill. The default four-cycle limit applies.

## Cycle 1 — review, address, verify

Native code review identified unrestricted omitted-counter defaults, unproven v2 egress/reconciliation defaults, safe help admission, and coordinated Terraform expression tampering. The fixes retain full configuration parity: only the specifically evidenced v1 counter D rule is admitted; v2 requires explicit evidence. The observer admission validator checks the complete generated Terraform projection, including expressions and inventory, after byte snapshots, hashes and portable-intent consistency checks. CLI output has one serialization/write/flush owner and caller-cancellation precedence.

Independent installed-SDK replay and the official Google generated v1 client justify the scoped five-counter omission rule. The exact source and replay boundaries are recorded in the [mapping assessment](issue-891-provider-mapping.md). Creator-version annotations remain separate from reader-version evidence. No provider read or cloud mutation occurred.

Process validation found that joining all tasks could postpone timeout cleanup; deadline now has an independent signal. A temporary profiling run measured a Darwin tree kill consuming about 34 ms of a 300 ms call's 30 ms reserved cleanup allowance. The native short-budget test therefore permits only the two specified safe outcomes, Deadline or CleanupUnconfirmed; both map to Unknown. A separate two-second test requires confirmed Deadline cleanup. This corrects a new test's unsupported universal cleanup assumption, preserves the original deadline and reserve, and does not change the configuration proof or an inherited test/gate. A mistaken cleanup-budget renewal was removed before accepting validation. No timing threshold or coverage policy was lowered.

## Cycle 2 — current review

The native reviewer confirmed the v1 D rule, v2 explicit-field boundary and complete projection validation. An int32 generation concern was stale: both generation fields already select int32 for v1 and int64 for v2. The reviewer re-read the current source and returned a clean review with no actionable production findings. The loop stops at cycle two, before the outside-review checkpoint. Outside cross-model coverage is not claimed.

Verification is recorded with current commands/results in the [execution progress record](issue-891-execution-progress.md). Full coverage, CLI QA, packed distribution proof and CI are separate required gates; this review does not imply they passed.
