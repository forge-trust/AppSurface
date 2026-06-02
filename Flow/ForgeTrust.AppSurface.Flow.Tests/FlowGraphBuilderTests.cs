namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowGraphBuilderTests
{
    [Fact]
    public void Build_WithValidGraph_ReturnsDefinition()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval", "2026-05-31")
            .AddNode("start", new OutcomeNode(FlowNodeOutcome<TestState>.Next("finish", new TestState("next"))), "finish")
            .AddNode("finish", new OutcomeNode(FlowNodeOutcome<TestState>.Complete(new TestState("done"))))
            .StartAt("start")
            .Build();

        Assert.Equal("approval", definition.FlowId);
        Assert.Equal("2026-05-31", definition.Version);
        Assert.Equal("start", definition.StartNodeId);
        Assert.Equal(["finish", "start"], definition.Nodes.Keys.Order());
    }

    [Fact]
    public void AddNode_WithDuplicateId_ThrowsArgumentException()
    {
        var builder = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new OutcomeNode(FlowNodeOutcome<TestState>.Complete(new TestState("done"))));

        Assert.Throws<ArgumentException>(() => builder.AddNode("start", new OutcomeNode(FlowNodeOutcome<TestState>.Complete(new TestState("again")))));
    }

    [Fact]
    public void Build_WithoutStart_ThrowsFlowDefinitionException()
    {
        var builder = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new OutcomeNode(FlowNodeOutcome<TestState>.Complete(new TestState("done"))));

        var exception = Assert.Throws<FlowDefinitionException>(() => builder.Build());

        Assert.Contains("start node", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_WithMissingStartNode_ThrowsFlowDefinitionException()
    {
        var builder = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new OutcomeNode(FlowNodeOutcome<TestState>.Complete(new TestState("done"))))
            .StartAt("missing");

        var exception = Assert.Throws<FlowDefinitionException>(() => builder.Build());

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithMissingNextTarget_ThrowsFlowDefinitionException()
    {
        var builder = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new OutcomeNode(FlowNodeOutcome<TestState>.Next("missing", new TestState("next"))), "missing")
            .StartAt("start");

        var exception = Assert.Throws<FlowDefinitionException>(() => builder.Build());

        Assert.Contains("missing node 'missing'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WithEmptyFlowId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => FlowGraphBuilder<TestState>.Create(" "));
    }

    private sealed record TestState(string Value);

    private sealed class OutcomeNode : IFlowNode<TestState>
    {
        private readonly FlowNodeOutcome<TestState> _outcome;

        internal OutcomeNode(FlowNodeOutcome<TestState> outcome)
        {
            _outcome = outcome;
        }

        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_outcome);
    }
}
