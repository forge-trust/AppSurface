using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceRunBudgetTests
{
    [Fact]
    public void BrokerAllowance_ShouldIgnoreWallClockChangesAndConsumeElapsedTime()
    {
        var clock = new AdjustableTimeProvider();
        clock.MoveUtcBy(TimeSpan.FromDays(-1));
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(20),
            [new(EvidenceRunStage.Producer, TimeSpan.FromSeconds(5))],
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), out var budget));
        Assert.True(budget!.TryBeginNextStage(default, out _));
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.MoveUtcBy(TimeSpan.FromDays(-1));
        Assert.Equal(TimeSpan.FromSeconds(18), budget.JobRemaining);
        Assert.Equal(TimeSpan.FromSeconds(3), budget.CurrentStageRemaining);
    }

    [Fact]
    public void Defaults_ShouldMatchApprovedProtectedCaps()
    {
        Assert.Equal(256L * 1024 * 1024, EvidenceRunBudgetLimits.MaximumArtifactBytes);
        Assert.Equal(16L * 1024 * 1024, EvidenceRunBudgetLimits.MaximumProcessOutputBytes);
        Assert.Equal(1024 * 1024, EvidenceRunBudgetLimits.RetainedOutputPrefixBytesPerStream);
        Assert.Equal(TimeSpan.FromSeconds(30), EvidenceRunBudgetLimits.Admission);
        Assert.Equal(TimeSpan.FromSeconds(120), EvidenceRunBudgetLimits.Start);
        Assert.Equal(TimeSpan.FromSeconds(60), EvidenceRunBudgetLimits.Collection);
        Assert.Equal(TimeSpan.FromSeconds(30), EvidenceRunBudgetLimits.Stopping);
        Assert.Equal(TimeSpan.FromMinutes(10), EvidenceRunBudgetLimits.Cleanup);
    }

    [Fact]
    public void QuotaFactories_ShouldAllowProtectedReductionsButRejectHigherCaps()
    {
        Assert.Equal(12, EvidenceRunByteQuota.CreateArtifact(12).Limit);
        Assert.Equal(34, EvidenceRunByteQuota.CreateProcessOutput(34).Limit);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EvidenceRunByteQuota.CreateArtifact(EvidenceRunBudgetLimits.MaximumArtifactBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EvidenceRunByteQuota.CreateProcessOutput(EvidenceRunBudgetLimits.MaximumProcessOutputBytes + 1));
    }

    [Fact]
    public void ArtifactQuota_ShouldAcceptExactLimitAndIrreversiblyLatchOverflow()
    {
        var notifications = 0;
        var quota = EvidenceRunByteQuota.CreateArtifact(10, () => Interlocked.Increment(ref notifications));

        Assert.True(quota.TryReserve(10, out var exact));
        Assert.Equal(10, quota.AccountedBytes);
        exact!.Commit();
        Assert.Equal(10, quota.AccountedBytes);
        Assert.False(quota.TryChargeReceived(1));
        Assert.False(quota.TryChargeReceived(1));
        Assert.True(quota.IsFailed);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void ArtifactQuota_ShouldReleaseOnlyUncommittedSettledWrite()
    {
        var quota = EvidenceRunByteQuota.CreateArtifact(10);
        Assert.True(quota.TryReserve(0, out var emptyArtifact));
        emptyArtifact!.Commit();
        Assert.Equal(0, quota.AccountedBytes);
        Assert.True(quota.TryReserve(7, out var failedWrite));
        Assert.True(quota.TryReserve(3, out var successfulWrite));
        failedWrite!.Dispose();
        Assert.Equal(3, quota.AccountedBytes);
        successfulWrite!.Commit();
        Assert.Equal(3, quota.AccountedBytes);
        Assert.True(quota.TryChargeReceived(7));
        Assert.Equal(10, quota.AccountedBytes);
    }

    [Fact]
    public async Task OutputQuota_ShouldAccountConcurrentReceivedBytesAndSignalOnce()
    {
        var notifications = 0;
        var quota = EvidenceRunByteQuota.CreateProcessOutput(10_000, () => Interlocked.Increment(ref notifications));
        var charges = Enumerable.Range(0, 100).Select(_ => Task.Run(() => quota.TryChargeReceived(100)));

        var results = await Task.WhenAll(charges);

        Assert.Equal(100, results.Count(result => result));
        Assert.Equal(10_000, quota.AccountedBytes);
        Assert.False(quota.IsFailed);
        Assert.Equal(0, notifications);
        Assert.False(quota.TryChargeReceived(1));
        Assert.True(quota.IsFailed);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task ArtifactQuota_ConcurrentReservations_ShouldNeverOverbookAndLatchCrossing()
    {
        var notifications = 0;
        var quota = EvidenceRunByteQuota.CreateArtifact(1_000, () => Interlocked.Increment(ref notifications));
        var reservations = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(index => Task.Run(() => quota.TryReserve(40, out var reservation) ? reservation : null)));

        Assert.Equal(25, reservations.Count(reservation => reservation is not null));
        Assert.Equal(1_000, quota.AccountedBytes);
        Assert.True(quota.IsFailed);
        Assert.Equal(1, notifications);
        foreach (var reservation in reservations)
        {
            reservation?.Dispose();
        }

        Assert.True(quota.IsFailed);
        Assert.False(quota.TryReserve(1, out _));
    }

    [Fact]
    public void QuotaCallbackFailure_ShouldNotEscapeOrUndoFailure()
    {
        var quota = EvidenceRunByteQuota.CreateArtifact(1, () => throw new InvalidOperationException("private detail"));

        Assert.False(quota.TryChargeReceived(2));
        Assert.True(quota.IsFailed);
    }

    [Fact]
    public void TimeBudget_ShouldAdmitExactDeclaredFitAndKeepCleanupOutOfStoppingDoubleCount()
    {
        var clock = new AdjustableTimeProvider();
        var stages = new[]
        {
            new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(10)),
            new EvidenceRunStageDeadline(EvidenceRunStage.Start, TimeSpan.FromSeconds(20)),
            new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.FromSeconds(5)),
        };
        var total = TimeSpan.FromSeconds(10 + 20 + 5 + 10 + 30);
        Assert.True(EvidenceRunTimeBudget.TryCreate(clock, clock.UtcNow + total, stages,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), out var budget));
        Assert.Equal(total, budget!.JobRemaining);

        foreach (var expectedStage in stages)
        {
            Assert.True(budget.TryBeginNextStage(CancellationToken.None, out var actualStage));
            Assert.Equal(expectedStage, actualStage);
            Assert.True(budget.CompleteCurrentStage());
        }

        Assert.True(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        Assert.Equal(TimeSpan.FromSeconds(20), budget.StoppingAllowance);
        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Equal(TimeSpan.FromSeconds(18), budget.CleanupRemaining);
        Assert.Equal(TimeSpan.FromSeconds(18), budget.StoppingAllowance);
        Assert.True(budget.CompleteCleanup());
        Assert.True(budget.TryBeginCollection());
        Assert.True(budget.CompleteCollection());
    }

    [Fact]
    public void TimeBudget_ShouldRejectOneTickShortAndInitiallyExpiredJob()
    {
        var clock = new AdjustableTimeProvider();
        var stages = new[] { new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(2)) };
        var required = TimeSpan.FromSeconds(2 + 1 + 1);

        Assert.False(EvidenceRunTimeBudget.TryCreate(clock, clock.UtcNow + required - TimeSpan.FromTicks(1), stages,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreate(clock, clock.UtcNow, stages,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out _));
    }

    [Fact]
    public void TimeBudget_ShouldRejectInvalidStageAndReserveSettings()
    {
        var clock = new AdjustableTimeProvider();
        var deadline = clock.UtcNow + TimeSpan.FromHours(1);
        Assert.False(EvidenceRunTimeBudget.TryCreate(clock, deadline,
            [new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1))],
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreate(clock, deadline, [], TimeSpan.FromSeconds(61),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreate(clock, deadline, [], TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreate(clock, deadline,
            [new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.Zero)],
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreate(clock, deadline,
            [new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.MaxValue)],
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out _));
    }

    [Fact]
    public void TimeBudget_ShouldUseMonotonicElapsedTimeWhenUtcMovesBackward()
    {
        var clock = new AdjustableTimeProvider();
        var stages = new[] { new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(5)) };
        Assert.True(EvidenceRunTimeBudget.TryCreate(clock, clock.UtcNow + TimeSpan.FromSeconds(10), stages,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out var budget));
        clock.MoveUtcBy(TimeSpan.FromHours(-4));
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(5), budget!.JobRemaining);
        Assert.False(budget.TryBeginNextStage(CancellationToken.None, out _));
        clock.MoveUtcBy(TimeSpan.FromDays(-2));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.Zero, budget.CurrentStageRemaining);
        Assert.Equal(TimeSpan.Zero, budget.JobRemaining);
        Assert.False(budget.TryBeginCollection());
    }

    [Fact]
    public void TimeBudget_ShouldRecheckBeforeLaterStageAndPreserveCollectionCleanupReserves()
    {
        var clock = new AdjustableTimeProvider();
        var stages = new[]
        {
            new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(2)),
            new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.FromSeconds(3)),
        };
        Assert.True(EvidenceRunTimeBudget.TryCreate(clock, clock.UtcNow + TimeSpan.FromSeconds(12), stages,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), out var budget));
        Assert.True(budget!.TryBeginNextStage(CancellationToken.None, out _));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(budget.CompleteCurrentStage());
        Assert.False(budget.TryBeginNextStage(CancellationToken.None, out _));
        Assert.Equal(TimeSpan.FromSeconds(7), budget.JobRemaining);
    }

    [Fact]
    public async Task TimeBudget_ConcurrentStageStartRace_ShouldStartAtMostOnce()
    {
        var clock = new AdjustableTimeProvider();
        Assert.True(EvidenceRunTimeBudget.TryCreate(clock, clock.UtcNow + TimeSpan.FromSeconds(10),
            [new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(1))],
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out var budget));
        using var barrier = new Barrier(8);
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return budget!.TryBeginNextStage(CancellationToken.None, out _);
        }));

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result);
    }

    [Fact]
    public void TimeBudget_CancellationBeforeStageStartRejectsWithoutConsumingReservation()
    {
        var clock = new AdjustableTimeProvider();
        Assert.True(EvidenceRunTimeBudget.TryCreate(clock, clock.UtcNow + TimeSpan.FromSeconds(10),
            [new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(1))],
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out var budget));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.False(budget!.TryBeginNextStage(cancellation.Token, out _));
        Assert.True(budget.TryBeginNextStage(CancellationToken.None, out _));
        Assert.True(budget.CurrentStageRemaining > TimeSpan.Zero);
        Assert.True(budget.CompleteCurrentStage());
    }

    [Fact]
    public void TimeBudget_CancelledActiveStageCanCloseFutureStagesAndEnterSharedStopCleanup()
    {
        var clock = new AdjustableTimeProvider();
        Assert.True(EvidenceRunTimeBudget.TryCreate(clock, clock.UtcNow + TimeSpan.FromSeconds(20),
            [
                new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(5)),
                new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.FromSeconds(5)),
            ], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), out var budget));
        Assert.True(budget!.TryBeginNextStage(CancellationToken.None, out _));

        Assert.False(budget.TryAbandonStagesAndBeginCleanup(stageClosed: false));
        Assert.True(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        Assert.Equal(TimeSpan.Zero, budget.CurrentStageRemaining);
        Assert.False(budget.TryBeginNextStage(CancellationToken.None, out _));
        Assert.Equal(TimeSpan.FromSeconds(2), budget.StoppingAllowance);
        Assert.True(budget.CompleteCleanup());
        Assert.True(budget.TryBeginCollection());
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = DateTimeOffset.Parse("2026-10-02T12:00:00Z");

        public DateTimeOffset UtcNow => _utcNow;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _timestamp, duration.Ticks);
            _utcNow += duration;
        }

        internal void MoveUtcBy(TimeSpan delta) => _utcNow += delta;
    }
}
