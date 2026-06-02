namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowNodeDescriptorTests
{
    [Fact]
    public void Constructor_CapturesNodeAndNextTargets()
    {
        var node = new CompleteNode();

        var descriptor = new FlowNodeDescriptor<TestState>(
            "review",
            node,
            new HashSet<string>(["approve"], StringComparer.Ordinal));

        Assert.Equal("review", descriptor.NodeId);
        Assert.Same(node, descriptor.Node);
        Assert.Contains("approve", descriptor.NextNodeIds);
    }

    [Fact]
    public void Constructor_CopiesNextTargets()
    {
        var nextNodeIds = new HashSet<string>(["approve"], StringComparer.Ordinal);

        var descriptor = new FlowNodeDescriptor<TestState>("review", new CompleteNode(), nextNodeIds);
        nextNodeIds.Add("late");

        Assert.DoesNotContain("late", descriptor.NextNodeIds);
    }

    [Theory]
    [InlineData("nodeId")]
    [InlineData("node")]
    [InlineData("nextNodeIds")]
    public void Constructor_WithInvalidRequiredValue_Throws(string parameterName)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => parameterName switch
        {
            "nodeId" => new FlowNodeDescriptor<TestState>(" ", new CompleteNode(), EmptyTargets()),
            "node" => new FlowNodeDescriptor<TestState>("review", null!, EmptyTargets()),
            "nextNodeIds" => new FlowNodeDescriptor<TestState>("review", new CompleteNode(), null!),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    private static IReadOnlySet<string> EmptyTargets() => new HashSet<string>(StringComparer.Ordinal);

    private sealed record TestState(string Value);

    private sealed class CompleteNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Complete(context.State));
    }
}
