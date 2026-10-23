# Private C2 normal-worker measurement probe

`EvidencePrivateQualification=C2Measure` selects a separate, nonpackable measurement
image for one genuine normal case from the existing [C2 native driver](../../issue779-checkpoint2/run-native.py).
It answers whether the authenticated worker's private Coverlet hit files reach the
standard collector. The ordinary installed-package consumer and the seven-case C2
completion requirements remain described by the [producer-root measurement reference](producer-roots-README.md).
The [Supervision reference](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md)
defines the original native ownership, physical settlement and acceptance boundary.

## Build selection and isolation

[Directory.Build.props](../../../Directory.Build.props) appends only
`EVIDENCE_PRIVATE_C2_MEASURE` for `C2Measure`, preserving package-owned constants.
The existing validation target accepts that explicit selector, and the existing
non-`None` pack rejection prevents it from being shipped as a production package.
The default remains `None`; mutation `C2` retains its separate mutation symbol.

The [Supervision project](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/ForgeTrust.AppSurface.Evidence.Supervision.csproj)
links the existing [measurement helper](LinuxWorkerMeasurement.cs) into C2Measure.
Only the four existing field, capture, normal-exit and disposal guards in
[LinuxWorkerProcess](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxWorkerProcess.cs)
include the new symbol. Its fixed common async declaration prefix and field
initializer order are preserved.

Ordinary `None` and mutation `C2` contain neither the helper nor its worker call
sites. C2Measure does not define C3/C3Sdk, include generated C3 application
registrations, or change the runtime qualification's default `None` value. The
compiled application table stays empty. The metadata checks in
[QualificationImageBoundaryTests](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/QualificationImageBoundaryTests.cs)
inspect helper definitions and actual emitted worker calls without invoking a native owner.

## Status0 handoff contract

The original worker start path calls `TryCapture` before descriptor publication.
It retains the actual worker's PID/start-time/UID/GID/cgroup binding, original
pidfd, private `/proc/PID/root/tmp` directory FD and distinct host `/tmp` FD.
All original authenticated identity checks remain required.

Only the original joined exit monitor's natural `ExecMainCode == 1` and
`ExecMainStatus == 0` path calls `CaptureNormalExit`, which passes expected status0
to the unchanged `CaptureExit`. The helper independently requires the original
PID, terminal systemd facts and pidfd exit. It reads only the source-bound pinned
tracker files, retaining exact module hashes, candidate counts, byte lengths,
UID/GID, safe modes, single-link and stable-metadata guards.

The original bytes are copied unchanged into an exclusive root-owned host file
within the existing five-second cap and original job token. Equal worker/host
hit hashes are recorded by the existing `evidence-worker-measurement-v1` witness,
with `native_acceptance=false`. V1 does not serialize a separate exit-status
field: its status0 meaning is bound to this source and the original monitor call.
No witness schema, tracker implementation, counter arithmetic or collector merge
is changed. A prior valid root hit file stays unchanged; an unloaded worker
module may remain absent. The handles close after the original monitor joins.

C2Measure enables no SDK exit barrier or failed-natural-exit call. Signal deaths,
nonzero exits and cleanup failures retain their original failure meaning.

## One-case orchestration boundary

The separate private probe controller reuses `execute_case(args, "normal", end, receipt)`
from the existing native driver after its ordinary immutable staging and bootstrap
checks. It does not replace the installed ordinary package in the seven-case lane.
Its own receipt must identify the private C2Measure image and exactly one case;
it cannot claim seven-case completion.

Use a fresh, independently instrumented image with pinned Coverlet Console 10.0.1,
the existing six modules and one isolated standard JSON report. Initially use no
`--merge-with` input and import no report into the current coverage chain. Retain
the actual implementation source capture, original/instrumented image hashes,
sealed worker descriptor, raw bounded owner output and restored original hashes.
The controller must match the actual descriptor to the v1 witness and require
unchanged CLI/Contracts/Coverage worker handoffs. A missing helper, absent required
handoff or prior root hit file is a failed probe, not permission to manufacture,
clear, add or replace counters.

One successful normal-case probe supplies measurement evidence only. It does not
establish ordinary packaged-tool acceptance, negative-case coverage, completion
of C2, a CI coverage-gate pass or native acceptance. Source/metadata preparation
alone establishes none of those runtime results.

## Controller API and bounded failure cleanup

The [private controller](../c2_worker_probe.py) exposes `run` and `target` phases.
Both receive absolute `--tool`, `--runtime`, `--subject`, `--evidence` and `--console`
paths plus the captured `--source` and original C2 `--base` identity. Only `run`
starts the pinned standard collector. It supplies `target` with one original
monotonic `--work-deadline` and `--cleanup-deadline`: 900 seconds of work and a
separate 60-second cleanup reserve. Internal native case, worker, producer and
owner time limits remain unchanged. The
[probe workflow](../../workflows/issue779-c2-worker-probe.yml) selects fixed paths
and the explicit private build; it does not invoke this as package acceptance.

An outer timeout or output limit sends SIGTERM to the collector group first.
The target's cancellation handler raises through the original native procedure's
`finally` blocks, which stop its selected systemd units and join its separate
launcher session. The collector group is joined under the original cleanup
deadline before any final force containment. A cleanup failure remains a failed
probe and cannot become measurement success.

`native-started.json` is written before invoking the native case.
`settle_native(evidence, native, deadline)` then rereads the original
`native/normal.json` generation, requires its completed physical-group check and
independently rechecks only the generated owner, worker and producer cgroups.
Its `native-settlement.json` binds the actual original result bytes.
`retain(root)` requires that settlement whenever native execution started and
rechecks those groups again before applying private directory/file modes.
Missing, incomplete or substituted settlement blocks retention. This data never
converts a failed normal procedure or incomplete account cleanup into a pass.

The evidence includes original and instrumented module hashes, the exact copied
sealed worker descriptor, original bounded owner output, the existing unchanged
hit-handoff witness, the isolated standard JSON report and restoration result.
Only `normal-report.json` may reach 32 MiB; other leaves keep an 8 MiB cap, with
64 files and 48 MiB total. Files are private 0600 and directories 0700. No raw
counter reconstruction, report arithmetic or prior report input is permitted.
