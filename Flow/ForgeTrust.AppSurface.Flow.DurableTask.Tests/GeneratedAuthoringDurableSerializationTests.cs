using ForgeTrust.AppSurface.Flow;

namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

[FlowAuthoring("generated-durable", Version = "2026-06-03")]
public partial class GeneratedDurableFlow
{
    [FlowStart]
    [FlowNode("start", typeof(GeneratedDurableStartState))]
    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(GeneratedDurableDoneState))]
    public partial class StartNode : IFlowTransformerNode<GeneratedDurableStartState, StartNodeOutcomes>
    {
        public ValueTask<StartNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<GeneratedDurableStartState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StartNodeOutcomes>(
                StartNodeOutcomes.Done(new GeneratedDurableDoneState(context.State.Value)));
    }
}

public sealed record GeneratedDurableStartState(string Value);

public sealed record GeneratedDurableDoneState(string Value);

public sealed class GeneratedAuthoringDurableSerializationTests
{
    [Fact]
    public void Validate_WithGeneratedEnvelope_Succeeds()
    {
        var validator = new FlowContextSerializationValidator(new SystemTextJsonFlowContextSerializer());

        var result = validator.Validate(
            GeneratedDurableFlow.CreateStartContext(new GeneratedDurableStartState("created")));

        Assert.True(result.Succeeded, result.Message);
    }
}
