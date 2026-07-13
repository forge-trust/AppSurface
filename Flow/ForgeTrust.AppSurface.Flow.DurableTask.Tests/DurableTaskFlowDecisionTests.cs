namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

public sealed class DurableTaskFlowDecisionTests
{
    [Fact]
    public void DurableTaskFlowDecisionKind_ValuesAreStable()
    {
        Assert.Equal(0, (int)DurableTaskFlowDecisionKind.ScheduleNode);
        Assert.Equal(1, (int)DurableTaskFlowDecisionKind.WaitForExternalEvent);
        Assert.Equal(2, (int)DurableTaskFlowDecisionKind.Complete);
        Assert.Equal(3, (int)DurableTaskFlowDecisionKind.Fault);
        Assert.Equal(4, (int)DurableTaskFlowDecisionKind.TimedOut);
        Assert.Equal(5, (int)DurableTaskFlowDecisionKind.IgnoreLateEvent);
        Assert.Equal(6, (int)DurableTaskFlowDecisionKind.ScheduleActivity);
    }

    [Fact]
    public void ScheduleNode_CapturesContextAndRetryPolicy()
    {
        var retryPolicy = new FlowRetryPolicy(2, TimeSpan.FromSeconds(1));

        var decision = DurableTaskFlowDecision<TestState>.ScheduleNode(
            "review",
            new TestState("ready"),
            retryPolicy);

        Assert.Equal(DurableTaskFlowDecisionKind.ScheduleNode, decision.Kind);
        Assert.Equal("review", decision.NodeId);
        Assert.Equal(new TestState("ready"), decision.Context);
        Assert.Same(retryPolicy, decision.RetryPolicy);
    }

    [Fact]
    public void WaitForExternalEvent_CapturesEventAndTimeout()
    {
        var timeout = new FlowTimeout(TimeSpan.FromMinutes(5));

        var decision = DurableTaskFlowDecision<TestState>.WaitForExternalEvent(
            "review",
            "approved",
            new TestState("waiting"),
            timeout);

        Assert.Equal(DurableTaskFlowDecisionKind.WaitForExternalEvent, decision.Kind);
        Assert.Equal("review", decision.NodeId);
        Assert.Equal("approved", decision.EventName);
        Assert.Equal(new TestState("waiting"), decision.Context);
        Assert.Same(timeout, decision.Timeout);
    }

    [Fact]
    public void TimedOut_CapturesEventName()
    {
        var decision = DurableTaskFlowDecision<TestState>.TimedOut(
            "review",
            "approved",
            new TestState("timed-out"));

        Assert.Equal(DurableTaskFlowDecisionKind.TimedOut, decision.Kind);
        Assert.Equal("review", decision.NodeId);
        Assert.Equal("approved", decision.EventName);
        Assert.Equal(new TestState("timed-out"), decision.Context);
    }

    [Fact]
    public void Complete_CapturesContext()
    {
        var decision = DurableTaskFlowDecision<TestState>.Complete(
            "review",
            new TestState("complete"));

        Assert.Equal(DurableTaskFlowDecisionKind.Complete, decision.Kind);
        Assert.Equal("review", decision.NodeId);
        Assert.Equal(new TestState("complete"), decision.Context);
    }

    [Fact]
    public void Faulted_CapturesDiagnostic()
    {
        var fault = new FlowFault("approval.failed", "Approval failed.");

        var decision = DurableTaskFlowDecision<TestState>.Faulted("review", fault, "details");

        Assert.Equal(DurableTaskFlowDecisionKind.Fault, decision.Kind);
        Assert.Equal("review", decision.NodeId);
        Assert.Same(fault, decision.Fault);
        Assert.Equal("details", decision.Diagnostic);
    }

    [Fact]
    public void IgnoreLateEvent_CapturesDiagnostic()
    {
        var decision = DurableTaskFlowDecision<TestState>.IgnoreLateEvent(
            "review",
            "denied",
            "Event was late.");

        Assert.Equal(DurableTaskFlowDecisionKind.IgnoreLateEvent, decision.Kind);
        Assert.Equal("review", decision.NodeId);
        Assert.Equal("denied", decision.EventName);
        Assert.Equal("Event was late.", decision.Diagnostic);
    }

    [Fact]
    public void ScheduleActivity_CapturesTypedRequestWithoutRetryMetadata()
    {
        var activity = FlowNodeOutcome<TestState>.Activity(
            new FlowActivityCallsite<TestWork, TestResult>("send-email", 2, 3),
            new TestWork("APR-1001"),
            new TestState("activity-pending"));

        var decision = DurableTaskFlowDecision<TestState>.ScheduleActivity("review", activity);

        Assert.Equal(DurableTaskFlowDecisionKind.ScheduleActivity, decision.Kind);
        Assert.Equal("review", decision.NodeId);
        Assert.Same(activity, decision.Activity);
        Assert.Same(activity.Context, decision.Context);
        Assert.Null(decision.RetryPolicy);
    }

    [Fact]
    public void ScheduleActivity_WithNullRequest_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskFlowDecision<TestState>.ScheduleActivity("review", null!));
    }

    [Theory]
    [InlineData("schedule-node")]
    [InlineData("wait-node")]
    [InlineData("wait-event")]
    [InlineData("complete-node")]
    [InlineData("timed-out-node")]
    [InlineData("timed-out-event")]
    [InlineData("fault-node")]
    [InlineData("ignore-node")]
    [InlineData("ignore-event")]
    [InlineData("ignore-diagnostic")]
    [InlineData("activity-node")]
    public void Factories_WithEmptyRequiredText_ThrowArgumentException(string scenario)
    {
        var state = new TestState("ready");
        var fault = new FlowFault("approval.failed", "Approval failed.");
        var activity = FlowNodeOutcome<TestState>.Activity(
            new FlowActivityCallsite<TestWork, TestResult>("send-email"),
            new TestWork("APR-1001"),
            state);
        var exception = Assert.Throws<ArgumentException>(() => scenario switch
        {
            "schedule-node" => DurableTaskFlowDecision<TestState>.ScheduleNode(" ", state),
            "wait-node" => DurableTaskFlowDecision<TestState>.WaitForExternalEvent(" ", "approved", state, null),
            "wait-event" => DurableTaskFlowDecision<TestState>.WaitForExternalEvent("review", " ", state, null),
            "complete-node" => DurableTaskFlowDecision<TestState>.Complete(" ", state),
            "timed-out-node" => DurableTaskFlowDecision<TestState>.TimedOut(" ", "approved", state),
            "timed-out-event" => DurableTaskFlowDecision<TestState>.TimedOut("review", " ", state),
            "fault-node" => DurableTaskFlowDecision<TestState>.Faulted(" ", fault),
            "ignore-node" => DurableTaskFlowDecision<TestState>.IgnoreLateEvent(" ", "denied", "Event was late."),
            "ignore-event" => DurableTaskFlowDecision<TestState>.IgnoreLateEvent("review", " ", "Event was late."),
            "ignore-diagnostic" => DurableTaskFlowDecision<TestState>.IgnoreLateEvent("review", "denied", " "),
            "activity-node" => DurableTaskFlowDecision<TestState>.ScheduleActivity(" ", activity),
            _ => throw new InvalidOperationException("Unknown scenario."),
        });

        Assert.NotNull(exception.ParamName);
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("wait")]
    [InlineData("complete")]
    [InlineData("timed-out")]
    public void Factories_WithNullContext_ThrowArgumentNullException(string scenario)
    {
        Assert.Throws<ArgumentNullException>(() => scenario switch
        {
            "schedule" => DurableTaskFlowDecision<TestState>.ScheduleNode("review", null!),
            "wait" => DurableTaskFlowDecision<TestState>.WaitForExternalEvent("review", "approved", null!, null),
            "complete" => DurableTaskFlowDecision<TestState>.Complete("review", null!),
            "timed-out" => DurableTaskFlowDecision<TestState>.TimedOut("review", "approved", null!),
            _ => throw new InvalidOperationException("Unknown scenario."),
        });
    }

    [Fact]
    public void Faulted_WithNullFault_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableTaskFlowDecision<TestState>.Faulted("review", null!));
    }

    private sealed record TestState(string Status);

    private sealed record TestWork(string ApprovalId);

    private sealed record TestResult(string Status);
}
