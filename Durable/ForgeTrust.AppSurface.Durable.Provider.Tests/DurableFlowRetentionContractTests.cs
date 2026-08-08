using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

public sealed class DurableFlowRetentionContractTests
{
    private static readonly DurableScopeId Scope = new("retention-scope");
    private static readonly DurableFlowInstanceId Flow = new("retention-flow");
    private static readonly DurableCommandId Command = new("retention-command");
    private static readonly DurableRetentionDigest Closure = new(
        "durable-flow-closure-v1",
        "1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly DurableRetentionDigest Watermark = new(
        "durable-flow-watermark-v1",
        "2222222222222222222222222222222222222222222222222222222222222222");

    [Fact]
    public void Public_retention_enums_should_keep_stable_numeric_values()
    {
        Assert.Equal(0, (int)DurableRetentionAssessmentStatus.Safe);
        Assert.Equal(1, (int)DurableRetentionAssessmentStatus.Blocked);
        Assert.Equal(2, (int)DurableRetentionAssessmentStatus.Indeterminate);

        Assert.Equal(0, (int)DurableRetentionAssessmentReason.Safe);
        Assert.Equal(1, (int)DurableRetentionAssessmentReason.FlowNotFound);
        Assert.Equal(2, (int)DurableRetentionAssessmentReason.FlowNotTerminal);
        Assert.Equal(3, (int)DurableRetentionAssessmentReason.RepairRequired);
        Assert.Equal(4, (int)DurableRetentionAssessmentReason.ActiveFlowDependency);
        Assert.Equal(5, (int)DurableRetentionAssessmentReason.ActiveChildWork);
        Assert.Equal(6, (int)DurableRetentionAssessmentReason.UnknownDependency);
        Assert.Equal(7, (int)DurableRetentionAssessmentReason.ClosureLimitExceeded);
        Assert.Equal(8, (int)DurableRetentionAssessmentReason.ArchiveLimitExceeded);
        Assert.Equal(9, (int)DurableRetentionAssessmentReason.SourceChanged);
        Assert.Equal(10, (int)DurableRetentionAssessmentReason.ProtocolUnsupported);

        Assert.Equal(0, (int)DurableRetentionManifestState.Frozen);
        Assert.Equal(1, (int)DurableRetentionManifestState.ArchiveReceiptRecorded);
        Assert.Equal(2, (int)DurableRetentionManifestState.Verified);
        Assert.Equal(3, (int)DurableRetentionManifestState.Held);
        Assert.Equal(4, (int)DurableRetentionManifestState.Purged);

        Assert.Equal(0, (int)DurableRetentionManifestCreateOutcome.Created);
        Assert.Equal(1, (int)DurableRetentionManifestCreateOutcome.Duplicate);

        Assert.Equal(0, (int)DurableRetentionMutationOutcome.Applied);
        Assert.Equal(1, (int)DurableRetentionMutationOutcome.Duplicate);
        Assert.Equal(2, (int)DurableRetentionMutationOutcome.AlreadyPurged);
    }

    [Fact]
    public void Assessment_and_manifest_requests_are_bounded_and_fingerprinted()
    {
        var assessment = CreateSafeAssessment();
        var request = new DurableRetentionAssessmentRequest(Scope, Flow, 32, 1024);
        var create = new DurableRetentionManifestCreateRequest(Command, assessment);
        var replay = new DurableRetentionManifestCreateRequest(new DurableCommandId("retention-replay"), assessment);
        var changed = new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-changed"),
            new DurableRetentionAssessment(
                Scope,
                Flow,
                DurableRetentionAssessmentStatus.Safe,
                DurableRetentionAssessmentReason.Safe,
                Closure,
                Watermark,
                2,
                129));

        Assert.Equal(Scope, request.ScopeId);
        Assert.Equal(Flow, request.FlowInstanceId);
        Assert.Equal(32, request.MaximumClosureItems);
        Assert.Equal(1024, request.MaximumArchiveBytes);
        Assert.Equal("appsurface.durable.flow.retention.manifest-create.v1", create.Fingerprint.SchemaId);
        Assert.Equal(DurableCommandFingerprintMatch.Exact, create.Fingerprint.Compare(replay.Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, create.Fingerprint.Compare(changed.Fingerprint));
        Assert.Equal(2, create.Assessment.ClosureItemCount);
    }

    [Fact]
    public void Assessment_and_manifest_contracts_reject_unsafe_or_unbounded_values()
    {
        Assert.Throws<ArgumentException>(() => new DurableRetentionManifestId("bad value"));
        Assert.Throws<ArgumentException>(() => new DurableRetentionDigest("schema", new string('A', 64)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionAssessmentRequest(Scope, Flow, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionAssessmentRequest(Scope, Flow, 10_001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionAssessmentRequest(Scope, Flow, maximumArchiveBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionAssessment(
            Scope,
            Flow,
            DurableRetentionAssessmentStatus.Safe,
            DurableRetentionAssessmentReason.Safe,
            Closure,
            Watermark,
            0,
            0));
        Assert.Throws<ArgumentException>(() => new DurableRetentionAssessment(
            Scope,
            Flow,
            DurableRetentionAssessmentStatus.Blocked,
            DurableRetentionAssessmentReason.Safe,
            null,
            null,
            0,
            0));
        Assert.Throws<ArgumentException>(() => new DurableRetentionManifestCreateRequest(
            Command,
            new DurableRetentionAssessment(
                Scope,
                Flow,
                DurableRetentionAssessmentStatus.Blocked,
                DurableRetentionAssessmentReason.FlowNotTerminal,
                null,
                null,
                0,
                0)));
    }

    [Fact]
    public void Archive_receipt_package_and_mutations_preserve_exact_lifecycle_fingerprints()
    {
        var manifest = new DurableRetentionManifest(
            new DurableRetentionManifestId("retention-manifest"),
            Scope,
            Flow,
            Closure,
            Watermark,
            2,
            4,
            DurableRetentionManifestState.Frozen,
            1,
            DateTimeOffset.UtcNow);
        var bytes = "test"u8.ToArray();
        var packageDigest = new DurableRetentionDigest(
            "durable-flow-archive-v1",
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
        var package = new DurableArchivePackageV1(manifest, bytes, packageDigest, 2);
        var receipt = new DurableArchiveReceiptV1("archive-receipt", packageDigest, Closure, 2);
        var record = new DurableRetentionRecordArchiveReceiptRequest(
            Scope, manifest.ManifestId, Command, "operator", "archive", 1, receipt);
        var replay = new DurableRetentionRecordArchiveReceiptRequest(
            Scope, manifest.ManifestId, new DurableCommandId("retention-replay"), "operator", "archive", 1, receipt);
        var hold = new DurableRetentionHoldRequest(
            Scope, manifest.ManifestId, new DurableCommandId("retention-hold"), "operator", "hold", 2, true);
        var purge = new DurableRetentionPurgeRequest(
            Scope, manifest.ManifestId, new DurableCommandId("retention-purge"), "operator", "purge", 3);

        Assert.Equal(bytes, package.Bytes.ToArray());
        Assert.Equal(packageDigest, package.PackageDigest);
        Assert.Equal(DurableCommandFingerprintMatch.Exact, record.Fingerprint.Compare(replay.Fingerprint));
        Assert.Equal("appsurface.durable.flow.retention.hold.v1", hold.Fingerprint.SchemaId);
        Assert.Equal("appsurface.durable.flow.retention.purge.v1", purge.Fingerprint.SchemaId);
        Assert.Throws<ArgumentException>(() => new DurableArchivePackageV1(
            manifest,
            bytes,
            new DurableRetentionDigest("durable-flow-archive-v1", new string('0', 64)),
            2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionPurgeRequest(
            Scope, manifest.ManifestId, Command, "operator", "purge", 0));
    }

    [Fact]
    public void Retention_contracts_reject_invalid_result_and_archive_shapes()
    {
        var manifest = CreateManifest();
        var packageDigest = new DurableRetentionDigest(
            "durable-flow-archive-v1",
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
        var bytes = "test"u8.ToArray();
        var receipt = new DurableArchiveReceiptV1("archive-receipt", packageDigest, Closure, manifest.ClosureItemCount);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionAssessment(
            Scope, Flow, (DurableRetentionAssessmentStatus)99, DurableRetentionAssessmentReason.Safe, Closure, Watermark, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionAssessment(
            Scope, Flow, DurableRetentionAssessmentStatus.Blocked, (DurableRetentionAssessmentReason)99, null, null, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionAssessment(
            Scope, Flow, DurableRetentionAssessmentStatus.Blocked, DurableRetentionAssessmentReason.FlowNotTerminal, null, null, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionAssessment(
            Scope, Flow, DurableRetentionAssessmentStatus.Blocked, DurableRetentionAssessmentReason.FlowNotTerminal, null, null, 0, -1));
        Assert.Throws<ArgumentException>(() => new DurableRetentionAssessment(
            Scope, Flow, DurableRetentionAssessmentStatus.Safe, DurableRetentionAssessmentReason.Safe, null, Watermark, 1, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionManifest(
            manifest.ManifestId, Scope, Flow, Closure, Watermark, 0, 1, DurableRetentionManifestState.Frozen, 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionManifest(
            manifest.ManifestId, Scope, Flow, Closure, Watermark, 1, (64L * 1024 * 1024) + 1, DurableRetentionManifestState.Frozen, 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionManifest(
            manifest.ManifestId, Scope, Flow, Closure, Watermark, 1, 1, (DurableRetentionManifestState)99, 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionManifest(
            manifest.ManifestId, Scope, Flow, Closure, Watermark, 1, 1, DurableRetentionManifestState.Frozen, 0, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionManifestCreateResult((DurableRetentionManifestCreateOutcome)99, manifest));
        Assert.Throws<ArgumentNullException>(() => new DurableRetentionManifestCreateResult(DurableRetentionManifestCreateOutcome.Created, null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableArchiveReceiptV1("archive", packageDigest, Closure, 0));
        Assert.Throws<ArgumentNullException>(() => new DurableArchiveReceiptV1("archive", null!, Closure, 1));
        Assert.Throws<ArgumentNullException>(() => new DurableArchiveReceiptV1("archive", packageDigest, null!, 1));
        Assert.Throws<ArgumentNullException>(() => new DurableArchivePackageV1(null!, bytes, packageDigest, manifest.ClosureItemCount));
        Assert.Throws<ArgumentNullException>(() => new DurableArchivePackageV1(manifest, bytes, null!, manifest.ClosureItemCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableArchivePackageV1(manifest, ReadOnlyMemory<byte>.Empty, packageDigest, manifest.ClosureItemCount));
        Assert.Throws<ArgumentException>(() => new DurableArchivePackageV1(manifest, bytes, packageDigest, manifest.ClosureItemCount + 1));
        Assert.Throws<ArgumentException>(() => new DurableArchivePackageV1(
            manifest,
            bytes,
            new DurableRetentionDigest("durable-flow-archive-v1", new string('0', 64)),
            manifest.ClosureItemCount));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionMutationResult(
            manifest.ManifestId, (DurableRetentionMutationOutcome)99, DurableRetentionManifestState.Frozen, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionMutationResult(
            manifest.ManifestId, DurableRetentionMutationOutcome.Applied, (DurableRetentionManifestState)99, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionMutationResult(
            manifest.ManifestId, DurableRetentionMutationOutcome.Applied, DurableRetentionManifestState.Frozen, 0));
        Assert.Throws<ArgumentNullException>(() => new DurableRetentionRecordArchiveReceiptRequest(
            Scope, manifest.ManifestId, Command, "operator", "archive", 1, null!));

        Assert.Equal(manifest, new DurableArchivePackageV1(manifest, bytes, packageDigest, manifest.ClosureItemCount).Manifest);
        Assert.Equal(manifest.ClosureItemCount, new DurableArchivePackageV1(manifest, bytes, packageDigest, manifest.ClosureItemCount).RecordCount);
        Assert.Equal(manifest.ClosureItemCount, receipt.RecordCount);
    }

    [Fact]
    public void Retention_contracts_expose_projections_and_reject_all_bounded_edges()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var manifest = new DurableRetentionManifest(
            new DurableRetentionManifestId("retention-projection-manifest"),
            Scope,
            Flow,
            Closure,
            Watermark,
            2,
            4,
            DurableRetentionManifestState.Frozen,
            1,
            createdAt);
        var mutation = new DurableRetentionMutationResult(
            manifest.ManifestId,
            DurableRetentionMutationOutcome.Applied,
            DurableRetentionManifestState.Frozen,
            1);
        var packageDigest = new DurableRetentionDigest(
            "durable-flow-archive-v1",
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");

        Assert.Equal("retention-projection-manifest", manifest.ManifestId.ToString());
        Assert.Equal(createdAt, manifest.CreatedAtUtc);
        Assert.Equal(manifest.ManifestId, mutation.ManifestId);
        Assert.Throws<ArgumentException>(() => new DurableRetentionDigest("schema", new string('0', 63)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionAssessmentRequest(
            Scope,
            Flow,
            maximumArchiveBytes: (64 * 1024 * 1024) + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionManifest(
            manifest.ManifestId,
            Scope,
            Flow,
            Closure,
            Watermark,
            10_001,
            1,
            DurableRetentionManifestState.Frozen,
            1,
            createdAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRetentionManifest(
            manifest.ManifestId,
            Scope,
            Flow,
            Closure,
            Watermark,
            1,
            -1,
            DurableRetentionManifestState.Frozen,
            1,
            createdAt));
        Assert.Throws<ArgumentNullException>(() => new DurableRetentionManifestCreateRequest(Command, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableArchiveReceiptV1("receipt", packageDigest, Closure, 10_001));
    }

    private static DurableRetentionManifest CreateManifest() => new(
        new DurableRetentionManifestId("retention-manifest"),
        Scope,
        Flow,
        Closure,
        Watermark,
        2,
        4,
        DurableRetentionManifestState.Frozen,
        1,
        DateTimeOffset.UtcNow);

    private static DurableRetentionAssessment CreateSafeAssessment() => new(
        Scope,
        Flow,
        DurableRetentionAssessmentStatus.Safe,
        DurableRetentionAssessmentReason.Safe,
        Closure,
        Watermark,
        2,
        128);
}
