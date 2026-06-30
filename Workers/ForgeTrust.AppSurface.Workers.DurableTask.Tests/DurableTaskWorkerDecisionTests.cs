using ForgeTrust.AppSurface.Workers;
using ForgeTrust.AppSurface.Workers.DurableTask;

namespace ForgeTrust.AppSurface.Workers.DurableTask.Tests;

public sealed class DurableTaskWorkerDecisionTests
{
    [Fact]
    public void DecisionKind_NumericValuesRemainStable()
    {
        Assert.Equal(0, (int)DurableTaskWorkerDecisionKind.ScheduleExecutor);
        Assert.Equal(1, (int)DurableTaskWorkerDecisionKind.WaitForExternalEvent);
        Assert.Equal(2, (int)DurableTaskWorkerDecisionKind.RepairProjection);
        Assert.Equal(3, (int)DurableTaskWorkerDecisionKind.Complete);
        Assert.Equal(4, (int)DurableTaskWorkerDecisionKind.Fault);
        Assert.Equal(5, (int)DurableTaskWorkerDecisionKind.IgnoreLateSignal);
        Assert.Equal(6, (int)DurableTaskWorkerDecisionKind.WaitForRetry);
        Assert.Equal(7, (int)DurableTaskWorkerDecisionKind.TimedOut);
    }

    [Fact]
    public void WaitForExternalEvent_RejectsBlankEventName()
    {
        Assert.Throws<ArgumentException>(() =>
            DurableTaskWorkerDecision<string, string, string>.WaitForExternalEvent(TestCorrelation(), " "));
    }

    [Fact]
    public void ScheduleExecutor_RejectsNonClaimedOutcome()
    {
        var stale = Envelope(DurableWorkerProjectionOutcome.StaleFence);

        Assert.Throws<ArgumentException>(() =>
            DurableTaskWorkerDecision<string, string, string>.ScheduleExecutor(stale));
    }

    [Fact]
    public void RepairProjection_RejectsNonCompletedOutcome()
    {
        var duplicate = Envelope(DurableWorkerProjectionOutcome.AlreadyCompleted);

        Assert.Throws<ArgumentException>(() =>
            DurableTaskWorkerDecision<string, string, string>.RepairProjection(duplicate));
    }

    private static DurableWorkerEnvelope<string> Envelope(DurableWorkerProjectionOutcome outcome) =>
        new(outcome, $"worker.{outcome.ToString().ToLowerInvariant()}", DurableWorkerRetryability.Terminal, TestCorrelation(), "payload");

    private static DurableWorkerCorrelation TestCorrelation() =>
        new("worker", "work", "instance", "attempt");
}
