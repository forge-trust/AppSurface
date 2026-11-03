# Optional private SDK worker exit barrier

This directory supplies the private measurement rendezvous wired into the
[C3 workflow](../../workflows/issue779-checkpoint3-application.yml) and
[root bridge](../measurement/LinuxWorkerExitBarrier.cs). Its initial source-only
handoff was followed by successful local locked restore, formatting and combined
SDK compilation without compiler diagnostics. Genuine Linux byte handoff and
ordering verification remain required. This changes neither execution
success criteria nor admission, worker policy, PrivateTmp, quota or the original
external stop/job bounds.

## Build boundary and worker entry

Build [WorkerExitBarrier.csproj](WorkerExitBarrier.csproj) independently. It has only
BCL dependencies, no product/project/package references, and is explicitly neither
a test project nor packable. Its [empty net10.0 lock](packages.lock.json) is copied
byte-for-byte from the [existing dependency-free NativeHttpResource fixture](../../issue779-checkpoint3/payload/NativeHttpResource/packages.lock.json) so the
repository's mandatory locked restore remains enabled; no audit setting is disabled.
Its assembly name is `ForgeTrust.AppSurface.Evidence.ExitBarrier.Support`, outside
all seven existing measurement module names. Keep it **uninstrumented**: the complete
ProcessExit delegate, polling, native reads/writes, errors and descriptor disposal
execute in this support assembly. It must not call instrumented product code at exit.

The [CLI project](../../../Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj)
adds a static assembly reference only when both conditions hold:

- `EvidencePrivateQualification=C3Sdk`;
- `EvidenceSdkExitBarrierAssembly` names an already built support DLL.

That explicit build property enables `EVIDENCE_PRIVATE_SDK_EXIT_BARRIER`; an invalid
selected path fails reference resolution. This is a build input, not an environment
or worker argument. There is no ProjectReference or change to the ordinary NuGet
graph/lock files. None, C3, and C3Sdk without the property retain no helper reference.
Use fresh build outputs when switching image variants.

[`EvidenceWorkerCommand`](../../../Cli/ForgeTrust.AppSurface.Cli/EvidenceWorkerCommand.cs)
calls the sole public API `WorkerExitBarrier.Arm()` only in that opted-in C3Sdk image,
after the genuine `host.RunAsync` returns a manifest with `ClaimKind.None` and before
throwing the same `ASEVD211` exception. There is no CLI lambda, product callback,
second supervisor connection, supplied context or new public product API. `Arm()`
returns no owner, admission, counter data or success. A missing/malformed plan makes
the measurement rendezvous unavailable and does not replace the original failure.

## Exact root plan contract

Before descriptor publication, root exclusively creates the following objects in
the actual retained worker-private `/tmp`:

| Object | Ownership and mode |
| --- | --- |
| `appsurface-sdk-exit-<decimal actual PID>/` | root/root, exact `0755` directory |
| `plan.json` | root/root, exact `0444`, regular, single link, 1–2048 bytes |
| `worker/` | actual worker UID/GID, exact `0700` directory |

The plan has **exactly eight case-sensitive fields**, with duplicate and unknown
fields rejected:

| Field | Contract |
| --- | --- |
| `schema` | `evidence-sdk-exit-plan-v1` |
| `nonce` | fresh root-generated 32-character lowercase hexadecimal string |
| `pid` | positive Int32, equal to the actual worker/self PID |
| `uid` | positive UInt32 below the sentinel, equal to all four actual UID fields |
| `gid` | positive UInt32 below the sentinel, equal to all four actual GID fields |
| `start_time` | positive UInt64, actual `/proc/PID/stat` start-time ticks |
| `cgroup` | exact `/system.slice/appsurface-evidence-worker-<32 lowercase hex>.service` |
| `deadline_utc` | original `input.Request.JobDeadlineUtc`, formatted with invariant `O` at UTC |

Use `deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)` on the
root side. The helper also accepts canonical seven-fraction-digit UTC `Z` roundtrip
text if accepted by the runtime's `O` parser; `+00:00` is the defined interoperable
encoding. Nonzero offsets, durations, renewed deadlines and noncanonical text reject.
No directory device fields are part of this schema.

Root must publish a fully written and sealed plan before the SDK worker can read
its descriptor. A different PID cannot select a different path: the helper derives
the fixed directory name only from its own `Environment.ProcessId`. The plan is
root-selected measurement data; it supplies no product privilege or execution authority.

## Arming and original bounds

`Arm()` permits one attempt per process. It opens `/tmp`, the fixed slot, plan and
worker child through no-follow retained descriptors, checks exact file metadata,
and compares stable named/retained file identity. Directories bind device, inode,
UID, GID and full mode while permitting expected child-list size/timestamp changes.
The slot cannot be replaced by the worker because its parent/name and root ownership
are rechecked. All UID/GID values, PID/Tgid, cgroup and two bracketing start-time
samples must match the actual self proc records. This is Linux x64 mechanism code;
it performs no managed-ELF assumption or executable-text/reflection check.

`ready`, `ready.pending`, `ack`, `ack.pending`, `release` and `release.pending`
must all be absent at arm time.
Only native ENOENT is treated as absence. Symlinks, foreign modes/owners, hard-linked
files, malformed JSON, stale process identity or unavailable syscalls fail closed
for measurement. No existing file is adopted, overwritten or repaired.

The monotonic window starts when arming begins, is capped at five seconds and the
remaining original UTC interval, and is never renewed by ProcessExit or polling.
UTC is checked as an additional cap. The root's existing job token/live identity
checks remain authoritative for the original monotonic job deadline. The helper
does not claim to recover the host's inaccessible monotonic `JobRemaining` from UTC.
There is no cancellation parameter: original root containment/stop remains external
and cannot be extended by this helper.

## READY, root caching, ACK and RELEASE

The named support-assembly ProcessExit handler owns every operation below:

1. Recheck the remaining window, actual self identity and retained/named directories.
2. Exclusively create `worker/ready.pending` with actual worker ownership and `0600`.
3. Write exactly the nonce's 32 ASCII bytes, fsync, verify metadata/length, and close.
4. Atomically publish `worker/ready` with `renameat2(RENAME_NOREPLACE)`; no fallback.
5. Wait for root-level `ack`, then root-level `release`, within the same original
   nonrenewing window. Neither publication creates a new clock or allowance.

The root side must accept only a complete stable worker-owned `0600`, regular,
single-link READY with the exact nonce, while its original retained pidfd is live
and all four UID/GID samples still match. It caches the required original tracker
files using exact module/GUID/header/count/length/ownership checks. The required SDK set is CLI, Contracts, Coverage and Aspire, matching the [direct SDK host entry](../../../Cli/ForgeTrust.AppSurface.Cli/EvidenceWorkerCommand.cs). All seven instrumented image plans remain bound; an unused Evidence.Cli tracker can remain absent without fabricating a file or execution credit. If it has emitted a handoff, its bytes retain every existing equality check. It then writes
`ack.pending` exclusively, flushes, seals it root/root `0444`, and atomically renames
it to `ack` with no replacement. Partial publication is not a valid acknowledgement.
After ACK publication, root must observe its original retained pidfd still live under
the job token. Only then may it exclusively write `release.pending` as `0600`, flush,
seal it root/root `0444`, and atomically rename it without replacement to `release`.
This establishes that ACK preceded the worker exit. No post-RELEASE liveness check
is required; the worker may naturally exit immediately after reading RELEASE.

The helper accepts each of ACK and RELEASE only as root/root `0444`, regular,
single-link, exactly 32 matching bytes, with stable opened/named metadata. ACK means
the measurement read returned; RELEASE permits the delegate to return after root
observed the original process still live after ACK. Neither means execution success,
admission or accepted proof. Root may
publish cached bytes unchanged only after the original pidfd reports exit and
actual systemd code1/status1 is established. Root-file exclusivity and the existing
first-worker/prior-root-file rules remain mandatory.

ProcessExit ordering versus Coverlet/AssemblyLoadContext callbacks is **not proven
by this preparation**. A Linux run must establish that the required completed hit
files are available when READY is serviced. No reflection, forced tracker flush,
counter translation, invented file, partial module handoff or early cached-byte
publication can compensate for an unavailable phase.

## Failure and validation obligations

The handler catches its errors, attempts all retained descriptor closes, and returns.
It never sets `Environment.ExitCode`, calls `Exit`/`FailFast`, emits exception/path
text, launches a process, reconnects a supervisor, or calls an instrumented delegate.
The worker's original incomplete-execution exception and natural status1 remain.
Pending files may remain for root diagnosis; the helper does not delete or rewrite
tracker files or change product cleanup.

The focused [ordinary-image metadata test](../../../Cli/ForgeTrust.AppSurface.Cli.Tests/WorkerExitBarrierImageBoundaryTests.cs)
runs for non-C3Sdk builds: the CLI must contain no helper type, support assembly
reference or support namespace type reference. The ordinary-image check is validated
separately from native ordering and counter custody.

Parent validation must additionally cover:

- enabled C3Sdk static reference and exactly the intended `Arm` call; disabled
  C3Sdk/None/C3 absence, unchanged ordinary graph, and nonpackable helper;
- the full successful root-owned plan/READY/ACK/RELEASE flow with exact nonce and original
  code1/status1, plus post-ACK live observation before RELEASE, natural immediate exit after
  RELEASE, and callback completion before root publishes cached bytes;
- each malformed/duplicate/foreign field, wrong owner/mode/type, hard link/symlink,
  stale PID/start time/UID4/GID4/cgroup and preexisting READY/ACK/RELEASE/pending rejection;
- atomic publication, foreign/malformed ACK or RELEASE, either missing publication,
  deadline already expired,
  exit delayed after Arm, backward UTC movement, and one-attempt/one-handler behavior;
- partial acquisition, write/fsync/rename/read/close failures, every remaining FD
  close attempt, and no change to the original process result;
- exact unchanged tracker-byte validation and a genuine fresh Linux SDK negative
  run. Small-file data controls and local compilation cannot establish that result.

The workflow and negative finalizer bind the support DLL/PDB hashes before and after
standard instrumentation, staged deployment and collector restoration. These files
remain outside the seven fixed instrumentation modules. Their exact source remains in
the full source capture; no new coverage exclusion or public package dependency is
introduced.
