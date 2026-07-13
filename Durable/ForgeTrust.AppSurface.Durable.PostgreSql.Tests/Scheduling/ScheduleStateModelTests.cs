using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests.Scheduling;

public sealed class ScheduleStateModelTests
{
    [Fact]
    public void PauseAndResume_PreserveGenerationAndAdvanceRevision()
    {
        var original = new ScheduleStateModel(DurableScheduleState.Active, generation: 3, revision: 7);

        var paused = ScheduleStateTransitions.Pause(original, expectedRevision: 7);
        var resumed = ScheduleStateTransitions.Resume(paused.State, expectedRevision: 8);

        Assert.Equal(ScheduleStateTransitionCode.Applied, paused.Code);
        Assert.Equal(DurableScheduleState.Paused, paused.State.State);
        Assert.Equal(3, paused.State.Generation);
        Assert.Equal(8, paused.State.Revision);
        Assert.False(paused.InvalidatePendingOccurrences);
        Assert.Equal(ScheduleStateTransitionCode.Applied, resumed.Code);
        Assert.Equal(DurableScheduleState.Active, resumed.State.State);
        Assert.Equal(3, resumed.State.Generation);
        Assert.Equal(9, resumed.State.Revision);
        Assert.True(resumed.PendingOccurrenceBecameEligible);
    }

    [Fact]
    public void RepeatedLifecycleRequest_IsRecordedAsUnchangedRevision()
    {
        var paused = new ScheduleStateModel(DurableScheduleState.Paused, 1, 1);
        var active = new ScheduleStateModel(DurableScheduleState.Active, 1, 1);

        var pauseAgain = ScheduleStateTransitions.Pause(paused, 1);
        var resumeAgain = ScheduleStateTransitions.Resume(active, 1);

        Assert.Equal(ScheduleStateTransitionCode.Unchanged, pauseAgain.Code);
        Assert.Equal(2, pauseAgain.State.Revision);
        Assert.Equal(ScheduleStateTransitionCode.Unchanged, resumeAgain.Code);
        Assert.Equal(2, resumeAgain.State.Revision);
    }

    [Fact]
    public void Update_IncrementsGenerationPreservesPauseAndInvalidatesPending()
    {
        var result = ScheduleStateTransitions.Update(
            new ScheduleStateModel(DurableScheduleState.Paused, generation: 4, revision: 9),
            expectedRevision: 9);

        Assert.Equal(ScheduleStateTransitionCode.Applied, result.Code);
        Assert.Equal(DurableScheduleState.Paused, result.State.State);
        Assert.Equal(5, result.State.Generation);
        Assert.Equal(10, result.State.Revision);
        Assert.True(result.InvalidatePendingOccurrences);
    }

    [Fact]
    public void Delete_PreservesGenerationAndInvalidatesPending()
    {
        var result = ScheduleStateTransitions.Delete(
            new ScheduleStateModel(DurableScheduleState.Active, generation: 4, revision: 9),
            expectedRevision: 9);

        Assert.Equal(ScheduleStateTransitionCode.Applied, result.Code);
        Assert.Equal(DurableScheduleState.Deleted, result.State.State);
        Assert.Equal(4, result.State.Generation);
        Assert.Equal(10, result.State.Revision);
        Assert.True(result.InvalidatePendingOccurrences);
    }

    [Fact]
    public void RevisionConflict_DoesNotMutateState()
    {
        var state = new ScheduleStateModel(DurableScheduleState.Active, 1, 2);

        var result = ScheduleStateTransitions.Pause(state, expectedRevision: 1);

        Assert.Equal(ScheduleStateTransitionCode.RevisionConflict, result.Code);
        Assert.Same(state, result.State);
        Assert.Equal(
            ScheduleStateTransitionCode.RevisionConflict,
            ScheduleStateTransitions.Delete(state, expectedRevision: 1).Code);
    }

    [Theory]
    [InlineData(DurableScheduleState.Deleted, (int)ScheduleStateTransitionCode.Deleted)]
    [InlineData(DurableScheduleState.Suspended, (int)ScheduleStateTransitionCode.Suspended)]
    public void TerminalOrSuspendedState_RejectsMutations(
        DurableScheduleState state,
        int expectedCode)
    {
        var model = new ScheduleStateModel(state, 1, 1);
        var expected = (ScheduleStateTransitionCode)expectedCode;

        Assert.Equal(expected, ScheduleStateTransitions.Pause(model, 1).Code);
        Assert.Equal(expected, ScheduleStateTransitions.Resume(model, 1).Code);
        Assert.Equal(expected, ScheduleStateTransitions.Update(model, 1).Code);
        Assert.Equal(expected, ScheduleStateTransitions.Delete(model, 1).Code);
    }

    [Fact]
    public void StateModel_ValidatesValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleStateModel((DurableScheduleState)99, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleStateModel(DurableScheduleState.Active, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleStateModel(DurableScheduleState.Active, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleStateTransitions.Pause(
            new ScheduleStateModel(DurableScheduleState.Active, 1, 1), 0));
    }
}
