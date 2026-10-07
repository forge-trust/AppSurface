# Same-host C# native bootstrap

This private validation workflow builds the complete normal CLI and runs it as
both the privileged supervisor and its separate worker. The C# core owns
admission, process identity, budgets, STOP/WAIT, artifacts and final custody.
The [orchestrator](run-native.py) and reviewed shell helpers prepare
inputs and collect test evidence. They do not issue runtime authority.

## Immutable inputs and installation

The normal harness is a sole-parent child of
`e95a4e84bcba7f4974d8b188286b1e8f87139a2b`, on
`codex/issue779-csharp-linux-native-20261007-retry-3`. Its candidate remains
`2993dcfaac1b9b6dfb8adf057191f837876f01fe`, with 2819 exact source files and
captured modes, direct parent `4dd992ec1bc2df8220c73149115c5b478edb0085`.
Primary source is not recaptured by this workflow.

The reviewed destinations are `.github/issue779-csharp-native/` containing
`run-native.py`, `acquire-inbound.sh`, `retain-native.sh`,
`prepare-root-inputs-v2.sh`, `checkpoint-n01-n02-v5.sh`,
`prepare-os-audit-v2.py`, `source-review.json` and this README.
The normal builder remains `.github/issue779-csharp-build/build-fdd.sh`.
The workflow is `.github/workflows/issue779-csharp-linux-native.yml`.

Every script is SHA-pinned by the workflow before interpretation. Each root tooling
operation runs inside a generated root systemd utility unit: Typeexec,
RemainAfterExit=no, KillMode=control-group, and RuntimeMax bounded by its original
phase end minus five seconds. TimeoutStop3 and an independent root GNU timeout
contain both detached utility descendants and the waiting client. A root-created
armed file is removed before stop, denying a queued late start. An independent
OnBoot monotonic timer targets the original absolute phase cutoff, and its actual
NextElapseUSecMonotonic is read as a typed busctl integer and compared before any
payload dispatch. The root entry also checks the retained absolute cutoff and
armed file immediately before executing the selected tool. The selected
utility unit, deadline service and timer must be terminal with no pending job,
and their actual cgroups must be physically empty. This confirmation is attempted
even when root setup fails before the waiting client is created; the original
setup failure remains latched. An unprivileged kill is never a root
containment guarantee. A trusted
Ubuntu24.04 x64 runner, sudo and native OS tooling are prerequisites. The root
bootstrap uses only fixed OS executables with an empty environment. It creates
a fresh root-owned 0700 generation, copies and hashes reviewed bytes, and only
then invokes the selected root scripts. Acquisition and retention are installed
0500; the other reviewed data/code is 0444. Internal self-hashes check continuity
and do not establish trust retroactively.

## Build, copy and execution ordering

1. The unprivileged normal build preserves every CLI dependency, repository
   locks and audit policy. Its original 1200-second interval includes a
   five-second cleanup reserve. All 39 command records must settle with exit0,
   absent groups and zero diagnostics. The V5 receipt and complete TSV/node
   maps must bind all source phases and current outputs. Prior successful build
   hashes are historical evidence; the new run uses its own current hashes.
2. [Acquisition](acquire-inbound.sh) copies complete trees into
   `/opt/appsurface-evidence-inbound-G`. It validates original/copy full node
   membership, mode, byte length, single links and hashes, then verifies root
   ownership. The [V2 transport](prepare-root-inputs-v2.sh)
   receives those root-sealed trees and the **same original 120-second end**.
   It creates `/opt/appsurface-evidence-input-G`, retaining all empty directories.
   The final root is 0711, deployment directories are 0555, source files remain
   captured 0644/0755, tool/runtime files are 0444/0555, and control is 0700/0600.
   Repeated metadata/content samples reject detected drift; they are not an
   atomic snapshot against concurrent trusted writers. The build and reviewed
   inputs must remain frozen. Script/receipt JSON is not an approval issuer.
3. The [OS audit V2](prepare-os-audit-v2.py) runs
   unprivileged against those same-host sealed trees, with one original
   60-second monotonic end and an independent outer process group. It invokes
   fixed readelf/loader-cache tools, not ldd or a supplied payload. The multi-RID
   tool ELF option is omitted. The exact proposed audit is copied exclusively
   into root control and rehashed. Its OS aliases retain literal paths and actual
   same-machine identities; no deployment symlink exception is introduced.
4. [Fixture V5](checkpoint-n01-n02-v5.sh)
   receives all six current maps, current build receipt, source review, actual
   entry SHA, actual nonroot runner UID/GID and same-host audit. It owns one
   cumulative 600-second interval with a 30-second cleanup reserve. Its root
   process is externally contained within 630 seconds. The unchanged owner
   RuntimeMax240/Stop5 and worker policies remain intact. The orchestrator's
   original 900-second end and the workflow's original 35-minute end bracket
   every phase, final copy and terminal publication.

Python CLOCK_MONOTONIC is not treated as identical to Linux CLOCK_BOOTTIME.
Root shell deadlines are conservatively bridged through actual `/proc/uptime`;
the original outer interval still limits each subprocess. No copy/case retry
resets a child interval.

## Fresh fixture selection and private failure evidence

The fixture generates its own random namespace, separate from transport G.
Immediately before dispatch, fixed root OS code verifies that
`/run/appsurface-evidence-fixture` is absent and exclusively writes the root600
dispatch marker. [Retention](retain-native.sh) then accepts only zero or one
root0:0/0700 child under the newly created root0:0/0700 parent. Missing namespace
or case files remain missing facts. No arbitrary directory discovery or cleanup
is allowed. The collector never stops units or deletes accounts.

The fixed whitelist includes the fixture result, raw final three JSON files,
worker live sample, policy/final hashes, selected N01/N02/observer/cleanup logs,
and the two outer fixture logs. Each retained file must be root600, regular,
single-link and unchanged across copying. Per-file limit is 8MiB and total data
limit is 32MiB. The canonical fixed-order USTAR archive uses mode600 and zero
UID/GID/time. It stays root-private until an OS-only bounded read into a
runner-owned 0700 directory and exclusive 0600 file. No raw contents are printed.

The collector's original 30-second interval includes archive reading, hashing
and private copy. The orchestrator checks all member names/types/bounds and root
archive SHA before/after transfer. A nonzero fixture exit, timeout, failed join,
late publication or missing valid case receipt remains a failed native run.
Retention success cannot promote it. Final JSON uses pending publication and an
exclusive late-failure invalidation sidecar; the workflow separately captures
the actual coordinator numeric exit. Both must agree with success, with no late
failure sidecar, before this native checkpoint passes.

## Cases and limits of the result

N02 invokes the real image as the actual nonroot runner and requires rejection
before protected I/O. N01 invokes the real root supervisor and its actual worker,
checks live kernel identity/image/argv, performs the real empty Observation plan,
and verifies STOP/WAIT, physical exit, strict account closure and final custody.
Success requires numeric fixture exit0 plus its exact root-retained result with
N01 exit0, N02 exit1 and joined groups. The typed console mode remains
Observation/ObservationOnly/Informational; Trusted remains disabled.

The 14 other native controls, actual producer/application integrations, Python
cutover, fresh unchanged coverage gate and final draft PR validation remain
required. These two cases alone cannot complete #779.

## Source preparation evidence

The earlier gated partial proposal is preserved outside the installed harness as history.
Seven extracted-validator data controls ran
successfully. They cover absent success failure-field, command settlement,
map drift, duplicate JSON and fixed archive rejection. The prior genuine build
receipt's proposed-parent adaptation is pure parser data, not a new build or
native result. All root shell blocks parsed successfully; no root/native action
has run locally. The review-pending workflow's markers stay false until full
composition review and intentional private integration.

The absolute timer uses [systemd v255 OnBootSec semantics](https://github.com/systemd/systemd/blob/v255/man/systemd.timer.xml). Runtime service properties are not mutated after activation: the [v255 service setter](https://github.com/systemd/systemd/blob/v255/src/core/dbus-service.c) restricts those properties to transient stub setup. These source checks inform the tooling; actual Linux behavior remains to be measured.

Selected-unit queries capture status explicitly. The [v255 show-properties
implementation](https://github.com/systemd/systemd/blob/v255/src/systemctl/systemctl-show.c#L1964-L2001)
returns zero after printing complete inactive/not-found properties; its nonzero
not-found status applies to the separate status/help commands. This tooling
accepts only query status zero plus complete closed facts, and rejects every
nonzero transport or parsing result. No native query status is inferred from
this source check.

The first native workflow attempt built the complete CLI successfully but failed
inside its root tooling bootstrap before candidate dispatch. Its root utility
returned one with empty output; selected units, jobs and cgroups settled. The
inner cause remains unknown. The next diagnostic attempt adds fixed private
`ROOT_TOOL_FAILURE:<stage>:<status>` and
`ROOT_BOOTSTRAP_FAILURE:<stage>:<status>` markers, plus a fixed pre-exec marker.
Stages are assigned only by the reviewed shell source; no paths, arguments,
exception text or payload bytes are echoed. Failure statuses stay nonzero, and
all original clocks, unit policy, guards and cleanup remain unchanged.

## Build descendant completion correction

The prior diagnostic run stopped at a successful restore whose process group
was still present on the first leader-exit probe. The survivor's role was not
retained. The builder now waits for actual group absence within the original
command deadline while continuing all log limits. Persistent children are
killed and joined and the command still fails. No new allowance is introduced.
Three local controls use real unprivileged processes to verify natural child
completion, deadline rejection and forced join, and the no-child case. These
controls do not establish Linux native execution or any checkpoint result.
The root bootstrap diagnostic markers remain reviewed and await execution.

Final log hashing also rechecks the aggregate 64 MiB limit, so bytes written
between the last poll and child exit cannot bypass the total log bound. A
targeted real-file ordering control passed; the three prior process controls
remain historical and were not repeated.

## Literal root shell arguments

Root utility commands use `systemd-run --expand-environment=no` so embedded Bash
source reaches Bash unchanged. The [pinned v255 manual](https://github.com/systemd/systemd/blob/v255/man/systemd-run.xml)
documents the default expansion and this option. In the old source, the manager
could replace Bash array expressions before Bash constructed its guard path.
This source defect is separate from measured native failure attribution. A
portable construction control verifies the actual argv and unchanged shell
bytes before a mocked no-dispatch boundary; it is not a native passing receipt.

## Protected parent on the hosted runner

The separate read-only host probe observed `/opt` as root-owned mode `0777`;
that parent fails the existing prohibition on untrusted write access. It
observed `/`, `/var`, `/var/lib`, and `/run` as root-owned mode `0755` without
links. These are observations from the probe job, not a retroactive measurement
of the failed job. Validation bootstrap, inbound, transport input, and fixture
deployment namespaces now use the fixed `/var/lib` parent. Each use revalidates
the complete `/` -> `/var` -> `/var/lib` chain, without links or group/other
write bits. No host directory permissions are changed. All `/run` custody and
control paths, byte/task/time limits, and case requirements remain unchanged.
Fresh same-host OS auditing and actual N01/N02 execution remain required.
