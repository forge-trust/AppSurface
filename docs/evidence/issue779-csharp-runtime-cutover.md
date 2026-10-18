# EvidenceHost C# runtime cutover

The [supervision library](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md) is the sole runtime owner. The protected AppSurface executable runs `evidence supervise --request <protected-file>` as root and starts the same image as `evidence worker --control <protected-socket>` under its separate worker identity. The C# server owns authenticated protocol dispatch, process creation, application readiness, SDK/package inputs, reporter output accounting, retained artifact custody and strict cleanup. There is no Python runtime fallback.

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

The [runtime workflow](../../.github/workflows/evidencehost-runtime-proof.yml) calls the reusable [producer](../../.github/workflows/issue779-checkpoint2-producer.yml) and [application](../../.github/workflows/issue779-checkpoint3-application.yml) workflows on the same committed revision and runs the unchanged full coverage command independently. The ordinary producer image comes from a freshly packed and installed NuGet tool; the artifact-mutation image remains explicitly private and nonpackable. Application registration images are also private and nonpackable. Source captures before/after each lane bind committed bytes and modes. Neither a workflow definition nor a passing metadata assertion establishes execution.

## Checkpoint-four measured attempts

At source `8218fb0281231fd10a82f46d489cfcdf666a9912`, [run 39138780771](https://github.com/forge-trust/AppSurface/actions/runs/39138780771) passed both seven-control native consumer lanes. The ordinary producer used the freshly installed NuGet tool, and the package smoke completed all 15 commands (13 zero exits and two expected rejection exits). The application matrix repeated actual CLI/SDK coverage, readiness failure, factory stall, cancellation, TERM-resistant descendant containment and busy-unmount quarantine. Artifacts `12140798375` and `12141930107` had verified SHA-256 `065703920174ddf37f126ef458eb8af246140f28a5eff9279449b58a1f4035c1` and `4f8a013daab3d7c4917e54c4969b3d4f602693a7e4bcdd9b573f6e38a939dd05`. Both complete 2,965-file captures matched committed Git blobs, hashes and modes with zero mismatches. These results retain informational Observation claims and closed Trusted eligibility.

The same workflow's coverage lane stopped before numerical gating because package-index metadata lacked the internal Supervision entry. The correction classifies it as bundled, excluded and non-publishable, preserves authored qualification paragraphs outside generated release guidance, regenerates the chooser/readiness docs, and stores restore cache outside the captured source tree.

The subsequent full run at `d950f6e5` reached test execution but ended `ASCOV120`: root protocol fixture inputs were missing, the new core test project lacked the wrapper-required JUnit logger, and ordinary-help verification inherited an earlier host exit code. Its raw merged 86.46% line / 73.47% branch figures are not a gate result. The corrected lane arms the existing authenticated protocol test fixture around the unchanged wrapper; the core project declares the same central logger packages as existing tests; ordinary-help setup models a fresh process while preserving CommandService's original sticky failure behavior. Local source verification passed 1,740 core cases with the real JUnit logger and 10 process-entry cases, zero failures/skips/compiler diagnostics. Fresh complete coverage remains required.

## Claims and recovery

Successful observations remain informational with no gate eligibility. Runtime cutover does not populate the compiled production application table, accept a consumer proof, grant secrets, or enable Trusted PR/release execution. Follow the [consumer acceptance contract](issue779-consumer-acceptance.md) and [protected gate reference](evidencehost-migration.md#protected-gate-and-artifacts) before changing that boundary.

An unsuccessful join, deadline, output ledger, retained file, unmount or account close must keep the original failure. Do not publish a success record or reuse quarantined output or identities. A retry requires a fresh generation after physical containment is independently confirmed. Diagnostic retention cannot upgrade a failed run.
