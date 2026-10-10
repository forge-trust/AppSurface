using System.Security.Cryptography;

using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Retains one actual same-image worker from pre-start acquisition through native/output settlement.</summary>
/// <remarks>
/// Only the private native factory can construct this holder. It binds the same authenticated owner,
/// retained launch input, accounts, workspace and listener by reference before claiming the owner's only
/// worker attempt. The whole startup is reserved before pipe/backend acquisition; the separate pending
/// unit coordinator owns ambiguous D-Bus acceptance. The original starting connection retains AddRef.
/// This worker is deliberately outside the producer workload ledger: a worker's stop/wait request cannot
/// join its own process. The control server must join its handlers before disposing this holder.
/// PhysicallySettled permits subsequent custody work after failure; it is not successful execution,
/// artifact custody, account deletion permission, worker admission or accepted consumer proof.
/// </remarks>
internal sealed class LinuxWorkerProcess : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly EvidenceProtectedLaunchInput _input;
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxRunAccounts _accounts;
    private readonly LinuxRunWorkspace _workspace;
    private readonly LinuxControlListener _listener;
    private readonly LinuxRunAccountSnapshot _accountData;
    private readonly SupervisionWorkerLifetime _lifetime = new();
    private readonly SupervisionSingleAttempt _serverCreation = new();
    private readonly SupervisionPendingStart _pending;
    private LinuxOutputPipes? _pipes;
    private LinuxSystemdBackend? _backend;
    private LinuxSystemdBackend? _observations;
    private LinuxProcessIdentity? _worker;
    private LinuxWorkerUnit? _recipe;
    private CancellationTokenSource? _jobDeadline;
    private CancellationTokenSource? _outputDeadline;
    private CancellationTokenSource? _cleanupDeadline;
    private Task<LinuxUnitProperties>? _exit;
    private TaskCompletionSource? _dispose;
    private LinuxUnitProperties? _naturalTerminal;
    private SupervisionOutputReceipt? _output;
    // Retained only from the existing post-pump finalization reads; these values do not affect settlement.
    private LinuxUnitProperties? _failedSettlementUnit;
    private LinuxCgroupSample? _failedSettlementGroup;
    private bool _failedSettlementGroupAfterPumps;
    private readonly SupervisionCancellationPhaseObservation _cancellationPhase = new();
    private readonly SupervisionDescendantObservation _descendant = new();
    private LinuxN12LeaderExitObservation? _leaderExitObservation;
#if EVIDENCE_PRIVATE_N10
    private readonly LinuxN10PendingStartCheckpoint _n10PendingStart = new();
    private int _n10StopProcedureCalls;
#endif

    /// <summary>Gets only the original stderr-pump phase task; data creates no process authority.</summary>
    internal Task CancellationPhaseObserved => _cancellationPhase.Observed;
    private int _dispatchAttempted;
    private int _failed;
    private int _physicallySettled;

    private LinuxWorkerProcess(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxControlListener listener,
        LinuxRunAccountSnapshot accountData)
    {
        _input = input; _owner = owner; _accounts = accounts; _workspace = workspace;
        _listener = listener; _accountData = accountData;
        _pending = new(LinuxUnitName.Create(LinuxUnitRole.Worker, owner.RunId));
    }

    /// <summary>Gets the generated unit name as data, not an ownership or exit receipt.</summary>
    internal LinuxUnitName Unit => _pending.Unit;
    /// <summary>Gets actual dispatch/group/monitor/pipe settlement, even if the run remains failed.</summary>
    /// <remarks>Accounts still require separate retained filesystem custody and strict cleanup.</remarks>
    internal bool PhysicallySettled => Volatile.Read(ref _physicallySettled) != 0;
    /// <summary>Gets the original output observations only after actual native and pump joins.</summary>
    internal SupervisionOutputReceipt? Output
    {
        get
        {
            if (!_lifetime.StopJoined || !PhysicallySettled) throw LinuxSystemdBackend.InvalidControl();
            return _output;
        }
    }

    /// <summary>Copies private diagnostic data after the original stop procedure actually returns.</summary>
    /// <param name="generation">Original owner generation, as diagnostic binding data only.</param>
    /// <returns>The completed original collector receipt, or a closed unavailable projection.</returns>
    /// <remarks>
    /// Unlike Output, this diagnostic does not require or establish physical settlement. The receipt is
    /// assigned only after the original paired pumps join in FinalizeAsync. No READY, successful output,
    /// native monitor, cgroup or filesystem custody claim is supplied, and no reader or wait is dispatched.
    /// The fixed private cancellation failure path alone emits these bytes into its protected root log.
    /// </remarks>
    internal byte[] CaptureJoinedOutputDiagnostic(Guid generation) =>
        LinuxJoinedWorkerOutputDiagnostic.EncodeDetached(generation, _lifetime.StopJoined ? _output : null);

    /// <summary>Claims a single holder using actual retained native owners, before startup or acquisition.</summary>
    internal static LinuxWorkerProcess Create(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxControlListener listener,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts); ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(listener);
        owner.ClaimWorkerCreation(input, token);
        accounts.RequireOwnedBy(owner, token);
        workspace.RequireOwnedBy(owner, accounts, token);
        listener.RequireOwnedBy(owner, accounts, workspace, token);
        var snapshot = new LinuxRunAccountSnapshot(accounts.WorkerUid, accounts.WorkerGid,
            accounts.SubjectUid, accounts.SubjectGid, accounts.ResultsGid);
        owner.RequireLaunchInput(input, token);
        var worker = new LinuxWorkerProcess(input, owner, accounts, workspace, listener, snapshot);
        accounts.RetainWorkerForCustody(worker, owner);
        return worker;
    }

    /// <summary>Checks exact native account/owner references for the account holder's pre-start retention gate.</summary>
    /// <remarks>This establishes no exit or custody and cannot release account retention.</remarks>
    internal void RequireAccountOwner(LinuxRunAccounts accounts, LinuxOwnerActivation owner)
    {
        if (!ReferenceEquals(accounts, _accounts) || !ReferenceEquals(owner, _owner))
            throw LinuxSystemdBackend.InvalidControl();
    }

    /// <summary>Requires the actual server's original input, owner, accounts, workspace and listener references.</summary>
    /// <remarks>Equal descriptor fields do not establish this native binding or allow another server to adopt the worker.</remarks>
    internal void RequireServerOwner(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxControlListener listener, CancellationToken token)
    {
        if (!ReferenceEquals(input, _input) || !ReferenceEquals(owner, _owner)
            || !ReferenceEquals(accounts, _accounts) || !ReferenceEquals(workspace, _workspace)
            || !ReferenceEquals(listener, _listener)) throw LinuxSystemdBackend.InvalidControl();
        owner.RequireLaunchInput(input, token);
        accounts.RequireOwnedBy(owner, token);
        workspace.RequireOwnedBy(owner, accounts, token);
        listener.RequireOwnedBy(owner, accounts, workspace, token);
    }

    /// <summary>Claims this actual worker's one control-server holder before any server acquisition or policy read.</summary>
    /// <remarks>A failed attempt cannot adopt this generation again or replace its retained handler ownership.</remarks>
    internal void ClaimControlServer(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxControlListener listener, CancellationToken token)
    {
        RequireServerOwner(input, owner, accounts, workspace, listener, token);
        _serverCreation.Claim();
        RequireServerOwner(input, owner, accounts, workspace, listener, token);
    }

    /// <summary>Checks original native references and fresh selected cgroup emptiness after actual worker settlement.</summary>
    /// <remarks>
    /// Called only by native filesystem custody. A public settlement Boolean or unit metadata is insufficient;
    /// original startup/stop/monitor/output joins and irreversible pending-start closure must all exist.
    /// Failed execution remains failed, even when its retained paths can safely become root owned.
    /// </remarks>
    internal void RequireCustodyOwner(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxEmptyObservationControlServer server,
        CancellationToken token)
    {
        _ = RequireCustodyOwnerCore(input, owner, accounts, workspace, server, token);
    }

    private LinuxCgroupSample RequireCustodyOwnerCore(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxEmptyObservationControlServer server,
        CancellationToken token)
    {
        if (!ReferenceEquals(input, _input) || !ReferenceEquals(owner, _owner)
            || !ReferenceEquals(accounts, _accounts) || !ReferenceEquals(workspace, _workspace))
            throw LinuxSystemdBackend.InvalidControl();
        server.RequireCustodyOwner(input, owner, accounts, workspace, this, token);
        owner.RequireControlIdentity(token);
        var pending = _pending.Snapshot;
        LinuxCgroupSample? group = null;
        if (!_lifetime.StopJoined || !PhysicallySettled || !pending.StopJoined || pending.IsFailed
            || (pending.StartReserved && !pending.StartJoined) || _exit?.IsCompleted != true
            || _output is not { Successful: true }
            || !LinuxAccountUtility.GroupEmpty(group = LinuxCgroupProbe.Read(Unit, token)))
            throw LinuxSystemdBackend.InvalidControl();
        owner.RequireControlIdentity(token);
        input.Recheck(token);
        owner.RequireControlIdentity(token);
        return group!; // The unchanged short-circuit guard must have read and validated this sample.
    }

    /// <summary>Copies bounded negative-control facts only from this original physically joined native holder.</summary>
    /// <param name="input">Reference-equal original protected input, never a reconstructed request.</param>
    /// <param name="owner">Original authenticated owner.</param>
    /// <param name="accounts">Original retained actual accounts.</param>
    /// <param name="workspace">Original workspace retaining the descriptor and negative objects.</param>
    /// <param name="server">Original joined peer-authenticated server.</param>
    /// <param name="token">Original cleanup token; no allowance is renewed.</param>
    /// <returns>Immutable detached observation; it grants no custody, admission, proof or acceptance.</returns>
    /// <remarks>
    /// Reuses the existing custody guard's one fresh selected-group read. PID/starttime/UID4/GID4/group
    /// come only from the original retained identity; terminal data comes only from its successfully
    /// joined natural monitor. A failed monitor or missing committed READY cannot be replaced by stop,
    /// a generic exit code or caller metadata. No post-exit live proc recapture is attempted.
    /// Main must separately wire fixed variant emitters and authenticate bounded private fixture retention.
    /// </remarks>
    internal LinuxNegativeKernelObservation CaptureNegativeObservation(EvidenceProtectedLaunchInput input,
        LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxRunWorkspace workspace,
        LinuxEmptyObservationControlServer server, CancellationToken token)
    {
        var stage = LinuxCancellationProjectionStage.Unknown;
        return CaptureNegativeObservationCore(input, owner, accounts, workspace, server, token, ref stage);
    }

    private LinuxNegativeKernelObservation CaptureNegativeObservationCore(EvidenceProtectedLaunchInput input,
        LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxRunWorkspace workspace,
        LinuxEmptyObservationControlServer server, CancellationToken token, ref LinuxCancellationProjectionStage stage)
    {
        stage = LinuxCancellationProjectionStage.OriginalCustody;
        var group = RequireCustodyOwnerCore(input, owner, accounts, workspace, server, token);
        stage = LinuxCancellationProjectionStage.OriginalMonitor;
        if (_worker is null || _exit?.IsCompletedSuccessfully != true || _naturalTerminal is null || _output is null)
            throw LinuxSystemdBackend.InvalidControl();
        stage = LinuxCancellationProjectionStage.ReadyDescriptor;
        var descriptor = server.RequireNegativeReadyDescriptor(input, owner, accounts, workspace, this, token);
        stage = LinuxCancellationProjectionStage.KernelEncoding;
        return LinuxNegativeKernelObservation.CreateDetached(owner.RunId, _worker.SampledFacts,
            _naturalTerminal, group, _output, descriptor, token);
    }

    /// <summary>Copies the original kernel observation and complete charged streams for private cancellation retention.</summary>
    /// <param name="input">Original reference-equal protected input.</param>
    /// <param name="owner">Original authenticated root owner.</param>
    /// <param name="accounts">Original retained account holder.</param>
    /// <param name="workspace">Original retained workspace and descriptor.</param>
    /// <param name="server">Original joined peer-authenticated server.</param>
    /// <param name="token">Original cleanup token, without a reset deadline.</param>
    /// <param name="stage">Closed diagnostic checkpoint assigned immediately before each original guarded operation.</param>
    /// <returns>Copied detached data; no identity, custody, account release or acceptance is granted.</returns>
    /// <remarks>
    /// CaptureNegativeObservation runs unchanged, including its one fresh selected-group sample, all
    /// original startup/stop/server/monitor/pump joins, natural terminal and committed READY checks.
    /// The receipt is assigned only by the original joined collector and remains immutable thereafter.
    /// There is no second reader, supplied receipt, alternate path, native owner factory or new task.
    /// Missing/incomplete/oversized data rejects; no guessed hash or truncated prefix can replace it.
    /// </remarks>
    internal (LinuxNegativeKernelObservation Kernel, byte[] JoinedStreams) CaptureCancellationJoinedObservation(
        EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        LinuxRunWorkspace workspace, LinuxEmptyObservationControlServer server, CancellationToken token,
        ref LinuxCancellationProjectionStage stage)
    {
        var kernel = CaptureNegativeObservationCore(input, owner, accounts, workspace, server, token, ref stage);
        stage = LinuxCancellationProjectionStage.JoinedStreamEncoding;
        var output = _output ?? throw LinuxSystemdBackend.InvalidControl();
        var streams = LinuxNegativeKernelObservation.EncodeJoinedStreamsDetached(owner.RunId, output, token);
        stage = LinuxCancellationProjectionStage.HolderIdentity;
        owner.RequireControlIdentity(token);
        return (kernel, streams);
    }

    /// <summary>Copies failure-only data after the original worker and server owners have joined.</summary>
    /// <param name="input">Reference-equal protected launch input.</param>
    /// <param name="owner">Original authenticated owner and teardown token source.</param>
    /// <param name="accounts">Reference-equal retained accounts.</param>
    /// <param name="workspace">Reference-equal workspace retaining the sealed descriptor.</param>
    /// <param name="server">Original server with committed READY and closed handlers and I/O.</param>
    /// <param name="token">Original root teardown token, without a renewed allowance.</param>
    /// <returns>Detached bounded data that preserves failure and settlement flags without granting custody or success.</returns>
    /// <exception cref="EvidenceAdmissionException">An original binding, READY event or join prerequisite is missing.</exception>
    /// <exception cref="OperationCanceledException">The original teardown token is cancelled.</exception>
    /// <remarks>
    /// Terminal and cgroup fields come only from the existing post-pump finalization reads retained by this
    /// holder. This method performs no process, cgroup, timer or stop operation. It reports PhysicallySettled
    /// exactly as recorded, including false, and cannot clear the lifetime failure or quarantine state.
    /// </remarks>
    internal LinuxFailedSettlementObservation CaptureFailedSettlementObservation(EvidenceProtectedLaunchInput input,
        LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxRunWorkspace workspace,
        LinuxEmptyObservationControlServer server, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(input, _input) || !ReferenceEquals(owner, _owner)
            || !ReferenceEquals(accounts, _accounts) || !ReferenceEquals(workspace, _workspace)
            || token != owner.RootTeardownToken)
            throw LinuxSystemdBackend.InvalidControl();
        var descriptor = server.RequireNegativeReadyDescriptor(input, owner, accounts, workspace, this, token);
        var pending = _pending.Snapshot;
        if (!_lifetime.StartJoined || !_lifetime.StopJoined || !pending.StartReserved
            || !pending.StartJoined || !pending.Closed || !pending.StopJoined
            || _exit?.IsCompleted != true || _output is null || _worker is null)
            throw LinuxSystemdBackend.InvalidControl();
        var state = new LinuxFailedSettlementState(_lifetime.StartJoined, _lifetime.StopJoined,
            _lifetime.Failed, PhysicallySettled, LinuxFailedSettlementObservation.ClosedTaskState(_exit),
            _failedSettlementGroupAfterPumps);
        var observation = LinuxFailedSettlementObservation.CreateDetached(owner.RunId, _worker.SampledFacts,
            descriptor, state, pending, _failedSettlementUnit, _failedSettlementGroup, _output, token,
            EvidenceNativeQualification.CancellationEnabled ? LinuxFailedSettlementScenario.OriginalCancellation
                : LinuxFailedSettlementScenario.WorkerStall);
        owner.RequireControlIdentity(token);
        token.ThrowIfCancellationRequested();
        return observation;
    }

#if EVIDENCE_PRIVATE_N10
    /// <summary>Copies the actual N10 pending-start and post-stop samples after their original joins.</summary>
    /// <param name="input">Original reference-equal protected input.</param>
    /// <param name="owner">Original authenticated owner and teardown token source.</param>
    /// <param name="accounts">Original retained account holder.</param>
    /// <param name="workspace">Original retained workspace.</param>
    /// <param name="token">Original root teardown token; no allowance is renewed.</param>
    /// <returns>Bounded private observations; they prove no admission, custody or successful execution.</returns>
    /// <remarks>The systemd reply trigger is retained by the compile-owned checkpoint. This method only reads
    /// the original pending/lifetime snapshots, final post-pump unit/group sample, and joined output receipt.</remarks>
    internal byte[] CaptureN10PendingStartObservation(EvidenceProtectedLaunchInput input,
        LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxRunWorkspace workspace, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!EvidenceNativeQualification.PendingStartRaceEnabled || !ReferenceEquals(input, _input)
            || !ReferenceEquals(owner, _owner) || !ReferenceEquals(accounts, _accounts)
            || !ReferenceEquals(workspace, _workspace) || token != owner.RootTeardownToken
            || !_lifetime.StartJoined || !_lifetime.StopJoined || Volatile.Read(ref _dispatchAttempted) != 1
            || _failedSettlementUnit is null || _failedSettlementGroup is null || !_failedSettlementGroupAfterPumps
            || _output is null || _recipe is null)
            throw LinuxSystemdBackend.InvalidControl();
        owner.RequireControlIdentity(token);
        var pending = _pending.Snapshot;
        var bytes = _n10PendingStart.EncodeAfterJoin(owner.RunId, Unit, pending,
            startupJoined: _lifetime.StartJoined, startupFailed: _lifetime.Failed,
            stopJoined: _lifetime.StopJoined, physicallySettled: PhysicallySettled,
            stopProcedureCalls: Volatile.Read(ref _n10StopProcedureCalls),
            finalUnit: _failedSettlementUnit, unitStopped: _recipe.HasStopped(_failedSettlementUnit), group: _failedSettlementGroup,
            groupAfterPumps: _failedSettlementGroupAfterPumps, output: _output);
        owner.RequireControlIdentity(token);
        token.ThrowIfCancellationRequested();
        return bytes;
    }
#endif

    /// <summary>Reserves the entire one-attempt startup before any native pipe, connection or unit start.</summary>
    /// <remarks>
    /// Registers both pumps before dispatch, captures actual running unit and kernel identity, then seals
    /// the descriptor before the server may authenticate ready. A failed/canceled startup must still be
    /// joined through StopAndJoinAsync; never delete accounts or retry this generation after rejection.
    /// </remarks>
    internal Task StartAsync(CancellationToken token) => _lifetime.StartAsync(StartCoreAsync, token);

    /// <summary>Gets the actual captured worker for the listener, only while this startup remains usable.</summary>
    internal LinuxProcessIdentity RequireWorker(CancellationToken token)
    {
        RequireActive(token);
        if (!_lifetime.StartJoined || _lifetime.IsClosed || _lifetime.Failed || _worker is null)
            throw LinuxSystemdBackend.InvalidControl();
        _worker.Recheck(token);
        RequireActive(token);
        return _worker;
    }

    /// <summary>Builds fresh ready allowance data after all actual startup bindings and descriptor sealing.</summary>
    /// <remarks>The fixed standalone descriptor remains unchanged; the ready remainder never reuses a stale startup sample.</remarks>
    internal EvidenceWorkerDescriptorData CreateReadyData(CancellationToken token)
    {
        var worker = RequireWorker(token);
        var data = Descriptor(worker, _owner.Remaining);
        if (!_workspace.DescriptorWritten || Convert.ToHexStringLower(SHA256.HashData(data.DescriptorBytes))
            != _workspace.DescriptorSha256) throw LinuxSystemdBackend.InvalidControl();
        RequireActive(token);
        worker.Recheck(token);
        return data;
    }

    /// <summary>Joins the original native exit monitor; no caller timeout wrapper detaches it.</summary>
    /// <remarks>
    /// Call after startup. For cancellation use StopAndJoinAsync, which cancels and joins this exact
    /// monitor. Returned service metadata alone is not physical exit; group/pump joins remain required.
    /// This API belongs to the server owner, never the worker's own stop/wait control handler.
    /// </remarks>
    internal Task<LinuxUnitProperties> WaitForExitAsync()
    {
        lock (_gate)
        {
            if (!_lifetime.StartJoined || _exit is null) throw LinuxSystemdBackend.InvalidControl();
            return _exit;
        }
    }

    /// <summary>Captures N12 live child/group and unfinished output only after the original leader monitor joined, before stop.</summary>
    /// <remarks>
    /// The private frame is PID data, not authentication. This actual holder captures the child through
    /// root-owned retained proc handles using its original worker account and generated cgroup. It checks
    /// the original owner/deadline and unfinished original output task around the sample. Missing, dead,
    /// foreign or early-EOF children reject; the ordinary stop path still contains and joins the unit.
    /// No portable record or Boolean can construct this native holder or supply its saved observation.
    /// </remarks>
    internal async Task ObserveLeaderExitWithDescendantAsync(CancellationToken token)
    {
        if (!EvidenceNativeQualification.DescendantEnabled || _leaderExitObservation is not null
            || _exit?.IsCompletedSuccessfully != true || _naturalTerminal is null || _worker is null || _pipes is null
            || _lifetime.IsClosed) throw LinuxSystemdBackend.InvalidControl();
        _owner.RequireCleanupLaunchInput(_input, token);
        var pid = checked((uint)await _descendant.Observed.WaitAsync(token).ConfigureAwait(false));
        if (pid == _worker.Pid) throw LinuxSystemdBackend.InvalidControl();
        using var child = LinuxProcessIdentity.Capture(pid, _accountData.WorkerUid, _accountData.WorkerGid, Unit, token);
        child.Recheck(token);
        var group = LinuxCgroupProbe.Read(Unit, token);
        var outputJoined = _pipes.JoinAsync().IsCompleted;
        _owner.RequireCleanupLaunchInput(_input, token);
        child.Recheck(token);
        LinuxN12LeaderExitObservation.RequireBeforeStop(_naturalTerminal, _worker.Pid, child.SampledFacts,
            _accountData.WorkerUid, _accountData.WorkerGid, Unit, group, outputJoined);
        _leaderExitObservation = new(_owner.RunId, _worker.Pid, child.SampledFacts, group, _naturalTerminal);
    }

    /// <summary>Copies N12 observations only after original unit, native monitor, both EOFs and final group checks joined.</summary>
    /// <remarks>This bounded private record carries no completion/admission authority; root custody and account closure must still succeed.</remarks>
    internal byte[] CaptureJoinedDescendantObservation()
    {
        if (!EvidenceNativeQualification.DescendantEnabled || _leaderExitObservation is null) throw LinuxSystemdBackend.InvalidControl();
        RequireSuccessfulCompletion();
        _owner.RequireCleanup(default);
        var group = LinuxCgroupProbe.Read(Unit, default);
        if (!LinuxAccountUtility.GroupEmpty(group) || _output is not { Successful: true }) throw LinuxSystemdBackend.InvalidControl();
        var bytes = _leaderExitObservation.EncodeJoined(_output, group);
        _owner.RequireCleanup(default);
        return bytes;
    }

    /// <summary>Closes startup and shares pending containment, full startup join, group and pump finalization.</summary>
    /// <remarks>
    /// Uses one cumulative cleanup allowance bounded by the unchanged original owner/job deadline.
    /// A failed stop/start still attempts finalization. Physical joins never become successful execution
    /// merely because a stop reply or main PID zero was observed. The listener stays owned by the server
    /// until its handlers join; disposal subsequently closes every retained connection before proc FDs.
    /// </remarks>
    internal Task StopAndJoinAsync() => _lifetime.StopAsync(ContainAsync, FinalizeAsync);

    /// <summary>Requires natural zero-status exit, successful original startup/cleanup/output and a live cleanup bound.</summary>
    /// <remarks>A force-stopped worker, nonzero exit, incomplete output or sticky earlier failure cannot pass this guard.</remarks>
    internal void RequireSuccessfulCompletion()
    {
        if (!_lifetime.StopJoined || _lifetime.Failed || !PhysicallySettled || Volatile.Read(ref _failed) != 0
            || _naturalTerminal is not { ExecMainCode: 1, ExecMainStatus: 0 } || _output is not { Successful: true })
            throw LinuxSystemdBackend.InvalidControl();
        _owner.RequireCleanup(default);
    }

    /// <summary>Shares actual worker settlement and listener/identity disposal; no paths or accounts are deleted.</summary>
    /// <remarks>The root server must first join all handler tasks; retained connection closes cannot substitute for handler joins.</remarks>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose.Task);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispose = completion;
        }
        _ = DisposeCoreAsync(completion);
        return new(completion.Task);
    }

    private async Task StartCoreAsync(CancellationToken token)
    {
        try
        {
            RequireActive(token);
            _jobDeadline = CancellationTokenSource.CreateLinkedTokenSource(token, _owner.TeardownCancellation);
            _jobDeadline.CancelAfter(_owner.Remaining);
            // Startup/monitor cancellation is independent from output draining. Stop must not invent EOF.
            _outputDeadline = CancellationTokenSource.CreateLinkedTokenSource(_owner.TeardownCancellation);
            _outputDeadline.CancelAfter(_owner.Remaining);
            _pipes = LinuxOutputPipes.Create();
            _ = _pipes.BeginCollectAsync(_outputDeadline.Token, cancellationPhase:
                EvidenceNativeQualification.CancellationEnabled ? _cancellationPhase : null,
                descendant: EvidenceNativeQualification.DescendantEnabled ? _descendant : null);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(_jobDeadline.Token);
            startup.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.Remaining.Ticks,
                TimeSpan.FromSeconds(_input.Request.StartSeconds).Ticks)));
            _backend = await LinuxSystemdBackend.ConnectAsync(startup.Token).ConfigureAwait(false);
            RequireActive(startup.Token);
            var request = _input.Request;
            _recipe = LinuxWorkerUnit.Create(Unit, _owner.Unit, request.RuntimeHost, request.EntryPath,
                _accountData.WorkerUid, _accountData.WorkerGid, _workspace.ControlSocket, request.ToolRoot,
                request.SubjectRoot, _workspace.OutputParent, _workspace.RawResultsRoot,
                _owner.Remaining, TimeSpan.FromSeconds(request.StoppingSeconds),
                _pipes.StandardOutput, _pipes.StandardError);
            try
            {
                await _pending.StartAsync(ct =>
                {
                    RequireActive(ct);
                    Interlocked.Exchange(ref _dispatchAttempted, 1); // Before ambiguous OS I/O.
#if EVIDENCE_PRIVATE_N10
                    return EvidenceNativeQualification.PendingStartRaceEnabled
                        ? _backend.StartWorkerForN10Async(_recipe, _n10PendingStart, ct)
                        : _backend.StartWorkerAsync(_recipe, ct);
#else
                    return _backend.StartWorkerAsync(_recipe, ct);
#endif
                }, startup.Token).ConfigureAwait(false);
            }
            finally { _pipes.CloseWriteCopies(); } // The original FD-transfer/start task is joined.
            // Observation cancellation may abort its bus connection. Keep it separate from the
            // original starting connection, whose AddRef must survive through final settlement.
            _observations = await LinuxSystemdBackend.ConnectAsync(startup.Token).ConfigureAwait(false);
            while (true)
            {
                RequireActive(startup.Token);
                var facts = await _observations.ReadUnitAsync(Unit, startup.Token).ConfigureAwait(false);
                if (_recipe.HasRunningMain(facts))
                {
                    _worker = LinuxProcessIdentity.Capture(facts.MainPid, _accountData.WorkerUid,
                        _accountData.WorkerGid, Unit, startup.Token);
                    break;
                }
                if (_recipe.HasFinished(facts)) throw LinuxSystemdBackend.InvalidControl();
                RequireOutputNotFailed();
                await Task.Delay(TimeSpan.FromMilliseconds(25), startup.Token).ConfigureAwait(false);
            }
            RequireActive(startup.Token);
            _workspace.WriteDescriptor(Descriptor(_worker, _owner.Remaining).DescriptorBytes, startup.Token);
            _worker.Recheck(startup.Token);
            RequireActive(startup.Token);
            var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_lifetime.IsClosed) throw LinuxSystemdBackend.InvalidControl();
                // The entire startup remains reserved until this original async task is retained.
                // There is no separate result TCS that could publish before the monitor really joins.
                _exit = MonitorExitAsync(dispatch.Task, _jobDeadline.Token);
            }
            dispatch.TrySetResult(); // Native monitor work dispatches only outside the ownership lock.
        }
        catch { Fail(); throw; }
    }

    private async Task<LinuxUnitProperties> MonitorExitAsync(Task dispatch, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        try
        {
            while (true)
            {
                // The worker may exit naturally: owner/input remain live, proc liveness is not demanded here.
                _owner.RequireCleanupLaunchInput(_input, token);
                RequireOutputNotFailed();
                var facts = await _observations!.ReadUnitAsync(Unit, token).ConfigureAwait(false);
                if (_recipe!.HasFinished(facts))
                {
                    if (facts.ExecMainPid != _worker!.Pid) throw LinuxSystemdBackend.InvalidControl();
                    _owner.RequireControlIdentity(token);
                    _naturalTerminal = facts;
                    return facts;
                }
                if (!_recipe.HasRunningMain(facts) || facts.MainPid != _worker!.Pid)
                    throw LinuxSystemdBackend.InvalidControl();
                // Authenticated unit facts retain the captured main PID through natural exit. Requiring
                // live /proc after a running sample would race a legitimate exit; every control I/O
                // separately rechecks the retained live kernel identity before admitting a request.
                await Task.Delay(TimeSpan.FromMilliseconds(25), token).ConfigureAwait(false);
            }
        }
        catch { Fail(); throw LinuxSystemdBackend.InvalidControl(); }
    }

    private async Task ContainAsync()
    {
        try
        {
            _owner.BeginRootTeardown();
            _cleanupDeadline = CancellationTokenSource.CreateLinkedTokenSource(_owner.RootTeardownToken);
        }
        catch { _cleanupDeadline = new CancellationTokenSource(); _cleanupDeadline.Cancel(); Fail(); }
        await _pending.StopAsync(StopSelectedUnitAsync, _cleanupDeadline.Token).ConfigureAwait(false);
    }

    private async Task StopSelectedUnitAsync(CancellationToken token)
    {
#if EVIDENCE_PRIVATE_N10
        if (EvidenceNativeQualification.PendingStartRaceEnabled) Interlocked.Increment(ref _n10StopProcedureCalls);
#endif
        if (Volatile.Read(ref _dispatchAttempted) == 0) return;
        _owner.RequireCleanup(token);
        using var stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        stopDeadline.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.CleanupRemaining.Ticks,
            TimeSpan.FromSeconds(_input.Request.StoppingSeconds).Ticks)));
        using var stop = await LinuxSystemdBackend.ConnectAsync(stopDeadline.Token).ConfigureAwait(false);
        await stop.StopUnitAsync(Unit, stopDeadline.Token).ConfigureAwait(false);
        _owner.RequireCleanup(stopDeadline.Token);
    }

    private async Task FinalizeAsync()
    {
        var failed = false;
        var groupJoined = Volatile.Read(ref _dispatchAttempted) == 0;
        var unitJoined = groupJoined;
        var pipesClosed = _pipes is null;
        var monitorJoined = _exit is null;
        try
        {
            if (!groupJoined)
            {
                while (true)
                {
                    _owner.RequireCleanup(_cleanupDeadline!.Token);
                    // Read on the retained original start connection. Cancellation/error here loses
                    // retention and cannot publish settlement. A job path and empty group are insufficient.
                    var stopped = await _backend!.ReadUnitAsync(Unit, _cleanupDeadline.Token).ConfigureAwait(false);
                    if (_recipe!.HasStopped(stopped)
                        && (_worker is null || stopped.ExecMainPid == _worker.Pid)
                        && LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(Unit, _cleanupDeadline.Token)))
                    { groupJoined = true; unitJoined = true; break; }
                    await Task.Delay(TimeSpan.FromMilliseconds(25), _cleanupDeadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch { failed = true; }
        if (_exit is not null)
        {
            try { await _exit.ConfigureAwait(false); }
            catch { failed = true; }
            monitorJoined = _exit.IsCompleted;
        }
        if (_pipes is not null)
        {
            try { _pipes.CloseWriteCopies(); }
            catch { failed = true; }
            try
            {
                _output = await _pipes.JoinAsync().ConfigureAwait(false);
                if (!_output.Successful) failed = true;
            }
            catch { failed = true; }
            try { await _pipes.DisposeAsync().ConfigureAwait(false); pipesClosed = true; }
            catch { failed = true; }
        }
        try { _observations?.Dispose(); }
        catch { failed = true; unitJoined = false; }
        try
        {
            _owner.RequireCleanup(default);
            if (Volatile.Read(ref _dispatchAttempted) != 0)
            {
                // Keep the original AddRef until this last authenticated terminal/kernel observation
                // after pumps and the original monitor have joined. No renewed deadline is introduced.
                var stopped = await _backend!.ReadUnitAsync(Unit, _cleanupDeadline!.Token).ConfigureAwait(false);
                _failedSettlementUnit = stopped;
                unitJoined &= _recipe!.HasStopped(stopped) && (_worker is null || stopped.ExecMainPid == _worker.Pid);
                var finalGroup = LinuxCgroupProbe.Read(Unit, default);
                _failedSettlementGroup = finalGroup;
                _failedSettlementGroupAfterPumps = true;
                groupJoined &= LinuxAccountUtility.GroupEmpty(finalGroup);
            }
            _backend?.Dispose();
            var pending = _pending.Snapshot;
            if (!pending.StopJoined || pending.IsFailed || (pending.StartReserved && !pending.StartJoined))
                groupJoined = false;
            if (failed || !unitJoined || !groupJoined || !pipesClosed || !monitorJoined) failed = true;
            _owner.RequireCleanup(default);
        }
        catch { failed = true; Interlocked.Exchange(ref _physicallySettled, 0); }
        // Even a failed inspection must attempt the original connection close, after all real task joins.
        try { _backend?.Dispose(); }
        catch { failed = true; Interlocked.Exchange(ref _physicallySettled, 0); }
        if (failed)
        {
            Interlocked.Exchange(ref _physicallySettled, 0);
            Fail();
            throw LinuxSystemdBackend.InvalidControl();
        }
        // Publish only after every fallible final check and close. No concurrent reader may see a
        // transient true projection that a later owner recheck or descriptor close could revoke.
        Interlocked.Exchange(ref _physicallySettled, 1);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        var failed = false;
        try { await StopAndJoinAsync().ConfigureAwait(false); }
        catch { failed = true; }
        try { await _listener.DisposeAsync().ConfigureAwait(false); }
        catch { failed = true; }
        try { _worker?.Dispose(); }
        catch { failed = true; }
        foreach (var source in new[] { _jobDeadline, _outputDeadline, _cleanupDeadline })
        {
            try { source?.Dispose(); }
            catch { failed = true; }
        }
        if (failed) { Fail(); completion.TrySetException(LinuxSystemdBackend.InvalidControl()); }
        else completion.TrySetResult();
    }

    private EvidenceWorkerDescriptorData Descriptor(LinuxProcessIdentity worker, TimeSpan remaining) =>
        EvidenceWorkerDescriptorData.Create(_input.Request, _owner.RunId, checked((int)worker.Pid),
            Environment.ProcessId, _accountData, _workspace.Layout, _workspace.OutputParentIdentity,
            _input.EntrySha256, _input.PolicySha256, remaining);

    private void RequireActive(CancellationToken token)
    {
        if (Volatile.Read(ref _failed) != 0 || _lifetime.IsClosed) throw LinuxSystemdBackend.InvalidControl();
        _owner.RequireLaunchInput(_input, token);
        _accounts.RequireOwnedBy(_owner, token);
        _workspace.RequireOwnedBy(_owner, _accounts, token);
        _listener.RequireOwnedBy(_owner, _accounts, _workspace, token);
        _owner.RequireActive(token);
    }

    private void RequireOutputNotFailed()
    {
        var collected = _pipes!.JoinAsync();
        if (collected.IsCompleted && (!collected.IsCompletedSuccessfully || !collected.Result.Successful))
            throw LinuxSystemdBackend.InvalidControl();
    }

    private void Fail() { Interlocked.Exchange(ref _failed, 1); _workspace.Quarantine(); }
}
