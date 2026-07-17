namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Identifies how a schedule behaves when an occurrence is due while a prior occurrence is still running.
/// </summary>
public enum ScheduleOverlapPolicyKind
{
    /// <summary>
    /// Retain at most one pending occurrence and coalesce any additional overlaps into it.
    /// </summary>
    QueueOne = 0,

    /// <summary>
    /// Discard an occurrence that overlaps an active run.
    /// </summary>
    Skip = 1,

    /// <summary>
    /// Start occurrences concurrently up to an explicitly configured maximum.
    /// </summary>
    AllowConcurrent = 2,
}

/// <summary>
/// Describes how a schedule handles overlapping occurrences.
/// </summary>
/// <remarks>
/// <see cref="QueueOne"/> is the safe default: it bounds backlog growth while ensuring that an overlap causes one
/// follow-up run after the active run reaches any terminal state. Use <see cref="Skip"/> only when losing an occurrence
/// is acceptable. <see cref="AllowConcurrent(int)"/> should be reserved for targets that are safe to execute in parallel.
/// </remarks>
public sealed record ScheduleOverlapPolicy
{
    /// <summary>
    /// Gets the default policy, which retains one coalesced pending occurrence.
    /// </summary>
    public static ScheduleOverlapPolicy QueueOne { get; } = new(ScheduleOverlapPolicyKind.QueueOne, 1);

    /// <summary>
    /// Gets the policy that discards occurrences which overlap an active run.
    /// </summary>
    public static ScheduleOverlapPolicy Skip { get; } = new(ScheduleOverlapPolicyKind.Skip, 1);

    private ScheduleOverlapPolicy(ScheduleOverlapPolicyKind kind, int maximumConcurrentRuns)
    {
        Kind = kind;
        MaximumConcurrentRuns = maximumConcurrentRuns;
    }

    /// <summary>
    /// Gets the policy behavior.
    /// </summary>
    public ScheduleOverlapPolicyKind Kind { get; }

    /// <summary>
    /// Gets the maximum number of active runs. This value is greater than one only for
    /// <see cref="ScheduleOverlapPolicyKind.AllowConcurrent"/>.
    /// </summary>
    public int MaximumConcurrentRuns { get; }

    /// <summary>
    /// Creates a bounded concurrent-execution policy.
    /// </summary>
    /// <param name="maximumConcurrentRuns">Maximum simultaneously active occurrences.</param>
    /// <returns>A bounded concurrent-execution policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maximumConcurrentRuns"/> is less than two.
    /// </exception>
    public static ScheduleOverlapPolicy AllowConcurrent(int maximumConcurrentRuns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentRuns, 2);
        return new ScheduleOverlapPolicy(ScheduleOverlapPolicyKind.AllowConcurrent, maximumConcurrentRuns);
    }
}

/// <summary>
/// Identifies how occurrences missed while the scheduler was unavailable are recovered.
/// </summary>
public enum ScheduleMisfirePolicyKind
{
    /// <summary>
    /// Coalesce the entire missed range into one recovery occurrence.
    /// </summary>
    RunOnce = 0,

    /// <summary>
    /// Advance past the missed range without creating a recovery occurrence.
    /// </summary>
    Skip = 1,

    /// <summary>
    /// Materialize missed occurrences oldest-first up to an explicit bound.
    /// </summary>
    CatchUp = 2,
}

/// <summary>
/// Describes how a schedule recovers occurrences missed during downtime.
/// </summary>
/// <remarks>
/// <see cref="RunOnce"/> is the safe default and never enumerates every tick in a long missed range. Bounded catch-up is
/// appropriate only when each nominal occurrence carries distinct business meaning. The runtime may apply a lower
/// per-pass evaluation budget than <see cref="MaximumOccurrences"/> and continue recovery in a later pass.
/// </remarks>
public sealed record ScheduleMisfirePolicy
{
    /// <summary>
    /// Gets the default policy, which coalesces a missed range into one recovery run.
    /// </summary>
    public static ScheduleMisfirePolicy RunOnce { get; } = new(ScheduleMisfirePolicyKind.RunOnce, 1);

    /// <summary>
    /// Gets the policy that advances past missed occurrences without running them.
    /// </summary>
    public static ScheduleMisfirePolicy Skip { get; } = new(ScheduleMisfirePolicyKind.Skip, 0);

    private ScheduleMisfirePolicy(ScheduleMisfirePolicyKind kind, int maximumOccurrences)
    {
        Kind = kind;
        MaximumOccurrences = maximumOccurrences;
    }

    /// <summary>
    /// Gets the misfire behavior.
    /// </summary>
    public ScheduleMisfirePolicyKind Kind { get; }

    /// <summary>
    /// Gets the maximum number of missed occurrences that may be materialized by one complete catch-up operation.
    /// </summary>
    public int MaximumOccurrences { get; }

    /// <summary>
    /// Creates an oldest-first bounded catch-up policy.
    /// </summary>
    /// <param name="maximumOccurrences">Maximum missed occurrences to materialize before advancing to the future.</param>
    /// <returns>A bounded catch-up policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maximumOccurrences"/> is not positive.
    /// </exception>
    public static ScheduleMisfirePolicy CatchUp(int maximumOccurrences)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOccurrences);
        return new ScheduleMisfirePolicy(ScheduleMisfirePolicyKind.CatchUp, maximumOccurrences);
    }
}
