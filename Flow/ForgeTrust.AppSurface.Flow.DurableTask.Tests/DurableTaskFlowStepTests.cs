namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

public sealed class DurableTaskFlowStepTests
{
    [Fact]
    public void Constructor_CapturesAllProperties()
    {
        var state = new TestState("waiting");
        var resumeEvent = new FlowResumeEvent("approved", "andrew");

        var step = new DurableTaskFlowStep<TestState>(
            "approval",
            "1",
            "instance-1",
            "review",
            state,
            resumeEvent);

        Assert.Equal("approval", step.FlowId);
        Assert.Equal("1", step.Version);
        Assert.Equal("instance-1", step.InstanceId);
        Assert.Equal("review", step.NodeId);
        Assert.Same(state, step.Context);
        Assert.Same(resumeEvent, step.ResumeEvent);
    }

    [Theory]
    [InlineData("flowId")]
    [InlineData("version")]
    [InlineData("instanceId")]
    [InlineData("nodeId")]
    public void Constructor_WithEmptyRequiredText_ThrowsArgumentException(string parameterName)
    {
        var exception = Assert.Throws<ArgumentException>(() => parameterName switch
        {
            "flowId" => Step(flowId: " "),
            "version" => Step(version: " "),
            "instanceId" => Step(instanceId: " "),
            "nodeId" => Step(nodeId: " "),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullContext_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DurableTaskFlowStep<TestState>("approval", "1", "instance-1", "review", null!));
    }

    private static DurableTaskFlowStep<TestState> Step(
        string flowId = "approval",
        string version = "1",
        string instanceId = "instance-1",
        string nodeId = "review") =>
        new(flowId, version, instanceId, nodeId, new TestState("waiting"));

    private sealed record TestState(string Status);
}
