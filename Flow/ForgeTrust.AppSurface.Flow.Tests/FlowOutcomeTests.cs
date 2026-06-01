namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowOutcomeTests
{
    [Fact]
    public void Factories_CreateExpectedOutcomeTypes()
    {
        var state = new TestState("ready");
        var timeout = new FlowTimeout(TimeSpan.FromMinutes(5));

        Assert.IsType<FlowNext<TestState>>(FlowNodeOutcome<TestState>.Next("next", state));
        Assert.IsType<FlowWait<TestState>>(FlowNodeOutcome<TestState>.Wait("approved", state, timeout));
        Assert.IsType<FlowTimedOut<TestState>>(FlowNodeOutcome<TestState>.TimedOut("approved", state));
        Assert.IsType<FlowComplete<TestState>>(FlowNodeOutcome<TestState>.Complete(state));
        Assert.IsType<FlowFaultOutcome<TestState>>(FlowNodeOutcome<TestState>.Fault("approval.failed", "Approval failed."));
    }

    [Fact]
    public void Timeout_WithNonPositiveDuration_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowTimeout(TimeSpan.Zero));
    }

    [Fact]
    public void ResumeEventTimeout_MarksEventAsTimeout()
    {
        var resumeEvent = FlowResumeEvent.Timeout("approved");

        Assert.Equal("approved", resumeEvent.EventName);
        Assert.True(resumeEvent.IsTimeout);
    }

    [Fact]
    public void Fault_WithEmptyCode_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FlowFault(" ", "message"));
    }

    [Fact]
    public void Fault_WithEmptyMessage_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FlowFault("approval.failed", " "));
    }

    [Theory]
    [InlineData("next-node")]
    [InlineData("wait-event")]
    [InlineData("timed-out-event")]
    public void Factories_WithEmptyRequiredText_ThrowArgumentException(string scenario)
    {
        var state = new TestState("ready");

        Assert.Throws<ArgumentException>(() => scenario switch
        {
            "next-node" => FlowNodeOutcome<TestState>.Next(" ", state),
            "wait-event" => FlowNodeOutcome<TestState>.Wait(" ", state),
            "timed-out-event" => FlowNodeOutcome<TestState>.TimedOut(" ", state),
            _ => throw new InvalidOperationException("Unknown scenario."),
        });
    }

    [Theory]
    [InlineData("next")]
    [InlineData("wait")]
    [InlineData("timed-out")]
    [InlineData("complete")]
    public void Factories_WithNullContext_ThrowArgumentNullException(string scenario)
    {
        Assert.Throws<ArgumentNullException>(() => scenario switch
        {
            "next" => FlowNodeOutcome<TestState>.Next("next", null!),
            "wait" => FlowNodeOutcome<TestState>.Wait("approved", null!),
            "timed-out" => FlowNodeOutcome<TestState>.TimedOut("approved", null!),
            "complete" => FlowNodeOutcome<TestState>.Complete(null!),
            _ => throw new InvalidOperationException("Unknown scenario."),
        });
    }

    [Fact]
    public void FaultOutcome_WithNullFault_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new FlowFaultOutcome<TestState>(null!));
    }

    private sealed record TestState(string Value);
}
