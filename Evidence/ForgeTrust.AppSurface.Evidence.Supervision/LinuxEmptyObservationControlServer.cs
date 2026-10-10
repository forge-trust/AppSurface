using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Owns actual authenticated ready/stop/wait/exit I/O for the genuine empty-targeted Observation checkpoint.</summary>
/// <remarks>
/// The private factory accepts only the same native owners and an already started same-image worker. It
/// independently resolves protected policy/diff bytes; no caller-injected plan, handler, producer or join
/// assertion can enroll a workload. Run/artifact/application requests reject until their native checkpoint
/// is composed. The descendant ledger therefore has no dispatched work in this checkpoint. The worker
/// itself is excluded: its stop/wait request cannot join its own process or active control handler.
/// The N16 private image adds one fixed server-owned blocked workload after registering it in this ledger;
/// ordinary images have no such dispatch or barrier. Exit ACK is only protocol completion. The outer root owner must subsequently join the worker's original
/// native/output tasks, recheck custody, close filesystem ownership and delete accounts strictly.
/// </remarks>
internal sealed class LinuxEmptyObservationControlServer
{
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxControlListener _listener;
    private readonly LinuxWorkerProcess _worker;
    private readonly EvidenceProtectedLaunchInput _input;
    private readonly LinuxRunAccounts _accounts;
    private readonly LinuxRunWorkspace _workspace;
    private LinuxN04CheckpointOwner? _n04;
    private readonly SupervisionWorkRegistry _ledger = new();
    private readonly SupervisionControlSequence _sequence;
    private readonly SupervisionSingleAttempt _run = new();
    private readonly SemaphoreSlim _replyOrder = new(1, 1);
    private readonly TaskCompletionSource _exitCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _negativeReadyCommitted; // A past authenticated write event, not whole-protocol success.
    private readonly TaskCompletionSource _exitIntent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _executionGate = new();
    private Task? _execution;
    private int _ioJoined;
    private int _localOwnersClosed;
    private int _connectionCloseFailed;
    private readonly LinuxControlFailureLatch _failures = new();
    private readonly TaskCompletionSource _cancellationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancellationFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _cancellationSignal;
    private int _signalClaimed;
#if EVIDENCE_PRIVATE_N16
    private readonly object _n16Gate = new();
    private readonly TaskCompletionSource _n16BodyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _n16Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _n16StopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _n16WaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _n16Work;
    private int _n16WorkClaimed;
    private int _n16AcceptedCommitted;
    private int _n16ResponseCommitted;
    private int _n16StopCommitted;
    private int _n16WaitCommitted;
    private int _n16ControlsOverlapped;
#endif

    /// <summary>Whether the original registered signal task joined successfully; not token delivery.</summary>
    internal bool CancellationSignalJoined => _cancellationSignal?.IsCompletedSuccessfully == true;

#if EVIDENCE_PRIVATE_N16
    /// <summary>Projects private N16 past write/task events only after original server and native holder joins.</summary>
    /// <remarks>
    /// Requires the original custody owners, all accepted-body/control joins, authenticated response
    /// writes and the final committed EXIT. This bounded record is failure-only data; it grants no completion, admission,
    /// account deletion, product acceptance or authority to a reader.
    /// </remarks>
    internal byte[] CaptureAcceptedBlockedWork(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxWorkerProcess worker, CancellationToken token)
    {
        RequireCustodyOwner(input, owner, accounts, workspace, worker, token);
        if (!_sequence.ExitAcknowledged || !_exitCommitted.Task.IsCompletedSuccessfully) throw Rejected();
        lock (_n16Gate)
            if (_n16Work?.IsCompletedSuccessfully != true || Volatile.Read(ref _n16WorkClaimed) != 1
                || Volatile.Read(ref _n16AcceptedCommitted) != 1 || Volatile.Read(ref _n16ResponseCommitted) != 1
                || Volatile.Read(ref _n16StopCommitted) != 1 || Volatile.Read(ref _n16WaitCommitted) != 1
                || Volatile.Read(ref _n16ControlsOverlapped) != 1) throw Rejected();
        var bytes = EvidenceCanonicalJson.Serialize(new
        {
            schema = "issue779-accepted-blocked-work-v1", generation = owner.RunId.ToString("N"),
            @case = "N16", accepted_write_committed = true, request_blocked_during_control_overlap = true,
            original_body_joined = true, original_request_response_committed = true,
            stop_write_committed = true, positive_wait_write_committed = true,
            exit_write_committed = true,
            handlers_joined = true, active_workloads = 0, active_controls = 0,
            native_authority = false, native_acceptance = false
        });
        token.ThrowIfCancellationRequested();
        if (bytes.Length is 0 or > 4096) throw Rejected();
        RequireCustodyOwner(input, owner, accounts, workspace, worker, token);
        return bytes;
    }
#endif

    /// <summary>Projects the original signal task only after the existing server/holder joins.</summary>
    /// <remarks>Successful syscall and READY observations still cannot establish caller-token delivery.</remarks>
    internal string CaptureCancellationSignal(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxWorkerProcess worker, CancellationToken token)
    {
        RequireCustodyOwner(input, owner, accounts, workspace, worker, token);
        if (!CancellationSignalJoined || Volatile.Read(ref _signalClaimed) != 1
            || !_worker.CancellationPhaseObserved.IsCompletedSuccessfully || Volatile.Read(ref _negativeReadyCommitted) != 1)
            throw Rejected();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = "issue779-cancellation-signal-v1", generation = _owner.RunId.ToString("N"),
            @case = EvidenceOriginalCancellationCheckpoint.Case, signal = 2, syscall_exit = 0,
            original_signal_task_joined = true, ready_committed = true, phase_observed = true,
            native_authority = false
        });
    }

    private async Task SignalCancellationOwnedAsync(Task dispatch, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        try
        {
            await _cancellationReady.Task.WaitAsync(token).ConfigureAwait(false);
            await _worker.CancellationPhaseObserved.WaitAsync(token).ConfigureAwait(false);
            _worker.RequireServerOwner(_input, _owner, _accounts, _workspace, _listener, token);
            var identity = _worker.RequireWorker(token);
            if (Interlocked.Exchange(ref _signalClaimed, 1) != 0) throw Rejected();
            LinuxOriginalCancellationSignal.Send(_worker, identity, _owner, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (Recoverable(error))
        {
            _cancellationFailed.TrySetException(error);
            _ = _cancellationFailed.Task.Exception;
            throw;
        }
    }

    /// <summary>Gets first caught server-fault data; this establishes no protocol or native outcome.</summary>
    internal LinuxControlFailure? FirstFailure => _failures.First;

    private LinuxEmptyObservationControlServer(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxControlListener listener, LinuxWorkerProcess worker)
    {
        _input = input; _owner = owner; _listener = listener; _worker = worker;
        _accounts = accounts; _workspace = workspace;
        _sequence = new(_ledger, StopEmptyDescendantsAsync);
    }

    /// <summary>Binds the original actual owners and verifies a genuine resolved empty targeted policy before server construction.</summary>
    /// <remarks>This cannot activate the reserved supervisor entry or qualify an application/consumer.</remarks>
    internal static LinuxEmptyObservationControlServer Create(EvidenceProtectedLaunchInput input,
        LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxRunWorkspace workspace,
        LinuxControlListener listener, LinuxWorkerProcess worker, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts); ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(listener); ArgumentNullException.ThrowIfNull(worker);
        worker.ClaimControlServer(input, owner, accounts, workspace, listener, token);
        _ = EvidenceEmptyObservationPlan.FromInput(input, token);
        _ = worker.RequireWorker(token);
        worker.RequireServerOwner(input, owner, accounts, workspace, listener, token);
        return new(input, owner, accounts, workspace, listener, worker);
    }

    /// <summary>Registers and joins every original accept, handler, response write and accepted connection close.</summary>
    /// <param name="token">Independent root/control lifetime; a worker's per-operation cancellation never owns this token.</param>
    /// <remarks>
    /// One pending accept and at most 32 handlers are retained. Per-request I/O is capped by the protected
    /// admission bound and original job remainder. Cleanup has one cumulative stop/cleanup bound; later
    /// requests cannot reset it. The reply semaphore orders ACK commits, including a client which sends
    /// its next request immediately after receiving LF while the prior server continuation still runs.
    /// No handler calls this drain or the worker's final stop/join. Caller cancellation closes actual I/O;
    /// every original operation remains joined before return, including ignored cancellation.
    /// A valid EXIT claim signals intent, forbids further accept dispatch and joins listening admission before
    /// ACK bytes. Intent grants no successful commit. Current handlers and connections remain retained through
    /// writes, release and final owner checks, then full listener disposal closes the native named handles.
    /// </remarks>
    internal Task RunAsync(CancellationToken token)
    {
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task execution;
        lock (_executionGate)
        {
            _run.Claim();
            execution = RunOwnedAsync(dispatch.Task, token);
            _execution = execution;
        }
        dispatch.SetResult();
        return execution;
    }

    /// <summary>Requires actual completed server I/O and original native references before filesystem custody.</summary>
    /// <remarks>Protocol failure may remain; this guard grants neither successful execution nor account deletion.</remarks>
    internal void RequireCustodyOwner(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxWorkerProcess worker, CancellationToken token)
    {
        if (!ReferenceEquals(input, _input) || !ReferenceEquals(owner, _owner)
            || !ReferenceEquals(accounts, _accounts) || !ReferenceEquals(workspace, _workspace)
            || !ReferenceEquals(worker, _worker)) throw Rejected();
        owner.RequireControlIdentity(token);
        lock (_executionGate)
            if (_execution?.IsCompleted != true || Volatile.Read(ref _ioJoined) == 0
                || Volatile.Read(ref _localOwnersClosed) == 0 || Volatile.Read(ref _connectionCloseFailed) != 0
                || !_ledger.IsControlAdmissionClosed || _ledger.ActiveControls != 0
                || !_ledger.IsWorkloadAdmissionClosed || _ledger.ActiveWorkloads != 0) throw Rejected();
        owner.RequireControlIdentity(token);
    }

    /// <summary>Gets descriptor data only after an actual peer-bound READY write committed and handlers joined.</summary>
    /// <param name="input">Reference-equal original protected input.</param>
    /// <param name="owner">Original actual owner.</param>
    /// <param name="accounts">Original actual accounts.</param>
    /// <param name="workspace">Original retained workspace and immutable sealed descriptor.</param>
    /// <param name="worker">Original selected worker, never caller PID data.</param>
    /// <param name="token">Original cleanup token, not a new deadline.</param>
    /// <returns>Descriptor SHA256 data; it authenticates neither an external record nor a subsequent action.</returns>
    /// <remarks>
    /// Unlike successful-sequence ReadyAcknowledged, the past event remains observable after a later
    /// negative outcome. It is set only after existing authenticated response I/O, connection release,
    /// owner checks and CompleteWrite succeed. Missing events reject; no failure flag is cleared.
    /// </remarks>
    internal string RequireNegativeReadyDescriptor(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxWorkerProcess worker, CancellationToken token)
    {
        RequireCustodyOwner(input, owner, accounts, workspace, worker, token);
        if (Volatile.Read(ref _negativeReadyCommitted) != 1 || _workspace.DescriptorSha256 is not { } descriptor)
            throw Rejected();
        return descriptor;
    }

    /// <summary>Obtains the actual drained listener's original named-inode comparison data for native custody.</summary>
    /// <remarks>Does not seal an inode, clear any failure or release accounts.</remarks>
    internal LinuxProtectedMetadata RequireCustodySocket(CancellationToken token)
    {
        RequireCustodyOwner(_input, _owner, _accounts, _workspace, _worker, token);
        return _listener.RequireCustodySocket(_owner, _accounts, _workspace, token);
    }

    /// <summary>Requires the original successful server task and all committed protocol steps before final-file claims.</summary>
    /// <remarks>Physical custody after a failed protocol remains possible, but cannot pass this success guard.</remarks>
    internal void RequireSuccessfulCompletion(CancellationToken token)
    {
        RequireCustodyOwner(_input, _owner, _accounts, _workspace, _worker, token);
        lock (_executionGate)
            if (_execution?.IsCompletedSuccessfully != true || !_sequence.ExitAcknowledged
                || !_ledger.IsSettled) throw Rejected();
        _owner.RequireControlIdentity(token);
    }

    private async Task RunOwnedAsync(Task dispatch, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        try
        {
            // Fixed private N04 image: acquisition belongs to the already registered original run.
            // No caller/environment selector or detached checkpoint task exists.
            if (EvidenceNativeQualification.PeerReplacementEnabled)
                _n04 = LinuxN04CheckpointOwner.Create(_input, _owner, _accounts, _workspace, _listener, _worker, token);
            await RunCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            var closed = true;
            if (_n04 is not null)
                try { await _n04.CloseAndJoinAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error))
                { _failures.Capture(LinuxControlFailureStage.HandlerJoin, null, error); closed = false; _sequence.RecordFailure(); }
            try { _replyOrder.Dispose(); }
            catch (Exception error) when (Recoverable(error))
            { _failures.Capture(LinuxControlFailureStage.ReplyGateClose, null, error); closed = false; _sequence.RecordFailure(); }
            if (!closed) throw Rejected();
            Interlocked.Exchange(ref _localOwnersClosed, 1);
        }
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        var stage = LinuxControlFailureStage.PeerCheck;
        try
        {
        var peer = _worker.RequireWorker(token);
        stage = LinuxControlFailureStage.WorkerExitTask;
        var naturalExit = _worker.WaitForExitAsync();
        stage = LinuxControlFailureStage.RequestLifetime;
        using var requests = CancellationTokenSource.CreateLinkedTokenSource(token, _owner.TeardownCancellation);
        stage = LinuxControlFailureStage.RequestLifetime;
        requests.CancelAfter(_owner.CleanupRemaining);
        stage = LinuxControlFailureStage.RequestLifetime;
        using var accepts = CancellationTokenSource.CreateLinkedTokenSource(requests.Token);
        stage = LinuxControlFailureStage.RequestLifetime;
        var handlers = new List<Task>(SupervisionWorkRegistry.MaximumActiveControls);
        Task<LinuxControlConnection>? pending = null;
        using var cancellationLifetime = CancellationTokenSource.CreateLinkedTokenSource(requests.Token);
        var signalDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellationSignal = EvidenceNativeQualification.CancellationEnabled
            ? SignalCancellationOwnedAsync(signalDispatch.Task, cancellationLifetime.Token)
            : Task.CompletedTask;
        signalDispatch.SetResult(); // Task retained before its actual wait/syscall dispatch, outside gates.
        try
        {
            while (!_exitIntent.Task.IsCompleted && !_exitCommitted.Task.IsCompleted && !naturalExit.IsCompleted)
            {
                stage = LinuxControlFailureStage.AcceptLoop;
                requests.Token.ThrowIfCancellationRequested();
                if (_cancellationFailed.Task.IsCompleted) await _cancellationSignal.ConfigureAwait(false);
                // Join each original completed handler before removing its retained task. No proxy
                // wait or success snapshot substitutes for observing the actual procedure.
                for (var index = handlers.Count - 1; index >= 0; index--)
                    if (handlers[index].IsCompleted)
                    {
                        stage = LinuxControlFailureStage.HandlerJoin;
                        await handlers[index].ConfigureAwait(false);
                        handlers.RemoveAt(index);
                    }
                if (handlers.Count == SupervisionWorkRegistry.MaximumActiveControls)
                {
                    stage = LinuxControlFailureStage.CapacityWait;
                    await Task.WhenAny(handlers.Append(_exitIntent.Task).Append(_exitCommitted.Task).Append(naturalExit).Append(_cancellationFailed.Task))
                        .WaitAsync(requests.Token).ConfigureAwait(false);
                    continue;
                }
                // This pause occurs before RegisterAccept's synchronous worker/name checks.
                // It is separate from the irreversible EXIT admission-close barrier.
                if (_n04 is not null) await _n04.BeforeNextAcceptAsync(requests.Token).ConfigureAwait(false);
                stage = LinuxControlFailureStage.Accept;
                SupervisionAcceptOwnership<LinuxControlConnection>.RegisteredAccept registeredAccept;
                lock (_executionGate)
                {
                    if (_exitIntent.Task.IsCompleted) break;
                    registeredAccept = _listener.RegisterAccept(peer, accepts.Token);
                }
                pending = registeredAccept.Task;
                registeredAccept.Dispatch(); // Actual accept work starts outside both admission gates.
                stage = LinuxControlFailureStage.AcceptJoin;
                var next = await Task.WhenAny(pending, _exitIntent.Task, _exitCommitted.Task, naturalExit, _cancellationFailed.Task)
                    .WaitAsync(requests.Token).ConfigureAwait(false);
                if (ReferenceEquals(next, _cancellationFailed.Task)) await _cancellationSignal.ConfigureAwait(false);
                if (_exitIntent.Task.IsCompleted || !ReferenceEquals(next, pending)) break;
                stage = LinuxControlFailureStage.AcceptJoin;
                var connection = await pending.ConfigureAwait(false);
                pending = null;
                stage = LinuxControlFailureStage.ControlRegistration;
                var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_executionGate)
                {
                    if (_exitIntent.Task.IsCompleted) break; // Published result remains listener-owned for final close.
                    _n04?.ReserveNextAcceptBeforeHandlerDispatch();
                    var control = _ledger.BeginControl();
                    stage = LinuxControlFailureStage.HandlerDispatch;
                    handlers.Add(HandleRegisteredAsync(dispatch.Task, connection, control, requests.Token));
                }
                dispatch.SetResult();
            }
        }
        catch (Exception error) when (Recoverable(error))
        {
            _failures.Capture(stage, null, error,
                stage is LinuxControlFailureStage.Accept or LinuxControlFailureStage.AcceptJoin ? _listener.FirstFailure : null);
            _sequence.RecordFailure();
        }
        finally
        {
            var ioJoined = true;
            // Cancel and join this original added task under the same requests/root deadline.
            try { cancellationLifetime.Cancel(); }
            catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); ioJoined = false; _sequence.RecordFailure(); }
            try { await _cancellationSignal.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationLifetime.IsCancellationRequested) { }
            catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); ioJoined = false; _sequence.RecordFailure(); }
            stage = LinuxControlFailureStage.AcceptCancel;
            try { accepts.Cancel(); }
            catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); _sequence.RecordFailure(); }
            // Interrupt and join original root-rendezvous I/O before joining its parent handlers.
            try { if (_n04 is not null) await _n04.CloseAndJoinAsync().ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error))
            { _failures.Capture(LinuxControlFailureStage.HandlerJoin, null, error); ioJoined = false; _sequence.RecordFailure(); }
            // Valid EXIT intent retains live handlers through their writes. Failure without EXIT still
            // closes all results first to interrupt their original I/O, as before.
            var exitIntent = _exitIntent.Task.IsCompleted;
            stage = LinuxControlFailureStage.ListenerClose;
            try
            {
                if (exitIntent) await _listener.CloseAcceptAdmissionAsync().ConfigureAwait(false);
                else await _listener.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error, _listener.FirstFailure); ioJoined = false; _sequence.RecordFailure(); }
            if (pending is not null)
            {
                stage = LinuxControlFailureStage.PendingAcceptJoin;
                try { await pending.ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { /* Listener drain independently latches unexpected failure; pending accept cancellation is expected. */ }
            }
            stage = LinuxControlFailureStage.HandlersJoin;
            try { await Task.WhenAll(handlers).ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); ioJoined = false; _sequence.RecordFailure(); }
            if (exitIntent)
            {
                stage = LinuxControlFailureStage.ListenerClose;
                try { await _listener.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error, _listener.FirstFailure); ioJoined = false; _sequence.RecordFailure(); }
            }
            stage = LinuxControlFailureStage.DescendantsStop;
            try { await _sequence.StopAsync(CleanupToken()).ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); _sequence.RecordFailure(); }
            stage = LinuxControlFailureStage.ControlsJoin;
            try { await _ledger.CloseAndJoinControlsAsync().ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); ioJoined = false; _sequence.RecordFailure(); }
            if (ioJoined && Volatile.Read(ref _connectionCloseFailed) == 0)
                Interlocked.Exchange(ref _ioJoined, 1);
        }
        stage = LinuxControlFailureStage.FinalCancellation;
        token.ThrowIfCancellationRequested();
        stage = LinuxControlFailureStage.CleanupBound;
        RequireCleanupBound();
        stage = LinuxControlFailureStage.OwnerCheck;
        _owner.RequireControlIdentity(default);
        if (!_sequence.ExitAcknowledged || !_ledger.IsSettled)
        {
            _failures.Capture(naturalExit.IsCompleted && !_exitCommitted.Task.IsCompleted
                ? LinuxControlFailureStage.WorkerTerminalTaskCompleted : LinuxControlFailureStage.ProtocolIncomplete, null, null);
            throw Rejected();
        }
        stage = LinuxControlFailureStage.CleanupBound;
        RequireCleanupBound();
        }
        catch (Exception error) when (Recoverable(error))
        { _failures.Capture(stage, null, error); throw; }
    }

    private async Task HandleRegisteredAsync(Task dispatch, LinuxControlConnection connection,
        SupervisionWorkRegistry.Control control, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        await HandleAsync(connection, control, token).ConfigureAwait(false);
    }

    private async Task HandleAsync(LinuxControlConnection connection, SupervisionWorkRegistry.Control control,
        CancellationToken rootToken)
    {
        SupervisionControlSequence.Reply? claim = null;
        var heldReplyOrder = false;
        var released = false;
        var committedExit = false;
        CancellationTokenRegistration cleanupCancellation = default;
        var isCleanupRequest = false;
        var stage = LinuxControlFailureStage.RequestLifetime;
        EvidenceControlOperation? operation = null;
        try
        {
        using var io = CancellationTokenSource.CreateLinkedTokenSource(rootToken);
        try
        {
            io.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.CleanupRemaining.Ticks,
                TimeSpan.FromSeconds(_input.Request.AdmissionSeconds).Ticks)));
            stage = LinuxControlFailureStage.RequestRead;
            var request = await connection.ReadRequestAsync(io.Token).ConfigureAwait(false);
            operation = request.Operation;
            stage = LinuxControlFailureStage.RequestClassify;
            isCleanupRequest = request is EvidenceStopControlRequest or EvidenceWaitControlRequest or EvidenceExitControlRequest;
            if (isCleanupRequest)
            {
                stage = LinuxControlFailureStage.CleanupRegistration;
                var cleanupToken = CleanupToken();
                cleanupCancellation = cleanupToken.UnsafeRegister(static state =>
                    ((CancellationTokenSource)state!).Cancel(), io);
                cleanupToken.ThrowIfCancellationRequested();
            }
            // STOP closes work admission before waiting behind an earlier response. It never joins
            // this control handler or the worker that issued the request.
            if (request is EvidenceStopControlRequest)
            {
                stage = LinuxControlFailureStage.Stop;
                var stopTask = _sequence.StopAsync(CleanupToken());
#if EVIDENCE_PRIVATE_N16
                if (EvidenceNativeQualification.AcceptedBlockedWorkEnabled)
                    _n16StopStarted.TrySetResult();
#endif
                await stopTask.ConfigureAwait(false);
            }
            // WAIT joins only an already owned STOP, outside response ordering. It never starts
            // containment itself or holds the reply gate while that original procedure is pending.
            else if (request is EvidenceWaitControlRequest)
            {
                stage = LinuxControlFailureStage.WaitJoin;
#if EVIDENCE_PRIVATE_N16
                if (EvidenceNativeQualification.AcceptedBlockedWorkEnabled && Volatile.Read(ref _n16WorkClaimed) == 1)
                {
                    lock (_n16Gate)
                        if (_n16Work?.IsCompleted == false && _ledger.ActiveWorkloads == 1 && _ledger.ActiveControls >= 3)
                            Volatile.Write(ref _n16ControlsOverlapped, 1);
                    _n16WaitStarted.TrySetResult();
                    await _n16StopStarted.Task.WaitAsync(io.Token).ConfigureAwait(false);
                }
#endif
                await _sequence.JoinStartedStopAsync().ConfigureAwait(false);
            }
#if EVIDENCE_PRIVATE_N16
            else if (request is EvidenceAcceptedBlockedWorkControlRequest)
            {
                if (!EvidenceNativeQualification.AcceptedBlockedWorkEnabled) throw Rejected();
                lock (_owner.WorkAdmissionGate)
                {
                    _owner.RequireActive(io.Token);
                    lock (_n16Gate)
                    {
                        stage = LinuxControlFailureStage.RequestClassify;
                        if (Interlocked.Exchange(ref _n16WorkClaimed, 1) != 0) throw Rejected();
                        var registration = _ledger.BeginWorkload();
                        _n16Work = RunN16BlockedWorkAsync(registration);
                    }
                }
                await _n16BodyEntered.Task.WaitAsync(io.Token).ConfigureAwait(false);
                // Acceptance is an intermediate frame on this original connection. Do not
                // hold the reply-order gate while the actual workload/request remains blocked.
                await connection.WriteAcceptedBlockedWorkAsync(io.Token).ConfigureAwait(false);
                Volatile.Write(ref _n16AcceptedCommitted, 1);
                Task originalWork;
                lock (_n16Gate) originalWork = _n16Work ?? throw Rejected();
                // The handler wait may cancel so final server draining can reach containment.
                // Its original body stays retained in _n16Work and the workload ledger; the
                // stop callback below always releases and joins that task before custody.
                await originalWork.WaitAsync(io.Token).ConfigureAwait(false);
            }
#endif
            stage = LinuxControlFailureStage.ReplyGate;
            await _replyOrder.WaitAsync(io.Token).ConfigureAwait(false);
            heldReplyOrder = true;
            byte[] response;
            switch (request)
            {
                case EvidenceReadyControlRequest:
                    stage = LinuxControlFailureStage.ReadyAuthorization;
                    _owner.RequireActive(io.Token);
                    // Connect/read above kept its Admission bound. The fixed root-helper audit
                    // now precedes admission and consumes this handler's original root/job token.
                    io.Token.ThrowIfCancellationRequested();
                    io.CancelAfter(Timeout.InfiniteTimeSpan);
                    await connection.WritePreparationAsync((long)_owner.Remaining.TotalMilliseconds, io.Token).ConfigureAwait(false);
                    if (_n04 is not null) await _n04.AdmissionPreparedAsync(io.Token).ConfigureAwait(false);
                    io.Token.ThrowIfCancellationRequested(); _owner.RequireActive(io.Token);
                    io.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.Remaining.Ticks,
                        TimeSpan.FromSeconds(_input.Request.AdmissionSeconds).Ticks)));
                    await connection.WriteAdmissionStartAsync(io.Token).ConfigureAwait(false);
                    stage = LinuxControlFailureStage.ReadyClaim;
                    claim = _sequence.ClaimReady();
                    stage = LinuxControlFailureStage.ReadyData;
                    response = _worker.CreateReadyData(io.Token).ReadyBytes;
                    if (_n04 is not null) await _n04.ReadyPreparedAsync(io.Token).ConfigureAwait(false);
                    break;
                case EvidenceStopControlRequest:
                    stage = LinuxControlFailureStage.CleanupBound;
                    RequireCleanupBound();
                    stage = LinuxControlFailureStage.ResponseData;
                    response = EvidenceCanonicalJson.Serialize(new { ok = true });
                    break;
                case EvidenceWaitControlRequest:
                    stage = LinuxControlFailureStage.CleanupBound;
                    RequireCleanupBound();
                    stage = LinuxControlFailureStage.WaitClaim;
                    claim = _sequence.ClaimWait();
                    stage = LinuxControlFailureStage.ResponseData;
                    response = EvidenceCanonicalJson.Serialize(new { ok = true, owned_exit = claim.Positive });
                    break;
#if EVIDENCE_PRIVATE_N16
                case EvidenceAcceptedBlockedWorkControlRequest:
                    response = EvidenceCanonicalJson.Serialize(new { ok = true, work_joined = true });
                    break;
#endif
                case EvidenceExitControlRequest:
                    stage = LinuxControlFailureStage.CleanupBound;
                    RequireCleanupBound();
                    stage = LinuxControlFailureStage.OwnerCheck;
                    _owner.RequireControlIdentity(io.Token);
                    stage = LinuxControlFailureStage.ExitClaim;
                    claim = _sequence.ClaimExit();
                    lock (_executionGate) _exitIntent.TrySetResult(); // Intent is not ACK or success.
                    stage = LinuxControlFailureStage.ListenerClose;
                    await _listener.CloseAcceptAdmissionAsync().ConfigureAwait(false);
                    RequireCleanupBound();
                    stage = LinuxControlFailureStage.ResponseData;
                    response = EvidenceCanonicalJson.Serialize(new { ok = true });
                    break;
                default:
                    throw Rejected();
            }
            stage = LinuxControlFailureStage.ResponseWrite;
            await connection.WriteResponseAsync(response, io.Token).ConfigureAwait(false);
            stage = LinuxControlFailureStage.ConnectionRelease;
            await _listener.ReleaseAsync(connection).ConfigureAwait(false);
            released = true;
            stage = LinuxControlFailureStage.PostWriteCheck;
            if (isCleanupRequest) RequireCleanupBound();
            stage = LinuxControlFailureStage.OwnerCheck;
            _owner.RequireControlIdentity(io.Token);
            stage = LinuxControlFailureStage.PostWriteCheck;
            if (isCleanupRequest) RequireCleanupBound();
#if EVIDENCE_PRIVATE_N16
            if (request is EvidenceAcceptedBlockedWorkControlRequest)
                Volatile.Write(ref _n16ResponseCommitted, 1);
            else if (request is EvidenceStopControlRequest && Volatile.Read(ref _n16WorkClaimed) == 1)
                Volatile.Write(ref _n16StopCommitted, 1);
#endif
            if (claim is not null)
            {
                stage = LinuxControlFailureStage.ReplyCommit;
                _sequence.CompleteWrite(claim, true);
#if EVIDENCE_PRIVATE_N16
                if (claim.Operation == EvidenceControlOperation.Wait && claim.Positive && Volatile.Read(ref _n16WorkClaimed) == 1)
                    Volatile.Write(ref _n16WaitCommitted, 1);
#endif
                var readyCommitted = claim.Operation == EvidenceControlOperation.Ready;
                if (readyCommitted)
                {
                    Interlocked.Exchange(ref _negativeReadyCommitted, 1);
                    _cancellationReady.TrySetResult(); // Only after original write/release/owner/CompleteWrite.
                }
                committedExit = claim.Operation == EvidenceControlOperation.Exit;
                claim = null;
                if (readyCommitted && _n04 is not null)
                    await _n04.ReadyCommittedAsync(io.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (Recoverable(error))
        {
            _failures.Capture(stage, operation, error,
                stage == LinuxControlFailureStage.ListenerClose ? _listener.FirstFailure : null);
            if (claim is not null)
            {
                try { _sequence.CompleteWrite(claim, false); }
                catch (EvidenceAdmissionException commitError) { _failures.Capture(LinuxControlFailureStage.HandlerFailureCommit, operation, commitError); /* Failed write remains consumed and latched. */ }
            }
            _sequence.RecordFailure();
        }
        finally
        {
            if (!released)
            {
                stage = LinuxControlFailureStage.ConnectionRelease;
                try { await _listener.ReleaseAsync(connection).ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error))
                { _failures.Capture(stage, operation, error); Interlocked.Exchange(ref _connectionCloseFailed, 1); _sequence.RecordFailure(); }
            }
            stage = LinuxControlFailureStage.ReplyGateRelease;
            if (heldReplyOrder) _replyOrder.Release();
            stage = LinuxControlFailureStage.ControlRelease;
            control.Dispose();
            stage = LinuxControlFailureStage.CleanupRegistrationClose;
            cleanupCancellation.Dispose();
            stage = LinuxControlFailureStage.ExitCommit;
            if (committedExit) _exitCommitted.TrySetResult();
        }
        }
        catch (Exception error) when (Recoverable(error))
        {
            _failures.Capture(stage, operation, error,
                stage == LinuxControlFailureStage.ListenerClose ? _listener.FirstFailure : null);
            throw;
        }
    }

    private Task<SupervisionControlJoinFacts> StopEmptyDescendantsAsync(CancellationToken token)
    {
#if EVIDENCE_PRIVATE_N16
        if (EvidenceNativeQualification.AcceptedBlockedWorkEnabled)
        {
            if (!_ledger.IsWorkloadAdmissionClosed) throw Rejected();
            return StopAndJoinN16WorkAsync(token);
        }
#endif
        _owner.RequireControlIdentity(token);
        if (!_ledger.IsWorkloadAdmissionClosed || _ledger.ActiveWorkloads != 0) throw Rejected();
        // This private server has no work-dispatch path or external workload registration API.
        // These are observations of the genuine empty descendant set, not worker exit assertions.
        return Task.FromResult(new SupervisionControlJoinFacts(true, true, true));
    }

#if EVIDENCE_PRIVATE_N16
    private async Task<SupervisionControlJoinFacts> StopAndJoinN16WorkAsync(CancellationToken token)
    {
        Task? n16Work;
        lock (_n16Gate) n16Work = _n16Work;
        if (n16Work is null)
        {
            if (_ledger.ActiveWorkloads != 0) throw Rejected();
            _owner.RequireControlIdentity(token);
            return new SupervisionControlJoinFacts(true, true, true);
        }
        if (_ledger.ActiveWorkloads != 1) throw Rejected();
        // Release the fixed accepted body, then join its original task even if the caller's
        // cleanup token is canceled. The body has no external work and completes on this signal.
        try { await _n16WaitStarted.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            // Even expiry or a missing WAIT must release and join the original body. Neither
            // canceled handler waits nor failed replies can erase its workload registration.
            _n16Release.TrySetResult();
            await n16Work.ConfigureAwait(false);
        }
        _owner.RequireControlIdentity(token);
        if (_ledger.ActiveWorkloads != 0) throw Rejected();
        return new SupervisionControlJoinFacts(true, true, true);
    }

    private async Task RunN16BlockedWorkAsync(SupervisionWorkRegistry.Workload registration)
    {
        _n16BodyEntered.TrySetResult();
        try { await _n16Release.Task.ConfigureAwait(false); }
        finally { registration.Complete(); }
    }
#endif

    private CancellationToken CleanupToken()
    {
        _owner.BeginRootTeardown();
        return _owner.RootTeardownToken;
    }

    private void RequireCleanupBound()
    {
        var token = CleanupToken();
        token.ThrowIfCancellationRequested();
        _owner.RequireControlIdentity(default);
        token.ThrowIfCancellationRequested();
    }

    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The protected empty Observation control operation was rejected.");
}
