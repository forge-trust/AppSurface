namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Describes a timeout requested by a flow wait outcome.
/// </summary>
/// <remarks>
/// The core in-memory runner returns timeout metadata to callers; durable hosts are responsible for turning it into a
/// durable timer and racing that timer against the external event.
/// </remarks>
public sealed record FlowTimeout
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowTimeout"/> class.
    /// </summary>
    /// <param name="duration">Timeout duration.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the duration is zero or negative.</exception>
    public FlowTimeout(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Timeout duration must be positive.");
        }

        Duration = duration;
    }

    /// <summary>
    /// Gets the timeout duration.
    /// </summary>
    public TimeSpan Duration { get; }
}
