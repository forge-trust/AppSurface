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
        Assert.Null(step.ActivityResult);
    }

    [Fact]
    public void Initializer_CapturesActivityResult()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email");
        var result = callsite.CreateResult(new TestResult("sent"));

        var step = new DurableTaskFlowStep<TestState>(
            "approval",
            "1",
            "instance-1",
            "review",
            new TestState("waiting"))
        {
            ActivityResult = result,
        };

        Assert.Same(result, step.ActivityResult);
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

    private sealed record TestWork(string ApprovalId);

    private sealed record TestResult(string Status);
}
