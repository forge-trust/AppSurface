namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>
/// Consumer-owned supervision operations required to stop work independently of a caller's
/// already-cancelled stage token. Implementations must acknowledge descendant and pump exit.
/// </summary>
internal interface IEvidenceExecutionSupervisor : IEvidenceArmedWorker
{
    /// <summary>Closes admission for new stages, child launches, and artifact writes.</summary>
    void CloseAdmission();

    /// <summary>Requests stop using a fresh host-owned token.</summary>
    ValueTask RequestStopAsync(CancellationToken stoppingToken);

    /// <summary>Waits for all supervisor-owned children and output pumps to acknowledge exit.</summary>
    ValueTask WaitForOwnedExitAsync(CancellationToken stoppingToken);
}

/// <summary>Outcome of one bounded callback stage.</summary>
internal enum EvidenceWorkerStageOutcome
{
    Passed,
    Failed,
    TimedOut,
    Cancelled,
    Rejected,
}

/// <summary>Stable, secret-free terminal failure codes retained for the run's lifetime.</summary>
internal enum EvidenceWorkerTerminalCode
{
    None,
    StageFailed,
    DeadlineExceeded,
    CallerCancelled,
    CleanupFailed,
    AdmissionClosed,
}

/// <summary>Test-only interruption thrown by the fatal-path seam and allowed to escape unchanged.</summary>
internal sealed class EvidenceWorkerTestInterruptionException(string message) : Exception(message);

/// <summary>
/// Runs callbacks away from the deadline-control thread, tracks all admitted owned work,
/// and applies the common stop/join-or-fail-stop rule. The cleanup allowance is cumulative
/// across stopping and serial disposal.
/// Completion is accepted only before the monotonic deadline, even when timer delivery is delayed.
/// </summary>
internal sealed class EvidenceWorkerExecution
{
    private static readonly TimeSpan MaximumStoppingGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumCleanup = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly IEvidenceExecutionSupervisor _supervisor;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _jobAllowance;
    private readonly TimeSpan _cleanupAllowance;
    private readonly TimeSpan _stoppingAllowance;
    private readonly TimeSpan _collectionReserve;
    private readonly Func<string, Exception> _fatalTermination;
    private readonly string _runId;
    private readonly List<Task> _ownedWork = [];
    private readonly List<Func<CancellationToken, ValueTask>> _disposers = [];
    private readonly long _startedAt;
    private long? _cleanupStartedAt;
    private CancellationTokenSource? _activeStage;
    private EvidenceWorkerTerminalCode _terminalCode;
    private Exception? _terminalException;
    private bool _admissionClosed;
    private bool _cleanupStarted;
    private Task<bool>? _cleanupTask;
    private bool _cleanupFinished;
    private bool _cleanupSucceeded = true;
    private bool _ownedExitEstablished;
    private bool _collectionStarted;
    private bool _collectionCompleted;
    private readonly EvidenceAdmissionResult? _admission;

    /// <summary>Creates a lifecycle with a monotonic job allowance and bounded teardown settings.</summary>
    /// <param name="supervisor">An armed consumer supervisor whose run identity remains current.</param>
    /// <param name="timeProvider">Clock and timer provider shared with deterministic tests.</param>
    /// <param name="jobRemaining">Monotonic duration remaining in the protected outer job.</param>
    /// <param name="cleanupAllowance">Cumulative stop and disposal allowance, at most ten minutes.</param>
    /// <param name="stoppingAllowance">Stop/join portion of cleanup, at most thirty seconds.</param>
    /// <param name="fatalTermination">Test-only fatal interruption seam; production callers omit this argument.</param>
    /// <param name="admission">Optional shared capability whose claim eligibility is revoked on the first terminal cause.</param>
    /// <param name="collectionReserve">Protected final collection time reserved from stopping and disposal, at most sixty seconds.</param>
    internal EvidenceWorkerExecution(
        IEvidenceExecutionSupervisor supervisor,
        TimeProvider timeProvider,
        TimeSpan jobRemaining,
        TimeSpan cleanupAllowance,
        TimeSpan stoppingAllowance,
        Func<string, Exception>? fatalTermination = null,
        EvidenceAdmissionResult? admission = null,
        TimeSpan collectionReserve = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!supervisor.IsArmed || string.IsNullOrWhiteSpace(supervisor.RunId))
        {
            throw new ArgumentException("An armed worker with a run identity is required.", nameof(supervisor));
        }

        if (jobRemaining <= TimeSpan.Zero || cleanupAllowance <= TimeSpan.Zero || cleanupAllowance > MaximumCleanup
            || stoppingAllowance <= TimeSpan.Zero || stoppingAllowance > MaximumStoppingGrace || stoppingAllowance > cleanupAllowance
            || collectionReserve < TimeSpan.Zero || collectionReserve > EvidenceRunBudgetLimits.Collection
            || collectionReserve >= jobRemaining)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupAllowance), "Worker lifecycle budgets are outside their protected bounds.");
        }

        _supervisor = supervisor;
        _runId = supervisor.RunId;
        _timeProvider = timeProvider;
        _jobAllowance = jobRemaining;
        _cleanupAllowance = cleanupAllowance;
        _stoppingAllowance = stoppingAllowance;
        _collectionReserve = collectionReserve;
        _fatalTermination = fatalTermination ?? FatalTermination;
        _admission = admission;
        _startedAt = timeProvider.GetTimestamp();
    }

    /// <summary>Gets the immutable first terminal code; None means the run remains eligible to proceed.</summary>
    internal EvidenceWorkerTerminalCode TerminalCode { get { lock (_gate) return _terminalCode; } }

    /// <summary>Gets the first terminal exception for internal control flow; its message is never surfaced.</summary>
    internal Exception? TerminalException { get { lock (_gate) return _terminalException; } }

    /// <summary>Gets whether new stages, owned work, and writes have been closed.</summary>
    internal bool IsAdmissionClosed { get { lock (_gate) return _admissionClosed; } }

    /// <summary>Gets whether callbacks, tracked writes/pumps, and supervisor-owned work have all acknowledged exit.</summary>
    internal bool OwnWorkStopped { get { lock (_gate) return _ownedExitEstablished && _ownedWork.All(static task => task.IsCompleted); } }

    /// <summary>Gets whether the cumulative teardown phase has completed successfully.</summary>
    internal bool CleanupCompleted { get { lock (_gate) return _cleanupFinished && _cleanupSucceeded; } }

    /// <summary>Gets whether bounded final collection completed successfully.</summary>
    internal bool CollectionCompleted { get { lock (_gate) return _collectionCompleted; } }

    /// <summary>Irreversibly latches a failure before the shared admission capability is wired to this lifecycle.</summary>
    internal void LatchFailure() => Latch(EvidenceWorkerTerminalCode.StageFailed, null);

    /// <summary>Latches a host-selected terminal cause, closes work admission, and stops/joins owned work.</summary>
    internal ValueTask RequestTerminalStopAsync(EvidenceWorkerTerminalCode code, CancellationToken callerCancellationToken = default)
    {
        if (code == EvidenceWorkerTerminalCode.None) throw new ArgumentOutOfRangeException(nameof(code));
        if (callerCancellationToken.IsCancellationRequested)
        {
            return StopAndJoinAsync(EvidenceWorkerTerminalCode.CallerCancelled, new OperationCanceledException(callerCancellationToken));
        }
        return StopAndJoinAsync(code, null);
    }

    /// <summary>Registers a serial, reverse-ownership-order disposer before terminal shutdown.</summary>
    internal bool RegisterDisposer(Func<CancellationToken, ValueTask> disposer)
    {
        ArgumentNullException.ThrowIfNull(disposer);
        lock (_gate)
        {
            if (_admissionClosed || _cleanupStarted)
            {
                return false;
            }

            _disposers.Add(disposer);
            return true;
        }
    }

    /// <summary>
    /// Tracks an already admitted child, pump, or write until its real task settles.
    /// The callback itself is dispatched with Task.Run, including synchronous work before its first await.
    /// </summary>
    internal Task? TrackOwnedWork(Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_admissionClosed || _terminalCode != EvidenceWorkerTerminalCode.None || !HasCurrentArmedLease())
            {
                return null;
            }

            var task = Task.Run(async () => await work(cancellationToken).ConfigureAwait(false), CancellationToken.None);
            _ownedWork.Add(task);
            return task;
        }
    }

    /// <summary>Runs one callback against its own deadline and caller cancellation.</summary>
    internal async ValueTask<(EvidenceWorkerStageOutcome Outcome, T? Value)> ExecuteAsync<T>(
        EvidenceRunStage stage,
        TimeSpan deadline,
        Func<CancellationToken, ValueTask<T>> callback,
        CancellationToken callerCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (deadline <= TimeSpan.Zero || !StageDeadlineAllowed(stage, deadline))
        {
            throw new ArgumentOutOfRangeException(nameof(deadline), "Stage deadline is outside its protected bound.");
        }

        CancellationTokenSource? stageCancellation = null;
        Task<T>? callbackTask = null;
        long stageStartedAt = 0;
        EvidenceWorkerTerminalCode rejectedCode = EvidenceWorkerTerminalCode.None;
        lock (_gate)
        {
            if (callerCancellationToken.IsCancellationRequested)
            {
                LatchLocked(EvidenceWorkerTerminalCode.CallerCancelled, new OperationCanceledException(callerCancellationToken));
                _admissionClosed = true;
                rejectedCode = EvidenceWorkerTerminalCode.CallerCancelled;
            }
            else
            {
                if (_admissionClosed || _terminalCode != EvidenceWorkerTerminalCode.None
                    || !HasCurrentArmedLease() || JobRemaining < deadline + CleanupRemaining + _collectionReserve)
                {
                    LatchLocked(EvidenceWorkerTerminalCode.AdmissionClosed, null);
                    _admissionClosed = true;
                    rejectedCode = _terminalCode;
                }
                else
                {
                    stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
                    _activeStage = stageCancellation;
                    stageStartedAt = _timeProvider.GetTimestamp();
                    callbackTask = Task.Run(async () => await callback(stageCancellation.Token).ConfigureAwait(false), CancellationToken.None);
                    _ownedWork.Add(callbackTask);
                }
            }
        }

        if (callbackTask is null)
        {
            await StopAndJoinAsync(null, null).ConfigureAwait(false);
            return (rejectedCode == EvidenceWorkerTerminalCode.CallerCancelled
                ? EvidenceWorkerStageOutcome.Cancelled
                : EvidenceWorkerStageOutcome.Rejected, default);
        }

        var timeoutTask = DelayForRemainingAsync(Min(Remaining(deadline, stageStartedAt), JobRemaining));
        var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, callerCancellationToken);
        var completed = await Task.WhenAny(callbackTask, timeoutTask, cancelTask).ConfigureAwait(false);
        if (completed == callbackTask)
        {
            try
            {
                var value = await callbackTask.ConfigureAwait(false);
                var ownershipTask = JoinAllOwnedWorkAsync();
                var ownershipResult = await Task.WhenAny(ownershipTask, timeoutTask, cancelTask).ConfigureAwait(false);
                if (ownershipResult != ownershipTask)
                {
                    var ownershipCause = callerCancellationToken.IsCancellationRequested
                        ? EvidenceWorkerTerminalCode.CallerCancelled
                        : EvidenceWorkerTerminalCode.DeadlineExceeded;
                    Exception ownershipException = ownershipCause == EvidenceWorkerTerminalCode.CallerCancelled
                        ? new OperationCanceledException(callerCancellationToken)
                        : new TimeoutException("Owned Evidence work exceeded the stage deadline.");
                    await StopAndJoinAsync(ownershipCause, ownershipException).ConfigureAwait(false);
                    return (ownershipCause == EvidenceWorkerTerminalCode.CallerCancelled
                        ? EvidenceWorkerStageOutcome.Cancelled
                        : EvidenceWorkerStageOutcome.TimedOut, default);
                }

                await ownershipTask.ConfigureAwait(false);
                if (Remaining(deadline, stageStartedAt) <= TimeSpan.Zero || JobRemaining <= TimeSpan.Zero)
                {
                    var timeout = new TimeoutException("Evidence stage deadline exceeded.");
                    await StopAndJoinAsync(EvidenceWorkerTerminalCode.DeadlineExceeded, timeout).ConfigureAwait(false);
                    return (EvidenceWorkerStageOutcome.TimedOut, default);
                }

                if (callerCancellationToken.IsCancellationRequested)
                {
                    await StopAndJoinAsync(EvidenceWorkerTerminalCode.CallerCancelled, new OperationCanceledException(callerCancellationToken)).ConfigureAwait(false);
                    return (EvidenceWorkerStageOutcome.Cancelled, default);
                }

                bool staleLease;
                EvidenceWorkerStageOutcome? rejectedOutcome = null;
                lock (_gate)
                {
                    if (Remaining(deadline, stageStartedAt) <= TimeSpan.Zero || JobRemaining <= TimeSpan.Zero)
                    {
                        LatchLocked(EvidenceWorkerTerminalCode.DeadlineExceeded, new TimeoutException("Evidence stage deadline exceeded."));
                    }
                    staleLease = !HasCurrentArmedLease();
                    if (staleLease && _terminalCode == EvidenceWorkerTerminalCode.None)
                    {
                        LatchLocked(EvidenceWorkerTerminalCode.AdmissionClosed, null);
                        _admissionClosed = true;
                    }
                    if (_terminalCode != EvidenceWorkerTerminalCode.None || _admissionClosed)
                    {
                        rejectedOutcome = _terminalCode switch
                        {
                            EvidenceWorkerTerminalCode.CallerCancelled => EvidenceWorkerStageOutcome.Cancelled,
                            EvidenceWorkerTerminalCode.AdmissionClosed => EvidenceWorkerStageOutcome.Rejected,
                            EvidenceWorkerTerminalCode.DeadlineExceeded => EvidenceWorkerStageOutcome.TimedOut,
                            _ => EvidenceWorkerStageOutcome.Failed,
                        };
                    }
                }

                if (rejectedOutcome is EvidenceWorkerStageOutcome rejected)
                {
                    await StopAndJoinAsync(null, null).ConfigureAwait(false);
                    return (rejected, default);
                }

                return (EvidenceWorkerStageOutcome.Passed, value);
            }
            catch (OperationCanceledException ex) when (callerCancellationToken.IsCancellationRequested)
            {
                await StopAndJoinAsync(EvidenceWorkerTerminalCode.CallerCancelled, ex).ConfigureAwait(false);
                return (EvidenceWorkerStageOutcome.Cancelled, default);
            }
            catch (Exception ex) when (IsRecoverableException(ex))
            {
                await StopAndJoinAsync(EvidenceWorkerTerminalCode.StageFailed, ex).ConfigureAwait(false);
                return (EvidenceWorkerStageOutcome.Failed, default);
            }
            finally
            {
                lock (_gate) if (ReferenceEquals(_activeStage, stageCancellation)) _activeStage = null;
                stageCancellation!.Dispose();
            }
        }

        var cause = callerCancellationToken.IsCancellationRequested
            ? EvidenceWorkerTerminalCode.CallerCancelled
            : EvidenceWorkerTerminalCode.DeadlineExceeded;
        Exception cancellation = cause == EvidenceWorkerTerminalCode.CallerCancelled
            ? new OperationCanceledException(callerCancellationToken)
            : new TimeoutException("Evidence stage deadline exceeded.");
        await StopAndJoinAsync(cause, cancellation).ConfigureAwait(false);
        _ = callbackTask.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (_gate) if (ReferenceEquals(_activeStage, stageCancellation)) _activeStage = null;
        stageCancellation!.Dispose();
        return (cause == EvidenceWorkerTerminalCode.CallerCancelled ? EvidenceWorkerStageOutcome.Cancelled : EvidenceWorkerStageOutcome.TimedOut, default);
    }

    /// <summary>
    /// Joins owned work and runs registered disposers once, serially in reverse order.
    /// Concurrent callers share the same cleanup result.
    /// </summary>
    internal ValueTask<bool> StopAndDisposeAsync()
    {
        Task<bool> cleanupTask;
        lock (_gate)
        {
            _cleanupTask ??= StopAndDisposeCoreAsync();
            cleanupTask = _cleanupTask;
        }

        return new ValueTask<bool>(cleanupTask);
    }

    private async Task<bool> StopAndDisposeCoreAsync()
    {
        await StopAndJoinAsync(null, null).ConfigureAwait(false);
        if (await HasUnsettledOwnedWorkAsync().ConfigureAwait(false)) return false;

        Func<CancellationToken, ValueTask>[] disposers;
        lock (_gate)
        {
            if (_cleanupFinished) return _terminalCode == EvidenceWorkerTerminalCode.None;
            _cleanupStarted = true;
            disposers = _disposers.AsEnumerable().Reverse().ToArray();
        }

        foreach (var disposer in disposers)
        {
            var remaining = CleanupRemaining;
            if (remaining <= TimeSpan.Zero) return await FailStopAsync("Evidence cleanup budget exhausted.").ConfigureAwait(false);
            using var disposeCancellation = new CancellationTokenSource(remaining, _timeProvider);
            var disposeTask = Task.Run(async () => await disposer(disposeCancellation.Token).ConfigureAwait(false), CancellationToken.None);
            lock (_gate) _ownedWork.Add(disposeTask);
            if (await Task.WhenAny(disposeTask, DelayForRemainingAsync(remaining)).ConfigureAwait(false) != disposeTask)
            {
                return await FailStopAsync("Evidence cleanup did not settle within its budget.").ConfigureAwait(false);
            }

            try { await disposeTask.ConfigureAwait(false); }
            catch (Exception ex) when (IsRecoverableException(ex))
            {
                lock (_gate) _cleanupSucceeded = false;
                Latch(EvidenceWorkerTerminalCode.CleanupFailed, ex);
            }

            if (CleanupRemaining <= TimeSpan.Zero)
            {
                return await FailStopAsync("Evidence cleanup completed after its budget expired.").ConfigureAwait(false);
            }
        }

        lock (_gate) _cleanupFinished = true;
        return TerminalCode == EvidenceWorkerTerminalCode.None;
    }

    /// <summary>
    /// Runs bounded final collection after owned work has stopped and cleanup has completed.
    /// A pre-existing terminal failure may still produce a collected failure result. A Passed
    /// outcome means collection completed; it does not clear the terminal cause or restore eligibility.
    /// Collection cannot register new owned work or reopen stage admission.
    /// </summary>
    internal async ValueTask<(EvidenceWorkerStageOutcome Outcome, T? Value)> CollectAsync<T>(
        TimeSpan deadline,
        Func<CancellationToken, ValueTask<T>> collector,
        CancellationToken callerCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collector);
        if (deadline <= TimeSpan.Zero || deadline > EvidenceRunBudgetLimits.Collection)
            throw new ArgumentOutOfRangeException(nameof(deadline), "Collection deadline is outside its protected bound.");
        if (callerCancellationToken.IsCancellationRequested)
        {
            Latch(EvidenceWorkerTerminalCode.CallerCancelled, new OperationCanceledException(callerCancellationToken));
            return (EvidenceWorkerStageOutcome.Cancelled, default);
        }

        EvidenceWorkerTerminalCode terminalCodeAtCollectionStart;
        lock (_gate)
        {
            if (!_ownedExitEstablished || !_cleanupFinished || _collectionStarted || JobRemaining < deadline || !HasCurrentArmedLease())
                return (EvidenceWorkerStageOutcome.Rejected, default);
            _collectionStarted = true;
            terminalCodeAtCollectionStart = _terminalCode;
        }

        var collectionStartedAt = _timeProvider.GetTimestamp();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        var task = Task.Run(async () => await collector(cancellation.Token).ConfigureAwait(false), CancellationToken.None);
        lock (_gate) _ownedWork.Add(task);
        var timeout = DelayForRemainingAsync(Min(Remaining(deadline, collectionStartedAt), JobRemaining));
        var caller = Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, callerCancellationToken);
        var completed = await Task.WhenAny(task, timeout, caller).ConfigureAwait(false);
        if (completed != task || Remaining(deadline, collectionStartedAt) <= TimeSpan.Zero || JobRemaining <= TimeSpan.Zero)
        {
            var cause = callerCancellationToken.IsCancellationRequested
                ? EvidenceWorkerTerminalCode.CallerCancelled
                : EvidenceWorkerTerminalCode.DeadlineExceeded;
            Latch(cause, cause == EvidenceWorkerTerminalCode.CallerCancelled
                ? new OperationCanceledException(callerCancellationToken)
                : new TimeoutException("Evidence collection deadline exceeded."));
            var cancelTask = Task.Run(cancellation.Cancel, CancellationToken.None);
            var grace = Min(_stoppingAllowance, Min(CleanupRemaining, JobRemaining));
            var graceStartedAt = _timeProvider.GetTimestamp();
            var settle = Task.WhenAll(task, cancelTask);
            if (grace <= TimeSpan.Zero || await Task.WhenAny(settle, DelayForRemainingAsync(grace)).ConfigureAwait(false) != settle
                || Remaining(grace, graceStartedAt) <= TimeSpan.Zero)
                await FailStopAsync("Evidence collection did not settle within its bounded grace.").ConfigureAwait(false);
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) when (IsRecoverableException(ex)) { }
            return (cause == EvidenceWorkerTerminalCode.CallerCancelled ? EvidenceWorkerStageOutcome.Cancelled : EvidenceWorkerStageOutcome.TimedOut, default);
        }

        try
        {
            var value = await task.ConfigureAwait(false);
            lock (_gate)
            {
                if (Remaining(deadline, collectionStartedAt) <= TimeSpan.Zero || JobRemaining <= TimeSpan.Zero)
                {
                    LatchLocked(EvidenceWorkerTerminalCode.DeadlineExceeded, new TimeoutException("Evidence collection deadline exceeded."));
                    return (EvidenceWorkerStageOutcome.TimedOut, default);
                }
                if (_terminalCode != terminalCodeAtCollectionStart || callerCancellationToken.IsCancellationRequested || !HasCurrentArmedLease())
                {
                    if (_terminalCode == EvidenceWorkerTerminalCode.None) LatchLocked(EvidenceWorkerTerminalCode.AdmissionClosed, null);
                    return (EvidenceWorkerStageOutcome.Failed, default);
                }
                _collectionCompleted = true;
            }
            return (EvidenceWorkerStageOutcome.Passed, value);
        }
        catch (Exception ex) when (IsRecoverableException(ex))
        {
            Latch(EvidenceWorkerTerminalCode.StageFailed, ex);
            return (EvidenceWorkerStageOutcome.Failed, default);
        }
    }

    private async ValueTask StopAndJoinAsync(EvidenceWorkerTerminalCode? code, Exception? cause)
    {
        Task[] tasks;
        lock (_gate)
        {
            if (code is EvidenceWorkerTerminalCode terminalCode) LatchLocked(terminalCode, cause);
            if (_terminalCode != EvidenceWorkerTerminalCode.None) _admission?.LatchFailure();
            _admissionClosed = true;
            if (_ownedExitEstablished) return;
            _cleanupStartedAt ??= _timeProvider.GetTimestamp();
            tasks = _ownedWork.ToArray();
        }

        var grace = Min(_stoppingAllowance, Min(CleanupRemaining, JobRemaining));
        if (grace <= TimeSpan.Zero) { await FailStopAsync("Evidence stopping budget exhausted.").ConfigureAwait(false); return; }
        var stopStartedAt = _timeProvider.GetTimestamp();

        var closeTask = Task.Run(_supervisor.CloseAdmission, CancellationToken.None);
        CancellationTokenSource? activeStage;
        lock (_gate) activeStage = _activeStage;
        var cancelTask = activeStage is null
            ? Task.CompletedTask
            : Task.Run(activeStage.Cancel, CancellationToken.None);
        using var stoppingCancellation = new CancellationTokenSource(grace, _timeProvider);
        if (!await WaitWithinStopDeadlineAsync(Task.WhenAll(closeTask, cancelTask), stopStartedAt, grace).ConfigureAwait(false))
        {
            await FailStopAsync("Evidence admission did not close within its bounded stopping grace.").ConfigureAwait(false);
            return;
        }

        try
        {
            await closeTask.ConfigureAwait(false);
            await cancelTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableException(ex))
        {
            await FailStopAsync("Evidence admission could not be closed safely.").ConfigureAwait(false);
            return;
        }

        var requestTask = Task.Run(async () => await _supervisor.RequestStopAsync(stoppingCancellation.Token).ConfigureAwait(false), CancellationToken.None);
        if (!await WaitWithinStopDeadlineAsync(requestTask, stopStartedAt, grace).ConfigureAwait(false))
        {
            await FailStopAsync("Evidence stop request did not settle within its bounded grace.").ConfigureAwait(false);
            return;
        }

        try { await requestTask.ConfigureAwait(false); }
        catch (Exception ex) when (IsRecoverableException(ex))
        {
            Latch(EvidenceWorkerTerminalCode.CleanupFailed, ex);
            await FailStopAsync("Evidence supervisor could not request owned-work stop.").ConfigureAwait(false);
            return;
        }

        var exitTask = Task.Run(async () => await _supervisor.WaitForOwnedExitAsync(stoppingCancellation.Token).ConfigureAwait(false), CancellationToken.None);
        var callbackJoins = Task.WhenAll(tasks.Select(SettleIgnoringCallbackFailureAsync));
        if (!await WaitWithinStopDeadlineAsync(callbackJoins, stopStartedAt, grace).ConfigureAwait(false))
        {
            await FailStopAsync("Owned Evidence callback or write remained active after its bounded grace.").ConfigureAwait(false);
            return;
        }

        try { await callbackJoins.ConfigureAwait(false); }
        catch (Exception ex) when (IsRecoverableException(ex)) { Latch(EvidenceWorkerTerminalCode.CleanupFailed, ex); }

        if (!await WaitWithinStopDeadlineAsync(exitTask, stopStartedAt, grace).ConfigureAwait(false))
        {
            await FailStopAsync("Evidence supervisor did not acknowledge owned-work exit within its bounded grace.").ConfigureAwait(false);
            return;
        }

        try { await exitTask.ConfigureAwait(false); }
        catch (Exception ex) when (IsRecoverableException(ex))
        {
            await FailStopAsync("Evidence supervisor failed to acknowledge owned-work exit.").ConfigureAwait(false);
            return;
        }

        lock (_gate) _ownedExitEstablished = true;
    }

    private async Task<bool> WaitWithinStopDeadlineAsync(Task task, long startedAt, TimeSpan allowance)
    {
        var remaining = Remaining(allowance, startedAt);
        if (remaining <= TimeSpan.Zero) return false;
        return await Task.WhenAny(task, DelayForRemainingAsync(remaining)).ConfigureAwait(false) == task
            && Remaining(allowance, startedAt) > TimeSpan.Zero;
    }

    private Task DelayForRemainingAsync(TimeSpan remaining)
    {
        // Task.Delay truncates fractional milliseconds. Round its notification upward; the
        // monotonic completion checks still enforce the original, unrounded allowance.
        var fractionalTicks = remaining.Ticks % TimeSpan.TicksPerMillisecond;
        var timerDelay = fractionalTicks == 0 ? remaining
            : remaining + TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - fractionalTicks);
        return Task.Delay(timerDelay, _timeProvider, CancellationToken.None);
    }

    private async Task JoinAllOwnedWorkAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_gate)
            {
                tasks = _ownedWork.ToArray();
                if (tasks.All(static task => task.IsCompleted))
                {
                    foreach (var task in tasks) task.GetAwaiter().GetResult();
                    return;
                }
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static async Task SettleIgnoringCallbackFailureAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (IsRecoverableException(ex)) { /* A faulted callback is settled; its stage outcome already latched failure. */ }
    }

    private async ValueTask<bool> HasUnsettledOwnedWorkAsync()
    {
        Task[] tasks;
        lock (_gate) tasks = _ownedWork.ToArray();
        if (tasks.Any(static task => !task.IsCompleted))
        {
            await FailStopAsync("Owned Evidence work remained active before cleanup.").ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private ValueTask<bool> FailStopAsync(string message)
    {
        throw _fatalTermination(message);
    }

    private void Latch(EvidenceWorkerTerminalCode code, Exception? exception)
    {
        lock (_gate) LatchLocked(code, exception);
    }

    private void LatchLocked(EvidenceWorkerTerminalCode code, Exception? exception)
    {
        if (_terminalCode == EvidenceWorkerTerminalCode.None)
        {
            _terminalCode = code;
            _terminalException = exception;
            _admissionClosed = true;
            _admission?.LatchFailure();
        }
    }

    private TimeSpan JobRemaining => Remaining(_jobAllowance, _startedAt);
    private TimeSpan CleanupRemaining
    {
        get
        {
            lock (_gate)
            {
                var remainingForCleanup = JobRemaining - _collectionReserve;
                if (remainingForCleanup <= TimeSpan.Zero) return TimeSpan.Zero;
                return _cleanupStartedAt is long started
                    ? Min(Remaining(_cleanupAllowance, started), remainingForCleanup)
                    : Min(_cleanupAllowance, remainingForCleanup);
            }
        }
    }

    private TimeSpan Remaining(TimeSpan allowance, long started)
    {
        var elapsed = _timeProvider.GetElapsedTime(started, _timeProvider.GetTimestamp());
        return elapsed >= allowance ? TimeSpan.Zero : allowance - elapsed;
    }

    private static bool StageDeadlineAllowed(EvidenceRunStage stage, TimeSpan deadline) => stage switch
    {
        EvidenceRunStage.Admission => deadline <= EvidenceRunBudgetLimits.Admission,
        EvidenceRunStage.Start => deadline <= EvidenceRunBudgetLimits.Start,
        EvidenceRunStage.Resource or EvidenceRunStage.Producer => true,
        _ => false,
    };

    private static bool IsFatalRuntimeException(Exception exception) => exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
    private static bool IsRecoverableException(Exception exception) => !IsFatalRuntimeException(exception)
        && exception is not EvidenceWorkerTestInterruptionException;
    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
    private bool HasCurrentArmedLease() => _supervisor.IsArmed && string.Equals(_supervisor.RunId, _runId, StringComparison.Ordinal);

    private static Exception FatalTermination(string message)
    {
        Environment.FailFast(message);
        return new InvalidOperationException("Environment.FailFast returned unexpectedly.");
    }
}
