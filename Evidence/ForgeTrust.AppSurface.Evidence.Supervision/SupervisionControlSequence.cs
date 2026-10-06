using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Explicit assertions returned by the real descendant stop procedure; these issue no native authority.</summary>
/// <param name="PendingStartsJoined">The owner joined every original descendant start operation.</param>
/// <param name="WorkloadsJoined">The owner joined actual descendant containment and workload completion.</param>
/// <param name="OutputPumpsJoined">The owner joined all descendant output pumps.</param>
internal sealed record SupervisionControlJoinFacts(bool PendingStartsJoined, bool WorkloadsJoined, bool OutputPumpsJoined);

/// <summary>Owns the closed ready/stop/wait/exit procedure and write commits, without authentication or admission.</summary>
/// <remarks>
/// The authenticated server registers each control handler before claiming a reply, performs actual I/O outside
/// this object's locks, and calls CompleteWrite only after the original write and close have joined. A failed
/// write consumes the claim and irreversibly fails this sequence. Never commit in advance of I/O.
/// Supply a descendant-only workload registry: neither the worker process nor the requesting control handler
/// belongs to its workload set. Final server drain must separately join control handlers and worker termination.
/// Stop closes workload admission before dispatch, joins the original callback task and the actual registry drain,
/// and never detaches ignored cancellation. The real callback must contain units, settle ambiguous starts/pumps
/// and complete registrations even on failure. The outer root lifetime contains stalls; no new timer is created.
/// Join assertions and reply claims are procedure data, not kernel receipts, leases or successful protected proof.
/// Cleanup stop/wait remains available after failure. Failure never resets or reenables work, and forbids exit ACK.
/// </remarks>
internal sealed class SupervisionControlSequence
{
    private static readonly AsyncLocal<ProcedureScope?> CurrentProcedure = new();
    private readonly object _gate = new();
    private readonly SupervisionWorkRegistry _workloads;
    private readonly Func<CancellationToken, Task<SupervisionControlJoinFacts>> _stopActual;
    private Task? _stop;
    private SupervisionControlJoinFacts? _joins;
    private Reply? _readyReply;
    private Reply? _waitReply;
    private Reply? _exitReply;
    private bool _readyClaimed;
    private bool _waitClaimed;
    private bool _exitClaimed;
    private bool _readyAcknowledged;
    private bool _positiveWaitAcknowledged;
    private bool _exitAcknowledged;
    private bool _failed;

    /// <summary>Creates empty procedure state; neither argument dispatches during construction.</summary>
    /// <param name="workloads">Actual descendant ledger, excluding worker-process and control-handler ownership.</param>
    /// <param name="stopActual">Original real containment/join task, returning explicit assertions only after joining.</param>
    internal SupervisionControlSequence(SupervisionWorkRegistry workloads,
        Func<CancellationToken, Task<SupervisionControlJoinFacts>> stopActual)
    {
        ArgumentNullException.ThrowIfNull(workloads);
        ArgumentNullException.ThrowIfNull(stopActual);
        _workloads = workloads;
        _stopActual = stopActual;
    }

    /// <summary>Gets irreversible workload admission closure, independently of physical settlement.</summary>
    internal bool IsWorkAdmissionClosed => _workloads.IsWorkloadAdmissionClosed;
    /// <summary>Gets sticky sequence or ledger failure; completing cleanup cannot clear it.</summary>
    internal bool Failed { get { lock (_gate) return FailedLocked; } }
    /// <summary>Gets whether the one ready write committed without a subsequently latched failure.</summary>
    internal bool ReadyAcknowledged { get { lock (_gate) return _readyAcknowledged && !FailedLocked; } }
    /// <summary>Gets a committed positive wait assertion; native facts must still be rechecked by the server.</summary>
    internal bool PositiveWaitAcknowledged { get { lock (_gate) return _positiveWaitAcknowledged && !FailedLocked; } }
    /// <summary>Gets committed exit protocol data, never whole-run or physical-exit authority.</summary>
    internal bool ExitAcknowledged { get { lock (_gate) return _exitAcknowledged && !FailedLocked; } }

    /// <summary>Claims exactly one ready response before its actual write; replay never dispatches work.</summary>
    internal Reply ClaimReady()
    {
        RequireOutsideProcedure();
        lock (_gate)
        {
            if (_readyClaimed || _stop is not null || IsWorkAdmissionClosed || FailedLocked) throw Rejected();
            _readyClaimed = true;
            return _readyReply = new(this, EvidenceControlOperation.Ready, true);
        }
    }

    /// <summary>Closes admission immediately and registers one shared containment plus workload-drain task.</summary>
    /// <param name="token">First caller's original owner cleanup token; later callers cannot replace it.</param>
    /// <returns>The same owned task, including permanent failure. Actual callback and ledger drain always join.</returns>
    /// <remarks>Does not join any control handler or the worker process; callback same-owner reentry rejects.</remarks>
    internal Task StopAsync(CancellationToken token)
    {
        RequireOutsideProcedure();
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task stop;
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            var drained = _workloads.CloseAndJoinWorkloadsAsync();
            stop = StopCoreAsync(dispatch.Task, drained, token);
            _stop = stop;
        }
        dispatch.SetResult();
        return stop;
    }

    /// <summary>Claims one wait response only after the shared stop task and workload drain actually completed.</summary>
    /// <returns>A positive claim only when every real-callback join assertion is true and no failure is latched.</returns>
    /// <remarks>A completed failed stop permits a negative cleanup response; it cannot create a positive wait.</remarks>
    internal Reply ClaimWait()
    {
        RequireOutsideProcedure();
        lock (_gate)
        {
            if (_waitClaimed || _stop is null || !_stop.IsCompleted) throw Rejected();
            _waitClaimed = true;
            return _waitReply = new(this, EvidenceControlOperation.Wait, JoinsCompleteLocked && !FailedLocked);
        }
    }

    /// <summary>Claims exactly one exit reply after ready ACK, positive wait ACK and zero closed workload count.</summary>
    /// <remarks>Stops/joins and workload counts are required procedure checks, never sufficient native proof.</remarks>
    internal Reply ClaimExit()
    {
        RequireOutsideProcedure();
        lock (_gate)
        {
            if (_exitClaimed || !_readyAcknowledged || !_positiveWaitAcknowledged
                || !JoinsCompleteLocked || FailedLocked) throw Rejected();
            _exitClaimed = true;
            return _exitReply = new(this, EvidenceControlOperation.Exit, true);
        }
    }

    /// <summary>Consumes this owner's exact reply claim after joined write/close; never retries a failed write.</summary>
    /// <param name="reply">The original claim object, not request metadata or a claim from another sequence.</param>
    /// <param name="succeeded">True only after actual response I/O joined successfully, as asserted by the owning server.</param>
    /// <remarks>
    /// Replay/foreign claims reject fixed ASEVD410. A failed write latches failure before rejection.
    /// A successfully written, previously claimed ready reply may commit after stop closed work: the worker
    /// can receive its LF before the server's actual write/close join completes. Sticky failure still forbids ACK.
    /// </remarks>
    internal void CompleteWrite(Reply reply, bool succeeded)
    {
        RequireOutsideProcedure();
        lock (_gate)
        {
            if (reply is null || !(ReferenceEquals(reply, _readyReply) || ReferenceEquals(reply, _waitReply)
                || ReferenceEquals(reply, _exitReply)) || !reply.TryComplete(this)) throw Rejected();
            if (!succeeded) { FailLocked(); throw Rejected(); }
            switch (reply.Operation)
            {
                case EvidenceControlOperation.Ready:
                    if (FailedLocked) { FailLocked(); throw Rejected(); }
                    _readyAcknowledged = true;
                    break;
                case EvidenceControlOperation.Wait:
                    if (reply.Positive && (!JoinsCompleteLocked || FailedLocked)) { FailLocked(); throw Rejected(); }
                    _positiveWaitAcknowledged = reply.Positive;
                    break;
                case EvidenceControlOperation.Exit:
                    if (!_readyAcknowledged || !_positiveWaitAcknowledged || !JoinsCompleteLocked || FailedLocked)
                    { FailLocked(); throw Rejected(); }
                    _exitAcknowledged = true;
                    break;
                default:
                    FailLocked(); throw Rejected();
            }
        }
    }

    /// <summary>Irreversibly records a closed control failure and closes work; cleanup remains available.</summary>
    internal void RecordFailure()
    {
        RequireOutsideProcedure();
        lock (_gate) FailLocked();
    }

    private bool FailedLocked => _failed || _workloads.IsFailed;
    private bool JoinsCompleteLocked => _stop?.IsCompletedSuccessfully == true
        && _joins is { PendingStartsJoined: true, WorkloadsJoined: true, OutputPumpsJoined: true }
        && IsWorkAdmissionClosed && _workloads.ActiveWorkloads == 0;

    private async Task StopCoreAsync(Task dispatch, Task<bool> drained, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        SupervisionControlJoinFacts? joins = null;
        var callbackFailed = false;
        var previous = CurrentProcedure.Value;
        CurrentProcedure.Value = new(this, previous);
        try
        {
            var actual = _stopActual(token);
            if (actual is null) throw Rejected();
            joins = await actual.ConfigureAwait(false);
            if (joins is null) throw Rejected();
        }
        catch (Exception) { callbackFailed = true; lock (_gate) FailLocked(); }
        finally { CurrentProcedure.Value = previous; }
        // Callback failure or cancellation never detaches already registered descendant ownership.
        await drained.ConfigureAwait(false);
        lock (_gate)
        {
            _joins = joins;
            if (token.IsCancellationRequested
                || joins is not { PendingStartsJoined: true, WorkloadsJoined: true, OutputPumpsJoined: true })
            { FailLocked(); callbackFailed = true; }
            if (callbackFailed) throw Rejected();
        }
    }

    private void FailLocked()
    {
        _failed = true;
        _workloads.RecordFailure(SupervisionWorkFailure.ControlFailed);
    }

    private void RequireOutsideProcedure()
    {
        for (var scope = CurrentProcedure.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Owner, this)) throw Rejected();
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The protected control sequence operation was rejected.");

    private sealed record ProcedureScope(SupervisionControlSequence Owner, ProcedureScope? Parent);

    /// <summary>A retained one-write procedure claim; constructing data alone registers no reply or native authority.</summary>
    internal sealed class Reply
    {
        private readonly SupervisionControlSequence _owner;
        private int _completed;

        /// <summary>Creates detached claim data; only the owning sequence's exact retained reference can commit.</summary>
        internal Reply(SupervisionControlSequence owner, EvidenceControlOperation operation, bool positive)
        { _owner = owner; Operation = operation; Positive = positive; }

        /// <summary>Consumes completion once for the same owner; callers must separately verify retained registration.</summary>
        internal bool TryComplete(SupervisionControlSequence owner) => ReferenceEquals(owner, _owner)
            && Interlocked.Exchange(ref _completed, 1) == 0;

        /// <summary>Gets the closed reply operation selected by this sequence.</summary>
        internal EvidenceControlOperation Operation { get; }
        /// <summary>Gets positive procedure assertions; a positive wait is uncommitted until actual write success.</summary>
        internal bool Positive { get; }
    }
}
