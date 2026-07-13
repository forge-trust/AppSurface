using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Tests.Scheduling;

public sealed class DurableScheduleContractTests
{
    [Fact]
    public void Factories_UseQueueOneAndRunOnceDefaults()
    {
        var schedules = new DurableSchedule[]
        {
            DurableSchedule.At(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5))),
            DurableSchedule.After(TimeSpan.FromMinutes(3)),
            DurableSchedule.Every(TimeSpan.FromHours(1)),
            DurableSchedule.Cron("0 9 * * MON-FRI", "America/New_York"),
        };

        Assert.Collection(
            schedules,
            schedule => Assert.Equal(DurableScheduleKind.At, schedule.Kind),
            schedule => Assert.Equal(DurableScheduleKind.After, schedule.Kind),
            schedule => Assert.Equal(DurableScheduleKind.Every, schedule.Kind),
            schedule => Assert.Equal(DurableScheduleKind.Cron, schedule.Kind));
        Assert.All(schedules, schedule => Assert.Same(ScheduleOverlapPolicy.QueueOne, schedule.OverlapPolicy));
        Assert.All(schedules, schedule => Assert.Same(ScheduleMisfirePolicy.RunOnce, schedule.MisfirePolicy));
    }

    [Fact]
    public void WithPolicies_PreservesConcreteShapeAndOriginal()
    {
        var original = DurableSchedule.Every(TimeSpan.FromMinutes(5));

        var changed = original
            .WithOverlap(ScheduleOverlapPolicy.AllowConcurrent(3))
            .WithMisfire(ScheduleMisfirePolicy.CatchUp(4));

        var every = Assert.IsType<DurableEverySchedule>(changed);
        Assert.Equal(TimeSpan.FromMinutes(5), every.Interval);
        Assert.Equal(ScheduleOverlapPolicyKind.AllowConcurrent, changed.OverlapPolicy.Kind);
        Assert.Equal(3, changed.OverlapPolicy.MaximumConcurrentRuns);
        Assert.Equal(ScheduleMisfirePolicyKind.CatchUp, changed.MisfirePolicy.Kind);
        Assert.Equal(4, changed.MisfirePolicy.MaximumOccurrences);
        Assert.Same(ScheduleOverlapPolicy.QueueOne, original.OverlapPolicy);
        Assert.Same(ScheduleMisfirePolicy.RunOnce, original.MisfirePolicy);
    }

    [Fact]
    public void WithPolicies_RejectsNull()
    {
        var schedule = DurableSchedule.After(TimeSpan.FromSeconds(1));

        Assert.Throws<ArgumentNullException>(() => schedule.WithOverlap(null!));
        Assert.Throws<ArgumentNullException>(() => schedule.WithMisfire(null!));
    }

    [Fact]
    public void At_NormalizesToUtc()
    {
        var schedule = DurableSchedule.At(new DateTimeOffset(2026, 7, 12, 8, 30, 0, TimeSpan.FromHours(-4)));

        Assert.Equal(new DateTimeOffset(2026, 7, 12, 12, 30, 0, TimeSpan.Zero), schedule.AtUtc);
    }

    [Fact]
    public void After_PreservesDelay()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), DurableSchedule.After(TimeSpan.FromMinutes(3)).Delay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void After_RequiresPositiveDelay(int ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableSchedule.After(TimeSpan.FromTicks(ticks)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Every_RequiresPositiveInterval(int ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableSchedule.Every(TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void Every_NormalizesExplicitAnchor()
    {
        var schedule = DurableSchedule.Every(
            TimeSpan.FromDays(1),
            new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(9)));

        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), schedule.AnchorUtc);
    }

    [Fact]
    public void Cron_PreservesVersionedInputs()
    {
        var schedule = DurableSchedule.Cron("H H * * * *", "Etc/UTC", CronGrammar.IncludeSeconds);

        Assert.Equal("H H * * * *", schedule.Expression);
        Assert.Equal("Etc/UTC", schedule.IanaTimeZoneId);
        Assert.Equal(CronGrammar.IncludeSeconds, schedule.Grammar);
        Assert.Equal(CronDialect.CronosV1, schedule.Dialect);
    }

    [Theory]
    [InlineData(null, "UTC")]
    [InlineData("", "UTC")]
    [InlineData("* * * * *", null)]
    [InlineData("* * * * *", " ")]
    public void Cron_RequiresText(string? expression, string? zone)
    {
        Assert.Throws<ArgumentException>(() => new DurableCronSchedule(expression!, zone!));
    }

    [Fact]
    public void Cron_RejectsUnknownEnums()
    {
        Assert.Throws<ArgumentException>(() => new DurableCronSchedule("* * * * *", "UTC", (CronGrammar)99));
        Assert.Throws<ArgumentException>(() => new DurableCronSchedule("* * * * *", "UTC", dialect: (CronDialect)99));
    }

    [Fact]
    public void PolicyFactories_ValidateAndExposeBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleOverlapPolicy.AllowConcurrent(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleMisfirePolicy.CatchUp(0));
        Assert.Equal(ScheduleOverlapPolicyKind.Skip, ScheduleOverlapPolicy.Skip.Kind);
        Assert.Equal(ScheduleMisfirePolicyKind.Skip, ScheduleMisfirePolicy.Skip.Kind);
        Assert.Equal(0, ScheduleMisfirePolicy.Skip.MaximumOccurrences);
    }
}
