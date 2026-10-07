using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Owns actual authenticated ready/stop/wait/exit I/O for the genuine empty-targeted Observation checkpoint.</summary>
/// <remarks>
/// The private factory accepts only the same native owners and an already started same-image worker. It
/// independently resolves protected policy/diff bytes; no caller-injected plan, handler, producer or join
/// assertion can enroll a workload. Run/artifact/application requests reject until their native checkpoint
/// is composed. The descendant ledger therefore has no dispatched work in this checkpoint. The worker
/// itself is excluded: its stop/wait request cannot join its own process or active control handler.
/// Exit ACK is only protocol completion. The outer root owner must subsequently join the worker's original
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
    private readonly SupervisionWorkRegistry _ledger = new();
    private readonly SupervisionControlSequence _sequence;
    private readonly SupervisionSingleAttempt _run = new();
    private readonly SemaphoreSlim _replyOrder = new(1, 1);
    private readonly TaskCompletionSource _exitCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _executionGate = new();
    private Task? _execution;
    private int _ioJoined;
    private int _localOwnersClosed;
    private int _connectionCloseFailed;

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
        try { await RunCoreAsync(token).ConfigureAwait(false); }
        finally
        {
            var closed = true;
            try { _replyOrder.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { closed = false; _sequence.RecordFailure(); }
            if (!closed) throw Rejected();
            Interlocked.Exchange(ref _localOwnersClosed, 1);
        }
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        var peer = _worker.RequireWorker(token);
        var naturalExit = _worker.WaitForExitAsync();
        using var requests = CancellationTokenSource.CreateLinkedTokenSource(token, _owner.TeardownCancellation);
        requests.CancelAfter(_owner.CleanupRemaining);
        using var accepts = CancellationTokenSource.CreateLinkedTokenSource(requests.Token);
        var handlers = new List<Task>(SupervisionWorkRegistry.MaximumActiveControls);
        Task<LinuxControlConnection>? pending = null;
        try
        {
            while (!_exitCommitted.Task.IsCompleted && !naturalExit.IsCompleted)
            {
                requests.Token.ThrowIfCancellationRequested();
                // Join each original completed handler before removing its retained task. No proxy
                // wait or success snapshot substitutes for observing the actual procedure.
                for (var index = handlers.Count - 1; index >= 0; index--)
                    if (handlers[index].IsCompleted)
                    {
                        await handlers[index].ConfigureAwait(false);
                        handlers.RemoveAt(index);
                    }
                if (handlers.Count == SupervisionWorkRegistry.MaximumActiveControls)
                {
                    await Task.WhenAny(handlers.Append(_exitCommitted.Task).Append(naturalExit))
                        .WaitAsync(requests.Token).ConfigureAwait(false);
                    continue;
                }
                pending = _listener.AcceptAsync(peer, accepts.Token);
                var next = await Task.WhenAny(pending, _exitCommitted.Task, naturalExit)
                    .WaitAsync(requests.Token).ConfigureAwait(false);
                if (!ReferenceEquals(next, pending)) break;
                var connection = await pending.ConfigureAwait(false);
                pending = null;
                var control = _ledger.BeginControl();
                // The handler scope already owns all I/O before the actual async procedure can run.
                handlers.Add(HandleAsync(connection, control, requests.Token));
            }
        }
        catch (Exception error) when (Recoverable(error)) { _sequence.RecordFailure(); }
        finally
        {
            var ioJoined = true;
            try { accepts.Cancel(); }
            catch (Exception error) when (Recoverable(error)) { _sequence.RecordFailure(); }
            // Close admission/socket/results first to interrupt pending I/O, then join the original
            // accept and handler tasks. Dispose does not claim that their read/write tasks have joined.
            try { await _listener.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error)) { ioJoined = false; _sequence.RecordFailure(); }
            if (pending is not null)
            {
                try { await pending.ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { /* Listener drain independently latches unexpected failure. */ }
            }
            try { await Task.WhenAll(handlers).ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error)) { ioJoined = false; _sequence.RecordFailure(); }
            try { await _sequence.StopAsync(CleanupToken()).ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error)) { _sequence.RecordFailure(); }
            try { await _ledger.CloseAndJoinControlsAsync().ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error)) { ioJoined = false; _sequence.RecordFailure(); }
            if (ioJoined && Volatile.Read(ref _connectionCloseFailed) == 0)
                Interlocked.Exchange(ref _ioJoined, 1);
        }
        token.ThrowIfCancellationRequested();
        RequireCleanupBound();
        _owner.RequireControlIdentity(default);
        if (!_sequence.ExitAcknowledged || !_ledger.IsSettled) throw Rejected();
        RequireCleanupBound();
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
        using var io = CancellationTokenSource.CreateLinkedTokenSource(rootToken);
        try
        {
            io.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.CleanupRemaining.Ticks,
                TimeSpan.FromSeconds(_input.Request.AdmissionSeconds).Ticks)));
            var request = await connection.ReadRequestAsync(io.Token).ConfigureAwait(false);
            isCleanupRequest = request is EvidenceStopControlRequest or EvidenceWaitControlRequest or EvidenceExitControlRequest;
            if (isCleanupRequest)
            {
                var cleanupToken = CleanupToken();
                cleanupCancellation = cleanupToken.UnsafeRegister(static state =>
                    ((CancellationTokenSource)state!).Cancel(), io);
                cleanupToken.ThrowIfCancellationRequested();
            }
            // STOP closes work admission before waiting behind an earlier response. It never joins
            // this control handler or the worker that issued the request.
            if (request is EvidenceStopControlRequest)
                await _sequence.StopAsync(CleanupToken()).ConfigureAwait(false);
            // WAIT joins only an already owned STOP, outside response ordering. It never starts
            // containment itself or holds the reply gate while that original procedure is pending.
            else if (request is EvidenceWaitControlRequest)
                await _sequence.JoinStartedStopAsync().ConfigureAwait(false);
            await _replyOrder.WaitAsync(io.Token).ConfigureAwait(false);
            heldReplyOrder = true;
            byte[] response;
            switch (request)
            {
                case EvidenceReadyControlRequest:
                    _owner.RequireActive(io.Token);
                    claim = _sequence.ClaimReady();
                    response = _worker.CreateReadyData(io.Token).ReadyBytes;
                    break;
                case EvidenceStopControlRequest:
                    RequireCleanupBound();
                    response = EvidenceCanonicalJson.Serialize(new { ok = true });
                    break;
                case EvidenceWaitControlRequest:
                    RequireCleanupBound();
                    claim = _sequence.ClaimWait();
                    response = EvidenceCanonicalJson.Serialize(new { ok = true, owned_exit = claim.Positive });
                    break;
                case EvidenceExitControlRequest:
                    RequireCleanupBound();
                    _owner.RequireControlIdentity(io.Token);
                    claim = _sequence.ClaimExit();
                    response = EvidenceCanonicalJson.Serialize(new { ok = true });
                    break;
                default:
                    throw Rejected();
            }
            await connection.WriteResponseAsync(response, io.Token).ConfigureAwait(false);
            await _listener.ReleaseAsync(connection).ConfigureAwait(false);
            released = true;
            if (isCleanupRequest) RequireCleanupBound();
            _owner.RequireControlIdentity(io.Token);
            if (isCleanupRequest) RequireCleanupBound();
            if (claim is not null)
            {
                _sequence.CompleteWrite(claim, true);
                committedExit = claim.Operation == EvidenceControlOperation.Exit;
                claim = null;
            }
        }
        catch (Exception error) when (Recoverable(error))
        {
            if (claim is not null)
            {
                try { _sequence.CompleteWrite(claim, false); }
                catch (EvidenceAdmissionException) { /* Failed write remains consumed and latched. */ }
            }
            _sequence.RecordFailure();
        }
        finally
        {
            if (!released)
            {
                try { await _listener.ReleaseAsync(connection).ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error))
                { Interlocked.Exchange(ref _connectionCloseFailed, 1); _sequence.RecordFailure(); }
            }
            if (heldReplyOrder) _replyOrder.Release();
            control.Dispose();
            cleanupCancellation.Dispose();
            if (committedExit) _exitCommitted.TrySetResult();
        }
    }

    private Task<SupervisionControlJoinFacts> StopEmptyDescendantsAsync(CancellationToken token)
    {
        _owner.RequireControlIdentity(token);
        if (!_ledger.IsWorkloadAdmissionClosed || _ledger.ActiveWorkloads != 0) throw Rejected();
        // This private server has no work-dispatch path or external workload registration API.
        // These are observations of the genuine empty descendant set, not worker exit assertions.
        return Task.FromResult(new SupervisionControlJoinFacts(true, true, true));
    }

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
