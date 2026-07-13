using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;

internal static class DurableExampleContracts
{
    internal const string WorkName = "example.resource.cleanup";
    internal const string ManualResolutionWorkName = "example.resource.cleanup.manual";
    internal const string WorkVersion = "v1";
    internal const string RetentionPolicyId = "operations-30d";

    internal static IDurablePayloadCodec<ResourceCleanupWork> CreateWorkCodec() =>
        new SystemTextJsonDurablePayloadCodec<ResourceCleanupWork>(
            "example.resource-cleanup.work",
            "v1",
            DurableDataClassification.Operational,
            DurableExampleJsonContext.Default.ResourceCleanupWork,
            static value => IsSafeCode(value.ResourceCode),
            maximumBytes: 1_024,
            retentionPolicyId: RetentionPolicyId);

    internal static IDurablePayloadCodec<ResourceCleanupResult> CreateResultCodec() =>
        new SystemTextJsonDurablePayloadCodec<ResourceCleanupResult>(
            "example.resource-cleanup.result",
            "v1",
            DurableDataClassification.Operational,
            DurableExampleJsonContext.Default.ResourceCleanupResult,
            static value =>
                IsSafeCode(value.ResourceCode)
                && IsProviderKey(value.ProviderOperationId),
            maximumBytes: 1_024,
            retentionPolicyId: RetentionPolicyId);

    private static bool IsSafeCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsProviderKey(string? value) =>
        value is { Length: <= 128 }
        && value.StartsWith("asdur-v1-", StringComparison.Ordinal);
}

internal sealed record ResourceCleanupWork(string ResourceCode);

internal sealed record ResourceCleanupResult(string ResourceCode, string ProviderOperationId);

internal sealed class ResourceCleanupExecutor : IDurableWorkerExecutor<ResourceCleanupWork, ResourceCleanupResult>
{
    public ValueTask<ResourceCleanupResult> ExecuteAsync(
        DurableWorkerEnvelope<ResourceCleanupWork> work,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = work.Payload
            ?? throw new InvalidOperationException("The durable runtime did not supply the registered work payload.");
        var identity = work.ExecutionIdentity
            ?? throw new InvalidOperationException("The native durable runtime did not supply an execution identity.");

        if (string.Equals(payload.ResourceCode, "simulate-unknown", StringComparison.Ordinal))
        {
            throw new TimeoutException("The example simulated a lost provider response after the effect permit.");
        }

        // A real ProviderKeyed executor sends identity.ProviderKey as the provider's idempotency key.
        Console.WriteLine(
            $"Provider call for resource '{payload.ResourceCode}' uses key '{identity.ProviderKey}' "
            + $"on attempt {identity.AttemptNumber}.");

        return ValueTask.FromResult(new ResourceCleanupResult(payload.ResourceCode, identity.ProviderKey));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ResourceCleanupWork))]
[JsonSerializable(typeof(ResourceCleanupResult))]
internal partial class DurableExampleJsonContext : JsonSerializerContext
{
}
