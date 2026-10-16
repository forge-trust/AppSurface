namespace ForgeTrust.AppSurface.Evidence.Readiness;

/// <summary>One monotonic cooperative budget preserving caller and deadline cancellation identities.</summary>
internal sealed class ReadinessDeadline : IDisposable
{
    private readonly TimeSpan _timeout;
    private readonly long _started;
    private readonly CancellationToken _caller;
    private readonly CancellationToken _stop;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private ReadinessAttempt _lastHttpAttempt;

    /// <summary>Starts a validated deadline. The optional stop token belongs to an adapter, not the caller.</summary>
    /// <param name="timeout">Finite positive timer-supported allowance; never restarted between phases.</param>
    /// <param name="timeProvider">Monotonic clock and timer provider, normally TimeProvider.System.</param>
    /// <param name="caller">Original caller identity, preserved separately from linked work cancellation.</param>
    /// <param name="stop">Optional adapter shutdown identity checked after caller and deadline.</param>
    internal ReadinessDeadline(TimeSpan timeout, TimeProvider timeProvider, CancellationToken caller, CancellationToken stop = default)
    {
        ValidateDuration(timeout, nameof(timeout));
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeout = timeout;
        TimeProvider = timeProvider;
        _started = timeProvider.GetTimestamp();
        _caller = caller;
        _stop = stop;
        _deadline = new CancellationTokenSource(timeout, timeProvider);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _deadline.Token, stop);
    }

    /// <summary>Gets the provider used for monotonic measurements, timers, and delays.</summary>
    internal TimeProvider TimeProvider { get; }
    /// <summary>Gets cooperative cancellation for work; errors must be checked with <see cref="Check"/>.</summary>
    internal CancellationToken Token => _linked.Token;
    /// <summary>Gets the remaining original budget, clamped to zero.</summary>
    internal TimeSpan Remaining
    {
        get
        {
            var remaining = _timeout - TimeProvider.GetElapsedTime(_started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Validates finite positive durations supported by BCL timers before acquiring resources.</summary>
    internal static void ValidateDuration(TimeSpan duration, string parameterName)
    {
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A finite positive timer-supported duration is required.");
        }
    }

    /// <summary>Checks caller first, then original deadline, then adapter shutdown; never resets the budget.</summary>
    /// <remarks>The single sequential operation retains only safe latest HTTP scalars so post-probe cleanup checks
    /// keep attempt/cause diagnostics. A deadline instance must not be shared across independent concurrent waits.</remarks>
    internal void Check(ReadinessPhase phase, ReadinessAttempt last = default)
    {
        if (phase == ReadinessPhase.Http)
        {
            if (last.Attempts > 0)
            {
                _lastHttpAttempt = last;
            }
            else
            {
                last = _lastHttpAttempt;
            }
        }

        _caller.ThrowIfCancellationRequested();
        if (_deadline.IsCancellationRequested || Remaining <= TimeSpan.Zero)
        {
            throw new ReadinessException(phase, ReadinessFailureCategory.Deadline, last);
        }

        _stop.ThrowIfCancellationRequested();
    }

    /// <summary>Releases timers and links after all cooperative work has settled.</summary>
    public void Dispose()
    {
        _linked.Dispose();
        _deadline.Dispose();
    }
}
