using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>One cumulative monotonic teardown schedule; constructing it establishes no native authority.</summary>
/// <remarks>
/// The actual owner explicitly begins this schedule when work closes, using its original job remainder
/// and protected reservation. Collection and cleanup may share a reservation; stopping is not added again.
/// Before Begin, Remaining and Token reject; Cancellation may be borrowed without starting teardown.
/// A later Begin never replaces the first reservation, timer or token.
/// The clock must provide matching monotonic timestamps and timers. UTC changes do not renew this budget.
/// Borrowers may link the token into local operations, but only this holder cancels or disposes its source.
/// Cancellation does not join operations, establish kernel exit, release accounts or issue custody.
/// </remarks>
internal sealed class SupervisionTeardownDeadline : IDisposable
{
    /// <summary>Maximum original job remainder permitted by the protected request contract.</summary>
    internal static readonly TimeSpan MaximumOriginalRemaining = TimeSpan.FromHours(1);

    /// <summary>Maximum separate collection plus cleanup reservation; stopping already consumes cleanup.</summary>
    internal static readonly TimeSpan MaximumReservedAllowance = EvidenceRunBudgetLimits.Collection + EvidenceRunBudgetLimits.Cleanup;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationToken _token;
    private ITimer? _timer;
    private long _startedAt;
    private TimeSpan _allowance;
    private TimeSpan _lastElapsed;
    private bool _begun;
    private bool _expired;
    private bool _failed;
    private bool _disposed;

    /// <summary>Creates scheduling state without starting a timer.</summary>
    /// <param name="clock">Owner-selected monotonic clock and matching timer provider.</param>
    internal SupervisionTeardownDeadline(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _token = _source.Token;
    }

    /// <summary>Gets whether the explicit first Begin claimed this schedule, even after failure or disposal.</summary>
    /// <remarks>False before a valid claim. This scheduling datum neither starts a timer nor grants cleanup.</remarks>
    internal bool IsStarted { get { lock (_gate) return _begun; } }

    /// <summary>Reserves the first expiry synchronously before timer acquisition or callbacks.</summary>
    /// <param name="originalRemaining">Positive original job remainder, no greater than one hour.</param>
    /// <param name="reservedAllowance">Positive protected reservation, no greater than collection plus cleanup.</param>
    /// <remarks>
    /// Inputs are checked on every call. Valid repeated calls are no-ops, including after expiry; they
    /// cannot restore usable Remaining or Token. Timer/clock failure permanently rejects this instance.
    /// </remarks>
    internal void Begin(TimeSpan originalRemaining, TimeSpan reservedAllowance)
    {
        RequireBound(originalRemaining, MaximumOriginalRemaining, nameof(originalRemaining));
        RequireBound(reservedAllowance, MaximumReservedAllowance, nameof(reservedAllowance));
        var cancel = false;
        lock (_gate)
        {
            RequireNotDisposed();
            if (_failed) throw Unavailable();
            if (_begun) return;
            _begun = true;
            _allowance = originalRemaining < reservedAllowance ? originalRemaining : reservedAllowance;
            try
            {
                _startedAt = _clock.GetTimestamp();
                _timer = _clock.CreateTimer(static state => ((SupervisionTeardownDeadline)state!).OnTimer(),
                    this, _allowance, Timeout.InfiniteTimeSpan);
                // Timer acquisition itself consumes the first reservation rather than moving its expiry.
                var remaining = ReadRemaining();
                if (remaining == TimeSpan.Zero) cancel = true;
                else if (!_timer.Change(remaining, Timeout.InfiniteTimeSpan)) throw Unavailable();
            }
            catch (Exception error) when (Recoverable(error)) { _failed = true; cancel = true; }
        }
        if (cancel) CancelSource();
        lock (_gate) if (_failed || _expired) throw Unavailable();
    }

    /// <summary>Gets the first reservation's positive remaining time; unbegun, expired or failed state rejects.</summary>
    /// <remarks>Each inspection checks elapsed monotonic time even if the timer callback has not run.</remarks>
    internal TimeSpan Remaining => Inspect();

    /// <summary>Gets the borrowed token only while the first reservation is usable.</summary>
    /// <remarks>The owner retains source lifetime. A token borrowed earlier is canceled on expiry or disposal.</remarks>
    internal CancellationToken Token { get { _ = Inspect(); return _token; } }

    /// <summary>Gets the borrowed source token without implicitly claiming or starting teardown.</summary>
    /// <remarks>
    /// Before Begin, already owned operations may link this token alongside their original job/caller
    /// bounds. It supplies no timer or cleanup permission then. Once begun, inspection checks the same
    /// monotonic expiry as Token and cancels borrowers on expiry. Failed or disposed holders reject.
    /// Linking or disposing a local source cannot cancel this holder's source. Cancellation requests
    /// interruption only; callers must still join each original operation, even if it ignores cancellation.
    /// </remarks>
    internal CancellationToken Cancellation
    {
        get
        {
            lock (_gate)
            {
                RequireNotDisposed();
                if (_failed) throw Unavailable();
                if (!_begun) return _token;
            }
            _ = Inspect();
            return _token;
        }
    }

    /// <summary>Irreversibly closes scheduling state, cancels borrowers and stops the owned timer/source once.</summary>
    /// <remarks>Join token consumers separately. Cancellation callbacks run outside the state lock.</remarks>
    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer;
        }
        CancelSource();
        var failed = false;
        try { timer?.Dispose(); }
        catch (Exception error) when (Recoverable(error)) { failed = true; }
        try { _source.Dispose(); }
        catch (Exception error) when (Recoverable(error)) { failed = true; }
        lock (_gate) failed |= _failed;
        if (failed) throw Unavailable();
    }

    private TimeSpan Inspect()
    {
        var remaining = TimeSpan.Zero;
        lock (_gate)
        {
            RequireNotDisposed();
            if (!_begun) throw Unavailable();
            if (!_failed && !_expired)
            {
                try { remaining = ReadRemaining(); }
                catch (Exception error) when (Recoverable(error)) { _failed = true; }
            }
        }
        if (remaining == TimeSpan.Zero) { CancelSource(); throw Unavailable(); }
        return remaining;
    }

    // Caller holds _gate. An observed clock regression cannot extend the first expiry.
    private TimeSpan ReadRemaining()
    {
        var elapsed = _clock.GetElapsedTime(_startedAt, _clock.GetTimestamp());
        if (elapsed < _lastElapsed || elapsed < TimeSpan.Zero) throw Unavailable();
        _lastElapsed = elapsed;
        if (elapsed >= _allowance) { _expired = true; return TimeSpan.Zero; }
        return _allowance - elapsed;
    }

    private void OnTimer()
    {
        var cancel = false;
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var remaining = ReadRemaining();
                if (remaining == TimeSpan.Zero) cancel = true;
                else if (_timer is null || !_timer.Change(remaining, Timeout.InfiniteTimeSpan)) throw Unavailable();
            }
            catch (Exception error) when (Recoverable(error)) { _failed = true; cancel = true; }
        }
        if (cancel) CancelSource();
    }

    private void CancelSource()
    {
        try { _source.Cancel(); }
        catch (ObjectDisposedException) { lock (_gate) if (!_disposed) _failed = true; }
        catch (Exception error) when (Recoverable(error)) { lock (_gate) _failed = true; }
    }

    private void RequireNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static void RequireBound(TimeSpan value, TimeSpan maximum, string name)
    {
        if (value <= TimeSpan.Zero || value > maximum) throw new ArgumentOutOfRangeException(name);
    }
    private static InvalidOperationException Unavailable() => new("The teardown schedule is unavailable.");
    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
