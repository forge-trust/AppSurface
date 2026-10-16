# C# checkpoint-two Linux validation candidate

This private harness prepares and observes the actual same-image C# supervisor and worker. Python supplies no production broker, process lease, artifact authority, admission or coverage result. The C# root owns account creation, subject SDK execution, unit/pump joins, report custody, final filesystem sealing and account release.

The candidate workflow builds the ordinary `None` source graph on Ubuntu 24.04 with SDK 10.0.401 and locked dependencies, then runs the actual Linux Supervision and focused coverage source tests. It acquires only the fourteen test packages pinned by the subject lock, comparing each package's signed-aware NuGet content hash against the lock before bounded extraction. It performs no subject MSBuild, discovery or test operation during preparation. The first subject evaluation/build/test happens under the actual nonroot producer unit.

The fixture stages one root-owned read-only SDK and tool deployment, including packaged ReportGenerator 5.5.10. It copies regular nonempty files into fresh nodes, rejects links, bounds each tree to 8192 files/1 GiB and each file to 256 MiB, and records the full hashes/modes. Empty packaging markers are omitted, so this candidate must demonstrate that its staged SDK/package inputs actually work; their presence is not assumed. The subject source is independently copied per case and sealed before the root-selected request. A fresh generation guard contains `<32hex>\n`, mode0600 under root0700. The `env -i` owner command and service environment match the production activation contract, with external RuntimeMax500s/stop10s and root FD capacity65536 for retained audits. Worker and subject limits remain unchanged.

Six ordinary-image controls are proposed: actual calculation coverage, actual test failure, caller cancellation while the producer is running, process-output quota exhaustion, a live descendant that ignores TERM while holding inherited output, and unsafe scratch causing strict cleanup failure. The normal case requires root exit0, the actual Passed Observation summary, nonempty real line and branch coverage meeting 95/85 with the existing0.5 tolerance, root-owned merged report bytes matching the actual manifest, all owned cgroups empty, and every generated NSS name and numeric ID absent. Ordinary negative cases require nonzero original root exit, no Passed manifest, physical containment and account cleanup. For the explicit cleanup-failure control only, a passing procedure followed by retained unsafe scratch and still-reserved original NSS identities proves quarantine; it must have no successful root receipt. That quarantine is a required negative result, never successful cleanup. The driver runs it last and leaves the isolated runner to destroy retained state. Fixture cleanup cannot turn an unexpected original failure positive.

The [subject controls](subject/README.md) publish their bounded readiness markers only after setup. Ignored cancellation additionally requires an observed live descendant with exact subject UID/GID, kernel start time and selected producer cgroup, then a marker written by its installed TERM handler, actual whole-group empty observations, and both original output-pump EOFs. Merely timing out or observing a leader exit cannot satisfy it. The six-case inventory remains unrun until an exact reviewed source revision completes it. A separate compile-private retained-report mutation control is also required before the full checkpoint-two exit gate.

Private stdout/stderr logs are never echoed. Retention checks per-file8MiB/total32MiB/count128, regular single-link entries, private directories0700/files0600. The workflow and driver are source candidates until their exact reviewed commit executes and retained artifacts are independently bound and verified. No native result, checkpoint completion, repository coverage gate, Trusted eligibility or accepted consumer proof is claimed by preparation or a structurally valid receipt.

## Review corrections before first native execution

Root staging now uses [retained no-follow directory/file descriptors](staging.py), inventories every node including omitted empty files before creating the destination, and rechecks names/metadata and exact streamed destination hashes. Nine portable controls verified ordinary copies, substitution and unsafe entries. Four driver data controls verified ledger projection and fixed policy; these are not Linux acceptance controls.

Negative cases require actual subject UID/GID/cgroup sampling and a fixture marker from the executed test body. Test failure additionally requires its recorded producer exit-one diagnostic; cancellation is sent only after the cancel branch marker; quota additionally requires the C# joined ledger observation to exceed the original16MiB limit. A generic failure cannot satisfy those cases. The positive result checks the complete sealed output tree including root:root, regular single-link files0400 and directories0700.

The final artifact tree uses root:root regular files0400. The producer workspace has a separate cleanup seal: mutable subject and scratch files become root:root0600. The ignored-cancel TERM marker remains in that producer workspace, so its post-seal guard checks0600; the final artifact mode does not apply to it.

The workflow installs a separate single SDK tree in runner temporary storage and binds every tracked source blob/hash/mode before build and after execution. The actual subject project is deliberately not evaluated before the C# root has armed the producer. A passing fixture packet remains Observation-only and does not change production catalogue or accepted-consumer proof.

## Package hash verification

The [SDK-only hash helper](NuGetContentHash/README.md) computes NuGet's `PackageArchiveReader.GetContentHash` and independently hashes the full archive. Signed packages have different unsigned-content and complete-ZIP hashes; the lock binds the former, while the `.nupkg.sha512` file binds the latter. The helper uses only the selected SDK's NuGet assemblies and never loads or evaluates a subject project. The original raw-hash attempt failed before subject launch and remains a failed receipt. [NuGet's implementation](https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Packaging/PackageArchiveReader.cs) handles signature normalization rather than a fixture ZIP rewrite.

## Explicit staging-parent traversal

The private driver uses umask077 for retained evidence. Fresh `/run` staging parents explicitly set0755 through their opened directory descriptor after creation, preserving root ownership and rejecting existing paths. Without this step, `mkdir(mode=0755)` silently creates0700 under that umask, and the protected deployment auditor correctly rejects the ancestor before any subject work. Only these generated input parents receive search access; the request, control, artifact and retained-evidence permissions remain private. A portable actual chmod/stat regression exercises the private umask and exclusive-path rejection.

## Failed producer diagnostics

When the C# producer has actually settled, infrastructure failures and normal nonzero test exits may retain root-private `stdout.prefix`, `stderr.prefix`, and the closed `state.json` described below under their fixed diagnostic generation. The native driver copies only these fixed root-owned0600/single-link files (at most1MiB each) from a validated root-owned0700 directory, without printing their contents. The copy is diagnostic only and cannot turn failed RUN, custody or cleanup into a passing case. Missing diagnostics remain missing evidence.

## Independent process sampling

The harness samples actual producer UID/GID tuples, cgroup and kernel start time before granting observation credit, then rechecks the manager PID. A missing or nonmatching sample remains pending within the same finite case deadline; it cannot authorize cancellation or make a case pass. Closed last-pending identity facts are retained for diagnosis. This avoids terminating the original C# run merely because a concurrent observation saw an incomplete activation transition. The C# owner's own independent identity guards remain unchanged.

The private producer diagnostic set also includes fixed `state.json` (closed operation stage, optional execution codes/statuses and pending start/stop facts). Only the selected generated producer cgroup's memory/task counters and event totals are sampled; peaks are observed lower bounds. This distinguishes a testhost OOM message from actual task/memory limit events before any resource policy correction. Unknown or pruned counters do not become zero or prove completion.

While the already-accepted producer identity is available, the same 100 ms observation loop makes a best-effort process/thread snapshot with a 15 ms per-sample deadline. `cgroup.procs` is bounded to 4096 bytes and 32 unique PIDs; every PID must retain the sampled subject's exact four UID/GID fields, exact selected cgroup, and stable `/proc/<pid>/stat` start time. The receipt keeps last/peak total threads, last/peak threads by a closed DLL-basename role set, and per-process PID/start-time/thread/role facts only for a new maximum. Raw command lines and paths are never retained. Missing, changing, malformed, or over-budget `/proc` data leaves that sample unavailable and cannot affect execution, cancellation, or acceptance.

## Seven-case completion inventory

The six ordinary cases are followed, before the final cleanup-quarantine case, by a separate `C2` compile-private image built from the same immutable source. Its [retained-report mutation](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/c2-artifact-mutation.md) requires the actual file's old and changed SHA-256, unchanged length/device/inode, then an authentic read rejection returning zero report bytes. That image is never substituted for the ordinary positive. Both images use the same audited SDK and fixed package input; their published bytes are inventoried separately. Completion requires all seven named cases, no skips, actual group joins and the case-specific account/custody outcome. The original four-case failures remain historical failed evidence, not seven-case acceptance.

Coverage-worker failures also retain fixed `stdout.prefix`, `stderr.prefix`, and `state.json` under a separate root-owned0700 worker diagnostic generation. The original stopped collector supplies those bytes without a second reader or a physical-settlement assertion. The root copies only fixed root0600 single-link bounded files. A missing worker diagnostic remains absent, and its raw contents are never echoed. Initial process-recheck failures forward the existing closed identity stage; no authentication guard is relaxed.

## Failed workspace inspection

After the original root returns unsuccessfully and the independently selected groups are empty, the harness may retain a diagnostic metadata inventory of the fixed generated producer workspace. It uses directory FDs and no-follow child opens, reads no file contents, bounds traversal to8192 nodes/depth32/five seconds, and records at most64 type/owner/mode/link/size violations. The private JSON is not custody or permission to release accounts. Missing or expired inspection remains incomplete; the original failed control is unchanged. Actual FIFO/symlink/hardlink/mode and empty-file portable controls cover the inspector without granting native authority.

### Quota output and failed-run cleanup

The [fixed subject quota target](subject/README.md#actual-quota-output) writes directly from the SDK
task to its original stdout. Successful testhost output can be captured by VSTest and cannot prove
that the supervisor received those bytes. The control still requires the actual shared C# ledger to
cross16MiB and the original root result to fail. Native quota drainage charges/discards all later
reads through actual EOF within the original deadline; it never accepts coverage after overflow.
The [native cleanup guards](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/producer-execution.md#quota-rejection-and-physical-cleanup)
separately require physical process/group settlement, complete ledger drainage, sealed filesystem
custody and strict account removal. The deliberate unsafe-workspace control must still quarantine.
