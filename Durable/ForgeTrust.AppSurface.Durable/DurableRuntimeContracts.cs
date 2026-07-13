namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Selects which durable surfaces a bounded pump pass may process.
/// </summary>
[Flags]
public enum DurableRuntimeSurface
{
    /// <summary>No durable surface.</summary>
    None = 0,
    /// <summary>Direct and Flow activity work.</summary>
    Work = 1,
    /// <summary>Flow commands, timers, and external-event continuations.</summary>
    Flow = 2,
    /// <summary>Schedule cursors and occurrences.</summary>
    Schedule = 4,
    /// <summary>All durable surfaces.</summary>
    All = Work | Flow | Schedule,
}

/// <summary>
/// Bounds one runtime pump pass for hosted and externally activated execution.
/// </summary>
public sealed record DurableRuntimePumpRequest
{
    /// <summary>
    /// Initializes a bounded pump request.
    /// </summary>
    public DurableRuntimePumpRequest(
        int maximumItems = 32,
        TimeSpan? timeBudget = null,
        DurableRuntimeSurface surfaces = DurableRuntimeSurface.All)
    {
        if (maximumItems is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }

        var resolvedBudget = timeBudget ?? TimeSpan.FromSeconds(10);
        if (resolvedBudget <= TimeSpan.Zero || resolvedBudget > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeBudget));
        }

        if (surfaces == DurableRuntimeSurface.None || (surfaces & ~DurableRuntimeSurface.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(surfaces));
        }

        MaximumItems = maximumItems;
        TimeBudget = resolvedBudget;
        Surfaces = surfaces;
    }

    /// <summary>Gets the total item bound.</summary>
    public int MaximumItems { get; }

    /// <summary>
    /// Gets the wall-clock budget for discovering and beginning additional items.
    /// </summary>
    /// <remarks>
    /// An already-started provider call or database transaction is allowed to finish past this budget. The runtime does
    /// not manufacture an ambiguous external outcome merely to enforce a hard stopwatch deadline.
    /// </remarks>
    public TimeSpan TimeBudget { get; }

    /// <summary>Gets the selected durable surfaces.</summary>
    public DurableRuntimeSurface Surfaces { get; }
}

/// <summary>
/// Summarizes one bounded runtime pump pass without high-cardinality identifiers.
/// </summary>
public sealed record DurableRuntimePumpResult
{
    /// <summary>
    /// Initializes a pump result.
    /// </summary>
    public DurableRuntimePumpResult(
        int discovered,
        int claimed,
        int processed,
        int deferred,
        int failed,
        bool hasMore,
        DateTimeOffset? nextDueAtUtc,
        TimeSpan elapsed)
    {
        RequireNonNegative(discovered, nameof(discovered));
        RequireNonNegative(claimed, nameof(claimed));
        RequireNonNegative(processed, nameof(processed));
        RequireNonNegative(deferred, nameof(deferred));
        RequireNonNegative(failed, nameof(failed));

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        Discovered = discovered;
        Claimed = claimed;
        Processed = processed;
        Deferred = deferred;
        Failed = failed;
        HasMore = hasMore;
        NextDueAtUtc = nextDueAtUtc?.ToUniversalTime();
        Elapsed = elapsed;
    }

    /// <summary>Gets discovered candidate count.</summary>
    public int Discovered { get; }

    /// <summary>Gets successfully claimed count.</summary>
    public int Claimed { get; }

    /// <summary>Gets successfully processed count.</summary>
    public int Processed { get; }

    /// <summary>Gets policy-deferred count.</summary>
    public int Deferred { get; }

    /// <summary>Gets safely failed or suspended count.</summary>
    public int Failed { get; }

    /// <summary>Gets whether immediately eligible work may remain.</summary>
    public bool HasMore { get; }

    /// <summary>Gets the earliest known future due time.</summary>
    public DateTimeOffset? NextDueAtUtc { get; }

    /// <summary>Gets elapsed duration of the bounded pass.</summary>
    public TimeSpan Elapsed { get; }

    private static void RequireNonNegative(int value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Pump counts must not be negative.");
        }
    }
}

/// <summary>
/// Executes one bounded pass of the authoritative durable runtime.
/// </summary>
/// <remarks>
/// Hosted loops and external activators must call this same primitive. A notification, queue, or HTTP wake-up may
/// accelerate a pass but cannot become a correctness dependency.
/// </remarks>
public interface IDurableRuntimePump
{
    /// <summary>
    /// Executes one bounded processing pass.
    /// </summary>
    ValueTask<DurableRuntimePumpResult> RunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default);
}
