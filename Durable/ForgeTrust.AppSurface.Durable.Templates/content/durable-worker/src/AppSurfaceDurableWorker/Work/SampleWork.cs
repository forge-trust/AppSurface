using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;

namespace AppSurfaceDurableWorker.Work;

/// <summary>Input persisted by the starter's application-owned typed Work contract.</summary>
/// <param name="Value">A nonblank value of at most 200 characters.</param>
public sealed record SampleWork(string Value);

/// <summary>Deterministic result stored when the sample executor reaches a successful terminal state.</summary>
/// <param name="Value">The bounded processed value.</param>
public sealed record SampleWorkResult(string Value);

/// <summary>Source-generated JSON metadata used by the durable input and result codecs.</summary>
[JsonSerializable(typeof(SampleWork))]
[JsonSerializable(typeof(SampleWorkResult))]
internal partial class SampleWorkJsonContext : JsonSerializerContext;

/// <summary>Defines the immutable name, version, codecs, safety classification, and retry policy for the sample Work.</summary>
public static class SampleWorkDefinition
{
    /// <summary>Stable persisted Work name. Renaming it is a data-contract rollout decision.</summary>
    public const string WorkName = "appsurface.sample-work";

    /// <summary>Immutable contract version encoded with every accepted request.</summary>
    public const string WorkVersion = "v1";

    /// <summary>Application-owned scope used by the starter's producer example.</summary>
    public const string Scope = "appsurface-sample";

    /// <summary>Gets the one definition shared by registration and request construction.</summary>
    public static DurableWorkDefinition<SampleWork, SampleWorkResult> Definition { get; } = DurableWork.Define(
        WorkName,
        WorkVersion,
        CreateWorkCodec(),
        CreateResultCodec(),
        DurableProviderSafety.Idempotent,
        DurableWorkRetryPolicy.Default);

    /// <summary>Creates the bounded, source-generated JSON input codec used by this contract.</summary>
    /// <returns>A codec with the stable sample input identity and a 1 KiB encoded-payload limit.</returns>
    public static SystemTextJsonDurablePayloadCodec<SampleWork> CreateWorkCodec() => new(
        "appsurface.sample-work",
        WorkVersion,
        DurableDataClassification.ApprovedApplication,
        SampleWorkJsonContext.Default.SampleWork,
        static work => !string.IsNullOrWhiteSpace(work.Value) && work.Value.Length <= 200,
        maximumBytes: 1024);

    /// <summary>Creates the bounded, source-generated JSON result codec used by this contract.</summary>
    /// <returns>A codec with the stable sample result identity and a 1 KiB encoded-payload limit.</returns>
    public static SystemTextJsonDurablePayloadCodec<SampleWorkResult> CreateResultCodec() => new(
        "appsurface.sample-work-result",
        WorkVersion,
        DurableDataClassification.ApprovedApplication,
        SampleWorkJsonContext.Default.SampleWorkResult,
        static result => !string.IsNullOrWhiteSpace(result.Value) && result.Value.Length <= 220,
        maximumBytes: 1024);
}
