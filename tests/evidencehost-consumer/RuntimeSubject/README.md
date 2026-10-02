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

The candidate driver requires the Trusted missing-proof control to exit 1 with the exact safe
`ASEVD407` diagnostic, no stdout, and no allocated output anchor. An unrelated launcher failure
does not satisfy that control. After successful Observation and acknowledged exit, the driver
collects bounded artifacts from the protected root, checks the coverage bytes against their
manifest metadata, and runs the published CLI's `evidence verify` command against the collected
plan and manifest. That command recomputes structural bindings and re-resolves the policy;
it grants no provenance or gate authority. The portable rejection regression checks are
[`test_runtime_proof.py`](../test_runtime_proof.py).
