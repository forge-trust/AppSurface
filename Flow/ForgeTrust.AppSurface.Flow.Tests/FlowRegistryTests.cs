namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowRegistryTests
{
    [Fact]
    public void RegisterAndGetRequired_ReturnsDefinition()
    {
        var registry = new FlowDefinitionRegistry();
        var definition = Definition();

        registry.Register(definition);

        Assert.Same(definition, registry.GetRequired<TestState>("approval", "1"));
    }

    [Fact]
    public void Register_DuplicateDefinition_ThrowsArgumentException()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(Definition());

        Assert.Throws<ArgumentException>(() => registry.Register(Definition()));
    }

    [Fact]
    public void TryGet_WithMissingDefinition_ReturnsFalse()
    {
        var registry = new FlowDefinitionRegistry();

        var found = registry.TryGet<TestState>("approval", "1", out var definition);

        Assert.False(found);
        Assert.Null(definition);
    }

    [Fact]
    public void GetRequired_WithMissingDefinition_ThrowsFlowDefinitionException()
    {
        var registry = new FlowDefinitionRegistry();

        var exception = Assert.Throws<FlowDefinitionException>(() => registry.GetRequired<TestState>("approval", "1"));

        Assert.Contains("not registered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FlowDefinition<TestState> Definition() =>
        FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new CompleteNode())
            .StartAt("start")
            .Build();

    private sealed record TestState(string Value);

    private sealed class CompleteNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(FlowNodeOutcome<TestState>.Complete(context.State));
    }
}
