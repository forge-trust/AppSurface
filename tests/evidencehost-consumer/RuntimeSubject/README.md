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
Root account creation and cleanup use the fixed `/usr/sbin/useradd`, `/usr/sbin/groupadd`,
`/usr/sbin/userdel` and `/usr/sbin/groupdel` executables, independently of the sanitized worker PATH.
A host command that cannot spawn records `host-command-start-failed`, its allowlisted operation
and bounded numeric errno; no attempted argv, paths or operating-system exception text is published.

The [runtime driver](../runtime-proof.py) requests this record for Observation only, reads a bounded
protected file through root after failure, validates its closed schema, and publishes only those safe
categories. A missing or invalid diagnostic cannot satisfy the proof. It reserves its driver-owned
`0700` structural-verification directory before protecting the parent, then uses fresh `0600` local
plan/manifest copies after acknowledged exit. Neither diagnostics nor private copies grant authority.
