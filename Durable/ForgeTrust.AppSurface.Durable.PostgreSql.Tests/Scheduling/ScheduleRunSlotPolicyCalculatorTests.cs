using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests.Scheduling;

public sealed class ScheduleRunSlotPolicyCalculatorTests
{
    private static readonly DateTimeOffset Due = DateTimeOffset.Parse("2026-07-12T12:00:00Z");

    [Fact]
    public void QueueOne_StartsQueuesCoalescesAndStartsOneFollowUpOnTerminalRelease()
    {
        var first = Occurrence(generation: 1, Due);
        var second = Occurrence(generation: 1, Due.AddMinutes(1));
        var third = Occurrence(generation: 1, Due.AddMinutes(2));

        var started = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            new ScheduleRunSlotState(0),
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.QueueOne,
            first);
        var queued = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            started.State,
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.QueueOne,
            second);
        var coalesced = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            queued.State,
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.QueueOne,
            third);
        var released = ScheduleRunSlotPolicyCalculator.Release(
            coalesced.State,
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.QueueOne);

        Assert.Equal(ScheduleRunSlotDecisionKind.StartNow, started.Kind);
        Assert.Equal(ScheduleRunSlotDecisionKind.Queued, queued.Kind);
        Assert.Equal(ScheduleRunSlotDecisionKind.Coalesced, coalesced.Kind);
        Assert.Equal(Due.AddMinutes(1), coalesced.State.PendingOccurrence!.NominalDueUtc);
        Assert.Equal(Due.AddMinutes(2), coalesced.State.PendingOccurrence.CoalescedThroughUtc);
        Assert.Equal(2, coalesced.State.PendingOccurrence.CoveredOccurrenceCount);
        Assert.Equal(ScheduleRunSlotDecisionKind.ReleasedAndStartedPending, released.Kind);
        Assert.Equal(1, released.State.ActiveRuns);
        Assert.Null(released.State.PendingOccurrence);
        Assert.Equal(Due.AddMinutes(1), released.OccurrenceToStart!.Window.NominalDueUtc);
        Assert.Equal(Due.AddMinutes(2), released.OccurrenceToStart.Window.CoveredThroughUtc);
    }

    [Fact]
    public void QueueOne_PreservesUnknownCountWhenRecoveryRangesCoalesce()
    {
        var pending = new SchedulePendingOccurrence(1, Due, Due.AddMinutes(10), null);
        var state = new ScheduleRunSlotState(1, pending);

        var decision = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            state,
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.QueueOne,
            Occurrence(1, Due.AddMinutes(11)));

        Assert.Equal(ScheduleRunSlotDecisionKind.Coalesced, decision.Kind);
        Assert.Null(decision.State.PendingOccurrence!.CoveredOccurrenceCount);
        Assert.Equal(Due.AddMinutes(11), decision.State.PendingOccurrence.CoalescedThroughUtc);
    }

    [Fact]
    public void Skip_DiscardsOverlapButStartsWhenIdle()
    {
        var start = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            new ScheduleRunSlotState(0),
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.Skip,
            Occurrence(1, Due));
        var overlap = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            start.State,
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.Skip,
            Occurrence(1, Due.AddMinutes(1)));

        Assert.Equal(ScheduleRunSlotDecisionKind.StartNow, start.Kind);
        Assert.Equal(ScheduleRunSlotDecisionKind.SkippedOverlap, overlap.Kind);
        Assert.Equal(1, overlap.State.ActiveRuns);
    }

    [Fact]
    public void AllowConcurrent_StartsUpToConfiguredMaximum()
    {
        var policy = ScheduleOverlapPolicy.AllowConcurrent(2);
        var first = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            new ScheduleRunSlotState(0), DurableScheduleState.Active, 1, policy, Occurrence(1, Due));
        var second = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            first.State, DurableScheduleState.Active, 1, policy, Occurrence(1, Due.AddMinutes(1)));
        var third = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            second.State, DurableScheduleState.Active, 1, policy, Occurrence(1, Due.AddMinutes(2)));

        Assert.Equal(ScheduleRunSlotDecisionKind.StartNow, second.Kind);
        Assert.Equal(2, second.State.ActiveRuns);
        Assert.Equal(ScheduleRunSlotDecisionKind.SkippedOverlap, third.Kind);
    }

    [Fact]
    public void Pause_HoldsOneOccurrenceAndResumeActivationStartsIt()
    {
        var held = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            new ScheduleRunSlotState(0),
            DurableScheduleState.Paused,
            1,
            ScheduleOverlapPolicy.Skip,
            Occurrence(1, Due));
        var activated = ScheduleRunSlotPolicyCalculator.ActivatePending(
            held.State,
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.Skip);

        Assert.Equal(ScheduleRunSlotDecisionKind.HeldPaused, held.Kind);
        Assert.NotNull(held.State.PendingOccurrence);
        Assert.Equal(ScheduleRunSlotDecisionKind.ReleasedAndStartedPending, activated.Kind);
        Assert.Equal(1, activated.State.ActiveRuns);
    }

    [Theory]
    [InlineData(DurableScheduleState.Deleted, (int)ScheduleRunSlotDecisionKind.InvalidatedDeleted)]
    [InlineData(DurableScheduleState.Suspended, (int)ScheduleRunSlotDecisionKind.InvalidatedSuspended)]
    public void InactiveLifecycle_InvalidatesOccurrence(
        DurableScheduleState state,
        int expected)
    {
        var decision = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            new ScheduleRunSlotState(0), state, 1, ScheduleOverlapPolicy.QueueOne, Occurrence(1, Due));

        Assert.Equal((ScheduleRunSlotDecisionKind)expected, decision.Kind);
    }

    [Fact]
    public void StaleGeneration_IsInvalidated()
    {
        var decision = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            new ScheduleRunSlotState(0),
            DurableScheduleState.Active,
            activeGeneration: 2,
            ScheduleOverlapPolicy.QueueOne,
            Occurrence(1, Due));

        Assert.Equal(ScheduleRunSlotDecisionKind.InvalidatedGeneration, decision.Kind);
    }

    [Fact]
    public void Release_PreservesPendingWhilePausedAndDiscardsItAfterDeleteOrGenerationChange()
    {
        var pending = new SchedulePendingOccurrence(1, Due, Due, 1);
        var paused = ScheduleRunSlotPolicyCalculator.Release(
            new ScheduleRunSlotState(1, pending),
            DurableScheduleState.Paused,
            1,
            ScheduleOverlapPolicy.QueueOne);
        var deleted = ScheduleRunSlotPolicyCalculator.Release(
            new ScheduleRunSlotState(1, pending),
            DurableScheduleState.Deleted,
            1,
            ScheduleOverlapPolicy.QueueOne);
        var stale = ScheduleRunSlotPolicyCalculator.Release(
            new ScheduleRunSlotState(1, pending),
            DurableScheduleState.Active,
            2,
            ScheduleOverlapPolicy.QueueOne);

        Assert.NotNull(paused.State.PendingOccurrence);
        Assert.Equal(ScheduleRunSlotDecisionKind.ReleasedAndDiscardedPending, deleted.Kind);
        Assert.Null(deleted.State.PendingOccurrence);
        Assert.Equal(ScheduleRunSlotDecisionKind.ReleasedAndDiscardedPending, stale.Kind);
    }

    [Fact]
    public void Release_RejectsDuplicateSlotRelease()
    {
        Assert.Throws<InvalidOperationException>(() => ScheduleRunSlotPolicyCalculator.Release(
            new ScheduleRunSlotState(0),
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.QueueOne));
    }

    [Fact]
    public void ReleaseWithoutPendingAndBlockedActivationAreNoOpsAfterRelease()
    {
        var released = ScheduleRunSlotPolicyCalculator.Release(
            new ScheduleRunSlotState(1),
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.QueueOne);
        var noPending = ScheduleRunSlotPolicyCalculator.ActivatePending(
            released.State,
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.QueueOne);
        var pending = new ScheduleRunSlotState(1, new SchedulePendingOccurrence(1, Due, Due, 1));
        var blocked = ScheduleRunSlotPolicyCalculator.ActivatePending(
            pending,
            DurableScheduleState.Active,
            1,
            ScheduleOverlapPolicy.QueueOne);
        var stale = ScheduleRunSlotPolicyCalculator.ActivatePending(
            new ScheduleRunSlotState(0, new SchedulePendingOccurrence(1, Due, Due, 1)),
            DurableScheduleState.Active,
            2,
            ScheduleOverlapPolicy.QueueOne);

        Assert.Equal(ScheduleRunSlotDecisionKind.Released, released.Kind);
        Assert.Equal(ScheduleRunSlotDecisionKind.Released, noPending.Kind);
        Assert.Equal(ScheduleRunSlotDecisionKind.Released, blocked.Kind);
        Assert.Equal(ScheduleRunSlotDecisionKind.ReleasedAndDiscardedPending, stale.Kind);
    }

    [Fact]
    public void Coalesce_RejectsDifferentPendingGeneration()
    {
        Assert.Throws<InvalidOperationException>(() => ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            new ScheduleRunSlotState(1, new SchedulePendingOccurrence(2, Due, Due, 1)),
            DurableScheduleState.Active,
            activeGeneration: 1,
            ScheduleOverlapPolicy.QueueOne,
            Occurrence(1, Due.AddMinutes(1))));
    }

    [Fact]
    public void PendingOccurrence_ValidatesRangeAndCount()
    {
        Assert.Throws<ArgumentException>(() => new SchedulePendingOccurrence(1, Due, Due.AddTicks(-1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchedulePendingOccurrence(1, Due, Due, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleRunSlotState(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleGenerationOccurrence(0, Occurrence(1, Due).Window));
        Assert.Throws<ArgumentNullException>(() => new ScheduleGenerationOccurrence(1, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            new ScheduleRunSlotState(0), DurableScheduleState.Active, 0, ScheduleOverlapPolicy.QueueOne, Occurrence(1, Due)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
            new ScheduleRunSlotState(0), (DurableScheduleState)99, 1, ScheduleOverlapPolicy.QueueOne, Occurrence(1, Due)));
    }

    private static ScheduleGenerationOccurrence Occurrence(long generation, DateTimeOffset due) =>
        new(generation, new ScheduleOccurrenceWindow(due, due, 1, isRecovery: false));
}
