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

    [Fact]
    public void TimeBudget_OverflowingSerialDeclarationsAreRejectedBeforeAnyStageAdmission()
    {
        var clock = new AdjustableTimeProvider();
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.MaxValue,
            [new(EvidenceRunStage.Resource, TimeSpan.MaxValue), new(EvidenceRunStage.Producer, TimeSpan.FromTicks(1))],
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out var rejected));
        Assert.Null(rejected);
    }

    [Theory]
    [InlineData(0, 1, 1, 1, (int)EvidenceRunStage.Start, 1)]
    [InlineData(300, 0, 1, 1, (int)EvidenceRunStage.Start, 1)]
    [InlineData(300, 1, 0, 1, (int)EvidenceRunStage.Start, 1)]
    [InlineData(300, 1, 601, 1, (int)EvidenceRunStage.Start, 1)]
    [InlineData(300, 1, 2, 0, (int)EvidenceRunStage.Start, 1)]
    [InlineData(300, 1, 40, 31, (int)EvidenceRunStage.Start, 1)]
    [InlineData(300, 1, 1, 1, (int)EvidenceRunStage.Start, 121)]
    [InlineData(300, 1, 1, 1, 999, 1)]
    public void TimeBudget_InvalidProtectedDeclarationsDoNotProduceAnAdmissibleBudget(
        int allowance, int collection, int cleanup, int stopping, int stage, int duration)
    {
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(new AdjustableTimeProvider(), TimeSpan.FromSeconds(allowance),
            [new((EvidenceRunStage)stage, TimeSpan.FromSeconds(duration))],
            TimeSpan.FromSeconds(collection), TimeSpan.FromSeconds(cleanup), TimeSpan.FromSeconds(stopping), out var budget));
        Assert.Null(budget);
    }

    [Fact]
    public void TimeBudget_NullStageCannotBeAdmittedAsUnboundedWork()
    {
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(new AdjustableTimeProvider(), TimeSpan.FromSeconds(20),
            [null!], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out var budget));
        Assert.Null(budget);
    }

    [Fact]
    public void TimeBudget_StageCompletionCannotAdvanceTwiceOrOverlapFinalCollection()
    {
        var clock = new AdjustableTimeProvider();
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(20),
            [new(EvidenceRunStage.Resource, TimeSpan.FromSeconds(3)), new(EvidenceRunStage.Producer, TimeSpan.FromSeconds(2))],
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), out var budget));
        Assert.False(budget!.CompleteCurrentStage());
        Assert.False(budget.CompleteCollection());
        Assert.Equal(TimeSpan.Zero, budget.CleanupRemaining);
        Assert.True(budget.TryBeginNextStage(default, out var resource));
        Assert.Equal(EvidenceRunStage.Resource, resource!.Stage);
        Assert.False(budget.TryBeginCollection());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(2), budget.CurrentStageRemaining);
        Assert.True(budget.CompleteCurrentStage());
        Assert.False(budget.CompleteCurrentStage());
        Assert.True(budget.TryBeginNextStage(default, out var producer));
        Assert.Equal(EvidenceRunStage.Producer, producer!.Stage);
        Assert.True(budget.CompleteCurrentStage());
        Assert.True(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        Assert.False(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        Assert.True(budget.CompleteCleanup());
        Assert.False(budget.CompleteCleanup());
        Assert.Equal(TimeSpan.Zero, budget.CollectionRemaining);
        Assert.True(budget.TryBeginCollection());
        Assert.False(budget.TryBeginCollection());
        Assert.False(budget.TryBeginNextStage(default, out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), budget.CollectionRemaining);
        Assert.True(budget.CompleteCollection());
        Assert.False(budget.CompleteCollection());
    }

    [Fact]
    public void TimeBudget_CleanupExpiryCannotBeReportedCompleteOrConsumeCollectionReserve()
    {
        var clock = new AdjustableTimeProvider();
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(7), [],
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), out var budget));
        Assert.False(budget!.CompleteCleanup());
        Assert.True(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(2), budget.JobRemaining);
        Assert.Equal(TimeSpan.Zero, budget.CleanupRemaining);
        Assert.Equal(TimeSpan.Zero, budget.StoppingAllowance);
        Assert.False(budget.CompleteCleanup());
        Assert.False(budget.TryBeginCollection());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
    }

    [Fact]
    public void TimeBudget_CollectionClampsToJobDeadlineAndCannotRestartCleanup()
    {
        var clock = new AdjustableTimeProvider();
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(3), [],
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), out var budget));
        Assert.True(budget!.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        Assert.True(budget.CompleteCleanup());
        Assert.True(budget.TryBeginCollection());
        Assert.False(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.Zero, budget.CollectionRemaining);
        Assert.Equal(TimeSpan.Zero, budget.JobRemaining);
        Assert.True(budget.CompleteCollection());
        Assert.False(budget.TryBeginCollection());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public void QuotaFactories_RejectNonpositiveProtectedLimitsBeforeAnyNotification(bool processOutput, long limit)
    {
        var notified = false;
        Assert.Throws<ArgumentOutOfRangeException>(() => processOutput
            ? EvidenceRunByteQuota.CreateProcessOutput(limit, () => notified = true)
            : EvidenceRunByteQuota.CreateArtifact(limit, () => notified = true));
        Assert.False(notified);
    }

    [Fact]
    public void Quota_InvalidChargesDoNotLatchFailureOrConsumeAValidWriteAllowance()
    {
        var notifications = 0;
        var quota = EvidenceRunByteQuota.CreateArtifact(5, () => notifications++);
        Assert.Throws<ArgumentOutOfRangeException>(() => quota.TryReserve(-1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => quota.TryChargeReceived(-1));
        Assert.True(quota.TryChargeReceived(0));
        Assert.True(quota.TryReserve(3, out var reservation));
        reservation!.Commit();
        Assert.True(quota.TryChargeReceived(2));
        Assert.Equal(5, quota.AccountedBytes);
        Assert.False(quota.IsFailed);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void Quota_AbortingAnEmptyWriteIsIdempotentAndCannotReleaseCommittedBytes()
    {
        var quota = EvidenceRunByteQuota.CreateArtifact(3);
        Assert.True(quota.TryReserve(0, out var empty));
        empty!.Dispose();
        empty.Dispose();
        Assert.Throws<InvalidOperationException>(() => empty.Commit());
        Assert.True(quota.TryReserve(3, out var complete));
        complete!.Commit();
        complete.Dispose();
        Assert.Throws<InvalidOperationException>(() => complete.Commit());
        Assert.Equal(3, quota.AccountedBytes);
        Assert.False(quota.TryReserve(1, out _));
    }

    [Fact]
    public void Quota_ReservationCrossingRetainsLatchAfterReleaseAndSwallowsNotificationFault()
    {
        var notifications = 0;
        var quota = EvidenceRunByteQuota.CreateArtifact(5, () =>
        {
            notifications++;
            throw new InvalidOperationException("safe fixture notification failure");
        });
        Assert.True(quota.TryReserve(4, out var pending));
        Assert.False(quota.TryReserve(2, out var rejected));
        Assert.Null(rejected);
        pending!.Dispose();
        Assert.Equal(0, quota.AccountedBytes);
        Assert.True(quota.IsFailed);
        Assert.False(quota.TryReserve(0, out _));
        Assert.False(quota.TryChargeReceived(0));
        Assert.False(quota.TryChargeReceived(1));
        Assert.Equal(1, notifications);
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
