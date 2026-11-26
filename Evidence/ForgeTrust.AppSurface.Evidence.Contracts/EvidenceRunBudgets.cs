using System.Collections.ObjectModel;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>
/// Defines bounded, internal resource budgets shared by Evidence run entry points.
/// Values supplied to these types must come from protected registration; callers
/// must not use head-controlled settings to raise the documented defaults.
/// </summary>
internal static class EvidenceRunBudgetLimits
{
    /// <summary>Maximum aggregate artifact bytes accepted for one Evidence run.</summary>
    internal const long MaximumArtifactBytes = 256L * 1024 * 1024;

    /// <summary>Maximum aggregate process bytes received for one Evidence run.</summary>
    internal const long MaximumProcessOutputBytes = 16L * 1024 * 1024;

    /// <summary>Maximum in-memory prefix retained for each individual output stream.</summary>
    internal const int RetainedOutputPrefixBytesPerStream = 1 * 1024 * 1024;

    /// <summary>Maximum time to check admission before a run starts.</summary>
    internal static readonly TimeSpan Admission = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time for startup.</summary>
    internal static readonly TimeSpan Start = TimeSpan.FromSeconds(120);

    /// <summary>Maximum time reserved to collect finalized output after cleanup.</summary>
    internal static readonly TimeSpan Collection = TimeSpan.FromSeconds(60);

    /// <summary>Maximum stopping grace within the cleanup allowance.</summary>
    internal static readonly TimeSpan Stopping = TimeSpan.FromSeconds(30);

    /// <summary>Maximum shared allowance for stopping and disposal.</summary>
    internal static readonly TimeSpan Cleanup = TimeSpan.FromMinutes(10);
}

/// <summary>Names the serial work stages whose declared deadlines are admitted together.</summary>
internal enum EvidenceRunStage
{
    /// <summary>Checks the caller's admission before dispatch.</summary>
    Admission,
    /// <summary>Starts the selected worker or application.</summary>
    Start,
    /// <summary>Waits for a declared resource.</summary>
    Resource,
    /// <summary>Runs a declared producer.</summary>
    Producer,
}

/// <summary>A declared serial stage and its positive, protected deadline.</summary>
/// <param name="Stage">The stage category, used to enforce any category-specific cap.</param>
/// <param name="Duration">The positive stage deadline declared by protected registration.</param>
internal sealed record EvidenceRunStageDeadline(EvidenceRunStage Stage, TimeSpan Duration);

/// <summary>Identifies one admitted stage execution, even when declarations repeat.</summary>
/// <param name="deadline">The declaration admitted for this execution.</param>
internal sealed class EvidenceRunStageAdmission(EvidenceRunStageDeadline deadline)
{
    /// <summary>The admitted stage declaration and its duration.</summary>
    internal EvidenceRunStageDeadline Deadline { get; } = deadline;
}

/// <summary>
/// Admits declared serial work against a frozen monotonic job allowance and preserves
/// collection and cleanup reserves as work advances. Stopping consumes the cleanup
/// reserve; it is never added to that reserve a second time.
/// </summary>
internal sealed class EvidenceRunTimeBudget
{
    private enum RunPhase
    {
        BetweenStages,
        StageActive,
        CleaningUp,
        ReadyForCollection,
        Collecting,
        Finished,
    }

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly long _allowanceSampledAt;
    private readonly TimeSpan _jobAllowance;
    private readonly ReadOnlyCollection<EvidenceRunStageDeadline> _stages;
    private readonly TimeSpan _collection;
    private readonly TimeSpan _cleanup;
    private readonly TimeSpan _stopping;
    private TimeSpan _remainingStageDuration;
    private int _nextStage;
    private EvidenceRunStageAdmission? _activeStage;
    private RunPhase _phase = RunPhase.BetweenStages;
    private long _phaseStartedAt;

    private EvidenceRunTimeBudget(
        TimeProvider timeProvider,
        long allowanceSampledAt,
        TimeSpan jobAllowance,
        ReadOnlyCollection<EvidenceRunStageDeadline> stages,
        TimeSpan totalStageDuration,
        TimeSpan collection,
        TimeSpan cleanup,
        TimeSpan stopping)
    {
        _timeProvider = timeProvider;
        _allowanceSampledAt = allowanceSampledAt;
        _jobAllowance = jobAllowance;
        _stages = stages;
        _remainingStageDuration = totalStageDuration;
        _collection = collection;
        _cleanup = cleanup;
        _stopping = stopping;
    }

    /// <summary>Validates serial stages and reserves against the independently supplied remaining job allowance.</summary>
    /// <param name="timeProvider">The monotonic clock used for admission and elapsed-time checks.</param>
    /// <param name="allowance">The positive remaining allowance measured by the independent launcher.</param>
    /// <param name="allowanceSampledAt">The timestamp from the same monotonic clock when the launcher measured the allowance.</param>
    /// <param name="stages">Ordered serial stages, captured once for validation and accounting. Deadlines must be positive. Admission and startup also have category-specific caps.</param>
    /// <param name="collection">Positive collection allowance, no greater than 60 seconds.</param>
    /// <param name="cleanup">Positive cumulative stopping/disposal allowance, no greater than ten minutes.</param>
    /// <param name="stopping">Positive stopping grace, no greater than 30 seconds or cleanup.</param>
    /// <param name="budget">The created budget on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when all declared stages and reserves fit the frozen job allowance.</returns>
    /// <remarks>
    /// Carry the launcher's original timestamp with its allowance so handoff time
    /// counts against the job. Name the
    /// <c>collection:</c>, <c>cleanup:</c>, and <c>stopping:</c> arguments at the call site.
    /// Budget creation grants no execution authority.
    /// </remarks>
    internal static bool TryCreateFromAllowance(TimeProvider timeProvider, TimeSpan allowance, long allowanceSampledAt,
        IReadOnlyList<EvidenceRunStageDeadline> stages, TimeSpan collection, TimeSpan cleanup, TimeSpan stopping,
        out EvidenceRunTimeBudget? budget)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(stages);
        budget = null;
        if (allowance <= TimeSpan.Zero || !IsWithin(collection, EvidenceRunBudgetLimits.Collection)
            || !IsWithin(cleanup, EvidenceRunBudgetLimits.Cleanup)
            || !IsWithin(stopping, EvidenceRunBudgetLimits.Stopping) || stopping > cleanup)
        {
            return false;
        }

        var elapsed = timeProvider.GetElapsedTime(allowanceSampledAt, timeProvider.GetTimestamp());
        if (elapsed < TimeSpan.Zero || elapsed >= allowance)
        {
            return false;
        }

        var stageAllowance = allowance - elapsed - collection - cleanup;
        if (stageAllowance < TimeSpan.Zero)
        {
            return false;
        }

        var remaining = stageAllowance;
        var stageSnapshot = Array.AsReadOnly(stages.ToArray());
        foreach (var stage in stageSnapshot)
        {
            if (stage is null || stage.Duration <= TimeSpan.Zero || !IsAllowedStage(stage.Stage, stage.Duration)
                || stage.Duration > remaining)
            {
                return false;
            }

            remaining -= stage.Duration;
        }

        budget = new EvidenceRunTimeBudget(timeProvider, allowanceSampledAt, allowance, stageSnapshot,
            stageAllowance - remaining, collection, cleanup, stopping);
        return true;
    }

    /// <summary>The remaining monotonic time until the job deadline, never negative.</summary>
    internal TimeSpan JobRemaining => JobRemainingAt(_timeProvider.GetTimestamp());

    /// <summary>Begins the next declared stage only when its full deadline and all later reserves fit.</summary>
    /// <param name="cancellationToken">A caller cancellation signal checked before the stage is admitted.</param>
    /// <param name="admission">The handle to pass back on completion; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> only when this ordered stage can start without consuming later reserves.</returns>
    internal bool TryBeginNextStage(CancellationToken cancellationToken, out EvidenceRunStageAdmission? admission)
    {
        lock (_gate)
        {
            admission = null;
            if (cancellationToken.IsCancellationRequested || _phase != RunPhase.BetweenStages || _nextStage >= _stages.Count)
            {
                return false;
            }

            var reserve = _remainingStageDuration + _collection + _cleanup;
            var now = _timeProvider.GetTimestamp();
            if (JobRemainingAt(now) < reserve)
            {
                return false;
            }

            admission = new EvidenceRunStageAdmission(_stages[_nextStage]);
            _activeStage = admission;
            _phaseStartedAt = now;
            _phase = RunPhase.StageActive;
            return true;
        }
    }

    /// <summary>Returns the active stage's monotonic remaining duration.</summary>
    internal TimeSpan CurrentStageRemaining
    {
        get
        {
            lock (_gate)
            {
                return _phase == RunPhase.StageActive
                    ? GetActivePhaseRemaining(_stages[_nextStage].Duration)
                    : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Closes accounting for the active stage so the next stage can be considered.</summary>
    /// <param name="admission">The handle returned by this budget when it started the stage.</param>
    /// <returns>Whether the handle matched and the active stage was closed.</returns>
    /// <remarks>The execution layer determines the stage outcome and enforces the deadline before starting more work.</remarks>
    internal bool CompleteCurrentStage(EvidenceRunStageAdmission? admission)
    {
        lock (_gate)
        {
            if (_phase != RunPhase.StageActive || !ReferenceEquals(admission, _activeStage))
            {
                return false;
            }

            _remainingStageDuration -= _stages[_nextStage].Duration;
            _nextStage++;
            _activeStage = null;
            _phase = RunPhase.BetweenStages;
            return true;
        }
    }

    /// <summary>Begins the single collection window after cleanup completes and the allowance still fits.</summary>
    /// <returns>Whether the collection window was started.</returns>
    internal bool TryBeginCollection()
    {
        lock (_gate)
        {
            if (_phase != RunPhase.ReadyForCollection)
            {
                return false;
            }

            var now = _timeProvider.GetTimestamp();
            if (JobRemainingAt(now) < _collection)
            {
                return false;
            }

            _phaseStartedAt = now;
            _phase = RunPhase.Collecting;
            return true;
        }
    }

    /// <summary>Returns the collection allowance remaining from its monotonic start.</summary>
    internal TimeSpan CollectionRemaining
    {
        get
        {
            lock (_gate)
            {
                return _phase == RunPhase.Collecting
                    ? GetActivePhaseRemaining(_collection)
                    : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Closes the collection window permanently for this run.</summary>
    /// <returns>Whether a collection window was active.</returns>
    /// <remarks>The collector determines its outcome and enforces its deadline separately.</remarks>
    internal bool CompleteCollection()
    {
        lock (_gate)
        {
            if (_phase != RunPhase.Collecting)
            {
                return false;
            }

            _phase = RunPhase.Finished;
            return true;
        }
    }

    /// <summary>
    /// Abandons any remaining stages and starts the shared stopping/disposal cleanup clock.
    /// The caller marks the stage closed after cancellation/fault; this only closes future
    /// stage admission. The lifecycle still owns stopping and joining active work before disposal.
    /// </summary>
    /// <param name="stageClosed">Whether the caller has closed admission to work in the current stage.</param>
    /// <returns>Whether the cleanup window was started.</returns>
    /// <remarks>Deadline exhaustion prevents a successful cleanup claim. The caller still owns stop, join, and disposal.</remarks>
    internal bool TryAbandonStagesAndBeginCleanup(bool stageClosed)
    {
        lock (_gate)
        {
            if (!stageClosed || _phase is not (RunPhase.BetweenStages or RunPhase.StageActive))
            {
                return false;
            }

            var now = _timeProvider.GetTimestamp();
            if (JobRemainingAt(now) <= TimeSpan.Zero)
            {
                return false;
            }

            // Completion may arrive from a timeout/cancellation path. Once cleanup begins,
            // no later serial stage can be admitted, even if its declared deadline remains.
            _activeStage = null;
            _phaseStartedAt = now;
            _phase = RunPhase.CleaningUp;
            return true;
        }
    }

    /// <summary>Returns remaining cumulative cleanup time, shared by stop/join and disposal.</summary>
    internal TimeSpan CleanupRemaining
    {
        get
        {
            lock (_gate)
            {
                return GetCleanupRemaining();
            }
        }
    }

    /// <summary>Returns stopping grace capped by the shared cleanup and job reserves.</summary>
    /// <remarks>The caller samples this allowance when stopping begins and enforces that deadline through stop and join.</remarks>
    internal TimeSpan StoppingAllowance => Min(_stopping, CleanupRemaining);

    /// <summary>Completes stopping/disposal, leaving the collection allowance reserved.</summary>
    /// <returns>Whether active cleanup finished before its own deadline with at least the collection reserve remaining.</returns>
    internal bool CompleteCleanup()
    {
        lock (_gate)
        {
            if (_phase != RunPhase.CleaningUp)
            {
                return false;
            }

            var now = _timeProvider.GetTimestamp();
            if (Remaining(_cleanup, _phaseStartedAt, now) <= TimeSpan.Zero || JobRemainingAt(now) < _collection)
            {
                return false;
            }

            _phase = RunPhase.ReadyForCollection;
            return true;
        }
    }

    // Caller holds _gate while evaluating the active cleanup window.
    private TimeSpan GetCleanupRemaining()
    {
        if (_phase != RunPhase.CleaningUp)
        {
            return TimeSpan.Zero;
        }

        var now = _timeProvider.GetTimestamp();
        var timeBeforeCollection = JobRemainingAt(now) - _collection;
        return Min(Remaining(_cleanup, _phaseStartedAt, now), timeBeforeCollection > TimeSpan.Zero ? timeBeforeCollection : TimeSpan.Zero);
    }

    private TimeSpan GetActivePhaseRemaining(TimeSpan duration)
    {
        var now = _timeProvider.GetTimestamp();
        return Min(Remaining(duration, _phaseStartedAt, now), JobRemainingAt(now));
    }

    private TimeSpan JobRemainingAt(long now) => Remaining(_jobAllowance, _allowanceSampledAt, now);

    private TimeSpan Remaining(TimeSpan duration, long started, long now)
    {
        var elapsed = _timeProvider.GetElapsedTime(started, now);
        return elapsed >= duration ? TimeSpan.Zero : duration - elapsed;
    }

    private static bool IsAllowedStage(EvidenceRunStage stage, TimeSpan duration)
        => stage switch
        {
            EvidenceRunStage.Admission => IsWithin(duration, EvidenceRunBudgetLimits.Admission),
            EvidenceRunStage.Start => IsWithin(duration, EvidenceRunBudgetLimits.Start),
            EvidenceRunStage.Resource or EvidenceRunStage.Producer => true,
            _ => false,
        };

    private static bool IsWithin(TimeSpan value, TimeSpan maximum) => value > TimeSpan.Zero && value <= maximum;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}

/// <summary>
/// Atomically accounts aggregate bytes across concurrent producers or output streams.
/// A rejected over-limit charge permanently latches failure and invokes the optional
/// host signal outside the lock. Callback exceptions are swallowed and never exposed.
/// </summary>
internal sealed class EvidenceRunByteQuota
{
    private readonly object _gate = new();
    private readonly Action? _onExceeded;
    private readonly Action<long, bool> _finishReservation;
    private long _committedBytes;
    private long _reservedBytes;
    private bool _failed;

    /// <summary>Creates a quota no larger than the protected default/maximum.</summary>
    private EvidenceRunByteQuota(long limit, long protectedMaximum, Action? onExceeded = null)
    {
        if (limit <= 0 || limit > protectedMaximum)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Quota must be positive and cannot exceed its protected maximum.");
        }

        Limit = limit;
        _onExceeded = onExceeded;
        _finishReservation = FinishReservation;
    }

    /// <summary>Creates the artifact-byte quota, optionally lowered by protected registration.</summary>
    /// <param name="limit">A positive aggregate limit no greater than 256 MiB.</param>
    /// <param name="onExceeded">An optional no-argument host signal invoked once after an overflow latch.</param>
    internal static EvidenceRunByteQuota CreateArtifact(long limit = EvidenceRunBudgetLimits.MaximumArtifactBytes, Action? onExceeded = null)
        => new(limit, EvidenceRunBudgetLimits.MaximumArtifactBytes, onExceeded);

    /// <summary>Creates the received process-output quota, optionally lowered by protected registration.</summary>
    /// <param name="limit">A positive aggregate limit no greater than 16 MiB.</param>
    /// <param name="onExceeded">An optional no-argument host signal invoked once after an overflow latch.</param>
    internal static EvidenceRunByteQuota CreateProcessOutput(long limit = EvidenceRunBudgetLimits.MaximumProcessOutputBytes, Action? onExceeded = null)
        => new(limit, EvidenceRunBudgetLimits.MaximumProcessOutputBytes, onExceeded);

    /// <summary>Maximum bytes accepted by this run-wide quota.</summary>
    internal long Limit { get; }

    /// <summary>Whether a quota crossing has irreversibly failed the run.</summary>
    internal bool IsFailed
    {
        get { lock (_gate) return _failed; }
    }

    /// <summary>Bytes committed plus currently reserved by admitted writes.</summary>
    internal long AccountedBytes
    {
        get { lock (_gate) return _committedBytes + _reservedBytes; }
    }

    /// <summary>Reserves artifact bytes. Dispose releases an unfinished write; Commit retains its charge.</summary>
    /// <param name="bytes">The nonnegative byte ceiling the owner must enforce for one artifact write. Zero supports empty artifacts.</param>
    /// <param name="reservation">An owned reservation on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="false"/> after any quota crossing or when this reservation would exceed the limit.</returns>
    /// <remarks>Pending reservations count against the limit. Releasing one after a rejected charge leaves the failure latched.</remarks>
    internal bool TryReserve(long bytes, out EvidenceRunByteReservation? reservation)
        => TryAccountBytes(bytes, reserve: true, out reservation);

    /// <summary>Charges actual received bytes, including bytes streamed or discarded after the retained prefix.</summary>
    /// <param name="bytes">The nonnegative byte count observed from a process output stream.</param>
    /// <returns><see langword="false"/> after any quota crossing or when this charge would exceed the limit.</returns>
    internal bool TryChargeReceived(long bytes)
        => TryAccountBytes(bytes, reserve: false, out _);

    private bool TryAccountBytes(long bytes, bool reserve, out EvidenceRunByteReservation? reservation)
    {
        reservation = null;
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        var notify = false;
        lock (_gate)
        {
            if (_failed)
            {
                return false;
            }

            if (bytes > Limit - _committedBytes - _reservedBytes)
            {
                _failed = true;
                notify = true;
            }
            else if (reserve)
            {
                _reservedBytes += bytes;
                reservation = new EvidenceRunByteReservation(_finishReservation, bytes);
            }
            else
            {
                _committedBytes += bytes;
            }
        }

        if (notify)
        {
            try { _onExceeded?.Invoke(); }
            catch (Exception) { /* Host notification must not expose callback exception text or undo the latch. */ }
        }

        return !notify;
    }

    private void FinishReservation(long bytes, bool commit)
    {
        lock (_gate)
        {
            _reservedBytes -= bytes;
            if (commit)
            {
                _committedBytes += bytes;
            }
        }
    }
}

/// <summary>An owned, exactly-once artifact quota reservation.</summary>
/// <remarks>One owner serializes commit and disposal for each reservation. Separate reservations can settle concurrently.</remarks>
internal sealed class EvidenceRunByteReservation : IDisposable
{
    private Action<long, bool>? _finish;

    /// <summary>Creates the reservation returned by <see cref="EvidenceRunByteQuota.TryReserve(long, out EvidenceRunByteReservation?)"/>.</summary>
    /// <param name="finish">The quota's private accounting callback. Its second argument indicates a committed write.</param>
    /// <param name="bytes">The byte count already reserved in the quota.</param>
    internal EvidenceRunByteReservation(Action<long, bool> finish, long bytes)
    {
        _finish = finish;
        Bytes = bytes;
    }

    /// <summary>Reserved byte count.</summary>
    internal long Bytes { get; }

    /// <summary>Commits the reservation after the corresponding write succeeds.</summary>
    /// <remarks>The owner enforces <see cref="Bytes"/> as the write ceiling. Committing charges the reserved amount.</remarks>
    /// <exception cref="InvalidOperationException">The reservation has already been committed or released.</exception>
    internal void Commit()
    {
        var finish = Interlocked.Exchange(ref _finish, null)
            ?? throw new InvalidOperationException("The reservation is already completed.");
        finish(Bytes, true);
    }

    /// <summary>Releases an uncommitted reservation after its write has settled.</summary>
    /// <remarks>Only the owner disposes this reservation. Repeated disposal by the owner is safe after the first call returns.</remarks>
    public void Dispose()
    {
        Interlocked.Exchange(ref _finish, null)?.Invoke(Bytes, false);
    }
}
