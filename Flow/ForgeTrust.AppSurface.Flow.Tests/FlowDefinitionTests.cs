namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowDefinitionTests
{
    [Fact]
    public void Constructor_CopiesNodeDictionary()
    {
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = Descriptor("start", new CompleteNode()),
        };

        var definition = new FlowDefinition<TestState>("approval", "1", "start", nodes);
        nodes["late"] = Descriptor("late", new CompleteNode());

        Assert.DoesNotContain("late", definition.Nodes.Keys);
    }

    [Fact]
    public void Constructor_CopiesDescriptorNextNodeIds()
    {
        var nextNodeIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "finish",
        };
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = new("start", new CompleteNode(), nextNodeIds),
            ["finish"] = Descriptor("finish", new CompleteNode()),
        };

        var definition = new FlowDefinition<TestState>("approval", "1", "start", nodes);
        nextNodeIds.Add("late");

        Assert.DoesNotContain("late", definition.Nodes["start"].NextNodeIds);
    }

    [Fact]
    public void Build_ReturnsDefinitionThatDoesNotTrackBuilderMutation()
    {
        var builder = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new CompleteNode())
            .StartAt("start");

        var definition = builder.Build();
        builder.AddNode("late", new CompleteNode());

        Assert.DoesNotContain("late", definition.Nodes.Keys);
    }

    [Fact]
    public void Constructor_WithMissingStartNode_ThrowsFlowDefinitionException()
    {
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = Descriptor("start", new CompleteNode()),
        };

        var exception = Assert.Throws<FlowDefinitionException>(() =>
            new FlowDefinition<TestState>("approval", "1", "missing", nodes));

        Assert.Contains("start node 'missing' does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithMismatchedDescriptorKey_ThrowsFlowDefinitionException()
    {
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = Descriptor("other", new CompleteNode()),
        };

        var exception = Assert.Throws<FlowDefinitionException>(() =>
            new FlowDefinition<TestState>("approval", "1", "start", nodes));

        Assert.Contains("does not match descriptor node id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithMissingNextTarget_ThrowsFlowDefinitionException()
    {
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = Descriptor("start", new CompleteNode(), "missing"),
        };

        var exception = Assert.Throws<FlowDefinitionException>(() =>
            new FlowDefinition<TestState>("approval", "1", "start", nodes));

        Assert.Contains("targets missing node 'missing'", exception.Message, StringComparison.Ordinal);
    }

    private static FlowNodeDescriptor<TestState> Descriptor(
        string nodeId,
        IFlowNode<TestState> node,
        params string[] nextNodeIds) =>
        new(nodeId, node, new HashSet<string>(nextNodeIds, StringComparer.Ordinal));

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
