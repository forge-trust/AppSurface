using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed first-failure data for a pending start and its cleanup procedures.</summary>
internal enum SupervisionPendingStartFailure
{
    /// <summary>No procedure failure has been observed.</summary>
    None,
    /// <summary>The start faulted, returned invalid reply data, or did not provide a task.</summary>
    StartFailed,
    /// <summary>The start was canceled; OS acceptance remains ambiguous.</summary>
    StartCancelled,
    /// <summary>An owned stop procedure failed or did not provide a task.</summary>
    StopFailed,
    /// <summary>An owned stop procedure was canceled.</summary>
    StopCancelled,
}

/// <summary>An atomic lifetime projection, never physical-exit, custody, or admission evidence.</summary>
/// <param name="StartReserved">Ownership was reserved before invoking the start procedure and is retained.</param>
/// <param name="Started">The start returned nonempty bounded reply data without caller cancellation.</param>
/// <param name="StartJoined">The actual start procedure has finished, including a faulted/canceled attempt.</param>
/// <param name="Closed">Further start admission is irreversibly closed.</param>
/// <param name="StopJoined">All attempted stops and the reserved start have actually finished.</param>
/// <param name="FirstFailure">The first observed procedure failure; it is never cleared.</param>
internal sealed record SupervisionPendingStartSnapshot(
    bool StartReserved, bool Started, bool StartJoined, bool Closed, bool StopJoined,
    SupervisionPendingStartFailure FirstFailure)
{
    /// <summary>Gets whether any procedure failure is latched.</summary>
    internal bool IsFailed => FirstFailure != SupervisionPendingStartFailure.None;

    /// <summary>Gets successful procedure settlement, not proof that a unit, process, or cgroup exited.</summary>
    internal bool IsSettled => Closed && StopJoined && (!StartReserved || StartJoined) && !IsFailed;
}

/// <summary>Retains one pending unit start across irreversible stop closure and ambiguous replies.</summary>
/// <remarks>
/// This internal procedure seam creates no authority. The root owner must first reserve its workload
/// ledger and validate deployment, owner activation, and the generated unit. Stop runs once before
/// joining a reserved start and again afterward, including when the start faults or is canceled.
/// An expired token prevents further procedure dispatch but never detaches an already dispatched task.
/// There is no timer renewal or retry after a failed stop sequence. Procedures that ignore cancellation
/// remain pending until they really finish or the root OS lifetime enforcement terminates the owner.
/// Physical unit/cgroup and pump joins remain separate prerequisites for the caller's completion.
/// </remarks>
internal sealed class SupervisionPendingStart
{
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _insideProcedure = new();
    private TaskCompletionSource<string>? _startCompletion;
    private TaskCompletionSource? _stopCompletion;
    private bool _started;
    private bool _startJoined;
    private bool _closed;
    private bool _stopJoined;
    private SupervisionPendingStartFailure _firstFailure;

    /// <summary>Creates lifetime bookkeeping for one closed, root-generated unit name.</summary>
    /// <param name="unit">Generated name data; this alone proves neither ownership nor existence.</param>
    internal SupervisionPendingStart(LinuxUnitName unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        Unit = unit;
    }

    /// <summary>Gets the selected name; procedures must operate on this same root-selected unit.</summary>
    internal LinuxUnitName Unit { get; }

    /// <summary>Gets a consistent snapshot of reservation, closure, joins, and sticky failure.</summary>
    internal SupervisionPendingStartSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new(_startCompletion is not null, _started, _startJoined, _closed,
                    _stopJoined, _firstFailure);
        }
    }

    /// <summary>Reserves the only start attempt before invoking its asynchronous procedure.</summary>
    /// <param name="start">Root-owned typed start operation; nonempty reply data is not an OS-exit assertion.</param>
    /// <param name="cancellationToken">Original caller deadline token, passed unchanged to the operation.</param>
    /// <returns>The original operation's bounded reply data, after its actual task is joined.</returns>
    /// <remarks>
    /// Replay and preclosed admission reject without dispatch. Fault/cancellation retains reservation,
    /// closes admission, and remains failed after cleanup. A concurrent stop can run before start I/O;
    /// its mandatory second stop follows the actual start task, not a canceled wait wrapper.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The procedure is missing.</exception>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD410 for replay, self-join, or procedure failure.</exception>
    internal Task<string> StartAsync(Func<CancellationToken, Task<string>> start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        RequireExternalCaller();
        TaskCompletionSource<string> completion;
        lock (_gate)
        {
            if (_closed || _startCompletion is not null) throw Rejected();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _startCompletion = completion;
        }
        _ = RunStartAsync(start, cancellationToken, completion);
        return completion.Task;
    }

    /// <summary>Closes start admission permanently and owns one shared stop/join/stop sequence.</summary>
    /// <param name="stop">Root-owned stop for <see cref="Unit"/>; it must not await this coordinator itself.</param>
    /// <param name="cleanupToken">Original cleanup allowance, never a fresh or extended deadline.</param>
    /// <returns>The same task for all concurrent/repeated callers; failure never publishes successful settlement.</returns>
    /// <remarks>
    /// The first call selects the procedure/token. Later callers join that task without replacing either
    /// or canceling their own wait separately. Both actual stop tasks and any reserved start are joined
    /// even after a failure. A stop before any reservation forbids start and needs only one stop call.
    /// Calling this from the same coordinator's start/stop procedure rejects before a self-join.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The procedure is missing.</exception>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD410 for self-join or a failed sequence.</exception>
    internal Task StopAsync(Func<CancellationToken, Task> stop, CancellationToken cleanupToken)
    {
        ArgumentNullException.ThrowIfNull(stop);
        RequireExternalCaller();
        TaskCompletionSource completion;
        Task<string>? pendingStart;
        lock (_gate)
        {
            _closed = true;
            if (_stopCompletion is not null) return _stopCompletion.Task;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopCompletion = completion;
            pendingStart = _startCompletion?.Task;
        }
        _ = RunStopAsync(stop, cleanupToken, pendingStart, completion);
        return completion.Task;
    }

    private async Task RunStartAsync(Func<CancellationToken, Task<string>> start, CancellationToken token,
        TaskCompletionSource<string> completion)
    {
        var previousContext = _insideProcedure.Value;
        _insideProcedure.Value = true;
        try
        {
            token.ThrowIfCancellationRequested();
            var operation = start(token) ?? throw Rejected();
            var reply = await operation.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(reply) || reply.Length > 4096 || reply.Any(char.IsControl)) throw Rejected();
            lock (_gate) { _started = true; _startJoined = true; }
            completion.TrySetResult(reply);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(SupervisionPendingStartFailure.StartCancelled);
            lock (_gate) _startJoined = true;
            completion.TrySetCanceled(token);
        }
        catch (Exception)
        {
            RecordFailure(SupervisionPendingStartFailure.StartFailed);
            lock (_gate) _startJoined = true;
            completion.TrySetException(Rejected());
        }
        finally { _insideProcedure.Value = previousContext; }
    }

    private async Task RunStopAsync(Func<CancellationToken, Task> stop, CancellationToken token,
        Task<string>? pendingStart, TaskCompletionSource completion)
    {
        var cancelled = await AttemptStopAsync(stop, token).ConfigureAwait(false);
        if (pendingStart is not null)
        {
            try { await pendingStart.ConfigureAwait(false); }
            catch (Exception) { /* Start already latched its closed failure; acceptance may still be ambiguous. */ }
            cancelled |= await AttemptStopAsync(stop, token).ConfigureAwait(false);
        }
        SupervisionPendingStartFailure failure;
        lock (_gate) { _stopJoined = true; failure = _firstFailure; }
        if (cancelled) completion.TrySetCanceled(token);
        else if (failure != SupervisionPendingStartFailure.None) completion.TrySetException(Rejected());
        else completion.TrySetResult();
    }

    private async Task<bool> AttemptStopAsync(Func<CancellationToken, Task> stop, CancellationToken token)
    {
        var previousContext = _insideProcedure.Value;
        _insideProcedure.Value = true;
        try
        {
            token.ThrowIfCancellationRequested();
            var operation = stop(token) ?? throw Rejected();
            await operation.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return false;
        }
        catch (OperationCanceledException)
        {
            RecordFailure(SupervisionPendingStartFailure.StopCancelled);
            return true;
        }
        catch (Exception)
        {
            RecordFailure(SupervisionPendingStartFailure.StopFailed);
            return false;
        }
        finally { _insideProcedure.Value = previousContext; }
    }

    private void RecordFailure(SupervisionPendingStartFailure failure)
    {
        lock (_gate)
        {
            if (_firstFailure == SupervisionPendingStartFailure.None) _firstFailure = failure;
            _closed = true;
        }
    }

    private void RequireExternalCaller()
    {
        if (_insideProcedure.Value) throw Rejected();
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The protected pending-start procedure was rejected.");
}
