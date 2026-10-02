# RuntimeSubject consumer fixture

`RuntimeSubject.csproj` is a small VSTest/xUnit project used by the disposable Ubuntu 24.04
EvidenceHost candidate workflow. The protected production CLI runs this project through its
restricted coverage producer, collects Coverlet data, and emits the required Cobertura report.
The fixture intentionally has no resource-backed dependencies and selects the explicit
`runtime-observation` profile with the `runtime-coverage` producer.

Its single test reads its declared `Program.cs` input and then checks the live Linux execution
boundary: the subject and worker have different UIDs; the protected published CLI and policy,
output anchor, worker descriptor and socket, and worker environment and memory are inaccessible
to the subject. The test uses access/open probes only. It does not write to protected files or
mutate host services, users, cgroups, or other infrastructure.

Run it only through [`verify-evidencehost-linux-runtime.sh`](../../../scripts/verify-evidencehost-linux-runtime.sh)
on the required disposable Ubuntu 24.04, systemd 255, cgroup-v2, x86_64 runner. The script also
builds and runs the independent control-protocol and worker-lifecycle mechanism fixtures before
launching the production CLI worker. Build and process steps have individual deadlines and fail
closed; unsupported hosts are failures, not skips.

The resulting manifest is `ObservationOnly` and informational. Neither this subject nor its
runtime receipt represents protected workflow acceptance or authorizes `Trusted` mode. Trusted
acceptance requires a separately reviewed protected workflow and an accepted consumer proof.

## Exact subject input staging

### Protected runtime workspace

The [runtime driver's workspace helpers](../runtime-proof.py) create a fresh
`/run/appsurface-evidencehost-runtime-<32 lowercase hexadecimal UUID>` through a fixed root command.
There is no caller-selected root path. The helper opens `/run` without following a link, requires
its root ownership and absence of group/other write permissions, and exclusively creates the child;
an existing name fails instead of being reused. It hands only that new `0700` directory to the
nonroot driver's exact UID/GID for build, publish, six-file projection and private cache preparation.

Before arming the launcher, the driver freezes the same directory by device/inode through a
no-follow directory descriptor, changes its owner/group to root and its mode to `0755`, and verifies
the result. This happens before protecting the tool/output children, preventing replacement of
their names after the freeze. Tool-tree worker read permissions and worker-owned `0700` output
anchors remain enforced by the launcher. Cache/home and reserved structural-copy directories keep
their private modes. The prepared workspace is retained on both outcomes on the disposable runner;
the driver performs no privileged recursive cleanup and never adopts an earlier workspace.

This places control-visible tool/output paths outside the private `/tmp` and `/var/tmp` namespaces.
`PrivateTmp=yes`, strict filesystem protection, mount/link guards and all supervisor markers remain
unchanged. The source revision, six input hashes and all six zero-warning build/publish logs retain
their existing bindings. Portable workspace controls verify the fixed root operation and caller
sequence with mocked root ownership; a subsequent native run must establish actual Observation.

### Declared fixture projection

The [runtime driver](../runtime-proof.py) projects an explicit six-file input set into its fresh
`subject-source` directory: this fixture's `RuntimeSubject.csproj`, `Program.cs` and `packages.lock.json`,
plus the repository-root `Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props`.
Every copied regular-file byte must match the original candidate source hash, within the aggregate
20 MiB staging bound. Required-file/ancestor links, missing or special files, undeclared ancestor
build inputs and explicit imports/project references reject staging. This candidate has no `global.json`
or NuGet config; introducing one requires an explicit staging-contract update.

The original Git revision and candidate source bindings remain unchanged. The launcher receives the
staged subject root with the original project/path layout. Its existing link, depth, count and byte
copy guards remain active. No `bin`, `obj`, `node_modules`, Git metadata or unrelated projects are staged.
This is the small dependency-free runtime fixture scope; pinned candidate tooling and its web-asset
preparation still execute through the existing workflow prerequisites. It is not whole-consumer execution.

The final runtime record adds `subjectInputScope: declared-runtime-fixture`, `stagedSubjectFilesSha256`
and its canonical `stagedSubjectManifestSha256`. The prepared input tree and original candidate source
hashes are rechecked after execution; these records grant no provenance or gate authority.

The candidate driver requires the Trusted missing-proof control to exit 1 with the exact safe
`ASEVD407` diagnostic, no stdout, and no allocated output anchor. An unrelated launcher failure
does not satisfy that control. After successful Observation and acknowledged exit, the driver
collects bounded artifacts from the protected root, checks the coverage bytes against their
manifest metadata, and runs the published CLI's `evidence verify` command against the collected
plan and manifest. That command recomputes structural bindings and re-resolves the policy;
it grants no provenance or gate authority. The portable rejection regression checks are
[`test_runtime_proof.py`](../test_runtime_proof.py).

The [launcher's subject-command pumps](../../../scripts/evidencehost-linux-launcher.py) separately
acknowledge stdout and stderr only after actual binary EOF without a read/count/stop error. Each
pump locks its EOF, failure and received-byte state; joining a terminated thread is insufficient.
Before returning a command response or registering its results root, the broker requires both
pump acknowledgements and matches their received-byte sum to the command budget and job quota
increase. Retained prefixes may be truncated, but discarded bytes remain counted. A failed or
unacknowledged pump permanently closes the lease, stops its owned units and denies owned-exit
acknowledgement and artifact access; it cannot produce final successful output. Normal EOF after
a quota stop retains the `ASEVD420` response. The [portable launcher controls](../test_linux_launcher.py)
exercise read exceptions in either pipe, short reads, prefix truncation and accounting mismatch.

## Provisional failure diagnostics

The [root launcher](../../../scripts/evidencehost-linux-launcher.py) accepts the internal optional
`--diagnostic-directory` argument. It requires an existing absolute, non-symlink, root-owned directory
with no group or other write permission. On failure it exclusively creates `launcher-failure.json`
with no-follow semantics and mode `0600`; an occupied filename rejects before launch. Without the
option no diagnostic file is written, and public stderr retains its generic failure or exact
Trusted `ASEVD407` response. The record contains only fixed host cause/error categories and optional
allowlisted operation/numeric exit or errno fields, never exception text, subject output or descriptors.
Worker protocol/unsuccessful-exit failures additionally retain bounded numeric `ExecMainCode` and
`ExecMainStatus` as `worker_main_code`/`worker_main_status` under the fixed `worker-exit` operation.
For `worker-protocol-incomplete`, the record also snapshots the broker under its condition lock:
`broker_ready_seen`, `broker_wait_completed`, `broker_exited` and `broker_work_closed` are exact
booleans; `broker_active_handlers` is an integer from 0 through 4096 and `broker_active_runs` from
0 through 1. These observed flags locate a failed protocol checkpoint; they do not certify that
a response was received or satisfy the [root completion contract](../../../scripts/evidencehost-linux-launcher.py).

With the diagnostic option, only this launch's internally generated worker unit is queried through
`/usr/bin/journalctl`. Collection and process reaping share a five-second deadline; only the first
4096 stdout bytes are read, and journal-command stderr is discarded. A full prefix is conservatively
classified as truncated. The raw prefix, which may contain hostile or sensitive text, is written
only to the fixed, exclusive, no-follow `launcher-worker-journal.log` with root ownership and mode
`0600` beneath the pinned protected diagnostic directory. Raw bytes must remain private and never
enter safe JSON or public console/log text. The driver can retain the bounded prefix only through
the separate private archive described below. An occupied filename or symlink is preserved, not replaced.

The safe JSON adds optional `worker_journal_state` (`collected`, `missing`, `unavailable`, `truncated`
or `read-error`), exact boolean `worker_journal_written`, integer `worker_journal_bytes` from 0 through
4096, and `worker_journal_codes`. That list contains only distinct explicitly allowlisted tokens
followed by a colon: `ASEVD211`, `ASEVD401` through `ASEVD411`, `ASEVD420` and `ASEVD421`. Unknown
codes, arbitrary journal strings, paths, commands, exception bytes and raw output never enter the
safe JSON. Even an allowlisted token remains untrusted diagnostic data and cannot replace the host
failure cause, numeric exit status, owned-exit acknowledgement or gate decision. Missing journals,
read failures and private write failures retain the original failure. Without the diagnostic option
there is no journal query or private journal file. Importing root parents may pass an already pinned
protected `diagnostic_directory_fd` keyword to `launch` or `launch_with_completion`; this optional
failure capture does not change their Path/completion results or completion checks.

If the initial worker `systemd-run` command exits nonzero, the launcher queries only that generated
worker unit, with a five-second deadline and a 4 KiB response limit. Under `worker-start`, it retains
the original command exit code, bounded numeric main-process codes and closed `worker_load_state` /
`worker_result` categories. Fixed causes distinguish an absent unit, rejected unit configuration,
unit failure, command failure with a successful unit result, and unavailable status. An absent unit
does not prove that no process ever started. Status-query failure retains a safe failure, and no raw
command stderr or arbitrary property text is published. These categories never replace owned exit
acknowledgement or qualify a failed run as a successful proof.

The v6 artifact confirms a loaded worker unit exiting with numeric status `226` (`EXIT_NAMESPACE`),
which systemd defines as mount/UTS/IPC namespace setup failure. It does not identify the exact path.
The former `/tmp` tool/output paths conflict with private temporary visibility: systemd mounts private `/tmp` before
applying restrictions to nested paths, as defined by the [systemd 255 documentation](https://github.com/systemd/systemd/blob/v255/man/systemd.exec.xml)
and [namespace implementation](https://github.com/systemd/systemd/blob/v255/src/core/namespace.c).
The `/run` workspace addresses that documented visibility conflict while preserving isolation.
The next actual run must confirm startup and Observation; portable controls do not prove the live
namespace behavior or that relocation resolves every possible namespace setup failure.

Root account creation and cleanup use the fixed `/usr/sbin/useradd`, `/usr/sbin/groupadd`,
`/usr/sbin/userdel` and `/usr/sbin/groupdel` executables, independently of the sanitized worker PATH.
A host command that cannot spawn records `host-command-start-failed`, its allowlisted operation
and bounded numeric errno; no attempted argv, paths or operating-system exception text is published.

The [runtime driver](../runtime-proof.py) requests this record for Observation only, reads a bounded
protected file through root after failure, validates its closed schema, and publishes only those safe
categories. A missing or invalid diagnostic cannot satisfy the proof. It reserves its driver-owned
`0700` structural-verification directory before protecting the parent, then uses fresh `0600` local
plan/manifest copies after acknowledged exit. Neither diagnostics nor private copies grant authority.

### Private journal retention on a failed disposable run

The [driver's `retain_private_worker_journal` helper](../runtime-proof.py) retains diagnostics after
the root launcher exits unsuccessfully. Its isolated root command accepts only the generated `/run`
workspace UUID and pinned device/inode; it accepts no arbitrary root path or file selector. It opens
the fixed `launcher-worker-journal.log` through no-follow directory/file descriptors and requires a
regular root-owned/root-group `0600` file, one link and at most 4096 bytes. It checks the named and
held identity, ownership, permissions, length and timestamps before/after reading, and rechecks the
workspace binding before returning bytes. Missing, linked, nonprivate, unowned or changed inputs fail
without printing exception text or journal data.

The root helper has a ten-second process deadline and emits one canonical USTAR archive, at most
10240 bytes, with exactly the fixed journal member and root `0600` metadata. The driver validates
that complete archive before exclusively creating `proof_directory/private-diagnostics/worker-journal.tar`
as its own `0600` file under a fresh, no-follow `0700` directory. It checks destination identity and
one-link ownership after writing. The existing candidate artifact path retains this private binary
archive; raw contents must not be rendered into public diagnostics or treated as proof authority.
The root-helper ownership overrides exist only for portable file controls; production always requires root.

Retention is optional failure diagnosis. Missing/invalid journals, root-helper errors and local copy
failures preserve the original launcher exit, safe cause, numeric status and checkpoint JSON. They
never make execution pass. Success performs no private diagnostic collection. The [portable driver
controls](../test_runtime_proof.py) verify fixed-name selection, file/link/mode/ownership/byte limits,
identity drift, private destination permissions, canary isolation and original-failure preservation.
