namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Thread-confined clock for transport tests; advancing time fires timers by due time and creation order without real waits.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private long _timestamp;
    private bool _isAdvancing;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _timestamp;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ValidateTimeout(dueTime, nameof(dueTime));
        ValidateTimeout(period, nameof(period));

        var timer = new ManualTimer(this, callback, state, dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock forward and fires all timers due by the target, honoring callback changes and disposal.</summary>
    /// <param name="amount">Nonnegative simulated elapsed time.</param>
    /// <exception cref="ArgumentOutOfRangeException">The requested interval is negative.</exception>
    /// <exception cref="InvalidOperationException">A callback attempts to advance the clock recursively.</exception>
    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        if (_isAdvancing)
        {
            throw new InvalidOperationException("Manual time cannot be advanced from a timer callback.");
        }

        var target = _utcNow + amount;
        _isAdvancing = true;
        try
        {
            while (TryGetNextDueTimer(target, out var timer, out var dueTime))
            {
                MoveTo(dueTime);
                timer.Fire();
            }

            MoveTo(target);
        }
        finally
        {
            _isAdvancing = false;
        }
    }

    private bool TryGetNextDueTimer(DateTimeOffset target, out ManualTimer timer, out DateTimeOffset dueTime)
    {
        timer = null!;
        dueTime = default;
        DateTimeOffset? earliestDueTime = null;

        foreach (var candidate in _timers)
        {
            if (candidate.DueTime is not { } candidateDueTime || candidateDueTime > target)
            {
                continue;
            }

            if (earliestDueTime is null || candidateDueTime < earliestDueTime.Value)
            {
                timer = candidate;
                dueTime = candidateDueTime;
                earliestDueTime = candidateDueTime;
            }
        }

        return earliestDueTime is not null;
    }

    private void MoveTo(DateTimeOffset utcNow)
    {
        _timestamp += (utcNow - _utcNow).Ticks;
        _utcNow = utcNow;
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private readonly TimerCallback _callback = callback;
        private readonly object? _state = state;
        private TimeSpan _period = period;
        private bool _disposed;

        public DateTimeOffset? DueTime { get; private set; } = Schedule(owner._utcNow, dueTime);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimeout(dueTime, nameof(dueTime));
            ValidateTimeout(period, nameof(period));
            if (_disposed)
            {
                return false;
            }

            DueTime = Schedule(owner._utcNow, dueTime);
            _period = period;
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            DueTime = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire()
        {
            var dueTime = DueTime!.Value;
            DueTime = _period > TimeSpan.Zero ? dueTime + _period : null;
            _callback(_state);
        }

        private static DateTimeOffset? Schedule(DateTimeOffset now, TimeSpan dueTime)
            => dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
    }
}
