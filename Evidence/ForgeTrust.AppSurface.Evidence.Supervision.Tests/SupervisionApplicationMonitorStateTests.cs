using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Procedure ordering controls only; these create no process owner, admission or native receipt.</summary>
public sealed class SupervisionApplicationMonitorStateTests
{
    [Fact]
    public void FailureBeforeStopRemainsLatchedAcrossStopAndRepeatedObservation()
    {
        var state = new SupervisionApplicationMonitorState();
        Assert.True(state.ShouldObserve);
        Assert.True(state.RecordObservationFailure());
        state.BeginStop();
        state.BeginStop();
        Assert.True(state.Failed);
        Assert.False(state.ShouldObserve);
        Assert.True(state.RecordObservationFailure());
    }

    [Fact]
    public void StopBeforeObservationFailureDoesNotManufactureLiveWorkloadFailure()
    {
        var state = new SupervisionApplicationMonitorState();
        state.BeginStop();
        Assert.False(state.RecordObservationFailure());
        Assert.False(state.Failed);
        Assert.False(state.ShouldObserve);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalObservationJoinsAndOnlyAPreStopFailureMakesWaitNegative(bool failBeforeStop)
    {
        var state = new SupervisionApplicationMonitorState();
        var ledger = new SupervisionWorkRegistry();
        var workload = ledger.BeginWorkload();
        var observationEntered = Signal();
        var releaseObservation = Signal();
        var observationReturned = Signal();
        async Task Observe()
        {
            observationEntered.TrySetResult();
            await releaseObservation.Task;
            if (state.RecordObservationFailure()) ledger.RecordFailure(SupervisionWorkFailure.WorkloadFailed);
            observationReturned.TrySetResult();
        }
        var original = Observe();
        var sequence = new SupervisionControlSequence(ledger, async _ =>
        {
            state.BeginStop();
            releaseObservation.TrySetResult();
            await original;
            Assert.True(observationReturned.Task.IsCompletedSuccessfully);
            Assert.True(workload.Complete());
            return new(true, true, true);
        });
        var ready = sequence.ClaimReady();
        sequence.CompleteWrite(ready, true);
        try
        {
            await observationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(original.IsCompleted);
            if (failBeforeStop)
            {
                releaseObservation.TrySetResult();
                await original.WaitAsync(TimeSpan.FromSeconds(5));
            }
            try { await sequence.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (EvidenceAdmissionException) when (failBeforeStop) { }
            var wait = sequence.ClaimWait();
            Assert.Equal(!failBeforeStop, wait.Positive);
            Assert.Equal(failBeforeStop, state.Failed);
            Assert.Equal(failBeforeStop ? SupervisionWorkFailure.WorkloadFailed : SupervisionWorkFailure.None,
                ledger.FirstFailure);
            Assert.Equal(0, ledger.ActiveWorkloads);
        }
        finally
        {
            releaseObservation.TrySetResult();
            await original.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
