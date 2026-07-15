using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

public sealed class DurableProviderContractTests
{
    private static readonly DurableScopeId Scope = new("scope");
    private static readonly DurableWorkId Work = new("work");
    private static readonly DurableCommandId Command = new("command");

    [Fact]
    public void CommandFingerprint_operator_mutations_have_versioned_semantic_fingerprints()
    {
        var reconcile = new DurableWorkReconcileRequest(Scope, Work, Command, "operator", "reason", 1);
        var manual = new DurableWorkManualResolutionRequest(
            Scope, Work, Command, "operator", "reason", 1, DurableManualResolutionKind.ProvenNotApplied);
        var retry = new DurableWorkRetrySafeRequest(Scope, Work, Command, "operator", "reason", 1);
        var release = new DurableWorkRecoveryReleaseRequest(Scope, Work, Command, "operator", "reason", 1);

        Assert.Equal("appsurface.durable.work.reconcile.v1", reconcile.Fingerprint.SchemaId);
        Assert.Equal("appsurface.durable.work.manual-resolution.v1", manual.Fingerprint.SchemaId);
        Assert.Equal("appsurface.durable.work.retry-safe.v1", retry.Fingerprint.SchemaId);
        Assert.Equal("appsurface.durable.work.recovery-release.v1", release.Fingerprint.SchemaId);

        Assert.Equal(DurableCommandFingerprintMatch.Exact, reconcile.Fingerprint.Compare(
            new DurableWorkReconcileRequest(Scope, Work, new DurableCommandId("replay"), "operator", "reason", 1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, reconcile.Fingerprint.Compare(
            new DurableWorkReconcileRequest(Scope, Work, Command, "operator", "other", 1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Exact, manual.Fingerprint.Compare(
            new DurableWorkManualResolutionRequest(
                Scope, Work, new DurableCommandId("replay"), "operator", "reason", 1, DurableManualResolutionKind.ProvenNotApplied).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, manual.Fingerprint.Compare(
            new DurableWorkManualResolutionRequest(
                Scope, Work, Command, "other", "reason", 1, DurableManualResolutionKind.ProvenNotApplied).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Exact, retry.Fingerprint.Compare(
            new DurableWorkRetrySafeRequest(Scope, Work, new DurableCommandId("replay"), "operator", "reason", 1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, retry.Fingerprint.Compare(
            new DurableWorkRetrySafeRequest(Scope, Work, Command, "operator", "other", 1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Exact, release.Fingerprint.Compare(
            new DurableWorkRecoveryReleaseRequest(Scope, Work, new DurableCommandId("replay"), "operator", "reason", 1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, release.Fingerprint.Compare(
            new DurableWorkRecoveryReleaseRequest(Scope, Work, Command, "other", "reason", 1).Fingerprint));
    }

    [Fact]
    public void Provider_boundaries_reject_default_ids()
    {
        Assert.Throws<ArgumentException>(() => new DurableWorkGetRequest(default, Work));
        Assert.Throws<ArgumentException>(() => new DurableWorkReconcileRequest(
            Scope, Work, default, "operator", "reason", 1));
        Assert.Throws<ArgumentException>(() => new DurableClaimedWork(
            Scope,
            default,
            "activity",
            "work",
            "v1",
            new DurableEncodedPayload("payload", "v1", DurableDataClassification.Operational, Array.Empty<byte>()),
            DurableProviderSafety.Idempotent,
            1,
            1,
            1,
            "epoch"));
    }

    [Fact]
    public void Work_list_result_defensively_copies_items()
    {
        var source = new List<DurableWorkListItem>();
        var result = new DurableWorkListResult(source, null);

        source.Add(new DurableWorkListItem(
            Work,
            "activity",
            "work",
            "v1",
            DurableWorkState.Ready,
            DurableProviderSafety.Idempotent,
            0,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            false,
            false));

        Assert.Empty(result.Items);
    }

    [Fact]
    public void Claim_maps_to_stable_worker_identity()
    {
        var claim = new DurableClaimedWork(
            Scope,
            Work,
            "activity",
            "work",
            "v1",
            new DurableEncodedPayload("payload", "v1", DurableDataClassification.Operational, Array.Empty<byte>()),
            DurableProviderSafety.ProviderKeyed,
            2,
            3,
            4,
            "epoch");

        var context = claim.ToExecutionContext();

        Assert.Equal("activity", context.ExecutionIdentity.ActivityId);
        Assert.Equal("activity", context.ExecutionIdentity.ProviderKey);
        Assert.Equal(2, context.ExecutionIdentity.AttemptNumber);
    }
}
