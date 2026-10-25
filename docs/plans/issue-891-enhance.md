# Issue 891 enhancement record

Scope: the [approved amended execution plan](../designs/issue-891-cloud-run-execution-observation.md), current branch implementation, new files and adjacent legacy behavior. Base: `origin/main`. The canonical gstack review checklist was read from the installed review skill. The default four-cycle limit applies.

## Cycle 1 — review, address, verify

Native code review identified unrestricted omitted-counter defaults, unproven v2 egress/reconciliation defaults, safe help admission, and coordinated Terraform expression tampering. The fixes retain full configuration parity: only the specifically evidenced v1 counter D rule is admitted; v2 requires explicit evidence. The observer admission validator checks the complete generated Terraform projection, including expressions and inventory, after byte snapshots, hashes and portable-intent consistency checks. CLI output has one serialization/write/flush owner and caller-cancellation precedence.

Independent installed-SDK replay and the official Google generated v1 client justify the scoped five-counter omission rule. The exact source and replay boundaries are recorded in the [mapping assessment](issue-891-provider-mapping.md). Creator-version annotations remain separate from reader-version evidence. No provider read or cloud mutation occurred.

Process validation found that joining all tasks could postpone timeout cleanup; deadline now has an independent signal. A temporary profiling run measured a Darwin tree kill consuming about 34 ms of a 300 ms call's 30 ms reserved cleanup allowance. The native short-budget test therefore permits only the two specified safe outcomes, Deadline or CleanupUnconfirmed; both map to Unknown. A separate two-second test requires confirmed Deadline cleanup. This corrects a new test's unsupported universal cleanup assumption, preserves the original deadline and reserve, and does not change the configuration proof or an inherited test/gate. A mistaken cleanup-budget renewal was removed before accepting validation. No timing threshold or coverage policy was lowered.

## Cycle 2 — current review

The native reviewer confirmed the v1 D rule, v2 explicit-field boundary and complete projection validation. An int32 generation concern was stale: both generation fields already select int32 for v1 and int64 for v2. The reviewer re-read the current source and returned a clean review with no actionable production findings. The loop stops at cycle two, before the outside-review checkpoint. Outside cross-model coverage is not claimed.

Verification is recorded with current commands/results in the [execution progress record](issue-891-execution-progress.md). Full coverage, CLI QA, packed distribution proof and CI are separate required gates; this review does not imply they passed.

## Completeness follow-up — scoped new review

The plan audit found two deliverables missing from the otherwise implemented baseline: explicit JSON collection admission and the complete library/published-bundle quickstart. The new observer-only helper admits at most 4096 direct object properties/array elements, inclusive, within the existing byte/depth limits. Both artifact and provider response paths apply it; legacy Verify admission is unchanged. Boundary tests cover the cap and cap+1, and the CLI guide now shows bounded byte reconstruction, direct rendered-result use and the safe offline envelope.

A narrow native review also found a present malformed v2 label map was treated as absent. The classifier now rejects that carrier as malformed; new null/array/scalar tests protect it. Current broad coverage is still running on the prior compiled baseline, so these changes and additional counter/timestamp/generation tests await focused validation and a fresh full gate. No clean post-change review or coverage result is claimed yet.

Repository test-path policy is applied to new dynamic fixture paths using the existing shared helper. No allowlist, inherited test, timeout, threshold, snapshot, CI or suppression rule is relaxed. PackageIndex's real verifier reported a stale generated chooser; the existing generator refreshed the documented manifest output.

## Coverage-focused enhancement pass

The measured local reports identified uncovered alias/environment paths, observer error handling, Windows launcher construction and actual CLI command/text paths. Focused tests now exercise these through the public observer, native reader contract and intended internal CLI seams. A pure internal platform selector tests the fixed Windows argv construction on this host without claiming Windows native cleanup. Impossible duplicate byte/UTF-8 checks after the sealed result constructor and an unreachable drain-loop allowance branch were removed; the enforcing constructor and exact/+1 native boundary tests remain.

Fresh current focused validation after host recovery passed 266 GCP tests and 88 CLI tests. One newly added validator test originally expected the projection diagnostic for an unknown nested field; the schema correctly returned ASDEPLOY171. Its name/card/assertion now test the actual closed-schema contract. This corrects a new test expectation and does not weaken an existing gate. Full solution coverage remains required.

## Canonical-input regression

A final parent review found that .NET regex `$` accepts a position before a trailing newline. An otherwise valid execution resource therefore passed syntax admission and reached the reader; the invalid logical-ID variant reached binding validation. Two public-API regression cases failed against the pre-fix source (ASDEPLOY179 / ASDEPLOY172 rather than local ASDEPLOY170 with zero I/O), then passed 2/2 after rebuilding the corrected isolated source. Absolute `\z` anchors now protect the new observer/CLI identity, digest, timestamp and scoped reference grammar. The existing legacy profile/Verify grammars are unchanged. A narrow native review verified the regular/verbatim C# string forms and found no accidental interpolation changes.

The first fixed-copy invocation reused an older incremental assembly because copied source retained older timestamps. It was not green evidence; touching only the isolated copied source forced compilation and produced the actual passing result. Red and rebuilt-green logs are retained separately. No product gate, timeout or threshold was relaxed.

## Measured CLI coverage follow-up

The second full run's CLI report identified unexercised duration suffix/precision, raw JSON flag, text-success routing, logical-ID redaction, local admission timeout and remaining-budget clamping paths. A new focused pass adds behavior tests through the intentionally internal seams, using deterministic clocks for deadline boundaries. The assertions, policy thresholds and inherited test timeouts remain unchanged. Focused validation and the final unchanged full gate are still pending; this is not a passing coverage claim.

The explicit advisory Codecov patch gate then exposed partial-condition gaps despite a passing aggregate measurement. Two focused review/address passes added malformed-evidence, identity, configuration, CLI and package-consumer failure matrices. The latest hybrid replacement diagnostic passes, but only a fresh unchanged full solution gate can clear the coverage obligation. Native review and final affected QA remain separate. No suppression, reflection, test retirement, timeout or threshold change was made.
