namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowExecutionContextTests
{
    [Fact]
    public void Constructor_CapturesExecutionProperties()
    {
        var state = new TestState("created");
        var resumeEvent = new FlowResumeEvent("approved", isTimeout: false);

        var context = new FlowExecutionContext<TestState>(
            "approval",
            "1",
            "review",
            state,
            resumeEvent);

        Assert.Equal("approval", context.FlowId);
        Assert.Equal("1", context.Version);
        Assert.Equal("review", context.NodeId);
        Assert.Equal(state, context.State);
        Assert.Same(resumeEvent, context.ResumeEvent);
    }

    [Fact]
    public void Constructor_DefaultsResumeEventToNull()
    {
        var context = new FlowExecutionContext<TestState>(
            "approval",
            "1",
            "review",
            new TestState("created"));

        Assert.Null(context.ResumeEvent);
    }

    private sealed record TestState(string Status);
}
