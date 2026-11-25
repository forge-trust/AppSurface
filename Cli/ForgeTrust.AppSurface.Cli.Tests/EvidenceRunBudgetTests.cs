using System.Collections;
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
            collection: TimeSpan.FromSeconds(5), cleanup: TimeSpan.FromSeconds(10), stopping: TimeSpan.FromSeconds(5), out var budget));
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
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, total, stages,
            collection: TimeSpan.FromSeconds(10), cleanup: TimeSpan.FromSeconds(30), stopping: TimeSpan.FromSeconds(20), out var budget));
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
    public void TimeBudget_ChargesElapsedTimeFromTheSameReadingThatAdmittedEachWindow()
    {
        var clock = new AdjustableTimeProvider();
        var window = TimeSpan.FromSeconds(2);
        var tick = TimeSpan.FromTicks(1);
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, window + window + window,
            [new(EvidenceRunStage.Producer, window)], collection: window, cleanup: window, stopping: window, out var budget));

        clock.AdvanceAfterNextTimestampRead(tick);
        Assert.True(budget!.TryBeginNextStage(default, out _));
        Assert.Equal(window - tick, budget.CurrentStageRemaining);
        Assert.True(budget.CompleteCurrentStage());

        clock.AdvanceAfterNextTimestampRead(tick);
        Assert.True(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        Assert.Equal(window - tick, budget.CleanupRemaining);
        Assert.True(budget.CompleteCleanup());

        clock.AdvanceAfterNextTimestampRead(tick);
        Assert.True(budget.TryBeginCollection());
        Assert.Equal(window - tick, budget.CollectionRemaining);
    }

    [Fact]
    public void TimeBudget_ShouldRejectOneTickShortAndInitiallyExpiredJob()
    {
        var clock = new AdjustableTimeProvider();
        var stages = new[] { new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(2)) };
        var required = TimeSpan.FromSeconds(2 + 1 + 1);

        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, required - TimeSpan.FromTicks(1), stages,
            collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1), [],
            collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.Zero, stages,
            collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out _));
    }

    [Fact]
    public void TimeBudget_ShouldRejectInvalidStageAndReserveSettings()
    {
        var clock = new AdjustableTimeProvider();
        var allowance = TimeSpan.FromHours(1);
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, allowance,
            [new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1))],
            collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, allowance, [], collection: TimeSpan.FromSeconds(61),
            cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, allowance, [], collection: TimeSpan.FromSeconds(1),
            cleanup: TimeSpan.FromSeconds(10), stopping: TimeSpan.FromSeconds(11), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, allowance,
            [new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.Zero)],
            collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out _));
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, allowance,
            [new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.MaxValue)],
            collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out _));
    }

    [Fact]
    public void TimeBudget_ShouldUseMonotonicElapsedTimeWhenUtcMovesBackward()
    {
        var clock = new AdjustableTimeProvider();
        var stages = new[] { new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(5)) };
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(10), stages,
            collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out var budget));
        clock.MoveUtcBy(TimeSpan.FromHours(-4));
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(5), budget!.JobRemaining);
        Assert.False(budget.TryBeginNextStage(CancellationToken.None, out _));
        clock.MoveUtcBy(TimeSpan.FromDays(-2));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.Zero, budget.CurrentStageRemaining);
        Assert.Equal(TimeSpan.Zero, budget.JobRemaining);
        Assert.False(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
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
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(12), stages,
            collection: TimeSpan.FromSeconds(2), cleanup: TimeSpan.FromSeconds(3), stopping: TimeSpan.FromSeconds(1), out var budget));
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
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(10),
            [new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(1))],
            collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out var budget));
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
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(10),
            [new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(1))],
            collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out var budget));
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
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(20),
            [
                new EvidenceRunStageDeadline(EvidenceRunStage.Admission, TimeSpan.FromSeconds(5)),
                new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.FromSeconds(5)),
            ], collection: TimeSpan.FromSeconds(2), cleanup: TimeSpan.FromSeconds(5), stopping: TimeSpan.FromSeconds(2), out var budget));
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
            collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out var rejected));
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
            collection: TimeSpan.FromSeconds(collection), cleanup: TimeSpan.FromSeconds(cleanup), stopping: TimeSpan.FromSeconds(stopping), out var budget));
        Assert.Null(budget);
    }

    [Fact]
    public void TimeBudget_NullStageCannotBeAdmittedAsUnboundedWork()
    {
        Assert.False(EvidenceRunTimeBudget.TryCreateFromAllowance(new AdjustableTimeProvider(), TimeSpan.FromSeconds(20),
            [null!], collection: TimeSpan.FromSeconds(1), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out var budget));
        Assert.Null(budget);
    }

    [Fact]
    public void TimeBudget_AccountsForTheSameStageSnapshotItValidates()
    {
        var stage = new EvidenceRunStageDeadline(EvidenceRunStage.Start, TimeSpan.FromSeconds(2));
        var declarations = new[] { stage };
        var stages = new ObservedStageList(declarations);
        var reserve = TimeSpan.FromSeconds(1);

        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(new AdjustableTimeProvider(), TimeSpan.FromSeconds(4),
            stages, collection: reserve, cleanup: reserve, stopping: reserve, out var budget));
        Assert.Equal(1, stages.EnumerationCount);
        declarations[0] = stage with { Duration = TimeSpan.FromSeconds(3) };

        Assert.True(budget!.TryBeginNextStage(default, out var admitted));
        Assert.Equal(stage, admitted);
        Assert.Equal(stage.Duration, budget.CurrentStageRemaining);
    }

    [Fact]
    public void TimeBudget_RejectsMissingClockOrStageList()
    {
        var allowance = TimeSpan.FromSeconds(10);
        var reserve = TimeSpan.FromSeconds(1);

        Assert.Throws<ArgumentNullException>("timeProvider", () =>
            EvidenceRunTimeBudget.TryCreateFromAllowance(null!, allowance, [], collection: reserve, cleanup: reserve, stopping: reserve, out _));
        Assert.Throws<ArgumentNullException>("stages", () =>
            EvidenceRunTimeBudget.TryCreateFromAllowance(new AdjustableTimeProvider(), allowance, null!, collection: reserve, cleanup: reserve, stopping: reserve, out _));
    }

    [Fact]
    public void TimeBudget_StageCompletionCannotAdvanceTwiceOrOverlapFinalCollection()
    {
        var clock = new AdjustableTimeProvider();
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(20),
            [new(EvidenceRunStage.Resource, TimeSpan.FromSeconds(3)), new(EvidenceRunStage.Producer, TimeSpan.FromSeconds(2))],
            collection: TimeSpan.FromSeconds(2), cleanup: TimeSpan.FromSeconds(5), stopping: TimeSpan.FromSeconds(2), out var budget));
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
        Assert.False(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
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
            collection: TimeSpan.FromSeconds(2), cleanup: TimeSpan.FromSeconds(5), stopping: TimeSpan.FromSeconds(2), out var budget));
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
            collection: TimeSpan.FromSeconds(2), cleanup: TimeSpan.FromSeconds(1), stopping: TimeSpan.FromSeconds(1), out var budget));
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

    [Fact]
    public async Task TimeBudget_CollectionCanStartOnceEvenWhenTimeRemains()
    {
        var clock = new AdjustableTimeProvider();
        var reserve = TimeSpan.FromSeconds(2);
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(20), [],
            collection: reserve, cleanup: reserve, stopping: reserve, out var budget));
        Assert.True(budget!.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        Assert.True(budget.CompleteCleanup());

        var starts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(budget.TryBeginCollection)));
        Assert.Single(starts, started => started);
        Assert.True(budget.CompleteCollection());

        Assert.True(budget.JobRemaining > reserve);
        var restarts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(budget.TryBeginCollection)));
        Assert.DoesNotContain(true, restarts);
        Assert.Equal(TimeSpan.Zero, budget.CollectionRemaining);
    }

    [Fact]
    public void TimeBudget_CollectionCannotStartAfterItsReservedTimeNoLongerFits()
    {
        var clock = new AdjustableTimeProvider();
        var reserve = TimeSpan.FromSeconds(2);
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromSeconds(4), [],
            collection: reserve, cleanup: reserve, stopping: reserve, out var budget));
        Assert.True(budget!.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        Assert.True(budget.CompleteCleanup());

        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.False(budget.TryBeginCollection());
        Assert.Equal(TimeSpan.Zero, budget.CollectionRemaining);
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

    private sealed class ObservedStageList(EvidenceRunStageDeadline[] stages) : IReadOnlyList<EvidenceRunStageDeadline>
    {
        public int Count => stages.Length;

        public EvidenceRunStageDeadline this[int index] => stages[index];

        internal int EnumerationCount { get; private set; }

        public IEnumerator<EvidenceRunStageDeadline> GetEnumerator()
        {
            EnumerationCount++;
            return ((IEnumerable<EvidenceRunStageDeadline>)stages).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private long _timestamp;
        private long _advanceAfterRead;
        private DateTimeOffset _utcNow = DateTimeOffset.Parse("2026-10-02T12:00:00Z");

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            var advance = Interlocked.Exchange(ref _advanceAfterRead, 0);
            return Interlocked.Add(ref _timestamp, advance) - advance;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _timestamp, duration.Ticks);
            _utcNow += duration;
        }

        internal void MoveUtcBy(TimeSpan delta) => _utcNow += delta;

        internal void AdvanceAfterNextTimestampRead(TimeSpan duration) => Interlocked.Exchange(ref _advanceAfterRead, duration.Ticks);
    }
}
