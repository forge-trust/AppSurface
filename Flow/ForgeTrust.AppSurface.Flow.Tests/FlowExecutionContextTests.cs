namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowExecutionContextTests
{
    [Fact]
    public void Type_IsValueType()
    {
        Assert.True(typeof(FlowExecutionContext<TestState>).IsValueType);
    }

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

    [Fact]
    public void Default_InstanceHasUnpopulatedMembers()
    {
        var context = default(FlowExecutionContext<TestState>);

        Assert.Null(context.FlowId);
        Assert.Null(context.Version);
        Assert.Null(context.NodeId);
        Assert.Null(context.State);
        Assert.Null(context.ResumeEvent);
    }

    private sealed record TestState(string Status);
}
