using System.Text;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class DurableWorkRequestFingerprintTests
{
    [Fact]
    public void Compute_ExcludesTransportKeys_ButIncludesSemanticPayloadAndPolicy()
    {
        var original = Create("scope-a", "command-a", "retry-a", "payload", DurableProviderSafety.Idempotent);
        var transportRetry = Create("scope-b", "command-b", "retry-b", "payload", DurableProviderSafety.Idempotent);
        var changedPayload = Create("scope-a", "command-a", "retry-a", "changed", DurableProviderSafety.Idempotent);
        var changedSafety = Create("scope-a", "command-a", "retry-a", "payload", DurableProviderSafety.ManualResolution);

        Assert.Equal(
            DurableWorkRequestFingerprint.Compute(original),
            DurableWorkRequestFingerprint.Compute(transportRetry));
        Assert.NotEqual(
            DurableWorkRequestFingerprint.Compute(original),
            DurableWorkRequestFingerprint.Compute(changedPayload));
        Assert.NotEqual(
            DurableWorkRequestFingerprint.Compute(original),
            DurableWorkRequestFingerprint.Compute(changedSafety));
    }

    private static DurableWorkRequest Create(
        string scope,
        string command,
        string retryKey,
        string payload,
        DurableProviderSafety safety) =>
        new(
            new DurableScopeId(scope),
            new DurableCommandId(command),
            retryKey,
            "tests.fingerprint",
            "v1",
            new DurableEncodedPayload(
                "tests.fingerprint",
                "v1",
                DurableDataClassification.Operational,
                Encoding.UTF8.GetBytes(payload)),
            safety);
}
