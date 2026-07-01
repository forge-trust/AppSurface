using ForgeTrust.AppSurface.Flow.DurableTask;
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
    public void WaitForExternalEvent_RejectsNullCorrelation()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskWorkerDecision<string, string, string>.WaitForExternalEvent(null!, "resume-approved"));
    }

    [Fact]
    public void ScheduleExecutor_RejectsNonClaimedOutcome()
    {
        var stale = Envelope(DurableWorkerProjectionOutcome.StaleFence);

        Assert.Throws<ArgumentException>(() =>
            DurableTaskWorkerDecision<string, string, string>.ScheduleExecutor(stale));
    }

    [Fact]
    public void ScheduleExecutor_RejectsNullEnvelope()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskWorkerDecision<string, string, string>.ScheduleExecutor(null!));
    }

    [Fact]
    public void RepairProjection_RejectsNonCompletedOutcome()
    {
        var duplicate = Envelope(DurableWorkerProjectionOutcome.AlreadyCompleted);

        Assert.Throws<ArgumentException>(() =>
            DurableTaskWorkerDecision<string, string, string>.RepairProjection(duplicate));
    }

    [Fact]
    public void RepairProjection_RejectsNullEnvelope()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskWorkerDecision<string, string, string>.RepairProjection(null!));
    }

    [Fact]
    public void Complete_DoesNotCarryProjectionPayloadWhenPayloadMatchesProjectionType()
    {
        var envelope = Envelope(DurableWorkerProjectionOutcome.Reconciled);

        var decision = DurableTaskWorkerDecision<string, string, string>.Complete(envelope);

        Assert.Equal(DurableTaskWorkerDecisionKind.Complete, decision.Kind);
        Assert.Null(decision.Projection);
        Assert.Equal(DurableWorkerProjectionOutcome.Reconciled, decision.SourceOutcome);
    }

    [Fact]
    public void CompleteProjection_CarriesProjectionPayload()
    {
        var envelope = Envelope(DurableWorkerProjectionOutcome.Reconciled);

        var decision = DurableTaskWorkerDecision<string, string, string>.CompleteProjection(envelope);

        Assert.Equal(DurableTaskWorkerDecisionKind.Complete, decision.Kind);
        Assert.Equal("payload", decision.Projection);
        Assert.Equal(DurableWorkerProjectionOutcome.Reconciled, decision.SourceOutcome);
    }

    [Fact]
    public void CompleteProjection_RejectsNonReconciledOutcome()
    {
        var stale = Envelope(DurableWorkerProjectionOutcome.StaleFence);

        Assert.Throws<ArgumentException>(() =>
            DurableTaskWorkerDecision<string, string, string>.CompleteProjection(stale));
    }

    [Fact]
    public void CompleteProjection_RejectsNullEnvelope()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskWorkerDecision<string, string, string>.CompleteProjection(null!));
    }

    [Fact]
    public void Complete_RejectsNullEnvelope()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskWorkerDecision<string, string, string>.Complete<string>(null!));
    }

    [Fact]
    public void Fault_CarriesDiagnosticAndRejectsNullEnvelope()
    {
        var diagnostic = new DurableWorkerDiagnostic(
            "worker.conflict",
            "The worker conflicted.",
            "Another attempt owns the fence.",
            "Retry with the latest fence.",
            DurableWorkerRetryability.OperatorRequired);
        var envelope = new DurableWorkerEnvelope<string>(
            DurableWorkerProjectionOutcome.Conflict,
            "worker.conflict",
            DurableWorkerRetryability.OperatorRequired,
            TestCorrelation(),
            "payload",
            diagnostic: diagnostic);

        var decision = DurableTaskWorkerDecision<string, string, string>.Fault(envelope);

        Assert.Equal(DurableTaskWorkerDecisionKind.Fault, decision.Kind);
        Assert.Same(diagnostic, decision.Diagnostic);
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskWorkerDecision<string, string, string>.Fault<string>(null!));
    }

    [Fact]
    public void IgnoreLateSignal_RejectsBlankEventNameAndNullEnvelope()
    {
        var envelope = Envelope(DurableWorkerProjectionOutcome.StaleFence);

        Assert.Throws<ArgumentException>(() =>
            DurableTaskWorkerDecision<string, string, string>.IgnoreLateSignal(envelope, " "));
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskWorkerDecision<string, string, string>.IgnoreLateSignal<string>(null!));
    }

    [Fact]
    public void WaitForRetry_CarriesRetryPolicyAndRejectsNullEnvelope()
    {
        var retry = new FlowRetryPolicy(2, TimeSpan.FromSeconds(1));
        var envelope = Envelope(DurableWorkerProjectionOutcome.Conflict);

        var decision = DurableTaskWorkerDecision<string, string, string>.WaitForRetry(envelope, retry);
        var defaultDecision = DurableTaskWorkerDecision<string, string, string>.WaitForRetry(envelope);

        Assert.Equal(DurableTaskWorkerDecisionKind.WaitForRetry, decision.Kind);
        Assert.Same(retry, decision.RetryPolicy);
        Assert.Null(defaultDecision.RetryPolicy);
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskWorkerDecision<string, string, string>.WaitForRetry<string>(null!, retry));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void TimedOut_RejectsBlankEventName(string eventName)
    {
        Assert.Throws<ArgumentException>(() =>
            DurableTaskWorkerDecision<string, string, string>.TimedOut(TestCorrelation(), eventName));
    }

    [Fact]
    public void TimedOut_RejectsNullCorrelation()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskWorkerDecision<string, string, string>.TimedOut(null!, "resume-approved"));
    }

    private static DurableWorkerEnvelope<string> Envelope(DurableWorkerProjectionOutcome outcome) =>
        new(outcome, $"worker.{outcome.ToString().ToLowerInvariant()}", DurableWorkerRetryability.Terminal, TestCorrelation(), "payload");

    private static DurableWorkerCorrelation TestCorrelation() =>
        new("worker", "work", "instance", "attempt");
}
