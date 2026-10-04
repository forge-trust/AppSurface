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
