using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;

/// <summary>Contains the deliberately tiny typed Work and Flow registrations used by the local tutorial.</summary>
internal static class DurableExampleContracts
{
    internal const string WorkName = "example.local-proof";
    internal const string WorkVersion = "v1";
    internal const string FlowId = "example.local-flow";
    internal const string FlowVersion = "v1";

    internal static IDurablePayloadCodec<LocalProofWork> CreateWorkCodec() =>
        new SystemTextJsonDurablePayloadCodec<LocalProofWork>(
            "example.local-proof.work",
            "v1",
            DurableDataClassification.Operational,
            DurableExampleJsonContext.Default.LocalProofWork,
            static value => !string.IsNullOrWhiteSpace(value.SafeCode) && value.SafeCode.Length <= 64,
            maximumBytes: 1_024,
            retentionPolicyId: "local-proof");

    internal static IDurablePayloadCodec<LocalProofResult> CreateResultCodec() =>
        new SystemTextJsonDurablePayloadCodec<LocalProofResult>(
            "example.local-proof.result",
            "v1",
            DurableDataClassification.Operational,
            DurableExampleJsonContext.Default.LocalProofResult,
            static value => !string.IsNullOrWhiteSpace(value.SafeCode) && value.SafeCode.Length <= 64,
            maximumBytes: 1_024,
            retentionPolicyId: "local-proof");

    internal static IDurablePayloadCodec<LocalProofFlowContext> CreateFlowCodec() =>
        new SystemTextJsonDurablePayloadCodec<LocalProofFlowContext>(
            "example.local-proof.flow-context",
            "v1",
            DurableDataClassification.Operational,
            DurableExampleJsonContext.Default.LocalProofFlowContext,
            static value => !string.IsNullOrWhiteSpace(value.SafeCode) && value.SafeCode.Length <= 64,
            maximumBytes: 1_024,
            retentionPolicyId: "local-proof");

    internal static FlowDefinition<LocalProofFlowContext> CreateFlowDefinition() =>
        FlowGraphBuilder<LocalProofFlowContext>
            .Create(FlowId, FlowVersion)
            .AddNode("complete", new LocalProofCompleteFlowNode())
            .StartAt("complete")
            .Build();
}

/// <summary>Represents a non-secret payload used only by the local tutorial's bounded Work proof.</summary>
internal sealed record LocalProofWork(string SafeCode);

/// <summary>Represents the terminal result of the local tutorial's bounded Work proof.</summary>
internal sealed record LocalProofResult(string SafeCode);

/// <summary>Represents the state of the tutorial's one-node Flow proof.</summary>
internal sealed record LocalProofFlowContext(string SafeCode);

/// <summary>Completes the tutorial Work without an external provider effect.</summary>
internal sealed class LocalProofExecutor : IDurableWorkerExecutor<LocalProofWork, LocalProofResult>
{
    /// <inheritdoc />
    public ValueTask<LocalProofResult> ExecuteAsync(
        DurableWorkerEnvelope<LocalProofWork> work,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new LocalProofResult(work.Payload?.SafeCode ?? "local-proof"));
    }
}

/// <summary>Completes the tutorial's one-node Flow deterministically.</summary>
internal sealed class LocalProofCompleteFlowNode : IFlowNode<LocalProofFlowContext>
{
    /// <inheritdoc />
    public ValueTask<FlowNodeOutcome<LocalProofFlowContext>> ExecuteAsync(
        FlowExecutionContext<LocalProofFlowContext> context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<FlowNodeOutcome<LocalProofFlowContext>>(
            FlowNodeOutcome<LocalProofFlowContext>.Complete(context.State));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LocalProofWork))]
[JsonSerializable(typeof(LocalProofResult))]
[JsonSerializable(typeof(LocalProofFlowContext))]
internal partial class DurableExampleJsonContext : JsonSerializerContext;
