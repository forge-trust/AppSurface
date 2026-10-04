# #806 implementation enhancement

Target: the [approved template plan](../designs/issue-806-durable-worker-template.md), branch `codex/make-it-so-durable-worker-template`, base `origin/main` at `7ae38084`. Default cycle budget: four. This is implementation review, not the earlier planning remedy recheck. Local fixes are authorized by make-it-so. No push, PR or release has occurred.

## Cycle 1 — review, address and focused verification

Native independent-context review identified these concrete in-scope defects:

- **P1:** case-sensitive template selection could skip the publisher's evidence requirement even though the plan/manifest validator accepts NuGet IDs case-insensitively. All orchestration/publishing selections now use ordinal case-insensitive identity. Lowercase/uppercase publisher regressions prove rejection before credential read or push.
- **P2:** timing hashed only package archive files while copying the whole extracted cache. Hashing now binds every relative directory/file and exact bytes in the bounded regular tree, with extracted-file and copied-root regressions.
- **P2:** public replay uninstall failure skipped safe owned-root deletion. Nested cleanup independently attempts deletion while retaining the failed verdict.
- **P2:** initdb cancellation could abandon output drains and still permit root deletion. Drains remain uncancelled; termination and bounded drain join must both succeed before original cancellation propagates. Otherwise the owner retains the root. Actual child-process cancellation regressions cover clean teardown and retained pipes.

Actual consumer QA found a separate **P1** quickstart filter collision: generated `FirstDurableWorker.Tests` namespaces matched `FullyQualifiedName~FirstDurableWork` for every test, including the separately configured native smoke. The Docker test passed all four checkpoints but the full command exited1. A fixed `DurableWorkerTemplate.Tests` namespace removes application-name-dependent selection; the assembly's friend identity still renames normally. Repository regression and fresh archived proof are required.

Parent additionally repaired malformed ZIP classification: `InvalidDataException` now becomes the safe `PackageIndexException` contract error instead of escaping archive validation. A sentinel regression protects its projection.

Focused evidence so far: new command orchestration suite plus CLI22/22, command100%line/branch; native/process/publisher integrated73/73; template repository/host72/72 after namespace import repair. Cache/replay final focused verification and corrected exact archive proof remain pending. All reports are diagnostic until the unchanged full coverage gate passes on final source.

## Coverage boundary

`COVERAGE_GATE=./scripts/coverage-solution.sh`, unchanged aggregate95%line/85%branch and patch95%line/85%branch against origin/main, Codecov line semantics. Attempt1 failed with stale binaries. Attempt2 ran all56 projects: only two RazorWire package-page visual mismatches failed; refreshed baselines address those observed changes. Aggregate95.06%line/89.01%branch is diagnostic only: ASCOV120 stopped before patch gate, and late test/production changes postdate its build. A fresh gate is mandatory.

## Open evidence

Actual three-OS candidate receipts, five serial primed/cold series, outside-checkout human trial, named Skoolit adopter certificate and doctor#801 case-owner disposition remain separate requirements. No result is inferred from workflow source, generated tests or local feasibility. Optional outside Claude Code review was unavailable in the earlier pass; native reviews do not establish cross-model independence. Cycle2 and final verification remain pending.

## Cycle 2 — independent recheck and evidence identity repair

The native adversarial reviewer found no new generated auth-order/settings/passive-host defect in its scoped production review. It found P2 missing hosted runner image revision in OS receipts. The schema now retains the observed bounded `ImageOS/ImageVersion` as `RunnerImage`; local observations may be empty but cannot authorize publication. The OS workflow fails when either observation is missing, and publication rejects empty/unsafe image identities. New projection and rejection regressions cover that boundary. The final focused tests and cycle-3 recheck are pending. Tests/fixtures were reviewed by the adversarial reviewer in summary mode only; optional outside Claude Code remained unavailable.

Corrected exact-archive Docker and sample replacement proof passed with all four assertion checkpoints and owned cleanup (`/tmp/issue806-docker-filter-fixed-final.log`, receipt `/private/tmp/issue806-current-consumer-docker.json`). Integrated template orchestration focus passed457/457 and template repository/host verifier72/72. These local dirty-source diagnostic proofs do not establish hosted OS, timing or adoption certificates.

An additional coverage run was launched by a nested watcher before late fixes settled. Parent stopped that stale run in its owner session74697. Projects1–31 completed, project32 was interrupted; exit1/OperationCanceledException is not a gate verdict. It generated partial reports only. The final exact gate must run after all code/test writers finish.

Image-boundary focused verification passed470/470 with fresh coverage (`/tmp/issue806-runner-image-focused.log`), executable workflow transport2/2, formatting and diff check passed. Cycle3 independently rechecks the narrow image-provenance remedy; full coverage remains mandatory.

## Focused coverage enhancement — latest native/receipt/cache delta

This new invocation targets the failed unchanged coverage gate and its actual patch targets; it does not extend the earlier review budget. The first independent read-only pass found a race between a one-second real-child timeout and its ready/identity observation, and a cleanup-refusal test that failed to remove its own retained proof root. Both narrow test repairs preserve strict termination/ownership assertions. The second pass identified the remaining process-disappearance window while reading `StartTime`; the timeout-only recovery now first awaits verified timed-out termination and checks any captured child's exit, while explicit cancellation remains strict. The third pass rechecks those remedies. No production scope was broadened.

Meaningful tests of native credentials/process/PID/log/marker/root ownership, malformed archive bounds, staging, safe CLI dispatch, exact release/tool/archive identity and cache/sample evidence are added. The explicit filesystem-root regression exposed an actual separator mismatch and passes only with the corrected guard. [Attempt4 analysis](issue-806-coverage-attempt4-analysis.md) distinguishes all fresh focuses, disk-full failures and diagnostic coverage from the required current-tree solution gate. The final native CLI QA replay and unchanged full gate are still pending; no clean coverage-ready verdict or draft PR is inferred from static review.
