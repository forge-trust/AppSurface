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

    private sealed record TestState(string Value);
}
