namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed record ScheduleOccurrenceWindow
{
    public ScheduleOccurrenceWindow(
        DateTimeOffset nominalDueUtc,
        DateTimeOffset coveredThroughUtc,
        long? coveredOccurrenceCount,
        bool isRecovery)
    {
        NominalDueUtc = nominalDueUtc.ToUniversalTime();
        CoveredThroughUtc = coveredThroughUtc.ToUniversalTime();
        if (CoveredThroughUtc < NominalDueUtc)
        {
            throw new ArgumentException("Covered-through instant must not precede the nominal due instant.", nameof(coveredThroughUtc));
        }

        if (coveredOccurrenceCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coveredOccurrenceCount));
        }

        CoveredOccurrenceCount = coveredOccurrenceCount;
        IsRecovery = isRecovery;
    }

    public DateTimeOffset NominalDueUtc { get; }

    public DateTimeOffset CoveredThroughUtc { get; }

    public long? CoveredOccurrenceCount { get; }

    public bool IsRecovery { get; }
}

internal sealed record ScheduleEvaluationBudget
{
    public static ScheduleEvaluationBudget Default { get; } = new(100, TimeSpan.FromMilliseconds(50));

    public ScheduleEvaluationBudget(int maximumOccurrences, TimeSpan maximumElapsedTime)
    {
        if (maximumOccurrences is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOccurrences));
        }

        if (maximumElapsedTime <= TimeSpan.Zero || maximumElapsedTime > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumElapsedTime));
        }

        MaximumOccurrences = maximumOccurrences;
        MaximumElapsedTime = maximumElapsedTime;
    }

    public int MaximumOccurrences { get; }

    public TimeSpan MaximumElapsedTime { get; }
}

internal sealed record ScheduleMisfireRecoveryResult
{
    public ScheduleMisfireRecoveryResult(
        IReadOnlyList<ScheduleOccurrenceWindow> occurrences,
        DateTimeOffset? nextNominalDueUtc,
        bool continuationRequired,
        bool missedRangeTruncated,
        int calculatorCalls)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        if (calculatorCalls < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(calculatorCalls));
        }

        Occurrences = occurrences.ToArray();
        NextNominalDueUtc = nextNominalDueUtc?.ToUniversalTime();
        ContinuationRequired = continuationRequired;
        MissedRangeTruncated = missedRangeTruncated;
        CalculatorCalls = calculatorCalls;
    }

    public IReadOnlyList<ScheduleOccurrenceWindow> Occurrences { get; }

    public DateTimeOffset? NextNominalDueUtc { get; }

    public bool ContinuationRequired { get; }

    public bool MissedRangeTruncated { get; }

    public int CalculatorCalls { get; }
}

internal interface IScheduleEvaluationClock
{
    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp);
}

internal sealed class StopwatchScheduleEvaluationClock : IScheduleEvaluationClock
{
    public static StopwatchScheduleEvaluationClock Instance { get; } = new();

    private StopwatchScheduleEvaluationClock()
    {
    }

    public long GetTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp);
}

internal sealed class ScheduleMisfireRecoveryCalculator
{
    private readonly IScheduleEvaluationClock clock;

    public ScheduleMisfireRecoveryCalculator(IScheduleEvaluationClock? clock = null)
    {
        this.clock = clock ?? StopwatchScheduleEvaluationClock.Instance;
    }

    public ScheduleMisfireRecoveryResult Calculate(
        IScheduleOccurrenceCalculator calculator,
        DateTimeOffset? nextNominalDueUtc,
        DateTimeOffset nowUtc,
        ScheduleMisfirePolicy policy,
        ScheduleEvaluationBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        ArgumentNullException.ThrowIfNull(policy);
        var resolvedBudget = budget ?? ScheduleEvaluationBudget.Default;
        var nextDue = nextNominalDueUtc?.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();

        if (nextDue is null || nextDue > now)
        {
            return new ScheduleMisfireRecoveryResult([], nextDue, false, false, 0);
        }

        return policy.Kind switch
        {
            ScheduleMisfirePolicyKind.RunOnce => CalculateRunOnce(calculator, nextDue.Value, now),
            ScheduleMisfirePolicyKind.Skip => CalculateSkip(calculator, now),
            ScheduleMisfirePolicyKind.CatchUp => CalculateCatchUp(calculator, nextDue.Value, now, policy, resolvedBudget),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), "Unknown schedule misfire policy."),
        };
    }

    private static ScheduleMisfireRecoveryResult CalculateRunOnce(
        IScheduleOccurrenceCalculator calculator,
        DateTimeOffset nextDue,
        DateTimeOffset now)
    {
        var coveredThrough = calculator.GetPreviousOccurrence(now, inclusive: true) ?? nextDue;
        if (coveredThrough < nextDue)
        {
            coveredThrough = nextDue;
        }

        var future = RequireAdvancingFuture(calculator.GetNextOccurrence(now, inclusive: false), now);
        long? knownCount = coveredThrough == nextDue ? 1 : null;
        var occurrence = new ScheduleOccurrenceWindow(nextDue, coveredThrough, knownCount, isRecovery: nextDue < now);
        return new ScheduleMisfireRecoveryResult([occurrence], future, false, false, 2);
    }

    private static ScheduleMisfireRecoveryResult CalculateSkip(
        IScheduleOccurrenceCalculator calculator,
        DateTimeOffset now)
    {
        var future = RequireAdvancingFuture(calculator.GetNextOccurrence(now, inclusive: false), now);
        return new ScheduleMisfireRecoveryResult([], future, false, true, 1);
    }

    private ScheduleMisfireRecoveryResult CalculateCatchUp(
        IScheduleOccurrenceCalculator calculator,
        DateTimeOffset nextDue,
        DateTimeOffset now,
        ScheduleMisfirePolicy policy,
        ScheduleEvaluationBudget budget)
    {
        var occurrences = new List<ScheduleOccurrenceWindow>(
            Math.Min(policy.MaximumOccurrences, budget.MaximumOccurrences));
        var maximumThisPass = Math.Min(policy.MaximumOccurrences, budget.MaximumOccurrences);
        var startedAt = clock.GetTimestamp();
        DateTimeOffset? cursor = nextDue;
        var calculatorCalls = 0;

        while (cursor is { } due
               && due <= now
               && occurrences.Count < maximumThisPass
               && clock.GetElapsedTime(startedAt) < budget.MaximumElapsedTime)
        {
            occurrences.Add(new ScheduleOccurrenceWindow(due, due, 1, isRecovery: due < now));
            var following = calculator.GetNextOccurrence(due, inclusive: false);
            calculatorCalls++;
            cursor = RequireStrictAdvance(following, due);
        }

        var dueRangeRemains = cursor is { } remaining && remaining <= now;
        if (!dueRangeRemains)
        {
            return new ScheduleMisfireRecoveryResult(occurrences, cursor, false, false, calculatorCalls);
        }

        if (occurrences.Count >= policy.MaximumOccurrences)
        {
            var future = RequireAdvancingFuture(calculator.GetNextOccurrence(now, inclusive: false), now);
            return new ScheduleMisfireRecoveryResult(
                occurrences,
                future,
                continuationRequired: false,
                missedRangeTruncated: true,
                calculatorCalls: calculatorCalls + 1);
        }

        return new ScheduleMisfireRecoveryResult(
            occurrences,
            cursor,
            continuationRequired: true,
            missedRangeTruncated: false,
            calculatorCalls);
    }

    private static DateTimeOffset? RequireAdvancingFuture(DateTimeOffset? candidate, DateTimeOffset now)
    {
        var normalized = candidate?.ToUniversalTime();
        if (normalized is not null && normalized <= now)
        {
            throw new InvalidOperationException("Schedule calculator returned a non-advancing future occurrence.");
        }

        return normalized;
    }

    private static DateTimeOffset? RequireStrictAdvance(DateTimeOffset? candidate, DateTimeOffset current)
    {
        if (candidate is null)
        {
            return null;
        }

        var normalized = candidate.Value.ToUniversalTime();
        if (normalized <= current)
        {
            throw new InvalidOperationException("Schedule calculator returned a non-advancing occurrence.");
        }

        return normalized;
    }
}
