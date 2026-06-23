namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowExecutionNodeTests
{
    [Fact]
    public void Constructor_WithNullDescriptor_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new FlowExecutionNode<TestState>(null!));
    }

    [Fact]
    public void SetNextNodes_WithNullNextNodes_ThrowsArgumentNullException()
    {
        var executionNode = new FlowExecutionNode<TestState>(
            new FlowNodeDescriptor<TestState>(
                "start",
                new CompleteNode(),
                new HashSet<string>(StringComparer.Ordinal)));

        Assert.Throws<ArgumentNullException>(() => executionNode.SetNextNodes(null!));
    }

    private sealed record TestState;

    private sealed class CompleteNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Complete(context.State));
    }
}
