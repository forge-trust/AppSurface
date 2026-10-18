using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation;

/// <summary>Represents the small, explicitly accepted demo Work payload.</summary>
/// <param name="Value">A bounded sample value persisted as application-approved data.</param>
internal sealed record DemoWork(string Value);

/// <summary>Represents the deterministic terminal result produced by the demo executor.</summary>
/// <param name="Value">The processed value.</param>
internal sealed record DemoWorkResult(string Value);

/// <summary>Provides source-generated JSON metadata for the registered demo Work contract.</summary>
[JsonSerializable(typeof(DemoWork))]
[JsonSerializable(typeof(DemoWorkResult))]
internal partial class DemoWorkJsonContext : JsonSerializerContext;

/// <summary>Executes one deterministic operation without performing an external side effect.</summary>
internal sealed class DemoWorkExecutor : IDurableWorkerExecutor<DemoWork, DemoWorkResult>
{
    /// <inheritdoc />
    public ValueTask<DemoWorkResult> ExecuteAsync(
        DurableWorkerEnvelope<DemoWork> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = work.Payload ?? throw new InvalidOperationException("The persisted demo Work payload is absent.");
        return ValueTask.FromResult(new DemoWorkResult($"processed:{payload.Value}"));
    }
}

/// <summary>Defines the immutable sample Work name, version, scope, and source-generated codecs.</summary>
internal static class DemoWorkContract
{
    /// <summary>Gets the single captured contract used by registration, CLI acceptance, and lifecycle proofs.</summary>
    internal static DurableWorkDefinition<DemoWork, DemoWorkResult> Definition { get; } = DurableWork.Define(
        WorkName,
        WorkVersion,
        CreateWorkCodec(),
        CreateResultCodec(),
        DurableProviderSafety.ProviderKeyed,
        DurableWorkRetryPolicy.Default);

    /// <summary>Gets the stable Work name registered by this host.</summary>
    internal const string WorkName = "external-activation-demo";

    /// <summary>Gets the immutable Work contract version.</summary>
    internal const string WorkVersion = "v1";

    /// <summary>Gets the trusted scope used by the demo acceptance CLI.</summary>
    internal const string Scope = "external-activation-demo";

    /// <summary>Creates the allowlisted durable input codec.</summary>
    internal static SystemTextJsonDurablePayloadCodec<DemoWork> CreateWorkCodec() => new(
        "external-activation.demo-work",
        "v1",
        DurableDataClassification.ApprovedApplication,
        DemoWorkJsonContext.Default.DemoWork,
        static work => !string.IsNullOrWhiteSpace(work.Value) && work.Value.Length <= 200,
        maximumBytes: 1024);

    /// <summary>Creates the allowlisted durable result codec.</summary>
    internal static SystemTextJsonDurablePayloadCodec<DemoWorkResult> CreateResultCodec() => new(
        "external-activation.demo-result",
        "v1",
        DurableDataClassification.ApprovedApplication,
        DemoWorkJsonContext.Default.DemoWorkResult,
        static result => !string.IsNullOrWhiteSpace(result.Value) && result.Value.Length <= 220,
        maximumBytes: 1024);
}
