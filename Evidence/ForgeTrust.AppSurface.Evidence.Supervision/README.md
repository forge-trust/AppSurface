# Evidence.Supervision

## Ordinary execution and private qualification images

The [C# migration plan](../../docs/plans/issue-779-csharp-supervision-migration.md) requires one source graph for ordinary supervisor and worker roles. [EvidenceNativeQualification](../ForgeTrust.AppSurface.Evidence.Contracts/EvidenceNativeQualification.cs) is internal build metadata with a production default of `None`. Ordinary execution creates no N04 root-helper rendezvous, cancellation-stage barrier, N15 stderr frame or wait, stderr phase observer, or root-injected SIGINT. The ordinary worker follows authenticated planning, allocation, activation, cleanup and collection.

Private qualification builds use the closed MSBuild property `EvidencePrivateQualification` with exactly one of `N04`, `N07`, `N08`, `N09`, `N10`, `N11`, `N12`, `N13`, `N14`, `N15` or `N16`; its default is `None`. [Directory.Build.props](../../Directory.Build.props) appends the corresponding private symbol across the complete graph while preserving package-owned symbols. Unknown property values reject before build; multiple manually supplied checkpoint symbols fail compilation. Private qualification builds reject `Pack` so their assemblies cannot be published as production NuGet packages. N04 preserves the original authenticated peer replacement check; N08/N09 retain the original stage/caller tokens and the original root-owned, joined signal procedure. N10 uses the [private pending-start checkpoint](LinuxN10PendingStartCheckpoint.cs) only after the original validated `StartTransientUnit` reply; its frame is a cancellation trigger, not a late-unit or settlement receipt. The same original startup token remains in force. N11 uses the [compile-owned synchronous input factory](../ForgeTrust.AppSurface.Evidence.Cli/EvidenceFixedSynchronousInputFactory.cs) inside the authenticated worker's original tracked Admission callback; its deliberate synchronous stall must be terminated by the existing lifecycle and external owner. N13/N14 hold that accepted request for external owner termination; N15 holds the original validated worker-start reply under its original startup token. N16 adds only the fixed authenticated request described below. See the [Contracts qualification reference](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#ordinary-execution-and-private-qualification-images) for selection constraints. No argument, environment variable, descriptor field, public callback or candidate digest selects these behaviors. These builds must remain private qualification artifacts and are not deployable production artifacts or accepted consumer proof.

The internal API exposes `Current` (`None`, `PeerReplacement`, `ParentIdentitySubstitution`, `CancellationBeforeAllocation`, `CancellationBeforeActivation`, `PendingStartRace`, `SynchronousWorkerStall`, `LeaderExitWithDescendant`, `OwnerKilledDuringAcceptedWork`, `OwnerStoppedDuringAcceptedWork`, `PendingStartOwnerDeath`, `AcceptedBlockedWork`), `PeerReplacementEnabled`, `ParentReplacementEnabled`, `CancellationEnabled`, `PendingStartRaceEnabled`, `WorkerStallEnabled`, `DescendantEnabled`, `OwnerDeathAcceptedWorkEnabled`, `PendingStartOwnerDeathEnabled`, `AcceptedBlockedWorkEnabled`, `N16StopWaitEnabled`, and `CreateCancellationCheckpoint()`. The cancellation checkpoint factory returns null for every image except N08/N09; cancellation images receive a single internal checkpoint whose wait remains inside the tracked callback. Root server construction and pump observation use the same build-owned selection. Native identities, deadlines, pending ownership, physical joins, FD custody, account closure and failure latches still apply independently of that selection.

A private image must compile Contracts, Supervision, Evidence.Cli and the executable from the same selected source and symbols. Mixing assemblies compiled with different selections is unsupported. Data/token tests do not establish genuine Linux execution; each native checkpoint still requires its own immutable source/image binding and retained kernel observations. The historical private-image sections below describe their original source snapshots rather than a passing result for this integrated source.


Internal C# supervision for the [EvidenceHost trust boundary](../../start-here/evidencehost.md).
The library is part of the protected CLI deployment and is not an independently supported NuGet API.
It references Contracts and Planner; root composition does not load Coverage or Aspire to supervise processes.

The [design](../../docs/designs/issue-779-csharp-supervision-core.md) and
[checkpoint plan](../../docs/plans/issue-779-csharp-supervision-migration.md) define the migration.
The current source composes a complete empty Observation run and connects it to the reserved supervisor
entry as a guarded checkpoint candidate. The early worker entry retains real peer authentication.
Supported Linux root, protected launch inputs and actual single-use owner activation are checked before
children start. Trusted OS bootstrap and all named Linux checkpoint controls remain unverified.
No native checkpoint, Python cutover, or Trusted qualification follows from these types.

### N12: leader exit with an inherited output holder

The private `N12` build adds a closed same-executable child role, [`LinuxN12Descendant`](LinuxN12Descendant.cs). The normal worker authenticates, performs its real empty Observation, and returns through the existing exit protocol before this private procedure starts the current pinned host and managed entry. The child inherits stdout; only its acknowledgement stderr is redirected to a parent-owned pipe. The parent requires a complete, canonical ACK containing the actual `Process.Id` before it writes `N12-descendant:<pid>` into its original stderr and exits normally. No caller case, shell, alternate executable, resource or application registration is accepted. The five-second ACK cap borrows the original caller token; the original systemd worker/job deadline still contains the entire operation. This cap creates no renewed job allowance.

[`SupervisionDescendantObservation`](LinuxN12Descendant.cs) receives this first-frame PID data from the original received-byte-charged stderr pump. It adds no second output reader and does not authenticate a process. After the actual original leader monitor joins, [`LinuxWorkerProcess.ObserveLeaderExitWithDescendantAsync`](LinuxWorkerProcess.cs) captures the child through real retained root proc handles and requires a distinct live PID, matching complete worker UID/GID tuples, kernel starttime and the exact generated cgroup. The same original output task must still be unfinished and the selected recursive cgroup must be populated and unfrozen. A missing child, malformed frame, foreign identity, early EOF or expired original allowance fails the run.

The existing stop procedure then stops the entire original worker unit, joins the leader monitor and both output pumps, checks actual EOF/received-byte accounting, and independently rechecks the selected kernel group. The detached [`LinuxN12LeaderExitObservation`](LinuxN12LeaderExitObservation.cs) record is limited to 4 KiB and is emitted only after these joins, **before** root filesystem custody and account closure. Its `custody_and_accounts_not_yet_closed` field is true, and `native_authority`/`native_acceptance` are false. Native verification must additionally require the final root process exit zero, successful cleanup summary, exact source/deployment binding and account/FD custody facts. An intermediate record alone cannot pass N12.

Ordinary `None` and every other private build reject `evidence n12-output-holder` before configuration and create no descendant observer or child. The role offers no help or artifact/control protocol, and cannot create an admitted worker. Pure grammar/data tests and owned-stream barriers validate parsing and join ordering only; they do not establish real Linux descendant containment. Actual N12 remains a required row in the [migration gate](../../docs/plans/issue-779-csharp-supervision-migration.md#checkpoint-1-one-real-supervised-worker).

### N15: supervisor death during pending worker start

Only the compile-owned `N15` image pauses the real [`LinuxWorkerProcess.StartCoreAsync`](LinuxWorkerProcess.cs) start. After [`LinuxSystemdBackend.StartWorkerAsync`](LinuxSystemdBackend.Start.cs) has joined the actual `StartTransientUnit` call and validated its returned job object path, it writes one bounded JSON line to the original root stderr containing the fixed N15 case/phase plus the selected worker unit and that validated reply path. The frame is a harness trigger only. The original `StartWorkerAsync` task then waits on the same startup token supplied by [`SupervisionPendingStart.StartAsync`](SupervisionPendingStart.cs); it creates no replacement deadline, retry, release API, admission or join claim. The pending-start reservation stays held and `StartJoined` stays false until that original token cancels or the owner process is terminated.

This source preparation is partial for native N15. [`LinuxEmptyObservationExecution`](LinuxEmptyObservationExecution.cs) binds and listens on the control socket before it starts the worker, but it creates and runs [`LinuxEmptyObservationControlServer`](LinuxEmptyObservationControlServer.cs) only after `worker.StartAsync` completes. While the N15 barrier holds that task, the real worker can connect to the listening backlog but no server accepts it or answers its handshake. The worker's existing [`EvidenceLinuxWorkerSupervisor.ConnectAsync`](../ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxWorkerSupervisor.cs) handshake has the fixed 30-second Admission cap, which begins in the worker and may already be running before the frame reaches the fixture. The fixture must obtain the actual systemd `MainPID`, start time and cgroup before that existing handshake expires; otherwise the worker may exit before PID verification. A deterministic case still needs a separately reviewed, compile-private worker preconnect hold or a demonstrated source-existing hold that preserves the actual worker and original bounds. Do not manufacture a process, send READY, or treat this frame as worker readiness or native evidence. The existing [`SupervisionPendingStartTests`](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionPendingStartTests.cs) test procedure ordering only; no build, test, or native run is claimed for this preparation.

### N16: accepted blocked work with concurrent stop and wait

Only the compile-owned `N16` image recognizes the exact operation-only request `{"op":"n16-accepted-work"}`. The authenticated server checks its original owner and registers a `SupervisionWorkRegistry.Workload` before starting the fixed body, all inside [the owner admission gate](LinuxOwnerActivation.cs). Every managed admission closure and owner disposal takes that same gate. A check before taking the gate is insufficient: STOP or containment could begin teardown between the check and dispatch. The gate is released before any asynchronous wait and grants no authority by itself. The body signals an in-image entry barrier and then waits on its server-owned release task. The server writes the fixed intermediate `{"ok":true,"phase":"work-accepted","body_blocked":true}` frame, keeping the original request connection and handler pending. The worker validates that exact frame through its original root-PID-pinned socket and issues STOP and WAIT concurrently over separate authenticated connections while the original request is still pending. The request accepts no executable, callback, producer, application, lease, or caller-selected work data.

In this image only, a WAIT handler that arrives before STOP waits on the stop-start barrier using its existing request token. The original stop callback first observes the actual WAIT handler while the accepted body and request are blocked, then releases and joins the fixed body; the registry drains after that callback. All three handlers wait outside the reply-order gate, avoiding self-join and gate deadlock. The original request returns its final `{"ok":true,"work_joined":true}` frame only after the body joins. A canceled handler wait does not remove its original workload: final containment releases and joins that body even on timeout or missing WAIT. STOP and WAIT use fresh cleanup tokens, with the root's existing cumulative bound. Existing deadlines remain in force, and admission never reopens. Other images reject the private operation and retain their existing WAIT behavior.

The worker joins all original request tasks, then sends the ordered EXIT exchange so the server's preceding post-write commits settle before worker termination. After these control exchanges the private worker deliberately fails with `ASEVD410`; it creates no admission, artifact slot or manifest. Only after the original server, worker monitor, pending stop, pumps and selected group have joined can the root emit the bounded `issue779-accepted-blocked-work-v1` event record and [negative kernel observation](LinuxNegativeKernelObservation.cs), before final filesystem custody and account closure. The event projection also requires the committed EXIT and original server completion, so receiving earlier reply bytes or later physical cleanup cannot substitute for that ordering. Every event field requires the original task or an actual authenticated write commit; neither detached JSON nor successful cleanup can upgrade that negative result. After strict custody account deletion, every original local-owner/handle close and the final captured monotonic deadline check, the root attaches the separate `issue779-accepted-work-root-cleanup-v1` record to its original negative exception. Missing final cleanup data cannot prove settlement; the deliberate root exit 1 is also the exit for cleanup failure. [Detached cleanup encoding](LinuxEmptyObservationExecution.cs) establishes data shape only. The native fixture must bind all three records to the actual source/root image and independently verify terminal custody, generated account absence and absence of publication. This is a managed workload/control concurrency case, not a producer or descendant-process proof.

On an N16 failure before the accepted-work projection write was attempted, the joined root path may attach one [`LinuxN16ProgressDiagnostic`](LinuxN16ProgressDiagnostic.cs) snapshot to its existing negative execution exception. It snapshots only the existing original-server milestone flags and the whole-millisecond age of the last recorded milestone from the monotonic clock. An unobserved milestone uses `Unknown` and a null duration. The fixed JSON is capped at 1 KiB and contains no owner reads, paths, argv, errors or caller data; it grants no authority and cannot change the first failure. An attempted accepted-work projection suppresses this snapshot, including after a partial write. The CLI emits a valid snapshot through its existing terminal error writer alongside the original failure record; no additional stderr write, cleanup token, task, or deadline is introduced. Invalid snapshot encoding falls back to the same original negative exception, and caller cancellation keeps its existing handling. Missing data establishes no milestone or cleanup fact. Consumers of the private N16 stderr stream must recognize this optional failure-only schema explicitly; ordinary, N13 and N14 images do not compile or emit it. The [data-only codec tests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxN16ProgressDiagnosticTests.cs) verify the closed shape and clock bounds, not native execution.

Historical local macOS validation, before this progress diagnostic was added, ran the private N16 and ordinary configurations: 115 and 109 Supervision controls, plus 36 and 27 CLI controls, all passing (287 total), with zero compiler diagnostics. Locked package data stayed unchanged. The initial NuGet-cache and local process-settlement attempts remain separate failed harness records. Two exact-scope whitespace formatters exited zero without changing source; the CLI formatter first reported a workspace-loading warning, and a later diagnostic run reported none after clean restores and builds.

The first cleanup-token progress diagnostic, before terminal exception attachment, passed one scoped local validation: locked restore, four-file whitespace formatting, 17 N16 data/framing/final-close controls, and ordinary/N13/N14 source builds. All six commands exited zero in 26.476 seconds with zero compiler diagnostics and unchanged lock files. The later terminal exception attachment passed 22 focused data/framing/final-close controls, an N16 CLI source build, and ordinary/N13/N14 Supervision source builds. Its six-command retry exited zero in 42.136 seconds with no compiler diagnostics, unchanged source and lock files, and every owned process group absent. The initial attempt's inaccessible test constructor caused a compile failure before test execution; that result remains preserved separately. The corrected test obtains its exception through the existing final-close validation API. Native runtime behavior and optional record retention remain unverified. These deterministic barrier and ledger controls are local tests only. They do not establish authenticated Linux execution, root process ownership, OS containment, or N16 native acceptance; those remain required by the [checkpoint-one native gate](../../docs/plans/issue-779-csharp-supervision-migration.md#checkpoint-1-one-real-supervised-worker).


### N13/N14: accepted work held for external owner death

The `EvidencePrivateQualification` build property may select `N13` or `N14`; these values compile exactly one fixed private image through the same project graph. Ordinary `None` retains its existing protocol grammar and has no accepted-work branch or phase frame. There is no argument, environment, JSON field, public API, producer, lease or application selector. The request is the existing operation-only authenticated N16 request. The server registers the actual fixed workload under its existing owner admission gate, writes the same intermediate acceptance frame on the original authenticated connection, then emits one fixed ASCII phase line to its original stdout only after that write and flush complete. The line is a harness synchronization trigger only; it does not authenticate a new peer, record a kernel fact, or establish acceptance.

The N13/N14 worker retains the original accepted-work request and waits for the actual connection to terminate. It sends no STOP, WAIT or EXIT and does not release the server body. The owner-loss driver must terminate the owner using externally measured, identity-bound OS facts; source code does not synthesize SIGKILL/SIGSTOP, a timer, or a successful join. N13 expects owner SIGKILL to make the existing worker `After`/`BindsTo` and control-group policy terminate the dependent unit. N14 expects the already configured owner `RuntimeMaxUSec` and stop/final-kill policy to terminate a SIGSTOPped owner and its dependent unit. Both still require independent unit/PID/start-time/cgroup, pump, account and final custody evidence under the original deadline. If termination or strict cleanup is uncertain, retain output and custody as quarantined failure.

**Pitfall:** the N13/N14 phase line is emitted by the root server after a real response write, but the line itself is not native readiness evidence; the case harness must independently bind the live owner and worker process to their generated units and cgroups before signaling. The current source seam is not a native-run receipt or acceptance claim.

## Process roles

The protected AppSurface image supplies two explicit roles before normal CLI discovery or configuration:

```text
appsurface evidence supervise --request /run/protected-run/request.json
appsurface evidence worker --control /run/protected-run/broker/control.sock
```

`EvidenceProcessRoleParser.IsReserved` intercepts worker/supervise attempts, including case aliases.
`Parse` requires exact lowercase role and option names, one absolute normalized path, no extra arguments,
and at most 100 UTF-8 bytes for a socket or 4096 for a request path. `--help`/`-h` prints fixed usage
without executing a role. Malformed role data returns fixed `ASEVD402` without echoing supplied values.
Parsing paths does not authenticate their owner. Worker execution still uses
[Contracts' root-peer and runtime binding](../ForgeTrust.AppSurface.Evidence.Contracts/README.md).

The supervisor entry calls `LinuxEmptyObservationExecution.RunAsync` directly, before ordinary command
configuration. Unsupported platforms and non-root callers reject before account, file or unit setup.
The native implementation supplies every owner; the entry accepts no backend, plan or runtime callback.
Only after execution, verification and cleanup return does stdout receive canonical JSON with exactly
`Mode`, `ClaimKind`, `Eligibility`, `ExecutionVerdict` and `CleanupCompleted`, copied from the actual
manifest. Failure emits a fixed diagnostic and no success record. This empty informational result
does not enroll an application, authorize Trusted execution or replace native acceptance.

Managed early dispatch occurs after CLR startup. The protected OS bootstrap must also clear
runtime hooks, profilers, probing overrides and additional dependencies **before** starting .NET,
and bind the actual runtime host plus managed entry for framework-dependent deployments.

## Protected launch inputs

`EvidenceSupervisorRequest.Parse(ReadOnlyMemory<byte>)` accepts at most 64 KiB of strict UTF-8 JSON.
The closed checkpoint-one schema is `evidence-supervisor-linux-v1`, with mode `observation`. It requires
`tool_root`, `runtime_root`, `runtime_host`, `entry_path`, `policy_file`, `subject_root`, `base_revision`,
`subject_revision`, `workflow_identity`, `paths`, `observation_profile_ids`, `observation_producer_ids`,
`job_deadline_utc`, `admission_seconds`, `start_seconds`, `collection_seconds`, `cleanup_seconds`, and
`stopping_seconds`. Optional `diff_file`/`diff_sha256` are paired; `solution` is optional. Revisions use
40 lowercase hexadecimal characters, diff SHA-256 uses 64. Lists are detached read-only snapshots;
unknown, duplicate, case-alias, missing and malformed fields return fixed `ASEVD402`.

These are protected input choices, not account IDs, arbitrary argv, environment, unit properties or proof.
Runtime/entry/policy/diff paths must lie beneath their declared roots; tool/runtime and subject roots
must be disjoint. Parsing establishes neither filesystem ownership nor revision provenance.

`EvidenceProtectedLaunchInput.Open` is a separate native factory. It requires actual root Linux x64,
opens the request component by component without symlinks, and retains its ancestors and file.
`LinuxProtectedDeployment.Open` inventories **every** file in the runtime and tool trees; a supplied
file list cannot omit dependencies or probing candidates. Nodes must be root-owned, untrusted-unwritable,
of the expected ordinary type, and stable by device/inode/owner/mode/link count/size/change times.
Deployment ancestors grant public search; inventoried directories grant public read/search and files public read so the selected worker can load them;
private request ancestors need no public traversal. This does not add any worker write path.

The inventory is bounded to 8192 retained nodes per tree, 32 descendant directory levels, 256 MiB per
nonempty regular file and 1 GiB total file bytes. Request bytes remain bounded to 64 KiB. Kernel
`getdents64` supplies directory names; child type is checked through retained `statx`, not its directory
entry hint. Descendants cannot cross mounts. Absolute ancestors may cross root-selected filesystem mounts,
while their root ownership and name binding remain checked. Ancestor-only comparisons ignore unrelated child-list changes; inventoried directories retain complete change metadata and are rechecked after the final file hashing pass. No managed pathname fallback is used.

The factory binds the actual executing runtime host and managed entry and measures entry/policy/diff
bytes from the retained inventory. It freezes the protected UTC deadline once against the system
monotonic clock, counts opening/auditing time, latches expiry, and checks the original remainder before/after each native inventory/read operation and after final hashing. `LinuxInputDeadline` is monotonic deadline data, never admission; it carries the original timestamp and allowance into those loops without resetting either. `Recheck` verifies all retained names/metadata and hashes before use. Callers join readers
and pending starts before `Dispose`. These objects prove retained inputs only: protected pre-CLR
bootstrap, single-use owner activation, accounts, unit/kernel binding and admission remain required.

The native operations are shared with
[Contracts' exclusive artifact root](../ForgeTrust.AppSurface.Evidence.Contracts/README.md) through
`EvidenceLinuxFileSystem`, preserving its unsupported-openat2/no-fallback behavior and known errno.
Portable metadata/directory-record controls create no native retained-input object.

## Control request data

`EvidenceControlProtocol.Parse(ReadOnlyMemory<byte>)` accepts one complete JSON object of at most 64 KiB
including whitespace and escaping. It returns one immutable internal request type for ready, stop, wait,
exit, run, artifacts, artifact, application-start or resource-wait. Members are exact and closed;
missing, unknown, duplicate, case-alias, null and wrong-type members reject with fixed `ASEVD420`.

Run requests have at most 128 arguments and each string token has at most 4096 decoded UTF-8 bytes.
Artifact requests preserve the client fields `relative_root`, `relative_path` and nonnegative `offset`;
the server selects chunks of at most 128 KiB, and rejects a caller `count` or alias `path`. Application/resource identifiers
and digests use the existing closed grammar. The server must authenticate the peer and select a compiled
procedure after parsing. An executable token, path or digest creates no permission or runtime lease.

## Pending work and control handlers

`SupervisionWorkRegistry.BeginWorkload` reserves ownership **before** asynchronous dispatch. Its explicit
`Workload.Complete` is bookkeeping by the owner after pending dispatch and actual OS work settle. In the
compile-owned private N16 image only, the server also registers its fixed managed blocked body before
starting that body; STOP releases and joins that original task before reporting the managed workload
settled. This does not represent a kernel process, producer, or application-work join.
Disposing a scope cannot silently establish workload exit. Limits default to 64 simultaneous workloads
and 32 control handlers; construction can lower those caps only.

`CloseAndJoinWorkloadsAsync` closes workload admission permanently and joins only workloads.
It deliberately excludes control-handler scopes, so an authenticated stop/wait handler does not wait
for itself. `CloseAndJoinControlsAsync` is the separate final server-drain operation; call it after
the server stops accepting handlers and outside a handler being joined. Canceled waits retain ownership
and cannot reopen admission. The first closed failure category remains latched.

`IsSettled` is ledger data, not proof of a stopped process or a final completion receipt.
The owner must also join actual units, pumps and pending start operations and recheck its original deadline.
The [two completion scopes](../../docs/designs/issue-779-csharp-supervision-core.md#state-deadlines-and-completion)
keep workload cleanup distinct from the worker's own final exit.

## Counted output

`SupervisionOutputCollector.CollectAsync` owns both stdout/stderr tasks through actual settlement.
One pair shares a received-byte limit, default 16 MiB, with at most a 1 MiB retained prefix per stream.
Both can be lowered. All returned bytes are charged before retention, including discarded and late
in-flight bytes. Quota or read failures signal sibling cancellation and remain unsuccessful.

Caller cancellation never detaches a pump. A stream that ignores cancellation remains owned until
the independent unit-stop path causes it to settle. The immutable receipt distinguishes EOF, read failure,
cancellation, overflow and byte accounting. Two EOFs and an error-free receipt establish stream completion
only. The run owner must share or aggregate quotas across all process pairs before final completion.

## Linux systemd backend

`LinuxSystemdBackend.ConnectAsync` requires root on Linux x64 and uses the fixed system bus socket
`/run/dbus/system_bus_socket`, with auto-reconnect disabled. It resolves systemd's unique bus owner,
authenticates that connection as UID 0 / PID 1, and sends subsequent operations to the pinned unique name.
Responses require the expected sender and signature. A dropped or malformed connection fails closed.

The pinned [Tmds.DBus.Protocol 0.95.1](https://github.com/tmds/Tmds.DBus/tree/rel/0.95.1)
client supplies message serialization and transport. Its MIT license is included in
[CLI third-party notices](../../Cli/ForgeTrust.AppSurface.Cli/THIRD-PARTY-NOTICES.md#tmdsdbusprotocol).
No shell command or environment-selected bus is used.

`LinuxUnitName.Create` uses one closed role plus the run GUID. `ReadCurrentUnitAsync` and `ReadUnitAsync`
return bounded typed properties; missing or wrong-type required values reject rather than become zero.
Generic state comes from the Unit interface; service execution facts and `ControlGroup` come from the
[systemd Service interface](https://raw.githubusercontent.com/systemd/systemd/v255/man/org.freedesktop.systemd1.xml).
`StopUnitAsync` requests a job; `KillUnitAsync` signals all processes of the retained unit with SIGKILL.
Neither operation's reply establishes physical exit. The root owner must verify kernel UID/GID/PID,
retain the selected cgroup across systemd collection, and observe group emptiness and actual stream EOF.

`SupervisionOperationJoin.RunAsync` aborts the connection on caller cancellation and joins the original
operation before returning cancellation. A canceled D-Bus start may already have been accepted, so
pending unit ownership must survive cancellation and ambiguous replies. A fresh independently
authenticated stop connection can remain usable when a workload connection is aborted.

## Worker unit and control connection

`LinuxWorkerUnit.Create` produces only the fixed same-image worker recipe. The root owner supplies
independently validated runtime/entry paths, a same-run generated owner/worker pair, actual nonroot account
IDs, selected paths, retained stdout/stderr write ends and the original remaining allowance. It cannot
accept extra argv, caller environment or arbitrary unit properties. `StartWorkerAsync` serializes
`StartTransientUnit` directly on the authenticated D-Bus connection and returns a job path only.
Reserve pending ownership before the call and retain it through cancellation, ambiguous replies and
late creation. A reply cannot establish readiness, physical exit or completion.

The worker uses the existing 64-task/1-GiB ceilings, no new privileges, empty capabilities and supplementary
groups, strict system/home protection, private temporary storage and control-group killing. Its sole
explicit writable host path is the selected worker output parent. Runtime plus stop allowance fit within
the original job remainder, capped at one hour with stopping at most 30 seconds. The control socket cannot overlap the writable output parent. `BindsTo`/`After` target the exact owner; that owner must already have the
single-use activation guard. Worker `RemainAfterExit` retains inspection metadata, not a live-work receipt. `AddRef=true` keeps
that unit referenced by the original authenticated starting connection through both pending-stop calls
and final kernel/pump joins; the owner closes that connection afterward.
Worker `RestrictSUIDSGID=false` preserves the previously measured openat2 compatibility requirement;
subject policy is unaffected. Fixed environment, empty pass-through and explicit startup-override removals
are worker settings; they do not prove that the privileged runtime was sanitized before CLR startup.

`LinuxControlConnection.Accept` takes an actual accepted UNIX stream socket and a privately constructed
`LinuxProcessIdentity`. It checks real/effective root Linux x64, live retained worker PID/start-time/UID4/GID4/cgroup,
and actual `SO_PEERCRED` PID/UID/GID. A tuple parsed from JSON cannot select a connection. Every network
read/write rechecks the retained process and kernel peer, using that operation's original token. Only its
private socket factory constructs an authenticated connection. The owner retains the process identity until
all connection users join. Procedure selection, admission and physical cgroup exit remain separate work.

### Retained listener ownership

`LinuxRunWorkspace.ClaimListenerParent` claims one listener attempt before opening the fixed broker parent,
requiring the same actual owner and account holder. `RequireOwnedBy` rechecks that binding and every retained
directory/descriptor. The caller owns the returned parent handle through actual accept/connection settlement.
Neither API accepts a caller pathname or creates worker admission.

`LinuxControlListener.Bind` exclusively binds `worker/broker/control.sock`. It never adopts or unlinks a collision.
The actual socket inode is retained with `O_PATH`, sealed root:worker **0660** through descriptor-relative
`fchownat`/`fchmodat2` with `AT_EMPTY_PATH`, and rechecked against its retained parent/name before listening.
This requires Linux x64 with openat2/statx/fchmodat2 support; there is no pathname chmod fallback. The
[Linux 6.8 implementation](https://github.com/torvalds/linux/blob/v6.8/fs/open.c)
defines those descriptor-relative operations. Parents retain the existing 0750/0710 policies; the worker's
unit write grants are unchanged. `LinuxControlListenerPolicy` checks sampled metadata only and cannot bind a socket.

Bind before starting the worker, then capture its actual service PID/kernel tuple and publish the sealed
descriptor before `AcceptAsync`. If publication fails, any already-started worker remains owned and must be stopped
and joined before account teardown. The first accept pins that same retained `LinuxProcessIdentity` object for
all later connections. One accept may be pending at a time, with at most 32 retained connections. Cancellation
closes the listening socket and joins the actual pending accept, including any late accepted socket; a canceled
wait cannot drop pending ownership. Failure closes admission. The existing worker peer must remain alive and
authenticated at every network operation. A replacement object or root/producer/application process rejects.

`ReleaseAsync` joins the close of one retained connection. `DisposeAsync` shares the listening-socket close,
actual pending accept join, all retained connection closes, and finally the native path-handle close. The
server registers handler ownership **before** awaiting accept and joins each handler's read/write tasks as
well: listener disposal alone cannot prove handler, worker, pump or cgroup completion. Socket/descriptor paths
are preserved; final artifact custody and account deletion follow actual users joining. The private root
dispatcher and final run are [composed below](#empty-observation-root-execution-and-final-files); the native
checkpoint controls remain required before the CLI supervisor role opens.

`SupervisionAcceptOwnership<T>` is portable procedure ownership only. It registers each pending accept before
calling its delegate, retains each successful result before task publication, and owns exactly one close for
release/disposal. A late result after closure is closed and joined before rejection. The seam creates no
`LinuxControlListener`, `LinuxProcessIdentity`, authenticated connection, admission or physical-exit receipt.
Unexpected accept faults remain failed even after cancellation/disposal closes admission. Only the fixed
`SupervisionAcceptShutdownException` marks a deliberately interrupted actual accept, and only after closure
or original-token cancellation; it proves no physical exit. The native adapter emits it for its own closed
socket or narrowly identified cancellation/abort from the actual socket operation. Inspection and other I/O
failures remain failures. Listener disposal invokes the generic reentrancy guard before mutating native close
state and waits for its registered drain before closing retained path FDs.

The accept token belongs to the control server's lifetime. An individual workload/handler deadline must not
cancel the listener as a shortcut for stopping that work. The pending dispatcher must preserve cleanup control
traffic while the original owner/worker remain authenticated, close new workload admission separately, and
enforce the original job/cleanup allowances. Listener creation requires active owner/workspace checks;
retained operations use authenticated cleanup continuity. The [server and final run](#empty-observation-root-execution-and-final-files)
compose those checks; native acceptance remains pending.

### Worker descriptor publication data

`EvidenceWorkerDescriptorData.Create` takes the parsed observation request, generation, selected PID/account
comparison data, fixed workspace layout, measured output-parent identity, entry/policy digest spellings and
original positive remainder. It serializes the existing
[Contracts worker descriptor](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#closed-descriptor-schema)
as v1: 38 required fields plus nullable `diff_file`, `diff_sha256` and `solution`. Its two-segment run ID and
worker unit/cgroup come from the generation; provider/platform are the existing `github-actions`/`linux-x64`,
mode is observation and proof digest is empty. No application metadata or Trusted enrollment is introduced.

The serializer checks the complete fixed layout/account relation and existing Contracts structural parsers.
`LinuxWorkspaceLayout.AccountData` retains all five immutable account scalars: directory owners alone do not
contain the subject's separate primary group and cannot substantiate that complete comparison.
Both byte getters return detached copies. The standalone descriptor fits 65,536 bytes; the complete ready
JSON object `{ok:true,descriptor:...,job_remaining_seconds:...}` fits **65,535 bytes**, reserving its framing LF.
Limits count emitted UTF-8 bytes including JSON escaping and every wrapper member; overflow rejects without
truncation. The remainder must be positive and at most one hour. No clock or deadline is reset.

This type is data only, including its field-based identity overload for portable controls. The root run must
authenticate live service/kernel PID/UID/GID/cgroup, actual broker PID, retained input hashes and output parent
before invoking it. Bind the listener, start the worker, capture the actual PID, then seal these descriptor
bytes before processing ready. A positive serialized ready object cannot establish admission or physical exit;
server dispatch and original deadline checks remain mandatory. A larger valid launch request may still reject
when the expanded descriptor or escaped ready response cannot fit the existing wire bound.

`SupervisionControlLineFraming` is an intentional unauthenticated portable seam: one request attempt and
one response, each at most 64 KiB **including LF**, no CR, counted reads, closed codec validation, and no
second dispatch on that socket. Extra bytes returned with LF reject; future bytes are not drained because
the existing client waits for a reply without half-closing. Cancellation closes the actual socket/stream
and awaits the original I/O even if it ignores cancellation. Close initiation is distinct from completion: `DisposeAsync` joins the actual close and rejects close failure; response/failure/cancellation paths also join any concurrent close. The final handler owner must join both I/O and async disposal before draining its ledger. A blocked I/O requires independent unit
termination. The current reply bound covers checkpoint one; artifact chunks require the explicitly bounded
checkpoint-two response shape. No server/dispatcher composition is inferred from this transport.

## Root owner activation and process lifetime

`LinuxSystemdBackend.ReadOwnerAsync` reads the current process's service through the already
PID-1/UID-0-authenticated manager. The native projection requires struct arrays for `Conditions`,
`ExecStart` and `EnvironmentFiles` before `LinuxOwnerFacts.Parse` performs its bounded compound-field
projection. Portable variant-array fixtures test metadata only. They cannot pass the native wire-kind
check or construct a backend/activation. The v255 references are
[Unit conditions](https://github.com/systemd/systemd/blob/v255/src/core/dbus-unit.c),
[command tuples](https://github.com/systemd/systemd/blob/v255/src/core/dbus-execute.h) and
[termination properties](https://github.com/systemd/systemd/blob/v255/src/core/dbus-kill.c).

`LinuxOwnerFacts.Require` is a pure check, not an authority factory. It requires the generated
`appsurface-evidence-owner-<32 lowercase hex>.service`, actual owner PID in both main fields,
root numeric user/group, loaded/active/running exec service, exact generated kernel cgroup,
control-group kill, restart disabled and `RemainAfterExit=false`. Forced final SIGKILL must remain
enabled. The owner's activation timestamp and finite runtime plus stop must fit the original
monotonic job allowance; neither a new operation nor cleanup resets that deadline.

The protected bootstrap clears inherited environment **before CLR startup** with one fixed
`/usr/bin/env -i` command. Its 13 argv entries are the env executable, `-i`, the five ordered assignments
in `LinuxOwnerFacts.FixedEnvironment`, then the pinned runtime host, managed entry, `evidence`,
`supervise`, `--request` and retained request path. The service has no pass-through environment or
environment files. Extra assignments, changed order or arguments reject. Runtime, entry and request
paths must be canonical absolute paths without `$` or `%`, because systemd expands command arguments
before execution. The runtime-host path also cannot contain `=`: env would treat it as another
assignment and choose the following operand as the command. Entry/request paths occur after that
command and may contain `=` when otherwise canonical. The managed input owner still pins the actually executing dotnet image and entry;
the configured service executable is env. `Type=exec` establishes initial env execution, not managed ready.

`LinuxOwnerFacts.ManagedArguments(role, runtimeHost, entryPath, controlPath)` also supplies the worker's
fixed 13-element argv. Only the two reserved roles are permitted. Worker uses `evidence worker --control`
after the same command boundary; its existing 4096-byte deployment operand bound is retained, while
supervisor operands are bounded to 4095 bytes. The worker recipe separately applies its 100-byte socket
bound and protected/writable path disjointness. Runtime `=` is rejected for either role; positional entry
and control paths may contain `=`. This shared helper is copied read-only data, not a process launcher or
readiness receipt. The worker's five Environment entries, eighteen UnsetEnvironment entries, numeric
identities, 64-task/one-GiB ceilings, zero capabilities and thirty unit properties are retained.

This recipe depends on the trusted OS env executable, loader/libraries, locale infrastructure,
manager configuration and bootstrap environment; env itself runs before it clears inheritance. The
bootstrap must pin a genuine native dotnet image rather than a script: `execvp` can invoke a shell after
an `ENOEXEC` failure. Managed checks cannot undo a startup hook that already executed, and matching
service metadata does not authenticate the OS. The source review used
[Coreutils v9.4 environment replacement](https://github.com/coreutils/coreutils/blob/v9.4/src/env.c#L772-L837),
[systemd v255 argument expansion](https://github.com/systemd/systemd/blob/v255/src/core/exec-invoke.c#L4716-L4756)
and [glibc 2.39 fallback](https://github.com/bminor/glibc/blob/glibc-2.39/posix/execvpe.c#L75-L82);
these examined implementations are not a claim about installed runner versions.

The bootstrap also creates `/run/appsurface-evidence-owners/<generation>/armed`: a root:root 0600
ordinary single-link file containing the 32 lowercase generation characters and LF, beneath a
root:root 0700 generation directory. Its sole systemd condition is successful
`ConditionPathExists` for that exact file, without trigger or negation. `LinuxOwnerActivation.OpenAsync`
binds the actual current service and repeated root proc identity, rechecks protected inputs, then
`LinuxOwnerGuard.Consume` uses retained descriptors and `renameat2(RENAME_NOREPLACE)` to move `armed`
to `consumed`. There is no delete/create fallback and no rearming after failure. The bootstrap must
retain that generation until every pending dependent start and unit has settled; closing a managed
handle is insufficient permission to recreate or remove it.

`RequireActive` rechecks actual process identity, consumed bytes/name and original deadline before
new work. Failure closes new work permanently. `RequireCleanup` permits cleanup inside the original
stop/job bounds after cancellation: it repeats complete native inspection through the original retained
root proc descriptors, with the original PID/start time and consumed guard, without reopening admission.
Native integrity rejection also closes cleanup; ordinary caller cancellation closes work admission
without inventing integrity loss. `Dispose` closes retained guard/proc handles only after
all users and pending starts join. An owner record never grants worker admission or consumer proof.

`LinuxProcessIdentity.Capture`/`CaptureOwner` retain actual proc descriptors on root Linux x64.
The portable `LinuxProcessData` helpers decode bounded UID4/GID4, stat PID/start time and one unified
cgroup row. Actual capture requires the proc filesystem and repeated named-object bindings; recheck
rejects missing, reused, moved, zombie or differently credentialed processes permanently. A cancelled
inspection produces no successful check; a later cleanup operation repeats the entire native inspection.
Actual native rejection remains permanent even if cancellation happens concurrently. Proc file size/timestamps are not byte limits. A live PID or matching cgroup string
does not establish cgroup emptiness or physical exit. Kernel reads are synchronous; the independently
armed owner lifetime contains a stalled read.

`SupervisionPendingStart` reserves the selected unit before invoking its start procedure. Stop closes
admission, attempts stop, joins the **actual** reserved start task, and stops again so late acceptance
cannot escape an earlier stop snapshot. Concurrent callers join one sequence. Procedure failure stays
latched; a cancelled caller does not detach an ignoring task or renew the cleanup deadline. Its
`Snapshot.IsSettled` means procedure settlement only: the caller still needs actual kernel group and
pump joins. Its internal procedure seam creates no protected authority.

### Account preparation boundary

`LinuxRunAccountNames` and `LinuxRunAccountCommand` provide generated names and the fixed absolute
`useradd`, `groupadd`, `userdel`, `groupdel` argument/environment data. Pending names are reserved
before dispatch and remain in reverse cleanup obligations even when utility acceptance is ambiguous.
`LinuxRunAccountNss` performs bounded forward/reverse libc identity inspection under the actual owner;
identities and supplementary groups must satisfy separation. NSS configuration/provider custody is
part of the protected bootstrap, and a stalled provider remains inside the external owner lifetime.

`LinuxRunAccounts.CreateAsync` now owns actual account creation through a private factory. It takes
an irreversible account-creation claim on the actual owner before constructing a holder or doing NSS work.
`SupervisionSingleAttempt.Claim` is the atomic bookkeeping primitive retained privately by that owner;
it has no release/reset or native authority. Concurrent/replayed factories reject before entering
rollback. The factory then observes all generated
names absent before reserving any obligation, records implicit groups and users before
utility dispatch, and keeps every accepted or ambiguous attempt until it physically settles. Successful
creation requires both named and reverse-number libc NSS mappings, distinct nonroot identities, private
empty supplementary membership, and the original live owner. `RequireOwnedBy` checks the same actual
owner and rejects metadata snapshots, closed account use and failed creation.

`CloseAsync` closes identity admission before one shared reverse cleanup task. Call it **after** closing
consumer admission and joining every pending start/consumer, and after securing workspace/artifact custody
against UID reuse. It attempts the retained reverse obligations, permits skipping an automatically deleted
private group only on actual NSS absence, validates known created identities before deletion, and requires
all names absent afterward. Utility failure remains failure. Uncertain utility exit or name absence
quarantines identities; a later attempt cannot convert that cleanup into success. Creation failure uses
this same cleanup path within the original protected cleanup allowance. Account-db/NSS configuration
and providers remain part of the trusted OS bootstrap, with a stalled native provider contained by the
independent owner lifetime. No supplied numeric snapshot constructs account ownership.

`LinuxAccountUnit` selects only `/usr/sbin/useradd`, `groupadd`, `userdel` or `groupdel` with generated
names and individual fixed arguments. Each utility is a fresh short systemd child with root UID/GID,
only CHOWN/DAC_OVERRIDE/FOWNER capabilities, no new privileges, strict system/home protection, private
temporary storage, protected cgroups, 64 tasks and 1 GiB memory. Root utility account-db/log writes are
limited to `/etc` and `/var/log`; these do not change worker or subject path grants. Runtime is capped
at ten seconds and stopping at five, with their sum inside the original job remainder. Cleanup tokens
remain linked to the one original cleanup phase. `After` and `BindsTo` target the exact single-use owner.

The unit uses `RemainAfterExit=true` for terminal inspection and `AddRef=true` to retain its metadata
on the original authenticated starting connection through stop and joins. In
[systemd v255](https://github.com/systemd/systemd/blob/v255/src/core/dbus-unit.c#L2222-L2241), AddRef
belongs to the calling bus connection; release follows connection disposal. It creates no process-exit
receipt. A service can also
[prune its cgroup in the exited state](https://github.com/systemd/systemd/blob/v255/src/core/service.c#L1147-L1150),
so terminal inspection accepts an empty cgroup property while retaining the exact generated kernel path.
Uninitialized main PID/code values do not establish success.

`LinuxAccountUtility.Create` retains one actual utility owner before `ExecuteAsync` can dispatch. It
starts both pumps before the typed D-Bus start, bounds each pair to 128 KiB received and 4 KiB retained
per stream, joins the actual start before closing local write copies, then requires zero-exit terminal
facts. Cleanup uses independently authenticated stop calls around the pending-start join, verifies the
actual kernel group, joins both pumps and all descriptor closure, and rechecks the owner. `PhysicallySettled`
is emitted only by that actual executor and permits strict rollback after a failure; it grants no
admission or passed result. An unknown surviving writer keeps the original task owned until the external
OS lifetime terminates it. There is no detached timeout task or shell launch.

### Root workspace, cgroup and pipe custody

`LinuxRunWorkspace.Create` requires the actual live owner and its created accounts and exclusively
prepares `/run/appsurface-evidence-<generation>` with root:worker `0750`, a root:worker `0710` worker
control child and its root:worker `0710` broker child, plus worker:worker `0700` output and
subject:results `0710` raw-results siblings. The descriptor is `worker/worker-control.json`; the socket
is `worker/broker/control.sock`. Keeping output outside the descriptor's control root preserves
[the existing strict client contract](../ForgeTrust.AppSurface.Evidence.Contracts/README.md). Existing paths
reject without adoption or chmod. Descriptor publication is one attempt: root `0640` creation, exact
bounded bytes, fsync, root:worker `0440` sealing, retained reader, SHA-256 and named identity rechecks.
The root starts the pending worker and captures its actual kernel PID before producing this descriptor;
sealing must finish before any ready response or admission. A guessed PID cannot be published to make
file publication precede exec. The worker waits on the authenticated control channel in that interval.
`OutputParentIdentity` reports the measured identity for the existing exclusive allocator; slot `evidence`
is not allocated by workspace creation. Quarantine and disposal retain paths/artifacts. Listener binding
and `0660` socket sealing remain separate composition. The separate subject cannot traverse the `0750`
outer root: checkpoint two must compose retained namespace access without relaxing these grants.

`LinuxCgroupProbe.Read` opens the fixed cgroup-v2 kernel mount and `system.slice` through retained
root descriptors, then the exact generated unit. It bounds `cgroup.events` to 1024 bytes and requires
closed `populated`/`frozen` rows, actual cgroup2 type and repeated named object identity. A missing leaf
can be observed only at initial acquisition under the verified parent; later pruning rejects. The
immutable sample is kernel data, not an exit receipt, and is used only after actual start/stop settlement
and before/after pump joins. No pathname scan or sampled PID alone proves completion.

`LinuxOutputPipes.Create` owns actual root Linux x64 anonymous CLOEXEC pipes with checked read/write
modes and matching FIFO identity. Begin collection before dispatch and retain both borrowed write handles
until the actual start has joined. `CloseWriteCopies` never closes systemd's transferred descriptors.
`JoinAsync` returns the original collector task. Shared `DisposeAsync` joins it before closing readers
and preserves close failure; cancellation cannot fabricate EOF. Its portable ownership seam supplies no
native pipe or account authority.

`LinuxOutputPipes.WrapOwnedRead(SafeFileHandle)` transfers an exclusively owned read handle to a
4096-byte buffered read stream without duplicating the descriptor. The three-argument
[`FileStream` handle constructor](https://learn.microsoft.com/dotnet/api/system.io.filestream.-ctor)
derives the stream's asynchronous mode from the handle. A newly wrapped raw Unix descriptor is not
marked asynchronous: passing `isAsync: true` explicitly rejects that handle before collection begins.
The caller retains the handle if construction fails; successful construction transfers it to the stream.
Do not toggle handle flags or force the Windows overlapped mode to work around this mismatch.
`ReadAsync` remains available with the derived mode; the [counted collector](#counted-output) retains
and joins both original read tasks. This wrapper is a stream-ownership seam, not a Linux pipe factory:
the actual factory still performs every platform, root UID/GID, FIFO, access-mode and CLOEXEC check.
Its real anonymous-pipe regression uses the same wrapper without root privileges, checks both byte
prefixes and pending EOF while writers remain open, and closes readers only after the paired join.
The test also collects on Windows without forcing Unix or overlapped flags; it makes no Linux
factory, cgroup, account or native acceptance claim on any platform.

At this foundation checkpoint the early supervisor role remained closed until root server dispatch, worker/kernel binding, final
custody and all sixteen native controls are complete. These account/workspace paths do not enroll a
production catalogue entry or qualify Trusted execution.

### Same-image worker ownership

`LinuxWorkerProcess.Create` requires reference-equal actual launch input, owner, account holder,
workspace and bound listener. `LinuxOwnerActivation.ClaimWorkerCreation` consumes one irreversible
worker claim before native acquisition; equal JSON fields cannot replace the retained input.
`LinuxControlListener.RequireOwnedBy` similarly validates actual owner/account/workspace references.
These helpers reopen no admission and change no worker grants or unit policy. `Layout` exposes immutable
workspace comparison data only, for the existing [descriptor serializer](#worker-descriptor-publication-data).

`StartAsync` reserves the entire startup through `SupervisionWorkerLifetime` before creating pipes or
connecting to systemd. Both pumps start before the separately reserved `StartTransientUnit` call. Local
write copies close only after that original start task joins. The starting connection retains AddRef
through unit/group/output settlement. Cancellable status reads use a separate authenticated observation
connection so their cancellation cannot dispose the starting reference. The original exit-monitor Task,
not a result signal, is retained and joined before either observation connection or proc handles close. Running service facts must match the fixed recipe before actual
Linux PID/starttime/UID/GID/cgroup capture. Descriptor publication follows capture and precedes ready.
`RequireWorker` returns that same private native identity while startup remains usable; `CreateReadyData`
uses a fresh original deadline remainder and verifies that the standalone descriptor matches the sealed
file. Uploaded metadata or guessed PID values cannot construct the holder.

`LinuxWorkerUnit.HasRunningMain` and `HasFinished` are detached predicates, not native capabilities.
They require the generated unit and exact sampled recipe policy. A running match needs active/running,
matching positive main PIDs and the exact generated cgroup. A terminal normal-exit record needs main PID
zero, retained positive execution PID and CLD_EXITED; status 0–255 remains the actual outcome. Nonzero
is failed execution. `HasStopped` separately requires inactive/dead or failed/failed with a retained
normal or signal termination record; active/exited cannot match stopped settlement. Empty pruned terminal
cgroup metadata is permitted only as data; actual group and output joins still follow. Uninitialized zero code/status cannot establish completion.

`WaitForExitAsync` joins the original monitor of the authenticated unit's captured main PID. The monitor
cannot demand live proc files after a running sample: natural exit can occur between those observations.
Live kernel continuity is checked separately on every admitted control I/O. `StopAndJoinAsync` closes
startup, cancels its retained monitor token, contains pending unit dispatch, joins the entire original
startup, and finalizes group/output/backend ownership. It uses one cumulative cleanup allowance inside
the unchanged original owner/job deadline. Finalization also requires authenticated stopped-unit facts
on the original starting connection, checked again after group/pump/monitor joins and before releasing
AddRef. Missing terminal facts, signal termination without a stopped state, or lost retention cannot
publish physical settlement. Startup faults and cancellation stay failed after cleanup.
`PhysicallySettled` reports successful containment/task/FD settlement even after a nonzero worker
outcome, allowing subsequent custody work. Any monitor, output, stop, inspection or close failure
prevents this projection; finalization clears it before rejecting. It is not a generic “all tasks ended” flag; `RequireSuccessfulCompletion` additionally requires a naturally recorded zero exit,
successful joined output and no earlier failure. Neither projection is consumer proof or admission.

The worker holder is outside the producer workload ledger. A worker's STOP/WAIT request must join
producer/application work while that worker remains alive. The EXIT handler sends its ACK and finishes
before the outer server joins worker exit. Join all handler tasks before holder disposal; closing retained
connections does not substitute for those joins. `DisposeAsync` shares native settlement, listener drain
and proc-handle closure and deletes no filesystem paths or accounts. Do not renew a stop timer or use a
canceled wait wrapper in place of any original task.

The account holder retains the actual worker before startup. Process exit, decoded receipts and worker
disposal never release that reservation. The [terminal root custody phase](#terminal-root-custody-and-shared-teardown)
now owns the strict deletion task after sealing the complete retained tree. Ordinary `CloseAsync` still
rejects before account deletion when a worker is retained; only that private native custody holder may
start the account-close procedure. Failure preserves paths and quarantines identities. The
[remaining native checkpoint](../../docs/plans/issue-779-csharp-supervision-migration.md) still gates the
supervisor entry and requires actual Linux custody and account-reuse controls.

`SupervisionWorkerLifetime` is a portable procedure seam only. It reserves startup before callbacks,
closes it irreversibly, attempts containment, joins even cancellation-ignoring startup, then attempts
finalization. Both phases run outside its lock, share one sticky result, and reject callback self-join,
including after an await. Original caller cancellation remains observable after startup returns and rejects use and successful
completion; stop-induced cancellation of the linked monitor token is distinct. Cancellation-source
disposal occurs only after finalization. Its tests exercise
actual task barriers; they construct no native worker, admission or accepted proof.

### Protected empty Observation preflight

`EvidenceEmptyObservationPlan.Resolve(request, policyBytes, expectedPolicySha256, diffBytes, token)`
is a data-only planner preflight for [checkpoint 1](../../docs/plans/issue-779-csharp-supervision-migration.md#checkpoint-1-one-real-supervised-worker).
It counts and copies each nonempty input up to the existing canonical JSON 20 MiB limit before parsing,
checks the original policy byte hash, validates the complete policy and invokes the real planner. This
byte hash differs from a canonical policy digest when the original JSON has different whitespace.
The allowed profiles must all exist, have targeted scope, and declare no resources, producers or obligations;
the allowed producer list must be empty. The real policy must still have its legitimate nonempty conservative
profile. Unmatched or mixed paths retain the planner's conservative fallback and therefore reject this
checkpoint. Declared protected diff bytes are required and hash checked; diff paths, including renamed
previous paths, participate in the same resolution as the worker. No empty path or substitute plan is fabricated.

`FromInput` accepts only the genuine privately constructed `EvidenceProtectedLaunchInput`. Its
`ReadPolicyBytes` and `ReadDiffBytes` expose copies of already inventoried exact files, through retained
FDs and measured hashes. `LinuxProtectedNode.ReadBounded` checks the counted limit before allocation;
`LinuxProtectedDeployment.ReadFile` requires an existing inventory entry, original byte hash, metadata,
EOF and named binding. Full input rechecks bracket resolution under the original monotonic allowance.
The returned plan creates no context, admission, provider, process lease or accepted proof. Invalid plan
data returns fixed ASEVD406 without input text; cancellation returns no plan. Native input errors retain
their fixed diagnostic. The worker independently resolves and admits its own protected inputs.

[`EvidenceEmptyObservationPlan.AsOptionalDiff(byte[]?)`](EvidenceEmptyObservationPlan.cs) is the
data-only adapter used by `FromInput` after the retained diff read. Null means no declared diff and
returns nullable absence (`HasValue == false`). Every array remains present, including an empty array;
`Resolve` still rejects undeclared present bytes and declared empty bytes, and preserves its counted
input and digest checks. The adapter views the supplied array rather than creating a protected input,
copying native ownership or granting admission. `Resolve` performs its existing counted copy.

The null branch must be explicitly typed as `ReadOnlyMemory<byte>?`: an untyped null in a conditional
with a `ReadOnlyMemory<byte>` branch can use the array-to-memory implicit conversion and become a
present empty struct before nullable wrapping. Existing tests passed nullable absence directly to
`Resolve`, bypassing this adapter. The focused [planner/data regression controls](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/EvidenceEmptyObservationPlanTests.cs)
now route absent, present-empty and declared valid bytes through the same adapter used by `FromInput`.
They establish no native input, worker execution, cleanup or acceptance; native checkpoints remain pending.

### Authenticated control server and cleanup continuity

`LinuxEmptyObservationControlServer.Create` binds reference-equal actual input, owner, account holder,
workspace, listener and worker; `LinuxWorkerProcess.ClaimControlServer` consumes its only server-holder
attempt before acquisition. Construction requires completed actual worker startup and the protected
empty-plan preflight above. Equal JSON fields cannot adopt another worker. The current server handles
only READY, STOP, WAIT and EXIT. The other five request forms reject; they remain later native checkpoints.
The reserved CLI supervisor entry now reaches the guarded empty-run candidate; bootstrap and custody
must authenticate during execution, and all N01–N16 remain required before runtime cutover.

`RunAsync` owns one original accept task and at most 32 original handler tasks. Each control scope is
registered before handler I/O; accepted connection close and original read/write joins both remain
mandatory. Successful completed handlers are joined before their task references are removed. The accept
token belongs to the independent root/control lifetime. Handler I/O has the protected admission bound,
capped by the original remainder. Closing that listener never reopens or rebinds it. Final server drain
closes acceptance/results, joins original accept and handler tasks, closes the descendant ledger and joins
controls. It never calls the worker's final stop/join from within a handler.

`SupervisionControlSequence` is a portable procedure seam, not a native authority factory. READY has
one reply claim; successful response write and close precede its commit. STOP immediately closes work
admission and shares one actual containment/drain task. WAIT is positive only after all actual descendant
start/work/output joins and zero closed workload registrations. In this specific server no descendant work
is dispatched, so these observations describe a genuine empty set. The worker and active control handler
are excluded. EXIT requires committed READY and positive WAIT plus successful closed ledger state. A reply
semaphore orders ACK commits when the client receives LF and sends its next request before the root
continuation runs. STOP may close work while a previously claimed READY is writing; successful prior
READY can still commit, while a new READY claim after STOP rejects. Failed writes consume their claim,
latch failure, forbid EXIT success and permit only cleanup. Final worker exit and custody follow separately.

`SupervisionControlSequence.JoinStartedStopAsync()` joins only the STOP task already registered by
`StopAsync`. It rejects without dispatch when no STOP exists. The server awaits this join for WAIT
**before** acquiring its reply semaphore, then calls `ClaimWait` while ordering response writes as before.
The method snapshots the retained task under the sequence lock and awaits it outside that lock; it
creates no cancellation source, replacement token or deadline. The first STOP's owner cleanup token
continues to bound the actual procedure. Cancellation-ignoring callback or workload ownership remains
pending until the original task settles. A failed STOP remains failed; after its callback and ledger drain
join, bounded authenticated response I/O can commit only a negative WAIT, never a positive EXIT.

Do not call `StopAsync` to implement WAIT: that would initiate containment when none was requested.
Do not acquire reply ordering before joining STOP, join the requesting handler/worker as a workload,
or use a canceled proxy wait as completion. Same-owner callback reentry is rejected to prevent self-join.
Multiple WAIT joiners observe the same STOP, but `ClaimWait` still permits only one reply claim. These
are [procedure and ledger checks](#pending-work-and-control-handlers), not native settlement or admission.

Cleanup borrows the [owner-held collection/cleanup expiry](#terminal-root-custody-and-shared-teardown),
capped by the original job. Unit stopping and request I/O retain their smaller local bounds. Fresh
connections or repeated STOP/WAIT cannot replace the owner token, reset a failure or reopen work.
`RequireControlIdentity` checks actual retained root PID/start time, consumed marker and original deadline;
`RequireControlOwnedBy` repeats account/workspace identity, named directories and sealed descriptor checks.
The connection factory additionally binds the worker's generated unit to this actual owner generation.
Every read/write checks the original worker proc identity and actual SO_PEERCRED. Transport continuity
grants no active work: READY and later execution procedures must separately require active admission.

Per-request cancellation is checked outside the synchronous default-token native continuity inspection,
so it closes that socket rather than invalidating a shared kernel identity. Canceled read-only workspace
inspection closes work admission but creates no new integrity quarantine. Interrupted mutation, actual
PID/UID/GID/cgroup/path/socket/descriptor substitution, or expired original lifetime remains rejected.
Existing quarantine is never cleared. Synchronous inspection still relies on the independently armed OS
owner to contain stalls; a managed token alone does not provide physical termination.

## Verification boundaries

Portable tests cover grammar, data snapshots, pending ownership, control-handler separation, output
accounting and cancellation joins. They create no protected admission or kernel exit receipt.
The checkpoint's 16 real Linux cases are mandatory before protected runtime cutover; wiring the guarded
candidate for those checks does not satisfy that gate.
Production compiled registration and consumer proof tables remain closed throughout foundation work.

**Historical worker-lifetime source validation:** the final exact-file formatter and full core source build/test
passed **721/721**, with zero failures, skips, timeouts and compiler warning/error diagnostics in **3.565 seconds**.
All 56 source hashes matched before/after and independently verified current bytes. Formatting changed
no bytes, and both owned process groups were absent. The local run used macOS arm64/.NET 10.0.102,
`--no-restore -p:UseSharedCompilation=false`. Final receipt:
`/private/tmp/issue779-csharp-worker-validation/attempt-6/receipt.json`, SHA-256
`1e744eb4b530760bd1f4a16cacf105ac8c790da3e38a9b66aadea7bc5fcb1fc4`. Earlier attempts remain
separate history, including the first test compile failure. This source/portable result does not establish
systemd, root/worker authentication, physical settlement, custody or the unchanged coverage gate. The
[next checkpoint](../../docs/plans/issue-779-csharp-supervision-migration.md) still requires root dispatch,
cleanup-only authentication, native custody/bootstrap and all N01–N16 before runtime cutover.

**Historical control-server source validation:** scoped formatting and the rebuilt full core test project passed
**772/772** in **4.705 seconds**, with zero failures, skips, timeouts or compiler warning/error diagnostics.
All 61 source bindings matched before/after and independently verified current bytes; formatting changed no
source bytes and both owned process groups were absent. The added procedure/data cases comprise 22 control
sequence cases, 28 genuine planner/preflight cases and one owner-generation comparison Fact. The native
server, cancellation→cleanup transport and filesystem holder checks were source reviewed; they were not
executed on Linux by this macOS arm64/.NET 10.0.102 run. The first attempt's wrong member reference and
malformed XML comment stopped compilation before tests and remain in a separate failed receipt. Successful
receipt: `/private/tmp/issue779-csharp-control-validation/attempt-2/receipt.json`, SHA-256
`fe2674fd0193dad043550d85179016745acabb61b8b66e03e117f80ced6f87e2`.
Public root dispatch, native bootstrap/custody proof, N01–N16, subsequent producer/application checkpoints and the
unchanged coverage gate remain required before claiming runtime migration or Trusted acceptance.

### Terminal root custody and shared teardown

`LinuxOwnerActivation.BeginRootTeardown` irreversibly closes work admission and starts one owner-held
monotonic expiry. An actual worker reservation retains the existing separate collection and cleanup
reserves together, capped by the original job deadline; pre-worker account rollback retains only cleanup.
Stopping remains a smaller unit-operation limit and is not added again. `CleanupRemaining` never starts
this clock during successful account creation. `RootTeardownToken` is borrowed: neither the server nor
worker may cancel or dispose its source. `TeardownCancellation` borrows that same source before Begin so
already-running monitor, pump, request and accept operations receive the first expiry. Linking it does
not start the clock. Continued exit observation uses `RequireCleanupLaunchInput`,
which repeats original input/root/guard checks without permitting new work.

`SupervisionTeardownDeadline` is the deterministic scheduling seam. `Begin` retains the first timestamp,
reservation, timer and token; repeated or concurrent calls cannot renew them. `Remaining` also checks
monotonic elapsed time when a timer callback is delayed. Failed/expired/disposed state rejects. Its
`IsStarted` value and pre-Begin `Cancellation` token are scheduling data; neither starts the timer, and
its test clock constructs no native owner or custody. Expiry requests interruption, while every original
operation still has to join, including an operation which ignores cancellation.

`LinuxRunWorkspace.TakeRootCustodyAsync` reserves one original task and binds the exact native input,
owner, account, worker and server references. Replays with the same references join that task; later
tokens cannot replace its original cancellation token. Beginning custody closes live workspace use. The server must have joined actual accept,
handler, response and connection-close tasks; the worker must have joined original start, stop, monitor,
unit, cgroup and output tasks. Both are freshly checked before native transfer. Protocol failure may
remain failed while physically settled paths become root-owned; it never becomes successful execution.

The private `RootCustody` preflights every fixed generation node through retained descriptors before any
ownership change. Fresh directory descriptions avoid reusing a `getdents` cursor. Evidence files first
receive an `O_PATH` type/owner/link/length check, then bounded nonblocking reads, EOF, SHA-256 and named
inode checks. The sealed descriptor must match its original byte hash. Checkpoint one permits only the
fixed descriptor/socket and `output/evidence/{evidence-plan,evidence-manifest,evidence-summary}.json`;
raw-results must be empty. A cancelled run may have no evidence slot or a known partial file set.
Unknown, linked, special, substituted or oversized nodes reject without deletion.

All account-associated nodes then become root:root under a **separate terminal policy**: directories
`0700`, regular files `0400`, retained socket `0600`. Device/inode/type/link count/length/mtime and bytes
must remain bound; only the intentional ownership, permissions and ctime change. Original workspace and
worker owners close before custody issuance, while the root copies remain held for subsequent reads and
account cleanup. This policy does not weaken the live workspace checks or change worker write grants.

`RootCustody.ReadFile` returns copied bytes only from one of the three fixed evidence files after full
terminal-tree checks. A partial set is custody for failure, not successful publication. These bytes,
`LinuxCustodyData` comparisons and `SupervisionCustodyTransfer.SuccessfulCompleted` are not admission
or accepted proof. Successful evidence verification remains an additional step in the
[checkpoint-one exit gate](../../docs/plans/issue-779-csharp-supervision-migration.md#checkpoint-1-one-real-supervised-worker).

`RootCustody.CloseAccountsAsync` reserves its whole task before dispatching strict account deletion.
The account owner rechecks that exact custody before and after each operation and final NSS absence.
A private dispatch-context guard prevents invoking account closure merely by passing around the holder.
The original worker reservation stays retained. Root descriptors cannot be disposed during this task,
and partial deletion or final native failure stays failed. No account deletion is retried into success.
Workspace disposal likewise cannot close original descriptors during a pending transfer; only that
actual transfer may close them after retaining and inspecting its own copies.

`SupervisionCustodyTransfer` owns validation → complete preflight → mutation → local close → final check.
Callbacks are fixed private native methods in production and run outside bookkeeping locks. Its portable
procedure tests exercise failures and ignored cancellation, but cannot issue `RootCustody`. Every
dispatched original task is joined; both close and final check are attempted after earlier failure. The
shared root expiry and independently enforced OS lifetime remain required around synchronous native work.
Partial transfer closes retained local handles, preserves paths, quarantines accounts, and publishes no
success. Neither deadline expiry nor JSON completion replaces physical settlement.

**Recorded custody/teardown source validation, before full-run composition:** exact-file formatting and the rebuilt full core test
project passed **897/897**, zero failures, errors, skips, timeouts or warning/error diagnostics in
**6.333 seconds**. All 68 source hashes matched before/after and independently verified current bytes;
formatting changed no source bytes and all three owned process groups were absent. The run used macOS
arm64/.NET 10.0.102, `--no-restore` and `UseSharedCompilation=false`. Receipt:
`/private/tmp/issue779-csharp-custody-validation/attempt-3/receipt.json`, SHA-256
`d7439a39401750c0defc5dab8031c406660aa3e1870306d7cb04c71468097fb6`; TRX SHA-256
`12c653f3afa20b2a47dca388b5200db3eaa2a3c865555357b68cdea1a807cd4e`.

Earlier attempts remain separate: attempt one passed 894 cases but retained an xUnit analyzer warning
missed by its original case-sensitive diagnostic scan; the assertion and scanner were corrected.
Attempt two passed the same 894 cases without diagnostics but predates the final original-task retention
and late-expiry propagation changes. The final run includes 59 custody metadata controls, 40 custody
procedure controls and 26 teardown scheduling controls. These construct no native custody holder.
Independent source review corrected cancellation reaching already-pending monitor/accept I/O. Native
Linux I/O abort/join, FD sealing, account deletion and the N01–N16 checkpoint remain unexecuted.

## Empty Observation root execution and final files

`LinuxEmptyObservationExecution.RunAsync(requestPath, token)` composes only the genuine checkpoint-one
procedure. The reserved supervisor entry invokes this internal native implementation as a guarded
candidate for [N01–N16 acceptance](../../docs/plans/issue-779-csharp-supervision-migration.md#checkpoint-1-one-real-supervised-worker).
Native acceptance and runtime cutover have not been established.
It has no supplied backend, account owner, plan, handler, producer, application or transport callback.
The fixed order is retained input → authenticated backend/owner activation → actual empty plan →
accounts → workspace → listener → same-image worker → ready/stop/wait/exit server.

### Closed native failure diagnostics

The private checkpoint records the first recoverable root execution or cleanup fault with
[`EvidenceNativeObservationFailure`](EvidenceNativeObservationFailure.cs). This is detached diagnostic
data, never a native owner, admission, custody holder or accepted result. Each actual setup/await/check
and cleanup operation supplies a fixed `EvidenceNativeObservationPhase` immediately before that
operation. `EvidenceNativeObservationFailureLatch.Capture(phase, error)` retains only the first closed
projection; later account, FD, backend or final-deadline failures cannot overwrite an earlier worker
startup failure. Capture is best effort and cannot replace the original execution/cleanup outcome.
No operation order, token, clock, gate, authority check, work grant or join is changed.

The reserved supervisor entry emits one stderr JSON line with exactly six fields, followed by the
unchanged fixed ASEVD410 admission message and numeric exit1:

```json
{"schema":"evidence-native-observation-failure-v3","phase":"WorkerStart","error_kind":"Admission","diagnostic_code":"ASEVD410","account_failure":null,"control_failure":null}
```

The example is shape documentation, not measured attempt14 cause. `phase` and `error_kind` are the
closed PascalCase enum values in the [diagnostic source](EvidenceNativeObservationFailure.cs).
`diagnostic_code` is nullable and allows only ASEVD402/404/407/409/410/420/421 from an actual
`EvidenceAdmissionException.Code`; messages and inner exceptions are never searched. Known account,
pipe and control-line classes have finite family labels; unrecognized exceptions map to `Unknown`.
No arbitrary type name, message, stack, path, inner error or caller content survives the projection.
`ToJson()` returns no LF; the entry writes the LF. All possible packets are below1024 UTF-8 bytes.
The JSON is additional failure evidence, not a replacement for the original numeric process outcome.


#### Account first-fault detail (v2)

The nullable `account_failure` field is populated only by the negative account lifecycle exceptions in
[`LinuxRunAccounts`](LinuxRunAccounts.cs). Other failures, unknown exception types, `Exception.Data`,
inner errors and canaries cannot supply it. The nested object has exactly nine fields:
`preparation_stage`, `utility_stage`, `operation`, `error_kind`, `diagnostic_code`, `account_code`,
`exec_main_code`, `exec_main_status`, `dbus_category`. Enum names are finite, invalid enum data becomes
`Unknown` or null, and numeric fields are nullable. It contains no account/unit name, path, raw error,
message, stack, NSS contents, output or native owner. The whole v2 JSON plus LF remains at most 1 KiB.
The version change is intentional: consumers expecting exactly four v1 fields must update their closed
schema before collecting v2, rather than silently ignore an unknown property.

[`LinuxAccountUtility.FirstFailure`](LinuxAccountUtility.cs) captures the first actual execution or
cleanup exception before boolean normalization and before whole-run teardown. Actual failed output or
physical-join predicates retain only their existing fixed rejection. The outer account owner retains
that detached projection before marking reservations failed and starting reverse cleanup. A later
rollback failure still selects the existing `CleanupFailed` outcome and quarantine, but cannot replace
the earlier preparation fault. If cleanup alone first fails, its actual cleanup stage is retained.
`LinuxRunAccountException.Failure` remains the outer operation outcome; nested `account_code` describes
an original caught account exception, when one exists. Neither value proves account deletion or absence.
A diagnostic capture failure is best effort and never changes flags, guards, task joins or the original
operation exception. Creation cancellation remains an `OperationCanceledException` with the original
caller token, carried by an internal negative subtype so its closed data can survive rollback; no raw
inner exception is retained. Quarantine still takes precedence when cleanup cannot safely settle.

`ObserveTerminalData` copies code/status only after an actual `ReadUnitAsync` result for the selected
loaded unit: MainPID zero, ExecMainPID positive, initialized code, and active/exited, inactive/dead or
failed/failed state. Missing, foreign, running, uninitialized or invalid numeric rows remain null;
zero is never invented. A reported status of zero is retained only with an initialized observed code.
The existing `HasFinished` policy still rejects nonzero/signal failures. Diagnostic numbers neither
prove success nor replace independent stops, original-start join, kernel-group checks or pump EOF.

[`LinuxSystemdBackend.FirstStartFailure`](LinuxSystemdBackend.Start.cs) retains a first closed projection
before the existing dispatch catch disposes and emits fixed ASEVD410. Only an actual pinned Tmds
`DBusErrorReplyException.ErrorName` selects an allowlisted category; all unknown names become `Other`.
Non-D-Bus faults have null `dbus_category`. The utility reads only its same retained starting backend.
`ErrorMessage`, messages and inner exceptions are never read. Error replies erased elsewhere (including
connection acquisition, stop or pending-start normalization) cannot be reconstructed from text; their
existing closed error/stage is the honest boundary. This diagnostic does not repair account policy or
establish the cause of the earlier `AccountCreate/Accounts/null` native failure.

New regression intentions cover first fault versus rollback outcome, original cancellation tokens,
closed schema/canaries, terminal versus uninitialized numeric samples and exact error-name mapping.
These are detached data controls: no root accounts, live NSS, supervisor or native capability is faked.
They are source-defined and unexecuted at this handoff; main owns compiler/formatter/tests and subsequent
native diagnosis. Account command grammar, permissions, limits, unit properties, clocks, cleanup ordering
and authority remain unchanged.

`EvidenceNativeObservationException` is internal and negative-only because the existing admission
exception is sealed. It carries the closed packet and exactly the old fixed ASEVD410 text, without a
raw inner exception. The final cleanup/result rejection throws it; only the selected reserved supervisor
entry consumes it. Successful execution emits no diagnostic packet. Unsupported/unprivileged
`RequirePlatform` remains before protected I/O and outside capture. The original final caller-cancellation
check is unchanged: clean cancellation still produces the existing ASEVD402 cancellation outcome,
without a new packet. Fatal exceptions excluded by the existing recoverability filter remain excluded.
If no caught fault exists at a negative result check, the fallback is explicitly `ResultCheck`/`Unknown`,
not an invented native exception or successful cleanup claim.

The new portable controls exercise schema bounds, code filtering, canary rejection, actual closed
exception families and first-fault/closure precedence only. They have been defined, not executed, in
this private source-preparation lane. Build/peer review and genuine native execution remain pending.
Attempt14 retained only a generic ASEVD410 and no worker-live/final files; its actual first cause remains
unmeasured. This diagnostic adds observation for a subsequent run and waives none of the
[native checkpoint requirements](../../docs/plans/issue-779-csharp-supervision-migration.md#checkpoint-1-one-real-supervised-worker).

Success joins the original server task, then the worker's natural exit monitor **before** unit stop.
Stopping immediately after EXIT could terminate the worker between its ACK and normal return. The
server's `RequireSuccessfulCompletion` requires that original task to have succeeded, all protocol ACKs
to be committed and both ledgers settled. Physical custody following a failed protocol never passes it.
`RootCustody.VerifyObservationFiles` also requires the worker's natural zero-status exit, successful
original joins/output and a fresh terminal-tree check. It resolves the expected plan again from the
same retained protected policy/diff bytes before examining all three copied final files.

`EvidenceEmptyObservationFiles.Verify(expected, planBytes, manifestBytes, summaryBytes, token)` is a pure
data guard. It copies only after all lengths pass: nonempty plan/manifest at most the existing 20 MiB
canonical JSON bound, and nonempty summary at most 4096 bytes. Native `ReadFile` applies that lower summary
bound before allocation too. Complete canonical plan equality includes policy, normalized changed paths,
matched rules and digests; the expected plan is independently reconstructed by the real planner. A digest
alone does not replace that comparison. Canonical manifest bytes must pass `EvidenceManifestBuilder.Verify`
and describe empty targeted informational Observation: Passed, ObservationOnly, Informational,
NotRequired envelope, no envelope assertion, resources, producers or obligations. Cleanup must succeed,
terminal/cleanup diagnostics must be absent and timing metrics must be coherent with the empty stages.

The summary has exactly seven canonical fields: `Mode`, `ClaimKind`, `Eligibility`, `ExecutionVerdict`,
`EnvelopeStatus`, `Procedure` and `SandboxAttestation`. The first five match the manifest; Procedure is
`registered-protected-producer` and attestation is false. Missing/extra/duplicate/aliased properties,
alternate encodings, changed plan or false success reject with fixed ASEVD410 and no supplied text/inner
exception. Original caller cancellation remains cancellation. Returned manifest data supplies no native
custody, admission, enrollment or accepted consumer proof.

After data verification the actual run still joins strict account deletion, closes root custody and all
original native/local owners, and checks the original monotonic remainder after those closes before it
returns. That final comparison uses only the remainder captured immediately before closing its owner;
it grants no fresh allowance. `RequireFinalClose(clock, startedAt, remaining, token)` exposes this timing
comparison for portable controls only; the native run supplies `TimeProvider.System` and its actual
remaining interval. It rejects expiry, a regressed clock and invalid remainder without creating authority.
The borrowed teardown token is checked before its owner closes: owner disposal deliberately cancels
borrowers, so that expected cancellation cannot distinguish normal closure from expiry afterward.
Any prior execution/cleanup/close failure stays failed. Failure cleanup
interrupts listener I/O and joins the original server outside handlers, then joins worker containment.
It attempts all remaining closes even if an earlier attempt failed; no successful file set repairs failure.

`RetainWorkspaceForCustody` binds the actual private workspace to its account owner **before the first
directory mutation**. Partial workspace creation can leave account-owned paths even without a worker.
Ordinary account close now rejects that reservation too; only complete native root custody permits
deletion. If startup fails before a sealed descriptor and actual server exist, this implementation has
no partial-tree custody fallback: it preserves paths and accounts for root quarantine. Closing FDs or
absence of a worker does not authorize UID/GID reuse. This conservative failed-run disposition must be
observed explicitly by native controls and is never reported as successful cleanup.

**Previous empty-run/bootstrap source validation:** exact-file formatters and a rebuilt full core test
project passed **982/982** across 29 classes, with zero failed, skipped, timed-out or warning/error
diagnostic cases in **5.941 seconds**. All 72 source hashes matched before/after; formatters changed no
bytes and all three owned process groups were absent. This used macOS arm64/.NET 10.0.102,
`--no-restore` and `UseSharedCompilation=false`. Receipt:
`/private/tmp/issue779-csharp-empty-observation-validation/attempt-3/receipt.json`, SHA-256
`5fe5fe15f7573dc034b59141227486edd71f2b8309d6a1e76b1a311e4108bfcc`; TRX SHA-256
`06f6fdedbd86a77776c739827662d2f315d75233cb8bc3e9766857837915584c`.

The first run passed 975/976: the null-expected test serialized null before calling its intended guard.
That fixture was corrected and the next run passed 976/976. A subsequent source review found that an
equals sign in the runtime operand would be consumed as an env assignment; the final run includes
106 owner-contract cases, 45 final-file data cases and three final-close timing cases after that fix.
All prior receipts remain separate. Source checks create no native owner or root custody and establish
no Linux acceptance, consumer qualification, Trusted enablement or numerical coverage-gate result.


**Current reserved-entry and worker-bootstrap source validation:** core **987/987** and CLI entry
**10/10** passed with zero warnings/errors/skips/timeouts in 22.829 seconds. Four exact-project
formatters changed no bytes; all 72 before/after hashes matched and all six process groups were absent.
The [migration record](../../docs/plans/issue-779-csharp-supervision-migration.md#2026-10-06-guarded-same-image-cli-candidate-and-worker-bootstrap)
binds the receipt/TRXs and five built-CLI QA outcomes, and preserves the initial QA heading mismatch.
These local macOS checks establish no native Linux process/custody or N01–N16 acceptance.


#### Control first-fault detail (v3)

The private [empty Observation control server](LinuxEmptyObservationControlServer.cs) retains a sticky, best-effort [LinuxControlFailure](EvidenceNativeObservationFailure.cs) projection at actual setup, handler and cleanup catches. It stores no exception object. Each core procedure and concurrent handler has its own fixed stage; parsed operation is null until actual request decoding completes. Capture allocation failure is swallowed and changes no admission, failure latch, original exception, task join, token or clock. The original peer check before the existing core try is covered by a diagnostic-only rethrow wrapper. The compile-owned N07 checkpoint-owner factory also precedes the core catch: its original recoverable exception is projected under `N07CheckpointCreate` and rethrown unchanged. Exceptions from the N07 `BeforeNextAcceptAsync` call are projected under `N07BeforeNextAccept`. Both are closed labels only; neither identifies the rejecting owner predicate or changes lifecycle behavior. Existing sequence failure and response/cleanup order are preserved.

The nullable sixth field `control_failure` appears only when the outer first-failure projection is ServerRun or ServerCompletion. It contains exactly `stage`, `operation`, `error_kind`, `diagnostic_code`, with clamped closed enums and existing error/code projection. Consumers must explicitly require the v3 six-field schema; v2 exact-field consumers cannot silently accept it. Existing nine-field account detail is unchanged. Whole packet plus LF stays below1KiB.

The N07 stage additions passed the 98 scoped [failure-data controls](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/EvidenceNativeObservationFailureTests.cs) under the N07 build selector, followed by N16 and ordinary source builds. All four retry commands exited zero in 15.618 seconds with no compiler diagnostics, unchanged source and lock files, and owned process groups absent. The first attempt failed before test execution because the public theory parameter used an internal enum; the corrected theory passes its underlying integer and retains both original enum cases. These local controls verify diagnostic shape and compatibility; they provide no Linux execution or N07 acceptance evidence.

`WorkerTerminalTaskCompleted` is recorded only at the final protocol rejection after handler joins when the original worker terminal task has completed and EXIT is still uncommitted; it does not invent a worker exit status, successful OS exit or physical settlement. Caught handler or cleanup faults retain their actual fixed stages and operation, and cannot overwrite an earlier retained projection. Expected pending accept cancellation during listener drain is not captured as an execution fault. Listener disposal independently records an unexpected drain failure. This diagnostic observes the first retained catch/category, not global chronological ordering among concurrent native events. No code reconstructs raw bytes, peer identities, messages, stacks, PIDs or paths.

Attempt37730854250 reached actual worker/server construction and then ServerRun/Admission/ASEVD410; the first underlying server cause remains unmeasured. Repeated protected-plan hashing and connect timing are candidates only, with no measured expiry and no justified clock change. The initial source handoff defined nine data-only regression facts without execution. Subsequent local [supervision test-project](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Tests.csproj) validation passed **1038/1038**, including **37** native-failure diagnostic cases, with zero failures/skips/compiler diagnostics. Locked restore, two exact-file formatting commands and the source test all exited zero in **10.016 seconds / 180 seconds**; no formatter changes occurred. Receipt `/private/tmp/issue779-csharp-control-first-fault-validation/attempt-1/receipt.json` SHA-256 `12ff97e34dc282fc1dc6b995646b37f37e81de76ab22a935fd4bda439fe17a11`; TRX SHA-256 `8eab8d78f680abddcf7c5aaf31326fc89e7296f5d7e0ca7d2fceea4ef17268b3`. Independent runtime review cleared the diagnostic-only execution flow; this paragraph corrects the earlier preparation status after validation. Linux native proof and the actual inner ServerRun cause remain pending.

### Listener acceptance failure checkpoints

The [native first-failure diagnostic](EvidenceNativeObservationFailure.cs) keeps its
existing `evidence-native-observation-failure-v3` shape. When actual listener
acceptance fails, `control_failure.stage` identifies the last original check:
listener state, cancellation, workspace, parent, socket metadata/name, endpoint,
worker selection, owner identity, worker identity, descriptor, native accept or
accepted peer. These are diagnostic categories and provide no authentication,
admission, physical-exit or cleanup authority.

The [listener](LinuxControlListener.cs) retains only its first closed projection.
The [server](LinuxEmptyObservationControlServer.cs) reads that same retained
listener after an `Accept` or `AcceptJoin` failure; it never replaces an earlier
server failure. Original exceptions, quarantine, socket disposal, checks, tokens,
limits and account custody ordering remain in effect. A checkpoint identifies a
failed check, not which underlying kernel fact failed. The portable projection
controls cannot establish that a native check ran or that a worker was accepted.


### Closed retained-process recheck diagnostics

[`LinuxProcessIdentity.FirstFailure`](LinuxProcessIdentity.cs) retains diagnostic data from the first
recoverable integrity rejection in `Recheck`, before its existing sticky rejection and fixed
`ASEVD402` normalization. The original caller-cancellation catch remains unchanged and does not
latch an integrity failure. `Process*` values in the existing
[`LinuxControlFailureStage`](EvidenceNativeObservationFailure.cs) distinguish closure/rejection,
each retained and named proc node, bounded reads from parsing, selected PID/nonzero start time/live
state/all-four UID/all-four GID/exact cgroup, and initial, within-read and repeated continuity checks.
They use the existing four-field control object and six-field v3 root envelope; no proc bytes, PID,
ID values, paths, unit names or exception objects are retained. Every original check must still pass.

The [`listener`](LinuxControlListener.cs) preserves an actual worker's earlier `FirstFailure` when
its existing `RequireWorker` recheck throws, then rethrows the same error. Existing listener and
server first-fault latches retain an earlier fault instead of replacing it with later process or
cleanup data. A stage identifies the existing operation/check which rejected; it does not establish
why the kernel state changed, prove that a process exited, or grant authority. A null detail is not
proof of success. Retained/named node stages cover the original inspection/open/comparison together;
no new native read, syscall, timer, cancellation rule or process selection is introduced.

The diagnostic `LinuxProcessData.RequireExpected(..., ref stage)` overload shares the original
ordered sample predicates with the existing overload. It accepts detached data for procedure tests,
not a native identity, owner, lease or admission. New data controls cover each failed sample predicate
with a valid neighbor, closed owner selection, first-fault propagation/precedence and all finite
process stages' unchanged schema and byte bound. They are defined but not executed at source freeze.
Attempt25 measured `ListenerWorkerIdentity/ASEVD402`; its exact inner predicate and native cause remain
unmeasured. This source preparation makes no Linux or native acceptance claim.

#### Retained PID-directory inspection subchecks

Attempt26 retained the closed `ProcessRetainedProcess/Admission/ASEVD402` checkpoint. It does not
distinguish an inspection rejection from changed metadata. The separate fixture observer's
post-exec credential/image sample does not establish proc-directory metadata continuity across
that root check; its `ready_protocol_observed:false` field is a literal, not a wire observation.

Only the retained PID-directory branch of [`LinuxProcessIdentity.CheckBindings`](LinuxProcessIdentity.cs)
now refines that checkpoint. It uses one original `fstatfs` result, the unchanged shared
[`StatFd`](../ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxFileSystem.cs) call with its existing
required mask, the nonzero-inode and directory-type predicates, then the original complete
device-major/device-minor/inode/UID/GID/mode equality rejection. Finite
[`LinuxControlFailureStage`](EvidenceNativeObservationFailure.cs) values identify the operation or
the first unequal field in that order. No native result is read twice, and no extra probe, retry,
timer, token, permission rule or process selection is introduced. All other retained nodes and
all named-node checks retain their existing inspections. The same stages cover both original
binding passes and do not distinguish which pass failed.

[`LinuxProcessData`](LinuxProcessIdentity.cs) exposes pure filesystem-result/type, inode/mode and
detached metadata comparison guards used by that actual branch. Its immutable
`LinuxProcessIdentity.ProcNodeMetadata` record holds sampled values only; constructing it creates
no descriptor, live process, admission, lease or authority. The helpers preserve the original
predicates. In particular, the type guard adds no permission requirement, and the final equality
guard still rejects every changed field. Native call exceptions retain their original family;
the existing recoverable catch and sticky first-fault latch remain responsible for rejection.

The existing four-field control object, six-field v3 root envelope and 1KiB bound remain unchanged.
No values, errno, paths, process IDs, messages or exception objects are added to the projection.
Sixteen new pure cases in five methods cover native-result data failures with valid neighbors,
inode/type ordering, each metadata field, first-fault precedence and closed bounded canary-safe
projection. They are defined and unexecuted at source freeze. These controls cannot establish
native inspection, worker acceptance, a kernel transition or N01–N16 completion. Actual native
cause and any runtime correction remain pending fresh measured evidence.


## EXIT accept-admission quiescence

The closed [empty Observation server](LinuxEmptyObservationControlServer.cs) separates EXIT intent from
successful response commit. After the existing `ClaimExit` prerequisites, intent prevents any additional
accept/handler dispatch. The server joins [the listener's](LinuxControlListener.cs)
`CloseAcceptAdmissionAsync()` before writing the final ACK. Admission quiescence closes the listening socket
once and joins the original pending accept and any unpublished late-result close. It retains the already
published EXIT connection and original named/parent handles until that handler's write, release, cleanup-bound
and owner checks finish. An accepted result which wins the same `WhenAny` race as intent is retained for later
close, never newly dispatched. Intent itself sets neither `ExitAcknowledged` nor the final completion signal.

The portable [accept owner](SupervisionAcceptOwnership.cs) exposes internal `Task CloseAdmissionAsync()`.
One original task is reserved under its admission gate before callbacks; concurrent/repeated calls share
completion, including sticky failure. It closes no already published result. Full `DisposeAsync()` still joins
that admission task and attempts every retained result close. Native listener prechecks and pending registration
share the same gate as closure. A late actual socket still undergoes retained workspace, name, worker and peer
checks before its owned close; cancellation cannot convert a corrupt identity into ordinary shutdown. Only the
listener's own endpoint query is unavailable after its actual close, having been checked before native accept.
No timer, grant, quota, admission token, lease or kernel-exit fact is created by these procedure APIs.

On orderly EXIT intent, the root joins original registered handlers before full listener disposal so the EXIT
connection remains writable through its ACK. On non-EXIT failure it retains the original full-close-first
interruption path. Callbacks must not reenter their same owner: accept, release, admission close and full disposal
reject fixed ASEVD410 before a self-join. An ignored cancellation or blocked late close remains owned and awaited;
independent containment and the original root deadlines must handle a stall. A close, pending accept, peer check
or response failure cannot commit successful EXIT and still requires all cleanup attempts.

The added deterministic controls in [SupervisionAcceptOwnershipTests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionAcceptOwnershipTests.cs)
exercise retained live connections, original pending/late-close barriers, shared completion, unexpected faults,
callback reentry and failed ACK commit through intentional portable bookkeeping. They do not construct a Linux
owner or prove native admission, worker exit, account deletion or coverage qualification. Source preparation
executes no tests. The source-supported EXIT ordering hazard is independent of attempt27's measured retained
PID-directory UID mismatch: numeric UID values and its temporal relation to EXIT remain unmeasured.

### Reserve under a gate; dispatch after releasing it

`SupervisionAcceptOwnership<T>.RegisterAccept` and `RegisterAdmissionClose` return portable procedure handles with the original `Task` and a one-time `Dispatch` operation. Registration does not invoke the supplied native callback. The enclosing listener can therefore combine its existing worker precheck with reservation atomically, release its gate and the server execution gate, and then dispatch. Closing admission joins a reserved accept even if its caller has not dispatched yet; forgetting to dispatch leaves that original work owned and cannot establish successful cleanup. Repeated closure reservations share the original completion, and a redundant dispatch gate cannot release the first reservation. These internal handles provide bookkeeping only and cannot construct a Linux listener, process identity or protected admission.

The [control server](LinuxEmptyObservationControlServer.cs) reserves the actual accept under its execution gate and dispatches afterward. The [listener](LinuxControlListener.cs) reserves the pending operation under the same gate as its worker prechecks, and reserves admission closure before releasing that gate. Existing callbacks, endpoint/name checks, peer authentication and sticky failures remain required. Three additional [portable ownership controls](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionAcceptOwnershipTests.cs) exercise the registration/closure gap, callback dispatch outside an enclosing gate, and repeated closure while an original accept remains reserved. They supply no Linux execution or physical-exit evidence.

### Listener closure first-fault diagnostics

Attempt28 retained the closed `ServerRun / ListenerClose / Admission / ASEVD402` control record with
no operation, wrapped by root `ASEVD410`. It did not retain the original socket error, failed native
close operation or exact admission-drain cause. This is a measured checkpoint, not a diagnosis of
`ConnectionAborted`, process identity change or successful EXIT.

The [listener](LinuxControlListener.cs) captures listening-socket, retained named-socket and parent-handle
disposal failures before their original failure handling. Its instance accepted-connection close wrapper
awaits the original `DisposeAsync()` ValueTask, captures `ListenerAcceptedClose` on failure and rethrows
the same exception. The portable owner still reserves the original callback and joins its outcome;
no extra dispatch, token or close attempt is introduced. Admission-drain wrapping retains an earlier
listener record. Existing closure catches in the [server](LinuxEmptyObservationControlServer.cs) now
forward that actual `FirstFailure` through the existing [first-fault latch](EvidenceNativeObservationFailure.cs).
An already latched server fault still wins. All original closes, failure flags, quarantine, task joins,
exception filters, cancellation tokens, deadlines and peer/name/process checks remain required.

`LinuxControlFailure.NativeAcceptStage(SocketError)` maps unexpected native accept errors to exactly
`ListenerNativeAcceptOperationAborted`, `ListenerNativeAcceptInterrupted`,
`ListenerNativeAcceptConnectionAborted` or `ListenerNativeAcceptSocketOther`. Invalid enum data also uses
`SocketOther`. This pure internal projection is called only after the existing intentional-shutdown
filters, only for a `SocketException` at the actual native accept checkpoint. It does not classify shutdown,
change exception families, suppress rejection or retain a numeric error. All nine added stages use the
existing four-field control object, six-field v3 root envelope and 1KiB bound; no raw exception, message,
path, process ID or socket-error number is published.

The [pinned .NET 10.0.12 accept source](https://github.com/dotnet/runtime/blob/4271d88e0aebf3d04f188f1334c2220d80555ef6/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.Tasks.cs#L1351-L1375)
throws cancellation for abort errors only when the original token is canceled; otherwise it constructs
a `SocketException`. The existing owned-close adapter already recognizes `OperationAborted` and
`Interrupted`. `ConnectionAborted` remains an unexpected sticky failure and a source-supported hypothesis,
not attempt28's measured cause. No shutdown filter is widened by this diagnostic change.

Sixteen new pure cases in [EvidenceNativeObservationFailureTests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/EvidenceNativeObservationFailureTests.cs)
cover all four socket categories, all nine stage projections, invalid enum/canary rejection, precise
listener-to-server/root wrapping and concurrent server-first precedence. They are defined and unexecuted
at this source freeze. Native socket disposal behavior and N01–N16 acceptance still require separate
actual Linux evidence; this source preparation issues no capability, lease, proof or successful-close fact.

### Custody first-fault data and bound socket names

The reserved root candidate now emits `evidence-native-observation-failure-v4`: exactly seven root fields (`schema`, `phase`, `error_kind`, `diagnostic_code`, `account_failure`, `control_failure`, `custody_failure`), bounded below1KiB including the terminating LF. Existing account and control objects keep their nine-field and four-field shapes. The new nullable five-field `custody_failure` describes `procedure`, `node_kind`, `operation`, `error_kind`, and `diagnostic_code`. See the [closed DTOs](EvidenceNativeObservationFailure.cs), [actual retained custody transfer](LinuxRunWorkspace.Custody.cs), and [root Record wiring](LinuxEmptyObservationExecution.cs).

`LinuxRunWorkspace.FirstCustodyFailure` exposes copied immutable diagnostic data from its actual private holder; it does not expose that holder or issue custody, admission, account deletion, or successful execution. `LinuxCustodyFailure.Capture` projects only finite enums and allowlisted codes. `FromTransfer` combines the completed original procedure's actual first category with a matching earlier callback fault; `LinuxCustodyFailureLatch` retains the first projection through later cleanup. Callers must not use these data APIs as a native factory or assume that null means success. Root attachment is restricted to Custody, CleanupCustody, FileVerification and AccountsClose. A post-transfer file/account failure has procedure None when the original transfer recorded no failure, rather than inventing a failed transfer category.

Capture/rethrow instrumentation records context before the original procedure catches normalize errors; it never retries, changes a guard, dispatches another task, reads extra native metadata, changes a syscall argument, or renews the original deadline. The completed transfer category is read only after its task joins, so observing diagnostic data cannot change the original first-fault/cancellation ordering. Cancellation that skipped the forward procedures cannot credit a later close error as the original cause: its node and operation are absent/Unknown. Independent original-owner close and final recheck attempts still run and join; a partial transfer remains quarantined with paths/accounts retained. Invalid supplied enum data clamps to None/Unknown/null; raw paths, IDs, error numbers, messages, inner exceptions and exception references are never retained.

Attempt29's measured first packet was Custody/Admission/ASEVD402 without an inner custody operation. This schema change records future failures; it does not identify that historical failing syscall or establish N01 acceptance. Separately, pinned [.NET10.0.12 Socket source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs) registers a UnixDomainSocketEndPoint bound filename and attempts File.Delete during Socket disposal. That automatic name lifetime conflicts with custody's requirement to reopen the same named socket after listener disposal unless deletion fails. In this combined candidate, the internal [LinuxOwnedUnixEndPoint adapter](LinuxOwnedUnixEndPoint.cs) supplies the standard UNIX address serialization directly to Bind while leaving pathname lifetime with the actual workspace. The listener still physically closes and joins the original socket operations, checks the returned kernel endpoint and retained inode/name, and requires the same named socket during root custody. The adapter performs no filesystem operation or authentication and grants no custody or admission. Its [supported-UNIX regressions](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxOwnedUnixEndPointTests.cs) verify disposal retains an owned name, collisions reject, and replacement sentinel bytes survive; these local checks do not replace genuine Linux root custody qualification or establish that attempt29 measured an unlink. Runtime upgrades require rechecking this pinned implementation relationship.

Fourteen new portable diagnostic cases are defined but unexecuted in this preparation. The original82 cases retain their behavior apart from the two explicit v3-to-v4 schema expectations and the six-to-seven-field DTO shape assertion. Native issuance, full retained tree/bytes rechecks, root identity, actual joins, strict account cleanup and final manifest publication remain independent requirements. Main owns formatting, source build/tests, peer review, capture and any native rerun; none was executed in this source preparation.


## Detached negative-control kernel observations

The internal [LinuxNegativeKernelObservation](LinuxNegativeKernelObservation.cs) is bounded observation data for future source-selected N05/N06 images. The normal [LinuxWorkerProcess](LinuxWorkerProcess.cs) exposes `CaptureNegativeObservation(input, owner, accounts, workspace, server, token)` only through its privately-issued actual holder: reference-equal originals, original startup/pending-stop/monitor/server/pump joins and a fresh existing selected-cgroup check must pass. No caller PID, new Boolean grant, arbitrary unit, callback, permission exception, new timer, proof or admission is accepted. The projection reuses the custody guard's one cgroup read; it neither recaptures a dead process nor substitutes a later PID for the original retained identity.

The [control server](LinuxEmptyObservationControlServer.cs) records a past READY event only after the original peer-bound response write, connection release, owner checks and successful `CompleteWrite`. Its internal `RequireNegativeReadyDescriptor` requires original references and joined server owners before returning the workspace's immutable descriptor hash. A later negative protocol failure does not erase that past event; it still cannot satisfy the separate whole-protocol success guard. Original root SO_PEERCRED and every accepted-worker identity/name check remain mandatory. Missing READY or a failed/unavailable original natural monitor rejects observation rather than inventing identity or terminal status.

Schema `issue779-negative-kernel-observation-v1` has twelve fixed root members: schema, generation, worker_unit, process, ready, terminal, cgroup, pumps, joins, observation_only, native_authority and native_acceptance. JSON is at most4096 bytes before copied return (the caller's LF is additional). Process data retains initial PID/starttime/UID4/GID4/exact generated group; selected natural systemd terminal data retains ExecMainPID/Code/Status and closed state labels. Signals and uninitialized terminal reject; a normal status zero remains recordable data and never qualifies a negative case. Group absence has null kernel identity/population values. Complete joined output has both EOFs, no collection failure or discarded bytes and exact received/retained counters; only full-byte SHA256 values are serialized, never output text. Returned byte arrays are independent snapshots.

`CreateDetached` is an intentional pure metadata seam, covered by [LinuxNegativeKernelObservationTests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxNegativeKernelObservationTests.cs). Constructing matching data or JSON does not authenticate a kernel observation, callback, peer, process, root owner, custody, lease, qualification or acceptance. The fixed `joins`/READY labels describe the production projection's checked provenance; detached test calls cannot establish it. Both native authority and acceptance remain false. Original cancellation propagates with its original token before encoding; no allowance resets. Malformed data yields only a fixed ASEVD410 without input or inner exception.

This preparation defines38 new pure cases and runs none. Variant emitters and private fixtures are deliberately unwired: main must source-bind complete rebuilt N05/N06 images and wire bounded root-private observations only at their existing guarded postjoin points. Actual409 allocation output, unchanged occupied/symlink nodes and sentinel bytes, original root410, expected terminal status, quarantine/NSS disposition, genuine positive prerequisites and authenticated canonical retention remain separate checks. If existing ownership or terminal guards cannot pass, retain failure and the missing fact; never weaken those guards or treat copied observation fields as authority. No native N05/N06 or all-sixteen-control acceptance is claimed here.
# Fixed private N04 READY checkpoint image (source preparation only)

This private fork adds [`LinuxN04CheckpointOwner`](LinuxN04CheckpointOwner.cs) to the
genuine [`LinuxEmptyObservationControlServer`](LinuxEmptyObservationControlServer.cs).
It is a compile-time fixed image. There is no public, CLI, environment, request, callback,
caller PID or Boolean activation selector. The ordinary production registry, eligibility,
proof and custody APIs are unchanged. Parent-reported CI31 N01/N02/N03 results are separate
from this source preparation; no N04 helper or native N04 execution has occurred here.

## API, ordering and retained I/O

`Create(input, owner, accounts, workspace, listener, worker, token)` accepts the exact original
native holders and runs the existing `worker.RequireServerOwner`/`RequireWorker` guards.
Acquisition happens inside the already registered original server run, so a failure before
`RunAsync` starts cannot leave a new checkpoint listener. Its root-only fixed path is
`/run/appsurface-evidence-n04-<owner.RunId:N>/checkpoint.sock`, outside the production
workspace. `/`, `/run`, the fresh root0:0 directory0700 and socket0600 are retained and
checked by descriptor and current name. Every leaf uses openat2 no-link/beneath/no-XDEV
resolution; `/run` retains the original mount-capable ancestor resolution. Socket nlink1,
zero length, exact UID/GID/mode and inode/metadata are mandatory. Collisions reject without
adoption or unlink. [`LinuxOwnedUnixEndPoint`](LinuxOwnedUnixEndPoint.cs) leaves name lifetime
with this owner; closure retains the root-private namespace for quarantine.

`ReserveNextAcceptBeforeHandlerDispatch()` runs before the first handler dispatch is released.
`BeforeNextAcceptAsync(originalToken)` waits before the next listener `RegisterAccept`,
including its synchronous worker/path checks. It also observes the original worker monitor
task; worker termination or cancellation rejects rather than reopening admission. This gate
is separate from EXIT intent and `CloseAcceptAdmissionAsync`; the original EXIT barrier is
not repurposed or weakened.

After genuine `CreateReadyData`, `ReadyPreparedAsync(originalRequestToken)` accepts exactly
one separate UNIX peer with actual SO_PEERCRED positive PID distinct from this root process,
UID0/GID0. The same socket and kernel peer PID remain pinned for both exchanges. The server
sends ASCII `N04 READY_PREPARED <owner.RunId:N>\n` and requires the exact fixed
`N04 RELEASE_READY <owner.RunId:N>\n` response. The retained worker may be stopped by the
separately authenticated root coordinator: the unchanged process live-state guard already
accepts `T` and `t`. No stopped-state exception was added.

Only after the original READY response write, accepted connection release, owner checks and
`CompleteWrite(true)` does `ReadyCommittedAsync(originalRequestToken)` send
`N04 READY_COMMITTED <owner.RunId:N>\n`. The reply-order semaphore stays held throughout;
ordinary STOP/WAIT is never used for this pre-ACK rendezvous. The coordinator must wait for
this commit notification before intentionally replacing the production socket name. Next
accept remains blocked through the genuine client's fresh peer check. Original accepted-peer,
process, socket name, owner and cgroup guards remain mandatory; intentional substitution must
eventually fail and preserve quarantine/accounts. Notifications are data, not readiness
capabilities, worker exit proofs or acceptance receipts.

Each exchange is created behind an asynchronous dispatch gate, retained before that gate
opens, and awaited as the original actual task. The original request token and independent
root/owner deadline bound every native read/write; no new timer or allowance exists.
`CloseAndJoinAsync()` shares one registered close, cancels and independently closes both
actual sockets, joins every original exchange even after another close fails, then closes
all retained descriptors. It rejects same-exchange reentry and runs in the outer server drain
before handler joins; no handler joins itself. The outer run also repeats the shared close
for failures before the accept loop. No cleanup deletes or adopts a substituted path.

## Data controls and pending native prerequisites

[`LinuxN04CheckpointOrderTests`](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxN04CheckpointOrderTests.cs)
contains eleven defined, unexecuted portable controls for reservation, replay, commitment,
original-token cancellation, fault stickiness and real original-task barriers/joins. The
detached `LinuxN04CheckpointOrder` owns no filesystem, process, listener or admission.
These controls cannot establish N04 or physical native settlement.

The independently prepared root helper/fixture still must authenticate its interpreter/image,
actual original root/worker PID, starttime, UID4/GID4, image/argv and generated unit/cgroup;
retain pidfds across STOP/CONT; recheck the same identities; and bind the intentional root
replacement while leaving every production guard intact. The replacement must capture the
same actual worker's SO_PEERCRED PID/UID/GID and zero request bytes/EOF, with genuine client
PID-mismatch evidence before serialization. No pre-sent data frame or UID0 peer alone supplies
those facts. It must use the unchanged request/root and external fixture bounds, join its
original operations and the original worker/group/pumps, retain exact private results, and
preserve unknown replacement state/accounts. A stalled/fast-exited helper, missing peer
observation, expired budget, failed close or absent join is failure/inconclusive, never an
N04 pass. Full image capture, compiler/formatter/tests, peer review, helper implementation,
fixture composition and actual native validation remain pending.

## Fixed private N04 genuine-peer probe image

This subsequent source-only image is based on checkpoint commit
`6a766e3471b8a6ea56a15e3e972bf8840289049b`. Its fixed changes live in the
[string-control CLI wrapper](../ForgeTrust.AppSurface.Evidence.Cli/EvidenceProtectedCliExecution.cs),
[protected worker requests](../ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxWorkerSupervisor.cs),
and [root composition](LinuxEmptyObservationExecution.cs). There is no public, request,
environment or callback case selector. The explicit-request CLI overload and ordinary
execution body are unchanged. This image is solely a negative peer probe; it cannot run a
successful Observation or enroll a producer, prove eligibility or issue an admission.

After the original authenticated Connect/READY, the string-role wrapper calls the real
`WaitForOwnedExitAsync` with its original caller token before creating `EvidenceWorkerExecution`,
admission or output. The genuine request reconnects and compares the actual SO_PEERCRED PID
with the original READY broker PID. Only that mismatch branch additionally requires positive
peer PID and actual UID0/GID0, then receives exactly one byte under the same request token.
Only zero-byte EOF completes the hold; an application byte rejects immediately. EOF still
throws the unchanged ASEVD402 and never reaches `ExchangeAsync`, request serialization or
sending. An unexpected accepted wait reply instead throws fixed ASEVD410. This lets the
existing ordinary worker error boundary report the actual peer-pin rejection before the
ordinary lifecycle's FailFast path is created; it is not a production cleanup change.

`RequestAsync` creates no new deadline. The supplied original I/O token and the original
external/root-owner containment bound this hold; the completed Connect handshake timer is
not a subsequent request timer. The separately reviewed fixed root coordinator must sample
the same live worker after accepting it, recheck PID/starttime/UID4/GID4/image/cgroup and
SO_PEERCRED, then call `Shutdown(Send)` before awaiting worker EOF. It sends zero application
bytes. Missing shutdown, cancellation, expiry, nonroot peer or any mismatched identity remains
failure. The coordinator and fixture are separate pending integrations and are not modified
or qualified by this image.

Only on an already failed root run, after the original server task and `StopAndJoinAsync`
have joined, root calls the existing
[`CaptureNegativeObservation`](LinuxWorkerProcess.cs) before custody/account close. The
original reference-equality, server I/O/ledger/local-owner joins, natural terminal task,
committed authentic READY, two complete pumps, undiscarded full output and fresh selected
empty-group guards remain mandatory. [`LinuxNegativeKernelObservation`](LinuxNegativeKernelObservation.cs)
returns at most4096 JSON bytes; this image writes those exact bytes plus one LF to actual
root stderr using the original cleanup token. Publication is diagnostic only; capture or
write failure preserves the failed outcome and all later cleanup attempts still run. There
is no fabricated fallback when a guard fails and no raw worker text in this frame.

The later original custody/name checks must still reject the intentional replacement and
preserve quarantined paths/accounts; the frame is neither positive custody nor N04 acceptance.
An external verifier still needs exact image/descriptor bindings, the coordinator's live
original-worker and zero-send facts, actual terminal code/status, original natural monitor,
pump hashes/EOFs and fresh group facts under original clocks. No compiler, formatter, tests,
helper execution or native validation was performed for this source packet. Previous
checkpoint validation records describe their earlier frozen image only.

## Fixed N04 preparation before admission

The private N04 image uses [LinuxN04CheckpointOwner](LinuxN04CheckpointOwner.cs) to finish the actual root helper's image and process audit before the worker's READY admission clock starts. The original authenticated READY request first receives a closed `pre-admission` frame containing the root's positive remaining job milliseconds (maximum one hour). The root then waits for its separately authenticated root helper under the original job token. The helper captures the already connected managed worker and broker, releases preparation, and still stops that worker only after actual READY data preparation.

The same retained connection sends `admission-start` and then the existing descriptor response under the unchanged admission allowance. [SupervisionControlLineFraming](LinuxControlConnection.cs) allows exactly one fixed preparation/start pair after a READY request and one final response. Each native write repeats kernel peer checks; phase cancellation closes the stream and joins the actual write. Preparation is registered before dispatch, retained alongside both original READY exchanges, and joined before any checkpoint descriptor closes. A phase notification provides no admission, native acceptance or ownership authority.

Preparation consumes the original job deadline. It does not extend the job, reset cleanup, alter the peer or workspace guards, or reopen next-accept. Replays, wrong order, expiry and premature close fail closed. The portable order/framing controls verify data and actual task joining; actual root/worker/helper execution remains a separate required Linux control.



## Fixed private N08/N09 original cancellation images

Source preparation only: this image has no native qualification or case credit. The [original lifecycle contract](../ForgeTrust.AppSurface.Evidence.Contracts/README.md) owns callback/STOP/WAIT settlement; the [private CLI callback](../ForgeTrust.AppSurface.Evidence.Cli/README.md) retains output roots until OwnWorkStopped. No proof/admission registry, sandbox, account, syscall guard or public role selector changes.

`EvidenceOriginalCancellationCheckpoint.SelectedPhase` is the sole compilation input: N08 BeforeAllocation or N09 BeforeActivation. The N09 `.cs.in` preparation input replaces exactly that one literal in a separately captured image; it is never read at runtime. The authenticated string-control worker constructs the checkpoint; the explicit request-mode overload remains unchanged and supplies none. Both actual original token checks and the N09 original root assignment retain their order. Checkpoint writes use the original linked stage token inside the original callback; a nonthrowing cancellation registration releases a TCS only. Missing/failed transfer cannot cause root signalling or create a capability. The actual original CallerTokenCancelled and lifecycle first-cause CallerCancelled are separate observations. ReportJoined requires actual OwnWorkStopped. Private stderr disposal additionally requires that same actual original settlement; root FD disposal retains its original guard.

The root server retains one original signal task before dispatch and does not block next accept. Actual READY notification follows original response write, Release, owner check and CompleteWrite(true). Phase bytes are observed by the existing retained stderr pump after its original received-byte charge; no second pipe reader, fabricated READY, background task, runtime selector or timer exists. Exact first-line data is bounded to 1024 bytes. A malformed/missing phase fails observation rather than signalling. The root task verifies the actual reference-equal worker under unchanged native-owner guards, rechecks PID/starttime/UID4/GID4/cgroup/proc bindings around pidfd_open, then sends exactly one SIGINT through its retained pidfd. It never signals a caller PID or broadcasts/retries. The successful signal-task projection states syscall success, not signal delivery/cancellation. Error wakeup is retained in the accept wait; original root cancellation cancels and joins the task before server I/O settlement. All original accept/peer/name/stop/wait/custody/account checks remain.

The root failure-only emitter uses the existing genuine CaptureNegativeObservation after original server/worker/monitor/pump joins, before unchanged custody/account cleanup. That holder guard still requires original natural terminal, full output, fresh empty selected group, reference-equal owners and genuine committed descriptor. Failure to capture is not replaced by detached expected metadata. No normal exit1, account absence, custody success or quarantine is invented. Actual selected runtime/caps/pidfd policy, full source/image/inventories, first SIGINT delivery, actual original-token true and CallerCancelled, no activation, N08 no allocation call and N09 actual retained-root/FD-close order still need native evidence.

Ten new Facts are defined, zero executed: original caller versus stage-only release; nonselected location; duplicate claim; ignored write cancellation retaining the original callback and borrowed stream; closed projection/bounds/canary; and four original-pump framing controls. These use only detached stream/task/token data, never fake leases or live owners. The ignored writer control is not physical FD proof. Existing lifecycle callback-join/fatal/FD-close controls remain byte-identical; compiler/formatter/test validation and independent review are pending. Do not authorize execution from this preparation packet alone.

### Joined original stream export for the private cancellation image

The failure-only root emitter now calls
[`LinuxWorkerProcess.CaptureCancellationJoinedObservation`](LinuxWorkerProcess.cs),
which first runs the unchanged `CaptureNegativeObservation` guard. The original
reference-equal input/owner/accounts/workspace/server, successful natural monitor,
committed READY, original startup/stop/server/pump joins and fresh selected-group
emptiness remain mandatory. The only bytes used are the immutable prefixes stored
by the original joined collector. No new reader, stream, task, callback, path,
caller-supplied receipt or identity factory is involved. The ordinary successful
root path emits no joined-stream record.

[`LinuxNegativeKernelObservation.EncodeJoinedStreamsDetached`](LinuxNegativeKernelObservation.cs)
requires both actual EOFs, no per-stream or shared failure, no failed stop signal,
no discarded bytes, exact shared received-byte accounting and the original
protected limit. Complete stdout plus stderr must total at most 64 KiB. This is
an additional export bound; the existing pump quotas and retained-prefix limits
remain unchanged. Nothing is truncated, padded, reconstructed or replaced by a
guessed expected hash. Missing, incomplete or oversized data rejects with the
existing fixed ASEVD410, and original cancellation propagates unchanged.

The exported `issue779-cancellation-joined-streams-v1` JSON object has exactly
nine top-level fields: `schema`, `generation`, `stdout`, `stderr`, `received_bytes`,
`received_byte_limit`, `observation_only`, `native_authority`, `native_acceptance`.
Each stream has exactly seven fields: `received_bytes`, `retained_bytes`,
`discarded_bytes`, `eof`, `failure`, `sha256`, `base64`. Base64 encodes the complete
original bytes, including LF and non-text bytes; SHA256 hashes those same original
bytes. The line including its one LF is at most 96 KiB. Relaxed JSON escaping is
restricted here to fixed field strings and base64/hex alphabets so the maximum
pair fits that bound. This record is neither HTML-safe presentation nor a public
diagnostic; retain it only in the fixed root-private stderr/canonical artifact.

The root prepares all projections before publication, then writes signal, kernel
and joined-stream lines in that order before original custody/account cleanup.
The added write is awaited by the original root execution task with the original
cleanup token and checks that token afterward. Existing independent unit/job
containment remains responsible for a writer that ignores cancellation; no new
timeout, allowance or task is introduced. A late write/cancellation failure stays
in the existing first-failure/cleanup path even if earlier data lines were written.
Record presence is never command success or native qualification, and the export
itself authorizes no account release. Original account/custody/quarantine handling
is unchanged.

The canonical data adapter must strictly decode base64 and match original full
byte counts and SHA256 against the kernel record. It must validate the exact phase
frame, joined caller-token/lifecycle cancellation record and terminal message
inside the decoded stderr; the root signal projection alone does not prove token
delivery. Source/image binding, actual Linux pidfd permissions/syscalls, original
kernel provenance, N08 slot absence/N09 retained allocation without activation,
retained-root FD disposal and physical filesystem/NSS disposition remain separate
native prerequisites. No manifest, artifact admission, Trusted or acceptance is
created by matching data.

[`LinuxCancellationJoinedStreamsTests`](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxCancellationJoinedStreamsTests.cs)
defined 20 pure cases in 13 methods, zero executed at the initial source handoff. They cover
binary/base64/hash round trips and schema, the exact maximum, zero bytes, both EOFs
and pump errors, shared errors/quota/stop signal, discarded bytes, wrong accounting
or limits, oversize, missing output, original cancellation and returned-copy
independence. They construct detached receipts only; no live native owner or
signal is fabricated. N09 uses the same changes on its separately captured image,
preserving its one compile-selected BeforeActivation literal. The later scoped validation ran all 20 codec cases successfully, formatted the
owned files, and built the CLI product with zero compiler diagnostics. The
independent source review found no remaining issue in this scope. These local
macOS results cover the data codec and compilation. Fresh image capture and
actual Linux cancellation, original kernel ownership, filesystem and account
disposition controls remain pending.

### Final cancellation cleanup observation

The fixed private cancellation image carries an optional `issue779-cancellation-root-cleanup-v1`
record through the [negative-only exception](EvidenceNativeObservationFailure.cs). Its nine fields are
`schema`, `generation`, `results_gid`, `accounts_closed`, `root_custody_closed`,
`original_owners_closed`, `observation_only`, `native_authority`, and `native_acceptance`.
The [original root composition](LinuxEmptyObservationExecution.cs) attaches it only after joined
root custody, strict account deletion, every local owner close and the original final deadline check.
A later cleanup failure suppresses the record even when an earlier execution fault remains first.
The reserved [process entry](../../Cli/ForgeTrust.AppSurface.Cli/EvidenceProcessEntryPoint.cs) writes
it after the original failure packet and before the unchanged fixed negative terminal message.

This diagnostic cannot authorize execution or turn cancellation into successful Evidence. Detached
encoding controls establish shape only. Native validation must bind the exact original root process
and image, require all account-associated nodes to have their terminal root:root policy, compare
retained output device/inode with the original descriptor, and check every generated account name
and numeric UID/GID absent in both NSS lookup directions. The original descriptor bytes retain the
pre-transfer identities; comparing their old UID/GID to root-owned terminal nodes would reject valid
cleanup. Missing cleanup data, mutable terminal nodes, present accounts or expired checks reject.

### Private N10 pending-start race observation

The N10 image is selected only by `EvidencePrivateQualification=N10`; ordinary `None` creates no checkpoint or wait. Inside the already reserved original worker startup, the normal backend first validates the actual `StartTransientUnit` reply and job object path. Only then does the typed [LinuxN10PendingStartCheckpoint](LinuxN10PendingStartCheckpoint.cs) emit the fixed `N10` / `start-transient-unit-reply` frame and await cancellation using that same startup token. The private runner may use that frame to cancel the original supervisor process. No callback, request field, environment value, caller path or public CLI option selects the behavior, and no deadline is reset.

Cancellation enters the existing [pending-start coordinator](SupervisionPendingStart.cs): it closes further start admission, performs the original first stop, joins the actual reserved start task, performs the second stop, and then lets the worker lifetime finish its original finalization and paired-pump joins. The N10 joined record is emitted only after that original stop/finalization task returns. It carries the actual pending/lifetime snapshots, the exact original stop-delegate call count, final selected-unit predicate, final cgroup sample after pumps, pump counters, and the worker's existing `PhysicallySettled` value verbatim. The sticky cancellation remains failed; `PhysicallySettled` can therefore remain false even when the separate unit/group samples look settled. The record explicitly precedes root custody and grants no custody, admission, success or native acceptance.

The reply frame proves only that systemd returned a validated job path. It does not prove the unit began later than the first stop, that a worker process or cgroup was created, or that a group escaped. The native fixture must independently establish actual OS start/identity/cgroup facts, original two-stop ordering, joined output/accounts, and no escaped group. Missing fields or failed original joins suppress the joined record; none of these source/data controls counts as native case credit.

### Private N11 failed-settlement observation

The internal [LinuxFailedSettlementObservation](LinuxFailedSettlementObservation.cs) is detached, bounded
failure data, not a completion receipt. Its JSON is capped at 64 KiB and exact combined raw stdout/stderr
export at 32 KiB. Raw bytes and hashes are present only when both original pumps joined with EOF, exact
received/retained counts, no discard and no stream/output failure; otherwise raw/hash fields are null and
the recorded counts and closed failure categories remain. The actual lifetime failure and
`PhysicallySettled` values are copied without being cleared, and emission cannot change quarantine, custody,
account closure or a failed result.

The compile-selected N11 emitter and N08/N09 failure fallback are skipped by ordinary `None`. They run after the original server and
worker join attempts and before filesystem custody/account closure, using only the original teardown token.
Terminal and cgroup values come from the existing post-pump finalization reads; no additional process or
cgroup sampling, timer, stop, or deadline reset is performed. Capture also requires the original
reference-equal holders, committed READY and closed server I/O. The descriptor accessor calls the
[server owner guard](LinuxEmptyObservationControlServer.cs), which checks completed I/O and handler
joins, closed workload/control admission and the original owner references. It does not call the worker
physical-custody guard. Failed settlement may therefore be retained as data when those original joins
finished; `PhysicallySettled` may remain false. A missing join, READY event or authenticated owner rejects
capture. Neither this descriptor data nor the observation releases custody or accounts.

`CreateDetached(..., token, scenario)` accepts the internal closed `LinuxFailedSettlementScenario` values
`WorkerStall` (the default) and `OriginalCancellation`. The first preserves
`issue779-n11-original-failed-settlement-v1`; the second uses
`issue779-cancellation-original-failed-settlement-v1` with identical members and bounds. An unknown enum
rejects with the fixed ASEVD410 data error. Native callers select the family from compile-owned image
metadata; neither the argument nor a detached call selects a runtime operation or grants ownership.

Focused local checks passed 87 executions: 59 codec/lifetime/pending-start controls, one N11 build-selection
control and 27 ordinary qualification/protected CLI controls. These checks establish local data and
procedure behavior; the deliberate stall and actual Linux stop/group/pump/custody evidence remain unverified.


### Private pre-READY worker failure diagnostics

The fixed N08/N09 cancellation images first attempt the [failed-settlement observation](#private-n11-failed-settlement-observation) when their existing kernel/custody projection fails before writing. This exposes the original monitor/lifetime/pending-start state and post-pump finalization samples even when successful physical settlement is unavailable. It preserves the failure and existing success/custody guards. If its original READY or join prerequisites are unavailable, they use [LinuxJoinedWorkerOutputDiagnostic](LinuxJoinedWorkerOutputDiagnostic.cs). A worker can terminate before READY, while the original collector has already retained its error. `LinuxWorkerProcess.CaptureJoinedOutputDiagnostic` reads only the immutable receipt assigned after the original paired pumps join in `FinalizeAsync`; a missing receipt remains `unavailable`. Its diagnostic accessor deliberately supplies no physical settlement, READY, filesystem custody, account cleanup or admission fact. The ordinary build never invokes either fallback.

The internal data-only `EncodeDetached(Guid, SupervisionOutputReceipt?)` API emits the closed `issue779-joined-worker-output-diagnostic-v1` schema. It records actual received counts, per-stream EOF and closed failures, bounded prefix hashes, and at most 65,536 stderr bytes in base64; the whole JSON is at most 98,304 bytes. Stdout is represented by counts and a hash, without raw bytes. `physical_settlement_unknown` is always true; `native_authority` and `native_acceptance` are always false. Detached encoder tests establish serialization only.

The native composition writes at most one fallback line only after the original stop procedure has returned, solely when the existing private cancellation projection rejected before any of its output writes were attempted. Once any original projection or fallback write is attempted, a partial write may already exist, so another large record is suppressed to preserve the fixed aggregate root-log bound. It reuses the original cleanup token and authenticated owner checks, preserving the first failure. The fixed native fixture redirects this private image's root stderr into a root-owned 0600 log in its protected directory, and its existing 114,688-byte retention cap bounds the diagnostic. Base64 is a reversible private encoding: do not echo it in public logs or treat it as sanitization. Read/decode it only through the verified bounded private artifact path. Diagnostic absence or write failure never permits retry, successful cleanup, account release, a native control pass or Trusted enablement.

### Cancellation projection failure checkpoints

The private N08/N09 [root composition](LinuxEmptyObservationExecution.cs) may fail to encode a negative observation even after the worker physically settles. The separate [projection diagnostic](LinuxCancellationProjectionFailure.cs) records the operation that was about to run: original custody/input recheck, monitor metadata, committed READY descriptor, kernel encoding, full joined-stream encoding, owner identity, signal provenance, individual output writes, or final token check. The original [first execution fault](EvidenceNativeObservationFailure.cs) remains unchanged.

### N09 retained allocation-slot observation

The compile-selected N09 root server opens a second root-held descriptor for the actual `output/evidence` allocation relative to the workspace's retained output-parent descriptor. It performs this only after the original authenticated READY and `BeforeActivation` phase have been observed and after the existing owner/worker identity checks, immediately before the original pidfd SIGINT. The fixed policy requires the same device/inode, expected worker UID/GID, exact `0700` directory mode, a parent inventory containing only `evidence`, and an empty slot inventory. No public request, option, callback or runtime selector is added.

After the original server and worker tasks join, the N09 cleanup path rechecks the retained and named directory identities and both inventories, closes the slot and parent descriptors, then writes the bounded `issue779-n09-allocation-slot-observation-v1` data record before root custody/account release. The record contains before-signal and after-join parent/slot device, inode, owner, group and mode samples. A failed check or close suppresses that record and remains a cleanup failure. The existing `issue779-cancellation-root-cleanup-v1` line remains consistency data only; neither it nor the new slot record replaces independent kernel, NSS, output-quarantine or final cleanup evidence. Detached encoding controls in [LinuxN09AllocationObservationTests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxN09AllocationObservationTests.cs) validate bounds and schema failure cases only; they create no workspace, file descriptor, lease or native proof.

**Pitfall:** this capture must remain between the actual phase observation and original SIGINT, and descriptor closure must precede custody/account deletion. Moving capture after the signal or trusting the separate `issue779-cancellation-root-cleanup-v1` record loses the required lifetime evidence. Ordinary `None` and N08 do not call this helper.

`LinuxCancellationProjectionFailure.EncodeDetached(Guid, LinuxCancellationProjectionStage, Exception)` produces eight fixed JSON fields under `issue779-cancellation-projection-failure-v1`. It permits at most 1 KiB plus one LF, contains a closed stage/error family and a filtered diagnostic code, and excludes exception text, inner errors, paths and raw streams. Missing generation/error or unknown enum values reject. Detached tests verify data encoding only. The composition emits it only from the existing compile-selected cancellation failure catch using the original cleanup token and owner checks. It adds no OS read, stop, task, allowance, custody capability or positive result.

The checkpoint is assigned before each original operation. It identifies the failed operation, not an inner syscall or the earliest concurrent failure. Failed diagnostic output cannot replace the original failure. The existing large-record attempt flag remains authoritative after any partial large write; this small additional record cannot authorize a second large export. Successful cancellation projections emit no new record. The fixed fixture still requires original kernel, signal, stream, custody and account cleanup evidence before a negative control qualifies.

### N16 client progress in the terminal failure diagnostic

The private N16 client emits seven fixed `ASEVDN16C:01` through `ASEVDN16C:07` tokens around the original accepted-work, STOP/WAIT, response-validation, and EXIT sequence. The same original EXIT `RequestAsync` call emits `ASEVDN16X:01` through `ASEVDN16X:05` immediately before connect, after root-peer verification, after the request line is written, immediately before response reading, and after the response is validated. If that call throws, one fixed `ASEVDN16E:SS:FF:CC` token records its failing operation stage, a closed failure class, and an optional closed diagnostic-code family. No exception text, command, path, or token is emitted. The original output pump remains the sole reader. After the worker stop and paired pumps join, `LinuxWorkerProcess` parses only the retained stderr prefix when the complete paired receipt is successful; missing EOF, pump failure, truncation, unknown marker, duplicate, or reordered marker yields `Unknown` and cannot be interpreted as a completed request. Unrelated stderr bytes are not copied into this diagnostic. The failure-only JSON schema advances to `issue779-n16-progress-diagnostic-v2` for the new closed client fields and remains bounded by its existing 1 KiB encoder. It does not change the original request, cancellation token, failure, cleanup sequence, deadlines, server milestones, or native evidence requirements. These markers localize a client await boundary; they do not establish peer behavior, process settlement, custody, EXIT acceptance, or native success. The pure data controls exercise success, stage/failure ordering, malformed tokens, canary exclusion, and incomplete joined receipts without a fake peer or process.


## N07 parent replacement observation (private source integration)

The fixed private N07 build selection is `ParentReplacementEnabled`. When selected, the control server
attaches its checkpoint to the original authenticated admission sequence, and the stderr observer shares
the existing paired output collector. It creates no additional reader or worker. Allocation-fault reporting
remains inside the original tracked admission callback and preserves the original allocation exception.

The server closes and joins the checkpoint with its existing admission handlers. Failure settlement is
constructed only from the original final unit/group reads and joined monitor/output tasks; emission does not
clear failure or establish successful custody. With ordinary `None`, the N07 checkpoint is not created,
its pump observer is absent, and the allocation-fault writer is not used. The shared N07 selector and its
complete compile/test/native qualification remain pending integration review; this source merge is not
a native acceptance claim.
