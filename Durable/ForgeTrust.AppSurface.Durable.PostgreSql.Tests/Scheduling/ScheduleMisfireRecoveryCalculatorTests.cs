using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests.Scheduling;

public sealed class ScheduleMisfireRecoveryCalculatorTests
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public void FutureOrCompletedSchedule_DoesNoWork()
    {
        var calculator = new ScheduleMisfireRecoveryCalculator();
        var occurrences = new EveryScheduleCalculator(Anchor, TimeSpan.FromSeconds(1));

        var future = calculator.Calculate(
            occurrences,
            Anchor.AddSeconds(2),
            Anchor,
            ScheduleMisfirePolicy.RunOnce);
        var complete = calculator.Calculate(
            occurrences,
            null,
            Anchor,
            ScheduleMisfirePolicy.RunOnce);

        Assert.Empty(future.Occurrences);
        Assert.Equal(Anchor.AddSeconds(2), future.NextNominalDueUtc);
        Assert.Empty(complete.Occurrences);
        Assert.Null(complete.NextNominalDueUtc);
    }

    [Fact]
    public void RunOnce_CoalescesLongDowntimeInConstantCalculatorCalls()
    {
        var calculator = new ScheduleMisfireRecoveryCalculator();
        var everySecond = new EveryScheduleCalculator(Anchor, TimeSpan.FromSeconds(1));
        var now = Anchor.AddDays(180).AddMilliseconds(250);

        var result = calculator.Calculate(
            everySecond,
            Anchor,
            now,
            ScheduleMisfirePolicy.RunOnce,
            new ScheduleEvaluationBudget(1, TimeSpan.FromMilliseconds(1)));

        var occurrence = Assert.Single(result.Occurrences);
        Assert.Equal(Anchor, occurrence.NominalDueUtc);
        Assert.Equal(Anchor.AddDays(180), occurrence.CoveredThroughUtc);
        Assert.Null(occurrence.CoveredOccurrenceCount);
        Assert.True(occurrence.IsRecovery);
        Assert.Equal(Anchor.AddDays(180).AddSeconds(1), result.NextNominalDueUtc);
        Assert.Equal(2, result.CalculatorCalls);
        Assert.False(result.ContinuationRequired);
    }

    [Fact]
    public void RunOnce_MarksSingleOnTimeOccurrenceWithKnownCount()
    {
        var result = new ScheduleMisfireRecoveryCalculator().Calculate(
            new EveryScheduleCalculator(Anchor, TimeSpan.FromMinutes(1)),
            Anchor,
            Anchor,
            ScheduleMisfirePolicy.RunOnce);

        var occurrence = Assert.Single(result.Occurrences);
        Assert.Equal(1, occurrence.CoveredOccurrenceCount);
        Assert.False(occurrence.IsRecovery);
    }

    [Fact]
    public void Skip_AdvancesDirectlyToFuture()
    {
        var result = new ScheduleMisfireRecoveryCalculator().Calculate(
            new EveryScheduleCalculator(Anchor, TimeSpan.FromMinutes(1)),
            Anchor,
            Anchor.AddMinutes(10).AddSeconds(1),
            ScheduleMisfirePolicy.Skip);

        Assert.Empty(result.Occurrences);
        Assert.Equal(Anchor.AddMinutes(11), result.NextNominalDueUtc);
        Assert.True(result.MissedRangeTruncated);
        Assert.Equal(1, result.CalculatorCalls);
    }

    [Fact]
    public void CatchUp_MaterializesOldestFirstAndTruncatesAtPolicyLimit()
    {
        var result = new ScheduleMisfireRecoveryCalculator().Calculate(
            new EveryScheduleCalculator(Anchor, TimeSpan.FromMinutes(1)),
            Anchor,
            Anchor.AddMinutes(10).AddSeconds(1),
            ScheduleMisfirePolicy.CatchUp(3),
            new ScheduleEvaluationBudget(10, TimeSpan.FromSeconds(1)));

        Assert.Equal(
            [Anchor, Anchor.AddMinutes(1), Anchor.AddMinutes(2)],
            result.Occurrences.Select(item => item.NominalDueUtc));
        Assert.Equal(Anchor.AddMinutes(11), result.NextNominalDueUtc);
        Assert.False(result.ContinuationRequired);
        Assert.True(result.MissedRangeTruncated);
        Assert.Equal(4, result.CalculatorCalls);
    }

    [Fact]
    public void CatchUp_PersistsContinuationWhenPassCountBudgetIsLowerThanPolicy()
    {
        var result = new ScheduleMisfireRecoveryCalculator().Calculate(
            new EveryScheduleCalculator(Anchor, TimeSpan.FromMinutes(1)),
            Anchor,
            Anchor.AddMinutes(10),
            ScheduleMisfirePolicy.CatchUp(20),
            new ScheduleEvaluationBudget(2, TimeSpan.FromSeconds(1)));

        Assert.Equal([Anchor, Anchor.AddMinutes(1)], result.Occurrences.Select(item => item.NominalDueUtc));
        Assert.Equal(Anchor.AddMinutes(2), result.NextNominalDueUtc);
        Assert.True(result.ContinuationRequired);
        Assert.False(result.MissedRangeTruncated);
    }

    [Fact]
    public void CatchUp_PersistsContinuationWhenTimeBudgetIsExhausted()
    {
        var clock = new ExhaustedClock();
        var result = new ScheduleMisfireRecoveryCalculator(clock).Calculate(
            new EveryScheduleCalculator(Anchor, TimeSpan.FromMinutes(1)),
            Anchor,
            Anchor.AddMinutes(10),
            ScheduleMisfirePolicy.CatchUp(20),
            new ScheduleEvaluationBudget(20, TimeSpan.FromMilliseconds(1)));

        Assert.Empty(result.Occurrences);
        Assert.Equal(Anchor, result.NextNominalDueUtc);
        Assert.True(result.ContinuationRequired);
        Assert.Equal(0, result.CalculatorCalls);
    }

    [Fact]
    public void CatchUp_CompletesWhenOneTimeScheduleHasNoFollowingOccurrence()
    {
        var result = new ScheduleMisfireRecoveryCalculator().Calculate(
            new OneTimeScheduleCalculator(Anchor),
            Anchor,
            Anchor.AddDays(1),
            ScheduleMisfirePolicy.CatchUp(5));

        Assert.Single(result.Occurrences);
        Assert.Null(result.NextNominalDueUtc);
        Assert.False(result.ContinuationRequired);
    }

    [Fact]
    public void RunOnce_DefensivelyClampsInvalidPreviousOccurrence()
    {
        var result = new ScheduleMisfireRecoveryCalculator().Calculate(
            new PreviousBeforeCursorCalculator(),
            Anchor,
            Anchor.AddMinutes(1),
            ScheduleMisfirePolicy.RunOnce);

        Assert.Equal(Anchor, Assert.Single(result.Occurrences).CoveredThroughUtc);
    }

    [Fact]
    public void CalculatorRejectsNonAdvancingImplementations()
    {
        var calculator = new ScheduleMisfireRecoveryCalculator();
        var nonAdvancing = new NonAdvancingCalculator();

        Assert.Throws<InvalidOperationException>(() => calculator.Calculate(
            nonAdvancing,
            Anchor,
            Anchor.AddMinutes(1),
            ScheduleMisfirePolicy.Skip));
        Assert.Throws<InvalidOperationException>(() => calculator.Calculate(
            nonAdvancing,
            Anchor,
            Anchor.AddMinutes(1),
            ScheduleMisfirePolicy.CatchUp(2)));
    }

    [Fact]
    public void EvaluationBudget_ValidatesBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleEvaluationBudget(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleEvaluationBudget(10_001, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleEvaluationBudget(1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleEvaluationBudget(1, TimeSpan.FromSeconds(31)));
        Assert.Throws<ArgumentException>(() => new ScheduleOccurrenceWindow(Anchor, Anchor.AddTicks(-1), 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleOccurrenceWindow(Anchor, Anchor, 0, false));
        Assert.Throws<ArgumentNullException>(() => new ScheduleMisfireRecoveryResult(null!, null, false, false, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleMisfireRecoveryResult([], null, false, false, -1));
    }

    private sealed class ExhaustedClock : IScheduleEvaluationClock
    {
        public long GetTimestamp() => 1;

        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.FromSeconds(1);
    }

    private sealed class NonAdvancingCalculator : IScheduleOccurrenceCalculator
    {
        public DateTimeOffset? GetNextOccurrence(DateTimeOffset fromUtc, bool inclusive) => fromUtc;

        public DateTimeOffset? GetPreviousOccurrence(DateTimeOffset fromUtc, bool inclusive) => fromUtc;
    }

    private sealed class PreviousBeforeCursorCalculator : IScheduleOccurrenceCalculator
    {
        public DateTimeOffset? GetNextOccurrence(DateTimeOffset fromUtc, bool inclusive) => fromUtc.AddMinutes(1);

        public DateTimeOffset? GetPreviousOccurrence(DateTimeOffset fromUtc, bool inclusive) => Anchor.AddTicks(-1);
    }
}
