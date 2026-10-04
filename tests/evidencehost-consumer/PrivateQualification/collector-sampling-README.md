# Private collector startup sampling

[`CollectorStartupSampler`](../../../scripts/evidencehost_private_collector_sample.py)
is a diagnostic data reader for the [private qualification controller](README.md).
It does not start a process, query a unit, signal a group, open a socket, create a
thread, write a file or change an execution result. Process roles and counters
confer no admission, readiness, owned-exit, coverage or qualification authority.

## API and ordering

The constructor has seven positional arguments:

```python
CollectorStartupSampler(unit, cgroup_path, subject_uid, subject_gid,
                        dotnet_host, sdk_root, job_deadline)
```

`unit` is exactly `evidencehost-<12 lowercase hex>-s-<0..127>.service`, with no
leading zeros in the decimal suffix. `cgroup_path` is its exact
`/system.slice/<unit>` path. UID/GID are positive integers. Host and SDK are
canonical absolute strings with `dotnet_host == sdk_root + '/dotnet'`.
`job_deadline` is a finite absolute monotonic deadline. The root launcher must
authenticate the selection and unit properties before calling `observe()`.

Keyword arguments `expected_root_uid=0`, `expected_root_gid=0`,
`proc_root='/proc'` and `cgroup_root='/sys/fs/cgroup'` select default root-owned
kernel data. Overrides allow ordinary owned portable fixture data and establish
no root origin or capability. They are not runtime authority switches.

Call `observe()` synchronously when the existing parent loop permits it. It
returns `None`, including on unavailable or rejected observations. A due call
has at most 100 ms within the original job deadline. Calls are separated by at
least 330 ms. The active five-second window starts only after an eligible
`datacollector` role is observed. At most 16 PIDs and 16 samples are retained.
All opened file descriptors close before each call returns; none persists in
the sampler. No new timer or execution deadline is granted.

Call `finish()` once. It returns canonical bounded bytes or `None` and prevents
further observations. If a later observation fails, prior valid samples remain
available with `capture_incomplete=true`, provided the original job deadline
still permits finishing. No sample, no collector trigger, invalid selection or
an expired job deadline yields no record. Missing data is unknown, not success.
The parent alone binds the selected unit/UID/GID and may write these bytes after
its existing owned-exit and idle guards. The
[private archive rules](retained-output-README.md#private-subject-collector-startup-observations)
retain only the two fixed entry names under the original archive limits.

## Closed numeric record

The record uses `issue779-private-collector-startup-v1` with exact top-level fields:
`schema`, `unit`, `cgroup`, `subject_uid`, `subject_gid`, `collector_observed`,
`capture_incomplete`, `sample_count`, `samples`, `network_observed` and `authority`.
The last two are always `false`. Each sample contains `elapsed_ms`, `processes`,
`unavailable_pids` and `counters`. Process fields are `pid`, `start_time`, four-entry
`uid` and `gid` lists, `groups` and `role`. Roles are closed to `dotnet`,
`vstest.console`, `datacollector`, `testhost` and `unknown`; a filename is never
process authentication or evidence that a connection completed.

Eligibility checks the owned process directory, repeated start time/status/
cgroup/command metadata and executable link. Role matching uses the exact selected
host and fixed `sdk/10.0.401/{vstest.console,datacollector,testhost}.dll` names.
Command text, comm strings, executable paths, environments and native exception
text are not exported. This does not establish the existence of a network peer.

Counters are `pids_current`, `pids_max`, `pids_events`, `memory_current`,
`memory_max` and `memory_events`. Limit records contain `value` and `unbounded`:
kernel `max` becomes `{value: null, unbounded: true}`. Events preserve missing
keys as `null`; malformed, duplicate or unknown counter keys make that counter
unavailable. Missing files, unsafe links/modes, read failures and expired reads
also mean `null`, never an inferred zero. Actual zero is retained as zero.
`validate_snapshot(bytes)` verifies bounded canonical JSON, exact fields and
types, limits and internal consistency. It neither authenticates the producer
nor replaces the parent's independent selection binding.

## Data controls and limits

[`test_private_collector_sample.py`](../test_private_collector_sample.py) defines
small real-file-descriptor fixtures with an ordinary cgroup-shaped tree and
proc-shaped status/stat/cgroup/cmdline files. Executable links point to an owned
fixture named `dotnet`; nothing is executed. Fake monotonic values advance only
at deterministic read boundaries. The controls cover identity drift, FD closure,
counter uncertainty, unsafe file shapes, limits, late incomplete capture and
canonical schema rejection without threads or timing sleeps.

The initial source handoff defined these controls without executing them. Local
validation later passed 18 sampler controls, six launcher orchestration controls
and 16 archive controls. The archive controls used an explicit Python 3.14 retry
after an unqualified interpreter rejected `dataclass(slots=True)` at import,
before test discovery;
the other 24 controls were not repeated. These ordinary owned fixtures establish
no kernel cgroup/process fact, root supervisor behavior, network connection,
systemd compatibility, accepted proof, protected positive or coverage result.
The [qualification procedure](README.md) retains the original failure even when
this optional diagnostic record is missing or cannot be retained.

## Stable directory identity and partial capture

The sampler pins each directory by device, inode, mode, UID and GID. Child counts,
size and timestamps may change while the same directory remains selected. File
identity and process UID, GID, start time and cgroup are still checked before and
after reads. Replacing a directory or changing its access metadata rejects that
observation.

If a later observation fails or would exceed the 64 KiB snapshot limit, previously
validated samples remain available with `capture_incomplete=true`. The oversized
last sample is discarded. This flag describes diagnostic availability; it is
never execution, readiness, owned-exit or admission authority. The initial source
handoff defined the data controls without executing them; later results belong in
the parent's bounded validation receipt.


## Private managed parallelism hypothesis

The retained V15 collector observations contain 15 samples over approximately
4.8 seconds. They reached 64 tasks with `pids.events.max=1`, while observed
memory was approximately 285–288 MB with no memory events recorded. These bounded
observations do not establish the cause of the collector startup timeout or
prove the absence of an event outside the observation window.

The retained V16 diagnosis observed a collector connection marker followed by a
`Process.Start` testhost launch failure reported as `Win32Exception`, native
error 11 (`EAGAIN`). Its one startup sample, at elapsed 0 ms before the failed
launch, recorded `pids.current=64` and `pids.events.max=0`. It contains no
post-failure limit-event snapshot. These facts do not identify the exact failing
native syscall, prove an exclusive cgroup cause or measure the actual processor
count. The connection marker does not establish a completed subject test run.
The local-only diagnosis ledger is
`TestResults/issue779-recovery-20261003/current-private-qualification-v16/artifact-diagnosis/diagnosis.json`
(SHA-256 `104c6a01a2ae972ca7b4e9dfdcdb2185b1986c0c499e5af3531cf995c1a2d6f1`).

The private root-selected subject command now places exactly
`DOTNET_PROCESSOR_COUNT=1` in the cleared `env -i` environment before the selected
.NET executable. The [.NET processor-count setting](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/environment-processorcount-on-windows#recommended-action)
fixes the runtime-reported processor count for components that use that value to
size parallel work. It does not cap the total number of threads. Its effect on
this private workload remains a hypothesis to measure within the same task ceiling.
The literal follows the launcher's environment defaults, so a caller or host
value cannot override it. It is not a public option, admission selector or proof.

The setting applies only to root-selected subject commands. Global launcher,
worker and application environments are unchanged. `TasksMax=64`, `MemoryMax=1G`,
functional command arguments, quotas, lifecycle checks and timeouts remain intact.
Two new [emitted-argv controls](../test_linux_launcher.py) use the existing
`SubjectUnitCompletionTests.exercise` procedure and inspect the actual `_run`
Popen call through its existing process double. The historical CPU2 version of
these two controls passed in the preserved local validation receipt, SHA-256
`62fed84faef10976b8fe0a430476d0c1eca77bda7baf527fa0c9953ab7ac8771`.
That receipt remains the CPU2 result. At the CPU1 source handoff, the two adapted
controls had not been executed; later results belong in the separately bound
validation receipt. No cases were added. This next private
hypothesis establishes no native success, qualification or coverage result.


## Private runtime diagnostic IPC hypothesis

V17 (run `37199204578`, harness `3689e61c740042b5ad2d61402d80732ddc17e233`,
source `197fe53eb891f6839637390b0f8e966dc38d8cbc`) observed collector connection
and testhost startup. The retained host trace then reported `OutOfMemoryException`
on `Thread.StartCore` through `PortableThreadPool.RegisterWaitForSingleObject`,
`ProcessHelper.SetExitCallback` and `TestHost.SetParentProcessExitCallback`.
That exception and stack do not independently establish memory-limit exhaustion
or reproduce the exact native failure.

The three retained sampler rows, at 0, 341 and 695 ms, recorded `pids.current`
values 52, 53 and 64 and `pids.events.max` values 0, 0 and 2. Memory-current
values were 257,196,032, 273,854,464 and 285,421,568 bytes; recorded memory events
were all zero. These bounded observations support investigating task pressure,
without proving exclusive causality or the absence of later memory events.
Qualification still failed in the CLI, with empty entries and `None` claim/
eligibility; the public Host entry did not run.

The next private root-selected subject environment fixes
`DOTNET_EnableDiagnostics_IPC=0` alongside `DOTNET_PROCESSOR_COUNT=1`, after the
launcher's defaults in the cleared `env -i` invocation. The official
[.NET diagnostic IPC setting](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables#dotnet_enablediagnostics_ipc)
is supported in .NET 8 and later: value `0` disables the runtime diagnostic port
and cannot be overridden by the other diagnostic settings. This is an optional
runtime diagnostic-port workload-reduction hypothesis. It does not demonstrate
any saved thread count, successful native execution, qualification or coverage.

Only the selected subject command receives this literal. Worker/application
environments, UID/GID and account selection, cgroup guards, physical exit/watch/
join checks, `TasksMax=64`, `MemoryMax=1G`, the existing 90-second timeout and all
functional arguments remain unchanged. VSTest `--diag` logging and the collector's
functional TCP settings are unchanged by this environment-map addition. No
capability or admission setting is introduced, and there is no public option.

The same two [emitted-argv controls](../test_linux_launcher.py) are adapted to
check one fixed IPC assignment before dotnet and reject inherited IPC value `1`
while retaining the CPU1 and original limit assertions. No cases were added.
Their earlier CPU handoffs and results are historical; this IPC source handoff
has not executed the two adapted controls. Later results require a separately
bound validation receipt, not an upgrade of the prior CPU-only evidence.
