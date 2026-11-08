namespace ForgeTrust.AppSurface.Durable.CloudTasks;

/// <summary>Invocation-local monotonic time remaining, shared only with the built-in metadata source.</summary>
internal sealed class SubmissionBudget
{
    private readonly long _started;
    private readonly TimeSpan _timeout;

    /// <summary>Starts a budget using the supplied clock.</summary>
    internal SubmissionBudget(TimeSpan timeout, TimeProvider timeProvider)
    {
        _timeout = timeout;
        TimeProvider = timeProvider;
        _started = timeProvider.GetTimestamp();
    }

    /// <summary>Clock whose monotonic timestamps govern this invocation.</summary>
    internal TimeProvider TimeProvider { get; }

    /// <summary>Unconsumed duration, clamped to zero.</summary>
    internal TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(0, (_timeout - TimeProvider.GetElapsedTime(_started)).Ticks));

    /// <summary>Whether the invocation's original duration has elapsed.</summary>
    internal bool IsExpired => Remaining == TimeSpan.Zero;
}
