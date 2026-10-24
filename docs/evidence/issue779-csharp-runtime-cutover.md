# EvidenceHost C# runtime cutover

## Recorded Linux validation — 2026-10-24

[Linux run 40104067585](https://github.com/forge-trust/AppSurface/actions/runs/40104067585),
at source `b5461cfa3b73fa00aa8d8b15e1b724f5bdb0b9ad`, independently verified all
sixteen C1 worker controls, seven C3 application controls, six workspace controls,
39 original root controls, 41 supplementary root controls, account creation and
expected rollback, and seven packaged C2 producer/artifact controls. Both Linux
shutdown regression rows passed, the corrected doc-clock check passed, and the
packaged smoke completed 22 commands with six expected rejection results. The
source capture binds 3,498 files, bytes and modes; its SHA-256 is
`d72894c34ab245be825f7cfa4e971dd0ed0bf840173115e507ef956bf67ba465`.

The same run completed the full solution test set and reached the unchanged
[solution coverage gate](issue779-coverage-gate.md). Aggregate coverage passed at
95.9057% lines and 87.0851% branches against 94.5% and 84.5% thresholds. Patch
coverage failed `ASCOV020` at 86.9492% lines and 79.9151% branches; the current
denominator requires 1,337 additional fully covered lines and 1,394 conditions.
The coverage and [acceptance status](issue779-acceptance-status.md) retain the
original thresholds, comparison base and exclusions. Further source edits require their own source-bound Linux and coverage verification. These observations do not close all 57 groups,
populate the production Trusted catalogue or accepted-proof registry, or establish
protected Windows/macOS/native-platform acceptance.

The [supervision library](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md) is the sole runtime owner. The protected AppSurface executable runs `evidence supervise --request <protected-file>` as root and starts the same image as `evidence worker --control <protected-socket>` under its separate worker identity. The C# server owns authenticated protocol dispatch, process creation, application readiness, SDK/package inputs, reporter output accounting, retained artifact custody and strict cleanup. There is no Python runtime fallback.

## Current correction and verification status — 2026-10-24

The CLI shutdown defect reported from revision 56 was fixed in revision 57 by
using an explicitly controlled fixture clock and the existing fatal test seam.
The actual Linux regression rows subsequently passed together with 578 focused
CLI tests on source `b5461cfa3b73fa00aa8d8b15e1b724f5bdb0b9ad`.
No production deadline or stopping allowance changed.

[Revision 74, run 40124730961](https://github.com/forge-trust/AppSurface/actions/runs/40124730961)
at source `569cb730dc63035a2e0ce9f5e959169695505370` has verified all sixteen
worker cases, both original seven-control application and packaged-producer
consumer groups, six workspace rejections, 39 original root controls and 41
supplementary root controls, plus genuine account creation and expected rollback.
The packaged record binds 22 smoke/planning command intentions and current-run
source/report lineage. Its measured worker chain and final unchanged numerical
coverage gate were still running when this preparation was recorded.

The next source makes existing [settlement](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/worker-settlement.md),
[cleanup](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/account-cleanup-procedure.md),
[workspace state](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/producer-workspace-state.md),
[control state](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/control-server-state.md),
[DCP metadata](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/dcp-cgroup-metadata.md)
and [report custody](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/report-custody-metadata.md)
branches directly verifiable. The focused local collector observed all 131 lines
and 194 branches in the eleven extracted helpers, with 303/303 selected tests
passing. This bounded helper result does not establish solution or patch coverage,
physical Linux ownership, or accepted consumer proof. The [unchanged coverage
command](../../scripts/coverage-solution.sh), original comparison base, thresholds,
Codecov full-line rule, exclusions and production limits remain unchanged. Every
new source revision still requires its own genuine Linux controls and final gate.

## Deployment and startup

The [protected request](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#protected-launch-inputs) is bounded data, not authority. Before CLR startup the consumer must sanitize runtime hooks and environment, pin the actual runtime host and managed image, and arm an independently terminating systemd owner. The C# process then validates retained root-owned deployment/request handles and consumes that owner's single-use activation. Unsupported platforms, unprivileged entry, invalid metadata, stale owners and unavailable registrations reject before work starts. See [packaged consumers](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/packaging.md) for the installed tool and SDK dependencies.

Do not launch a second ordinary CLI command with inherited configuration and call it supervised. Reserved roles dispatch before normal command discovery, authenticate the exact kernel peer and process identity, and retain the original deadline. Root ownership does not replace the pre-CLR bootstrap. The [design](../designs/issue-779-csharp-supervision-core.md) explains the workload versus worker shutdown scopes and pending-start ownership.

## Retired runtime

Checkpoint four removes `scripts/evidencehost-linux-launcher.py`, `scripts/evidencehost_linux_application.py`, their direct runtime wrapper and runtime-only regression suites. The older stand-alone mechanism workflow is retired too. Git history retains those source revisions and their dated observations; historical records do not establish current acceptance. The former completion context is replaced by the C# [run custody](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/report-custody.md) and [producer artifact owner](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/producer-artifacts.md), which retain handles/accounts until collection, strict closure and publication finish.

Python remains in repository preparation and finite native test drivers. Those drivers stage reviewed bytes, invoke the protected C# image, observe kernel facts, and fail their tests when facts are missing. They supply no production admission, lifecycle, arbitrary command, callback, process or filesystem capability. The [cutover verifier](../../.github/issue779-checkpoint4/verify-cutover.py) rejects reintroduced legacy runtime files or active C# and workflow references.

## Retained regression intentions

The deleted Python suites tested the retired implementation. Their runtime responsibilities now have behavior controls in the C# graph and genuine same-product process fixtures; this is not permission to delete independent planner, admission, artifact or downstream-gate tests.

| Runtime responsibility | Current control surface |
| --- | --- |
| Closed protocol and malformed/replayed operation rejection | [Control codec](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/EvidenceControlProtocolTests.cs), authenticated server and native worker cases. |
| Pending start/stop, late unit creation and original task joins | [Pending start](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionPendingStartTests.cs), [application lifetime](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionApplicationLifetimeTests.cs), and native cancellation/owner-death controls. |
| Original paired pumps, EOF/errors and aggregate reporter/process quota | [Output ownership](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxOutputPipesTests.cs), [shared ledger](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionRunOutputBudgetTests.cs), and native quota/descendant cases. |
| Root deployment audit and fresh retained workspace/artifact binding | [Deployment controls](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxProtectedDeploymentTests.cs), kernel allocation controls and native custody/report-mutation cases. |
| Application health, exact kernel/DCP identity and denial prerequisites | [Application unit/health grammar](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxApplicationUnitTests.cs), retained workspace/DCP controls and the seven genuine application cases. |
| Physical group exit, strict account closure and quarantine | Native worker/producer/application failures plus the explicit busy-unmount and unsafe-workspace quarantine controls. |

Pure controls remain pure; only source-bound native runs establish actual systemd/kernel/custody outcomes. The [57-group plan](../plans/issue-779-evidencehost-test-plan.md) and dated [acceptance map](issue779-acceptance-status.md) continue to identify independent consumer requirements.

## Native evidence and current acceptance

| Surface | Last independently verified result before checkpoint four | Limits |
| --- | --- | --- |
| Worker/control owner | 16/16 native checkpoint-one controls; see [implementation record](../plans/issue-779-csharp-supervision-migration.md). | Historical source-bound Linux evidence, not a new coverage result. |
| Producer and artifact custody | [Checkpoint two](../../.github/issue779-checkpoint2/README.md), 7/7 Linux controls. | Test failure, cancellation, quota, descendant, report mutation and quarantine are expected negative outcomes. |
| Application and both callers | [Run 39076625148](https://github.com/forge-trust/AppSurface/actions/runs/39076625148), 7/7 controls at `4ce55f5407c9bcf48e1b8dd6f64195450272fbfc`. | Actual AppHost/DCP/resource, CLI/SDK numerical coverage and cleanup; private compiled registrations. |
| Packaged consumer | [Run 39138780771](https://github.com/forge-trust/AppSurface/actions/runs/39138780771), producer lane 7/7 and fresh SDK restore/build/load passed at `8218fb0281231fd10a82f46d489cfcdf666a9912`. | Whole workflow failed its separate coverage prerequisites; this does not establish a numerical gate pass. |
| Complete solution and patch coverage | Required at checkpoint four. | Exact [unchanged gate](issue779-coverage-gate.md); fixture coverage is not repository coverage. |
| Production Trusted/platform acceptance | Closed. | Production catalogue and accepted-proof registry remain empty; no Windows/macOS native execution support is asserted. |

The [runtime workflow](../../.github/workflows/evidencehost-runtime-proof.yml) calls the reusable [worker](../../.github/workflows/issue779-checkpoint1-worker.yml), [producer](../../.github/workflows/issue779-checkpoint2-producer.yml) and [application](../../.github/workflows/issue779-checkpoint3-application.yml) workflows on the same committed revision. The worker lane must reconcile all sixteen controls without skips before the unchanged full coverage command can run. Its fourteen isolated selectors use a fresh Release publication, real build logs and complete source/tool/runtime inventories; historical checkpoint-one receipts cannot satisfy this dependency. Original native deadlines, kernel observations, owner termination and custody predicates still apply. The new workflow requires a fresh Linux execution before it establishes a current sixteen-control result.

The application lane supplies its current, source-bound native measurement to the account creation and rollback controls, then the packaged producer lane. The ordinary producer image comes from a freshly packed and installed NuGet tool; the artifact-mutation image remains explicitly private and nonpackable. Application registration images are also private and nonpackable. All measured images use the same pinned SDK and Release configuration as the solution lane, so branches from different compiler configurations are not mixed. Source captures before/after each lane bind committed bytes and modes. Neither a workflow definition nor a passing metadata assertion establishes execution.

## Checkpoint-four measured attempts

At source `8218fb0281231fd10a82f46d489cfcdf666a9912`, [run 39138780771](https://github.com/forge-trust/AppSurface/actions/runs/39138780771) passed both seven-control native consumer lanes. The ordinary producer used the freshly installed NuGet tool, and the package smoke completed all 15 commands (13 zero exits and two expected rejection exits). The application matrix repeated actual CLI/SDK coverage, readiness failure, factory stall, cancellation, TERM-resistant descendant containment and busy-unmount quarantine. Artifacts `12140798375` and `12141930107` had verified SHA-256 `065703920174ddf37f126ef458eb8af246140f28a5eff9279449b58a1f4035c1` and `4f8a013daab3d7c4917e54c4969b3d4f602693a7e4bcdd9b573f6e38a939dd05`. Both complete 2,965-file captures matched committed Git blobs, hashes and modes with zero mismatches. These results retain informational Observation claims and closed Trusted eligibility.

The same workflow's coverage lane stopped before numerical gating because package-index metadata lacked the internal Supervision entry. The correction classifies it as bundled, excluded and non-publishable, preserves authored qualification paragraphs outside generated release guidance, regenerates the chooser/readiness docs, and stores restore cache outside the captured source tree.

The subsequent full run at `d950f6e5` reached test execution but ended `ASCOV120`: root protocol fixture inputs were missing, the new core test project lacked the wrapper-required JUnit logger, and ordinary-help verification inherited an earlier host exit code. Its raw merged 86.46% line / 73.47% branch figures are not a gate result. The corrected lane arms the existing authenticated protocol test fixture around the unchanged wrapper; the core project declares the same central logger packages as existing tests; ordinary-help setup models a fresh process while preserving CommandService's original sticky failure behavior. Local source verification passed 1,740 core cases with the real JUnit logger and 10 process-entry cases, zero failures/skips/compiler diagnostics. Fresh complete coverage remains required.

## Claims and recovery

Successful observations remain informational with no gate eligibility. Runtime cutover does not populate the compiled production application table, accept a consumer proof, grant secrets, or enable Trusted PR/release execution. Follow the [consumer acceptance contract](issue779-consumer-acceptance.md) and [protected gate reference](evidencehost-migration.md#protected-gate-and-artifacts) before changing that boundary.

An unsuccessful join, deadline, output ledger, retained file, unmount or account close must keep the original failure. Do not publish a success record or reuse quarantined output or identities. A retry requires a fresh generation after physical containment is independently confirmed. Diagnostic retention cannot upgrade a failed run.

## Measuring the native C# owner

The [native measurement project](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md)
consumes actual Coverlet hit data from the guarded root and first normal worker. The application workflow
builds the same Release configuration as the solution lane and instruments exactly CLI, Contracts, Planner,
Coverage, Aspire and Supervision with pinned Coverlet Console 10.0.1. Deployment audit sees the actual
instrumented bytes. The [private C3 measurement fixture](../../.github/issue779-checkpoint4/measurement/README.md)
retains the authenticated worker's private temporary directory and copies genuine hit bytes unchanged after
original normal exit. Coverlet's own tracker and reader perform all accumulation. The fixture is compiled
out of ordinary images. Worker isolation, writable paths, capabilities, limits and deadlines remain unchanged.
The initial measurement left the SDK image uninstrumented, and killed-process or SDK-worker hits were not presumed. The [dual-image measurement](../../.github/issue779-checkpoint4/measurement/README.md) now uses a separate pinned SDK collector and source-bound report. [Run 39168743553](https://github.com/forge-trust/AppSurface/actions/runs/39168743553) verified both seven-control consumer lanes on `10c0a6529b561634f453e95ab918b4f6f4778eb8`, all 29 mandatory root controls without skips, and unchanged raw hit-file handoffs for all six SDK-worker modules. Its separate SDK report records positive hits in the host bootstrap and restricted application adapter. Both complete 3,256-file captures match the source manifest. Producer artifact `12152590015` has SHA-256 `967093dea6a7582079b16584a9cac8ee6f6e744ab0806063b0ee612f800a119d`; application artifact `12151889500` has SHA-256 `6f5375ed9fdbd16f3e7ab0732504d7b747345d649eca209ba21b8aa9fb7039a6`. These consumer and measurement results do not establish a pass of the separate unchanged solution and patch gates, or Trusted eligibility.

The solution lane downloads only its successful application's current-run artifact, stages the five
fixed source/measurement/receipt files, and configures the pinned collector's supported `MergeWith`
setting. The Linux test rejects absent files, source or digest mismatch, incomplete controls, missing
physical settlement and a report with no guarded root hits. The regular [PR/main build](../../.github/workflows/build.yml) calls the same reusable consumer
lanes and stages the same current-run measurement before its existing coverage command. It preserves
its original `HEAD^1` PR comparison and empty main comparison. That job pins the same 10.0.401
SDK/compiler as the native lane so merged branch offsets come from the same compiler; setup still
installs the current LTS runtime servicing patch. The dedicated runtime workflow covers
feature pushes and manual validation; it does not duplicate PR runs. Both coverage jobs explicitly pass
the restored NuGet cache, selected native-measurement flag and docs-evidence location through the
broker fixture's closed child environment. The regular PR build's independent package/platform checks
continue alongside these required native jobs.

This is measurement evidence, not runtime
authority or an accepted consumer proof. The existing coverage script, exclusions, numerical thresholds,
codecov semantics and default `origin/main` comparison are unchanged. A successful test or collector
merge alone does not establish a numerical gate pass.

At [source 3396c580](https://github.com/forge-trust/AppSurface/actions/runs/39141240670), all 55 solution
test projects passed, but final merge stopped at `ASCOV114`: the fixture environment selected an empty
NuGet cache instead of the cache restored by the workflow. The corrected child command explicitly
passes that restored cache to the ordinary coverage wrapper. Its isolated protected producer still
uses its separately staged SDK/package inputs. No numerical gate result is claimed for that attempt.

### Named mutex prerequisite discovered by measurement

[Run 39144415301](https://github.com/forge-trust/AppSurface/actions/runs/39144415301) at
`87fa756597c5a18bce1cf08e67712e02c45b1f91` reached actual application readiness and physical
settlement, then failed its normal control. Coverlet's process-exit tracker threw a named-mutex
`IOException` in the worker and root image, so its zero-hit report is not acceptance or useful gate input.
The captured SDK tree contained runtimes 10.0.12 and 10.0.13: the setup action installs the latest LTS
runtime separately from the selected 10.0.401 SDK. A bounded Linux container probe with runtime
10.0.13 reproduced the same mutex failure for root and nonroot under `HOME=/nonexistent`; each
succeeded with `XDG_RUNTIME_DIR=/tmp`. This is a mutex prerequisite probe, not a systemd acceptance run.

The closed [bootstrap environment](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#root-owner-activation-and-process-lifetime)
now requires that literal sixth assignment for both reserved roles. The native fixture emits the same
six assignments before CLR startup. The worker uses its existing private temporary filesystem;
capabilities, input/output mounts, deadlines and other isolation settings remain unchanged. This does
not change the hit-file location or infer hidden worker hits. A fresh seven-control Linux run and the
complete numerical gate remain required.

At [run 39146333953](https://github.com/forge-trust/AppSurface/actions/runs/39146333953), source
`771d0538347998bdbcff0257fe5089c350ca68bc` passed all seven packaged producer/artifact controls and
all seven CLI/SDK application controls. The downloaded artifacts had SHA-256
`e9ad9f73c2a293fb8a69bf0ad65133f002d34e915ce460bf2c5a3e819d5f0703` and
`196843ff312daa9e12fc709d2d29e29f665c1097f78f978b8f36e898c05b4d46`. Both complete 3,239-file
source captures matched immutable Git blobs, SHA-256 values and modes. The actual application
receipt confirmed final group emptiness for every case, and the native measurement contained
guarded root execution hits. The mutex exception was absent. The 15-command package smoke also
passed, including its two expected rejections. The separate full solution/patch gate was still
pending at this observation; native fixture success is not a numerical gate pass.

The subsequent [failed-handler drain correction](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#pending-work-and-control-handlers)
starts the original descendant stop before joining handlers when a worker disappears without EXIT
intent. Two deterministic barrier controls cover that ordering and retained first-stop ownership;
the focused sequence/deadline suite passed 57/57 with zero compiler diagnostics. This local result
does not replace a new native run or the final unchanged coverage gate on the corrected source.

The same source `771d0538` completed all 57 solution project selections and merged real native input,
but the wrapper exited `ASCOV120` before numerical gating: the CLI suite passed 3,181 of 3,182 cases.
Its existing `CoverageSolutionScriptTests.BuildWorkflow_ShouldUseCoverageSolutionScriptForDefaultLane`
required the canonical `./scripts/coverage-solution.sh` spelling, while the workflow invoked the same
file as `scripts/coverage-solution.sh`. Both workflow calls now use the canonical relative spelling.
The existing 36-case script/workflow suite passed after that correction. No wrapper, threshold,
exclusion, test expectation or gate policy changed. Raw merged coverage was 132,522/139,297 lines
(95.1363%) and 60,095/69,945 branches (85.9175%); those values are not a full-gate pass, and the
official patch result was not reached. The retained coverage artifact was verified against GitHub's
SHA-256 `650318850432d88e0c87b76cf3c479e0e094cc67e541bc4709fbbf60b3ee1f10` before inspection.

At [run 39150305318](https://github.com/forge-trust/AppSurface/actions/runs/39150305318), source
`781f000fa9735bde18ad6db84fa0a1a34ed5428c` again passed all seven application controls, including
both CLI and SDK callers. Artifact `12145158540` had SHA-256
`e0f3dd05c55609603a2d89c5f67ce4debda69a7d3f81e1c6b73b5f49f6683307`; its complete 3,236-file
capture matched Git blobs, file hashes and modes. Every application control confirmed both initial
and final empty groups. The producer lane passed its first four controls but failed the fifth,
`ignored-cancel`, so its remaining two controls were not reached. That artifact's SHA-256 was
`a7e5d66bc898a5b31b4a57c760dd85e7e123e626a45d5217c3f6213553b79eb0`, with the same complete
source verification.

The failed producer state retained `deactivating/final-sigkill`, joined output pumps, and no double
empty-group or physical-settlement receipt. The worker failed while requesting stop. The code gave
systemd's automatic termination the worker's entire ten-second stopping allowance, leaving no
reserved time for the response and final checks. This is a source-backed timing conflict; the
artifact does not independently timestamp signal delivery. The
[producer stop procedure](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/coverage-execution.md#bounded-producer-stop-escalation)
now uses half of the existing stopping reserve for cooperative observations and then explicitly
forces the same selected group under the original cleanup token. Its 52 focused procedure/unit
controls passed locally; a new native seven-control run is required. No previous failure is upgraded
by the source correction or the fixture's later containment.

The measurement lane also runs [root retained-file controls](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md#root-filesystem-measurement)
through the existing mandatory Linux factories and standard pinned collector. The extra report is
merged using Coverlet's supported report merge; no raw private-worker hit file is reconstructed.
The image hashes and zero-skip root TRX are required before that input can reach the unchanged gate.
This source preparation is not a measured coverage gain or a new native acceptance result.

### Current numerical gate and remaining work

The unchanged gate did reach numerical evaluation in
[run 39150305318](https://github.com/forge-trust/AppSurface/actions/runs/39150305318).
Aggregate coverage passed at 132,557/139,306 lines (95.1553%) and 60,113/69,949
branches (85.9383%). Patch coverage failed: 13,016/16,264 fully covered changed
lines (80.0295%) and 20,102/27,416 branch outcomes (73.3221%). The configured
95%/85% thresholds and 0.5 percentage-point tolerance were unchanged. This is a
failed gate, not permission to publish a completed checkpoint. The current
source still needs a fresh passing solution and patch result.

[Run 39153423641](https://github.com/forge-trust/AppSurface/actions/runs/39153423641)
on `5608180be49d52d08d7aec4e25024bae3fa9e51e` passed all seven producer/artifact
controls, including ignored cancellation after the stop correction. Its application
lane passed six controls, then the disposal control's observer raised
`JSONDecodeError` while polling the newly created descriptor; neither owner output
stream had bytes. The artifact does not establish an application failure cause.
Both artifacts' complete 3,239-file source captures matched Git blobs, SHA-256
values and modes. The [observer correction](../../.github/issue779-checkpoint3/README.md#descriptor-publication-observation)
waits for the existing descriptor seal; 12 portable controls pass, but the complete
native matrix must run again. The numerical gate job for that attempt was skipped.

## Latest measured gate and worker-measurement work

[Run 39155973682](https://github.com/forge-trust/AppSurface/actions/runs/39155973682) on `4f146641808ec1e24eb91023d522183a270b8d45` passed all 57 project execution records and both native consumer lanes. The unchanged numerical gate still failed `ASCOV020`: aggregate lines 132,573/138,529 (95.70%) and conditions 60,081/69,263 (86.74%) passed; patch fully covered lines 13,065/15,487 (84.36%) and conditions 20,048/26,056 (76.94%) failed. At those fixed denominators, the effective 94.5/84.5% requirements need 1,571 more fully covered lines and 1,970 conditions. Its coverage artifact `12148249302` is SHA-256 `aba10828b6a523c5707f4a62cbe8c528c1685559fd8f02eb07db4f3b60278ea8`. These are measured deficits, not predicted gains from individual tests.

The later source `995d2e1d8675764a4153e220d18b4a68b4f427d9` passed all seven producer, seven application, and 28 root filesystem/kernel controls in [run 39158246121](https://github.com/forge-trust/AppSurface/actions/runs/39158246121); its separate unchanged gate completed with aggregate 95.82% lines/86.91% branches passing and patch 85.41% lines/77.74% branches failing (`ASCOV020`). At that point, the six-module worker measurement and 140 recipe boundary controls still required a fresh run. The current [worker measurement contract](../../.github/issue779-checkpoint4/measurement/README.md) selects seven modules, including the shared Evidence.Cli workflow assembly, and requires fresh source-bound reports. The isolated worker-only helper proof is linked in the [native measurement reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md#current-verification-boundary), and supplies no AppSurface coverage or Trusted acceptance itself. No gate threshold, comparison base, Codecov rule or exclusion has been changed.

[Run 39162572720](https://github.com/forge-trust/AppSurface/actions/runs/39162572720), source `54aa6974aff638538888c56baf088080fd2ac50f`, verified the seven application consumers, 28 root controls, and unchanged first-worker hit handoffs for CLI, Contracts, Planner, Coverage and Supervision; Aspire had no worker file in the CLI control. The same run's producer observer failed its final control, as recorded in the [observer reference](../../.github/issue779-checkpoint2/README.md#observing-the-sealed-descriptor). Its separate unchanged gate reached numerical evaluation: aggregate 133,317/139,003 lines (95.9094%) and 60,281/69,283 conditions (87.0069%) passed; patch 13,266/15,438 fully covered lines (85.9308%) and 20,451/26,088 conditions (78.3924%) failed. At those fixed denominators, 1,323 more fully covered lines and 1,594 conditions are needed. Artifact `12150457433` has SHA-256 `ee4662b5cbb7c5911725c454dc2e6e1185d69ee92ba9c0435c44b3e667afcac8`. The exact diff and streamed XML independently reconcile those counts. Later source changes require a fresh gate; this failed result cannot establish checkpoint completion.

The runtime workflow retains the gate's actual `coverage-patch-targets.json` and `coverage-patch-targets.md` filenames alongside its numerical report. These remediation artifacts do not alter evaluation, comparison base, thresholds or exclusions.

### Latest SDK measurement and workspace failure coverage

[Run 39168743553](https://github.com/forge-trust/AppSurface/actions/runs/39168743553), source `10c0a6529b561634f453e95ab918b4f6f4778eb8`, passed both seven-control consumer lanes and all 29 required root tests without skips. The unchanged gate reached numerical evaluation and failed `ASCOV020`: aggregate 132,819/138,377 lines (95.9834%) and 59,815/68,428 conditions (87.4130%) passed; patch 12,832/14,812 fully covered lines (86.6325%) and 19,517/24,378 conditions (80.0599%) failed. At those fixed denominators, 1,166 more fully covered lines and 1,083 conditions are required. Coverage artifact `12153400464` has verified SHA-256 `fe7f5db23135448c3238835f7de947d99000b0aacb5f6c7c9a6a9ad5e76ab90b`. The exact source capture, immutable-base diff and streamed XML independently reconcile those counts.

The subsequent [workspace rejection controls](../../.github/issue779-checkpoint3/README.md#native-workspace-rejection-controls) prepare real unsafe objects and require failure/quarantine before any fixture containment. Their local data and compilation checks are not native verification or a predicted numerical gain. The original coverage command, Codecov full-line rule, comparison base, thresholds, tolerance and exclusions remain unchanged.

### Current worker lane and measurement configuration

[Run 39212528881](https://github.com/forge-trust/AppSurface/actions/runs/39212528881),
source `2befb98ad5e1a214a4d33abb357c1f8dfd11f0cf`, completed the application,
account creation, rollback and packaged producer measurements. The standard
report chain was C3 → C4Accounts → C4AccountRollback → C2 → None, with identical
3,290-file source captures. Its unchanged numerical gate failed `ASCOV020`:
aggregate 134,886/147,193 lines and 62,668/87,973 outcomes; patch 13,553/18,123
fully covered lines and 36,067/68,746 outcomes. Coverage artifact `12171214181`
has verified SHA-256 `077b33412e76e5bb67ca1c60b416f8eaba89526585ad4c8d23d01b0239b8390c`.
These results do not establish checkpoint completion.

The [account](../../.github/issue779-checkpoint4/measurement/accounts-README.md#build-and-entry-contract)
and [producer](../../.github/issue779-checkpoint4/measurement/producer-roots-README.md#workflow-command-construction)
publications now use Release consistently with C3, the installed package and the
solution build. Debug and Release branch maps can differ even for equal source
hashes. The correction requires fresh collection and the standard report merge;
it does not rewrite report rows or predict a passing percentage.

The reusable [current worker lane](../../.github/issue779-checkpoint1/README.md)
now supplies all sixteen controls through fourteen isolated selectors. The
source-owned inventory records availability to run, with mandatory actual raw
archive, command and stream readback. It does not turn the historical sixteen
passes or portable test data into a current result. Local preparation passed
87 portable controls and actual locked restore, formatting and Release
publication of all three helper projects. The SDK-free runtime copy was also
executed locally. These macOS preparation checks supply no Linux qualification,
native acceptance or coverage credit; the final source still requires a new
Linux matrix and passing unchanged solution and patch gates.
