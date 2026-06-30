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

    private static DurableWorkerCorrelation TestCorrelation() =>
        new("worker", "work", "instance", "attempt");
}
