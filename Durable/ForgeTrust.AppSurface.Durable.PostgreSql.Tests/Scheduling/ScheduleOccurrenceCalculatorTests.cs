using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests.Scheduling;

public sealed class ScheduleOccurrenceCalculatorTests
{
    private static readonly DurableScheduleId ScheduleId = new("schedule-1");

    [Fact]
    public void OneTimeCalculator_RespectsInclusiveBoundaries()
    {
        var due = DateTimeOffset.Parse("2026-07-12T12:00:00Z");
        var calculator = new OneTimeScheduleCalculator(due);

        Assert.Equal(due, calculator.GetNextOccurrence(due.AddSeconds(-1), inclusive: false));
        Assert.Equal(due, calculator.GetNextOccurrence(due, inclusive: true));
        Assert.Null(calculator.GetNextOccurrence(due, inclusive: false));
        Assert.Equal(due, calculator.GetPreviousOccurrence(due.AddSeconds(1), inclusive: false));
        Assert.Equal(due, calculator.GetPreviousOccurrence(due, inclusive: true));
        Assert.Null(calculator.GetPreviousOccurrence(due, inclusive: false));
    }

    [Fact]
    public void EveryCalculator_UsesElapsedUtcAndInclusiveBoundaries()
    {
        var anchor = DateTimeOffset.Parse("2026-07-12T12:00:00Z");
        var calculator = new EveryScheduleCalculator(anchor, TimeSpan.FromMinutes(5));

        Assert.Equal(anchor, calculator.GetNextOccurrence(anchor.AddMinutes(-1), inclusive: false));
        Assert.Equal(anchor, calculator.GetNextOccurrence(anchor, inclusive: true));
        Assert.Equal(anchor.AddMinutes(5), calculator.GetNextOccurrence(anchor, inclusive: false));
        Assert.Equal(anchor.AddMinutes(5), calculator.GetNextOccurrence(anchor.AddMinutes(3), inclusive: false));
        Assert.Null(calculator.GetPreviousOccurrence(anchor.AddTicks(-1), inclusive: true));
        Assert.Null(calculator.GetPreviousOccurrence(anchor, inclusive: false));
        Assert.Equal(anchor, calculator.GetPreviousOccurrence(anchor, inclusive: true));
        Assert.Equal(anchor, calculator.GetPreviousOccurrence(anchor.AddMinutes(3), inclusive: false));
        Assert.Equal(anchor.AddMinutes(5), calculator.GetPreviousOccurrence(anchor.AddMinutes(5), inclusive: true));
        Assert.Equal(anchor, calculator.GetPreviousOccurrence(anchor.AddMinutes(5), inclusive: false));
    }

    [Fact]
    public void EveryCalculator_RejectsInvalidIntervalAndReturnsNullPastTimestampRange()
    {
        Assert.Equal(
            ScheduleDefinitionError.InvalidInterval,
            Assert.Throws<ScheduleDefinitionException>(
                () => new EveryScheduleCalculator(DateTimeOffset.UtcNow, TimeSpan.Zero)).Error);

        var calculator = new EveryScheduleCalculator(DateTimeOffset.MaxValue.AddTicks(-1), TimeSpan.FromTicks(1));

        Assert.Null(calculator.GetNextOccurrence(DateTimeOffset.MaxValue, inclusive: false));

        var overflowing = new EveryScheduleCalculator(
            DateTimeOffset.MinValue.AddTicks(1),
            TimeSpan.MaxValue);
        Assert.Null(overflowing.GetNextOccurrence(DateTimeOffset.MaxValue, inclusive: false));
    }

    [Fact]
    public void Factory_AnchorsAfterAndEveryToDurableAcceptance()
    {
        var acceptedAt = DateTimeOffset.Parse("2026-07-12T12:00:00Z");
        var after = ScheduleOccurrenceCalculatorFactory.Create(
            ScheduleId,
            DurableSchedule.After(TimeSpan.FromMinutes(10)),
            acceptedAt);
        var every = ScheduleOccurrenceCalculatorFactory.Create(
            ScheduleId,
            DurableSchedule.Every(TimeSpan.FromMinutes(10)),
            acceptedAt);
        var at = ScheduleOccurrenceCalculatorFactory.Create(
            ScheduleId,
            DurableSchedule.At(acceptedAt.AddHours(1)),
            acceptedAt);

        Assert.Equal(acceptedAt.AddMinutes(10), after.GetNextOccurrence(acceptedAt, inclusive: true));
        Assert.Equal(acceptedAt, every.GetNextOccurrence(acceptedAt, inclusive: true));
        Assert.Equal(acceptedAt.AddHours(1), at.GetNextOccurrence(acceptedAt, inclusive: true));
        Assert.Throws<ArgumentNullException>(() => ScheduleOccurrenceCalculatorFactory.Create(
            ScheduleId, null!, acceptedAt));
    }

    [Fact]
    public void Factory_ReportsAfterTimestampOverflow()
    {
        var exception = Assert.Throws<ScheduleDefinitionException>(() =>
            ScheduleOccurrenceCalculatorFactory.Create(
                ScheduleId,
                DurableSchedule.After(TimeSpan.FromDays(1)),
                DateTimeOffset.MaxValue));

        Assert.Equal(ScheduleDefinitionError.InstantOutOfRange, exception.Error);
        Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
    }

    [Fact]
    public void CronosV1_ParsesFiveAndSixFieldGrammar()
    {
        var minute = new CronosV1ScheduleCalculator(
            ScheduleId,
            DurableSchedule.Cron("*/5 * * * *", "Etc/UTC"));
        var second = new CronosV1ScheduleCalculator(
            ScheduleId,
            DurableSchedule.Cron("*/5 * * * * *", "Etc/UTC", CronGrammar.IncludeSeconds));
        var from = DateTimeOffset.Parse("2026-07-12T12:00:01Z");

        Assert.Equal(DateTimeOffset.Parse("2026-07-12T12:05:00Z"), minute.GetNextOccurrence(from, false));
        Assert.Equal(DateTimeOffset.Parse("2026-07-12T12:00:05Z"), second.GetNextOccurrence(from, false));
        Assert.Equal(DateTimeOffset.Parse("2026-07-12T12:00:00Z"), second.GetPreviousOccurrence(from, false));
    }

    [Fact]
    public void CronosV1_UsesStableCryptographicSeedForH()
    {
        var definition = DurableSchedule.Cron("H * * * *", "Etc/UTC");
        var first = new CronosV1ScheduleCalculator(new DurableScheduleId("schedule-a"), definition);
        var again = new CronosV1ScheduleCalculator(new DurableScheduleId("schedule-a"), definition);
        var other = new CronosV1ScheduleCalculator(new DurableScheduleId("schedule-b"), definition);
        var from = DateTimeOffset.Parse("2026-07-12T12:00:00Z");

        Assert.Equal(first.JitterSeed, again.JitterSeed);
        Assert.NotEqual(first.JitterSeed, other.JitterSeed);
        Assert.Equal(
            first.GetNextOccurrence(from, false),
            again.GetNextOccurrence(from, false));
    }

    [Fact]
    public void CronosV1_RejectsInvalidExpressionWithStableError()
    {
        var exception = Assert.Throws<ScheduleDefinitionException>(() => new CronosV1ScheduleCalculator(
            ScheduleId,
            DurableSchedule.Cron("not cron", "Etc/UTC")));

        Assert.Equal(ScheduleDefinitionError.InvalidCronExpression, exception.Error);
        Assert.NotNull(exception.InnerException);
        Assert.Throws<ArgumentNullException>(() => new CronosV1ScheduleCalculator(ScheduleId, null!));
    }

    [Fact]
    public void CronosV1_RejectsMissingOrNonIanaTimeZone()
    {
        Assert.Equal(
            ScheduleDefinitionError.InvalidTimeZone,
            Assert.Throws<ScheduleDefinitionException>(() => IanaTimeZoneResolver.Resolve(" ")).Error);
        Assert.Equal(
            ScheduleDefinitionError.InvalidTimeZone,
            Assert.Throws<ScheduleDefinitionException>(() => IanaTimeZoneResolver.Resolve("missing/not-a-zone")).Error);

        Assert.Equal(
            ScheduleDefinitionError.InvalidTimeZone,
            Assert.Throws<ScheduleDefinitionException>(() => IanaTimeZoneResolver.Resolve("Eastern Standard Time")).Error);
    }

    [Fact]
    public void CronosV1_AppliesCronosSpringForwardBehavior()
    {
        var calculator = new CronosV1ScheduleCalculator(
            ScheduleId,
            DurableSchedule.Cron("30 2 * * *", "America/New_York"));

        var occurrence = calculator.GetNextOccurrence(
            DateTimeOffset.Parse("2026-03-08T05:00:00Z"),
            inclusive: false);

        Assert.Equal(DateTimeOffset.Parse("2026-03-08T07:00:00Z"), occurrence);
    }

    [Fact]
    public void TimeZoneRulesFingerprint_IsStableAndSensitiveToZone()
    {
        var utc = IanaTimeZoneResolver.Resolve("Etc/UTC");
        var eastern = IanaTimeZoneResolver.Resolve("America/New_York");

        var first = TimeZoneRulesFingerprint.Create(utc);
        var again = TimeZoneRulesFingerprint.Create(utc);
        var other = TimeZoneRulesFingerprint.Create(eastern);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Explanation_IncludesPoliciesCronMetadataAndOccurrences()
    {
        var schedule = DurableSchedule.Cron("0 9 * * MON-FRI", "America/New_York")
            .WithOverlap(ScheduleOverlapPolicy.Skip)
            .WithMisfire(ScheduleMisfirePolicy.CatchUp(2));
        var request = new DurableScheduleExplainRequest(
            new DurableScopeId("scope-1"),
            ScheduleId,
            schedule,
            DateTimeOffset.Parse("2026-07-10T20:00:00Z"),
            occurrenceCount: 2);

        var explanation = ScheduleExplanationCalculator.Explain(request);

        Assert.Equal(2, explanation.NextOccurrencesUtc.Count);
        Assert.Equal(CronDialect.CronosV1, explanation.CronDialect);
        Assert.Equal(CronGrammar.Standard, explanation.CronGrammar);
        Assert.Equal("America/New_York", explanation.IanaTimeZoneId);
        Assert.Equal("0.13.0", explanation.EvaluatorVersion);
        Assert.NotNull(explanation.JitterSeed);
        Assert.Equal(64, explanation.TimeZoneRulesFingerprint!.Length);
        Assert.Equal(ScheduleOverlapPolicy.Skip, explanation.OverlapPolicy);
        Assert.Equal(ScheduleMisfirePolicyKind.CatchUp, explanation.MisfirePolicy.Kind);
        Assert.Single(explanation.Notes);
    }

    [Fact]
    public void Explanation_NotesAcceptanceAnchoredDefinitionsAndStopsAfterOneTimeOccurrence()
    {
        var request = new DurableScheduleExplainRequest(
            new DurableScopeId("scope-1"),
            ScheduleId,
            DurableSchedule.After(TimeSpan.FromMinutes(5)),
            DateTimeOffset.Parse("2026-07-12T12:00:00Z"),
            occurrenceCount: 5);

        var explanation = ScheduleExplanationCalculator.Explain(request);

        Assert.Equal([DateTimeOffset.Parse("2026-07-12T12:05:00Z")], explanation.NextOccurrencesUtc);
        Assert.Single(explanation.Notes);
        Assert.Null(explanation.CronDialect);
    }
}
