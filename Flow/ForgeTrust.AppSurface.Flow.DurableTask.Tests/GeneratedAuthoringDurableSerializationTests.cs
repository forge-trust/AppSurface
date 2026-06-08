using ForgeTrust.AppSurface.Flow;

namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

/// <summary>
/// Public generated-authoring flow fixture used to validate Durable Task serialization compatibility.
/// </summary>
/// <remarks>
/// The fixture keeps the graph intentionally small: a single start node completes with a distinct output port.
/// That shape verifies that generated envelopes containing nullable concrete slots can round-trip through the
/// durable serializer before a host schedules real work.
/// </remarks>
[FlowAuthoring("generated-durable", Version = "2026-06-03")]
public partial class GeneratedDurableFlow
{
    /// <summary>
    /// Start node that copies the durable start value into a durable completion value.
    /// </summary>
    [FlowStart]
    [FlowNode("start", typeof(GeneratedDurableStartState))]
    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(GeneratedDurableDoneState))]
    public partial class StartNode : IFlowTransformerNode<GeneratedDurableStartState, StartNodeOutcomes>
    {
        /// <summary>
        /// Executes the durable serialization fixture node.
        /// </summary>
        /// <param name="context">Typed start state unwrapped from the generated envelope.</param>
        /// <param name="cancellationToken">Cancellation token supplied by the runner.</param>
        /// <returns>A generated completion outcome containing the copied value.</returns>
        public ValueTask<StartNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<GeneratedDurableStartState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StartNodeOutcomes>(
                StartNodeOutcomes.Done(new GeneratedDurableDoneState(context.State.Value)));
    }
}

/// <summary>
/// Serializable start state carried by the generated durable envelope.
/// </summary>
/// <param name="Value">Value copied into the completion state.</param>
public sealed record GeneratedDurableStartState(string Value);

/// <summary>
/// Serializable completion state carried by the generated durable envelope.
/// </summary>
/// <param name="Value">Value copied from the start state.</param>
public sealed record GeneratedDurableDoneState(string Value);

/// <summary>
/// Verifies generated envelope compatibility with durable context serialization validation.
/// </summary>
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

    [Fact]
    public void Validate_WhenSerializerThrows_ReturnsFailure()
    {
        var validator = new FlowContextSerializationValidator(new ThrowingSerializer());

        var result = validator.Validate(
            GeneratedDurableFlow.CreateStartContext(new GeneratedDurableStartState("created")));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Exception);
    }

    private sealed class ThrowingSerializer : IFlowContextSerializer
    {
        public string Serialize<TContext>(TContext context) => throw new InvalidOperationException("boom");

        public TContext Deserialize<TContext>(string payload) => throw new InvalidOperationException("boom");
    }
}
