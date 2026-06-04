using System.Diagnostics;

namespace ForgeTrust.AppSurface.CoverageRunner;

/// <summary>
/// Creates timers used for coverage runner duration reporting.
/// </summary>
internal interface IClock
{
    /// <summary>
    /// Starts a new timer.
    /// </summary>
    /// <returns>A started timer.</returns>
    ITimer StartTimer();
}

/// <summary>
/// Reports elapsed time.
/// </summary>
internal interface ITimer
{
    /// <summary>
    /// Gets elapsed whole seconds.
    /// </summary>
    long ElapsedSeconds { get; }
}

/// <summary>
/// Stopwatch-based system clock.
/// </summary>
internal sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public ITimer StartTimer() => new StopwatchTimer(Stopwatch.StartNew());

    private sealed class StopwatchTimer : ITimer
    {
        private readonly Stopwatch _stopwatch;

        public StopwatchTimer(Stopwatch stopwatch)
        {
            _stopwatch = stopwatch;
        }

        public long ElapsedSeconds => (long)_stopwatch.Elapsed.TotalSeconds;
    }
}
