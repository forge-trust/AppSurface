namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Describes a bounded projection repair pass over durable worker completion facts.
/// </summary>
/// <remarks>
/// Repair requests are intentionally bounded. Hosts should use small batches and repeatable cursors rather than
/// unbounded background scans. The contract repairs projections only; it must not re-run executor side effects.
/// </remarks>
public sealed record DurableWorkerProjectionRepairRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableWorkerProjectionRepairRequest"/> class.
    /// </summary>
    /// <param name="now">Clock value used to evaluate stale projections.</param>
    /// <param name="maxStaleness">Maximum acceptable projection staleness before repair is attempted.</param>
    /// <param name="maxItems">
    /// Maximum number of pending projection repairs to inspect or return. Defaults to 100 when omitted, which is the
    /// default bounded batch size for ordinary repair sweeps; callers with tighter latency or larger backfill workloads
    /// can override it.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxStaleness"/> is not positive or <paramref name="maxItems"/> is not positive.
    /// </exception>
    public DurableWorkerProjectionRepairRequest(DateTimeOffset now, TimeSpan maxStaleness, int maxItems = 100)
    {
        if (maxStaleness <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStaleness), "Projection staleness must be positive.");
        }

        if (maxItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Projection repair batches must be positive.");
        }

        Now = now;
        MaxStaleness = maxStaleness;
        MaxItems = maxItems;
    }

    /// <summary>
    /// Gets the clock value used to evaluate stale projections.
    /// </summary>
    public DateTimeOffset Now { get; }

    /// <summary>
    /// Gets the maximum acceptable projection staleness before repair is attempted.
    /// </summary>
    public TimeSpan MaxStaleness { get; }

    /// <summary>
    /// Gets the maximum number of pending projection repairs to inspect or return.
    /// </summary>
    public int MaxItems { get; }
}
