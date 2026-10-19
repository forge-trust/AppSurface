# Genuine private-worker measurement

This fixture is linked into the [supervision project](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/ForgeTrust.AppSurface.Evidence.Supervision.csproj) only for private C3/C3Sdk images. The ordinary executable contains neither this type nor its calls, checked through actual PE metadata. It is measurement infrastructure, with no admission, process owner, accepted proof or production runtime role.

The pinned Coverlet Console 10.0.1 instruments exactly the CLI, Contracts, Planner, Coverage, Aspire and Supervision modules. The workflow records all original and instrumented DLL/PDB hashes before the original OS owner starts. The entry module is explicitly selected with `--include-test-assembly`; the option does not make the application a test assembly.

Before publishing the worker descriptor, the root holder rechecks its original `LinuxProcessIdentity`, retains a pidfd and the actual worker's `/proc/<pid>/root/tmp` directory, then rechecks the full PID/start-time/UID/GID/cgroup binding. It requires a different directory identity from host `/tmp`. It changes no worker environment, writable path, private namespace, capability, unit limit or artifact inventory.

After the original monitor observes that exact worker's normal exit and the retained pidfd reports exit, the fixture reads the genuine worker tracker files through the retained namespace handle. Exact pinned constructor/field metadata binds the module, GUID, candidate count and `/tmp` name; bounded reads require the original nonroot UID/GID, regular single-link nodes, safe modes, stable metadata, exact header/count/length and nonnegative counters. It copies the original bytes unchanged into an exclusive root-owned host file. The root's own Coverlet tracker performs accumulation on root exit; the standard collector produces the report. No code here manufactures, translates or adds counters.

Only the **first eligible natural worker for each independently instrumented image** contributes raw hit bytes. Normal CLI/SDK controls retain their status0/v1 witnesses. The separate failed-SDK partition requires natural status1/v3 through the same original monitor, captured PID, pidfd and byte guards; it does not clear the worker's failure latch or change its zero-status successful-completion condition. If an earlier root process has already published a validated host hit file for that tracker, it is left unchanged. A module the worker never executed may remain honestly absent; no synthetic zero file is created. Required CLI, Contracts, Coverage and SDK Aspire handoffs must exist.

The separate C3Sdk image is now instrumented by a second invocation of the same pinned collector. The outer collector instruments C3; its target is the SDK collector, whose target is the existing seven-control driver. Both images are instrumented before the driver stages either image, so the matrix still executes once. [`measurement_arguments.py`](../measurement_arguments.py) constructs the closed nested argument vectors for .NET's argument grammar; no shell evaluates them. The SDK collector finishes `native-sdk.json` before the outer collector reads it through its supported `--merge-with` operation. Collector identities and candidate arrays stay separate, and no fixture combines or rewrites raw counters. The [pinned result implementation](https://github.com/coverlet-coverage/coverlet/blob/d21b5b6a08d48f51405ba2c5c5660f91a565776d/src/coverlet.core/Coverage.cs#L295-L308) performs report merging after target execution.

The source binding records separate original/instrumented maps for all twelve DLL/PDB files in each image. The normal SDK control must still prove its own unchanged v1 worker handoffs for CLI, Contracts, Coverage and Aspire. The standard SDK report must contain genuine `EvidenceHostBootstrap` and `EvidenceRestrictedAspireApplication` hits before the merged input reaches the gate. Once the negative report is merged, those report hits are aggregate SDK measurements, not independent attribution to the normal run. The normal run's original handoff witnesses remain separate. An absent SDK file or missing report fails validation; CLI measurements cannot stand in for SDK measurements.

The source-pinned dependency on the [Coverlet tracker and instrumenter](https://github.com/coverlet-coverage/coverlet/tree/d21b5b6a08d48f51405ba2c5c5660f91a565776d/src/coverlet.core) is intentional, not a public raw-hit import API. A separate Linux helper probe verified a child-only method and both branch outcomes using identical worker/host hit hashes before this integration. That helper supplied zero AppSurface coverage credit. This integration still requires its own genuine Linux run; local compilation and data tests do not establish it.

Capture uses the original job token and one additional **five-second maximum**, never a renewed job allowance. It is part of the original retained exit-monitor task, so a failure cannot detach from cleanup. Every measurement handle is closed after that task joins. A private failure remains a failed validation attempt; it cannot upgrade a consumer or issue Trusted eligibility. The source-bound measurement validator checks all seven physical application controls, the actual root filesystem TRX, the six modules, original/instrumented hashes and first-worker handoff hashes before the ordinary collector's supported JSON merge.

The C3 artifact now holds up to six modules per image, so its measurement-only retention allows each fixed `native-supervision.json`/`native-sdk.json` report up to 32 MiB, the combined XML up to 40 MiB, other files up to 8 MiB, and 96 MiB total/128 files. C2 retention and every product output/memory/task quota remain unchanged. The canonical [solution and patch gates](../../../scripts/coverage-solution.sh), comparison base, thresholds and exclusions remain unchanged.

## Separate SDK failed-test partition

Before the original matrix, a separate pinned collector instruments the SDK image for the fixed `test-failure` subject. [`sdk_failure.py`](../sdk_failure.py) stages only that normal SDK application and delegates launch/observation to the existing C3 controller. The C# server rechecks its original application readiness/liveness guard after the genuine test command returns1, before sending the response that permits SDK failure cleanup. This observation is not a new HTTP probe. The real six-test output must report one failed/five passed/no skipped, and its root-private failure state must report natural producer code1/status1 with joined startup, stop, output and empty groups. The manifest must be `Incomplete`, with one `Failed` producer, the existing code1 diagnostic, no artifacts/assertions, no claim/eligibility and completed cleanup. A natural worker code1/status1 with original pidfd and required unchanged tracker handoffs from its separately labelled pre-exit cache is independently required; arbitrary nonzero or signal termination cannot substitute.

The controller's receipt is provisional and outside retained evidence. After the collector writes `native-sdk-failure.json` and restores **all twelve** published originals, the finalizer hashes the completed report, verifies restoration, and publishes one finalized receipt. The following original SDK collector instruments afresh and consumes that report through supported `--merge-with`. No tracker bytes are added, deleted, translated or replaced manually. Original and failure instrumentation maps remain distinct and must share the same published originals. The solution's [measurement consistency check](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/SdkFailureMeasurement.cs) requires the separate report to hit the actual current nonzero-result arm, located from source whose bytes match the full capture.

The new partition has its own finite 900-second outer allowance; the original matrix's 1920-second wrapper, 1800-second clock, case deadlines, and 55-minute job remain unchanged. Accepted work still has to join under its original runtime deadlines. Exactly three extra files are retained: the negative report, finalized receipt and one bounded combined collector/owner diagnostic log. The negative report and log each retain the existing generic **8 MiB** ceiling; the total stays at **128 files / 96 MiB**. Temporary staging and intermediate receipts are not promoted. Diagnostic output is not echoed, and collector/finalizer failures remain failures before the original matrix can consume the report.

The [small-file/argument controls](../test_sdk_failure.py) and [receipt model tests](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/SdkFailureMeasurementTests.cs) verify data handling only. Their constructed observations do not establish native execution, admission, an accepted consumer proof, or measured coverage gain. This new partition requires a fresh complete Linux result.

The producer's unchanged `--verbosity quiet` command does not emit the assertion message. Failure inspection instead requires exactly the reviewed operation-zero failed test row, the one-failed/five-passed summary, and the actual durable `checkpoint2-case-entered` marker containing `test-failure`. The marker is written, flushed, closed and atomically published immediately before that reviewed assertion. Inspection opens its fixed producer scratch components through retained no-follow directory descriptors, after root sealing, and requires a root-owned single-link `0600` scratch file with exact bytes and stable named/retained identities. No verbosity, runtime argument or test assertion is changed to produce evidence.

An optional passive inotify probe in the finite Python fixture observes only notification metadata from the existing worker's private TMP. Three genuine process samples bracket arming. Its lifetime ceilings are eight 32-KiB batches and 2048 events under the original case deadline. The separate private log packet reports only fixed module-prefix/GUID event counts and closed unavailable/overflow/error flags; raw filenames and hit bytes are not exported. Prefix matches are not exact tracker bindings, missing events never prove absence, and the probe cannot recover counters or affect execution/acceptance. This helps distinguish potential systemd private-TMP deletion after nonzero exit from never-created files; neither explanation is inferred solely from an absent terminal file.

## Failed-worker exit cache

The [private root bridge](LinuxWorkerExitBarrier.cs) and
[uninstrumented worker support library](../exit-barrier/README.md) coordinate one
measurement snapshot after the genuine SDK host returns its incomplete manifest.
The whole final `ProcessExit` delegate lives in the support library. It is registered
after the required four modules have executed their actual SDK paths; the pinned
Coverlet delegates therefore flush and close their original files first. Registration
is lazy, so merely loading an assembly is insufficient. Missing required files fails
measurement. Later execution is not attributed as a complete terminal snapshot.

Before descriptor publication the root holder creates a generation-local, root-owned
`0755` slot inside the retained worker private TMP, a root-owned single-link `0444`
plan, and an actual-worker-owned `0700` child. The fixed plan binds the actual PID,
start time, all credentials, cgroup, nonce and original absolute job deadline. The
worker publishes its `0600` READY atomically and waits at most five seconds within
that original deadline. The root's existing monitor services READY while the original
pidfd remains live, rechecks its real kernel identity, and caches the unchanged
source-pinned GUID/header/count bytes through retained descriptors. It publishes a
root-owned readable `0444` ACK atomically only after all required bytes are cached.
The worker then waits for a separate root `0444` RELEASE under the same five-second
clock. Root observes its original pidfd still live after ACK, publishes RELEASE, and
imposes no post-RELEASE liveness condition. This avoids racing a legitimate immediate
exit when proving ACK preceded that exit.
Directory checks permit child-list churn while retaining inode, device, ownership and
mode; file reads require full stable metadata and named/retained identity.

Only the original pidfd exit and retained systemd `ExecMainPID`, code1/status1 permit
exclusive publication of cached bytes to host tracker paths. Standard Coverlet then
performs its own normal accumulation and report merge. The v3 witness explicitly
labels `pre_exit_after_original_handlers`, live capture and acknowledged ordering,
and requires equal worker/cache/host hashes. Existing positive v1 handoffs retain
their original post-exit path. The bridge adds no accepted proof or success status.
Its source preparation and local builds are separate from required fresh Linux
execution; native event observation and cache provenance must be checked before
claiming measurement success.

Private SDK startup also emits a best-effort closed `evidence-worker-measurement-startup-v1`
milestone before/after the original native start, kernel capture, measurement preparation
and descriptor publication. The ten enum labels contain no exception, path or native
success claim; an optional nullable measurement call returning is not proof it captured
anything. Diagnostic I/O failure is discarded, and every original token, startup
allowance, unit property and lifecycle guard remains authoritative. These milestones
locate an early startup cancellation separately from the later exit-cache protocol.


## SDK startup worker diagnostics

The SDK-only controller also retains the original worker's joined diagnostic prefixes
when startup ends before descriptor publication. The C3 controller already returns its
fresh selected `generation`; after that original call returns, the SDK fixture passes
that exact selection and role `worker` to the existing
[C2 strict prefix reader](../../issue779-checkpoint2/run-native.py). It does not infer a
PID, adopt another generation, retry a start, or change the original result. A missing
selection or unavailable/invalid diagnostic source grants no observation.

The three copied leaves (`stderr.prefix`, `stdout.prefix`, `state.json`) stay in the
existing unretained controller working directory under a fresh private child. A small
completion record is written only after all three copies validate; partial copies are
not used. The finalizer reopens the copies with no-follow/nonblocking FDs, exact private
ownership/modes, single-link/regular type and stable named metadata, and verifies the
completion lengths/hashes. Each leaf retains the existing 1 MiB prefix bound. This is
opaque diagnostic data, not a worker exit, signal, native outcome or acceptance proof.

After the collector and original owner writers have joined, the finalizer appends fixed
private worker sections to the existing `sdk-test-failure-diagnostics.log`, **before**
its original collector-exit assertion. This also works for a failed collector with no
provisional success receipt. Stderr is first. The combined log remains at its original
8 MiB ceiling; insufficient room causes explicit diagnostic truncation, or no append
if the marker itself cannot fit. No additional retained evidence files, generic quota,
clock, unit policy, output allowance or product/runtime code changes. Missing or rejected
copies do not prove absence and do not replace the original failure. Raw prefix/state
bytes remain only in this private log, with no public echo of payload or exception text.

The new `WorkerStartupDiagnosticDataTests` class in the
[small-file controls](../test_sdk_failure.py) covers exact selection, invalid/expired
selection, partial copies, one-field/node failures, private-only output, the shared log
ceiling, unsafe destinations and finalization before the original collector failure.
These controls create temporary data only; they establish neither a native worker
failure cause nor a successful runtime measurement. Source29/30 startup cancellation
remains undiagnosed until fresh retained worker bytes are actually inspected.
