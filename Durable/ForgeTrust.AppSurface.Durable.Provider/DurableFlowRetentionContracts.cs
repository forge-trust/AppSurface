using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>Identifies one immutable, one-Flow retention manifest.</summary>
public readonly record struct DurableRetentionManifestId
{
    /// <summary>Initializes a retention manifest identifier.</summary>
    public DurableRetentionManifestId(string value)
    {
        Value = ProviderContractValidation.Require(value, nameof(value), 200);
    }

    /// <summary>Gets the opaque manifest identifier.</summary>
    public string Value { get; }

    /// <summary>Creates a cryptographically random manifest identifier.</summary>
    public static DurableRetentionManifestId New() => new(Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies the versioned canonical bytes represented by a SHA-256 digest.</summary>
/// <remarks>
/// This value identifies source correspondence only. It never claims that an application-owned external archive is
/// durable, available, encrypted, or legally sufficient.
/// </remarks>
public sealed record DurableRetentionDigest
{
    /// <summary>Initializes a canonical retention digest.</summary>
    public DurableRetentionDigest(string schemaId, string sha256)
    {
        SchemaId = ProviderContractValidation.Require(schemaId, nameof(schemaId), 200);
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 64 || sha256.Any(static value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Retention digests require exactly 64 lowercase hexadecimal SHA-256 characters.", nameof(sha256));
        }

        Sha256 = sha256;
    }

    /// <summary>Gets the versioned canonicalization schema.</summary>
    public string SchemaId { get; }

    /// <summary>Gets the lowercase SHA-256 digest.</summary>
    public string Sha256 { get; }
}

/// <summary>Classifies the safety of removing one exact Flow history source set.</summary>
public enum DurableRetentionAssessmentStatus
{
    /// <summary>The source is a bounded terminal closure that can be frozen in a manifest.</summary>
    Safe = 0,
    /// <summary>A known dependency or supported behavior prevents retention.</summary>
    Blocked = 1,
    /// <summary>The provider cannot prove the candidate closure under the current protocol.</summary>
    Indeterminate = 2,
}

/// <summary>Provides a privacy-safe, deterministic reason for a retention assessment.</summary>
public enum DurableRetentionAssessmentReason
{
    /// <summary>The assessed closure is safe to freeze.</summary>
    Safe = 0,
    /// <summary>The Flow was not visible in the trusted scope.</summary>
    FlowNotFound = 1,
    /// <summary>The Flow has not reached a supported terminal state.</summary>
    FlowNotTerminal = 2,
    /// <summary>The Flow remains suspended and requires an explicit compatible repair.</summary>
    RepairRequired = 3,
    /// <summary>An active wait, timer, or dispatch remains online.</summary>
    ActiveFlowDependency = 4,
    /// <summary>A referenced child Work aggregate is not terminal.</summary>
    ActiveChildWork = 5,
    /// <summary>The candidate has a dependency not understood by this closure version.</summary>
    UnknownDependency = 6,
    /// <summary>The candidate exceeds the caller's bounded closure inventory.</summary>
    ClosureLimitExceeded = 7,
    /// <summary>The candidate's canonical archive representation exceeds the caller's byte bound.</summary>
    ArchiveLimitExceeded = 8,
    /// <summary>The source changed after an assessment or manifest was created.</summary>
    SourceChanged = 9,
    /// <summary>The installed provider cannot interpret the recorded Flow protocol facts.</summary>
    ProtocolUnsupported = 10,
}

/// <summary>Requests a bounded, non-mutating assessment of one Flow retention closure.</summary>
/// <remarks>
/// Assessment never archives, changes state, or deletes. It applies no universal age threshold: applications own the
/// policy that decides which terminal Flow to assess.
/// </remarks>
public sealed record DurableRetentionAssessmentRequest
{
    /// <summary>Initializes a bounded assessment request.</summary>
    public DurableRetentionAssessmentRequest(
        DurableScopeId scopeId,
        DurableFlowInstanceId flowInstanceId,
        int maximumClosureItems = 10_000,
        int maximumArchiveBytes = 64 * 1024 * 1024)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(flowInstanceId.Value, nameof(flowInstanceId), 200);
        if (maximumClosureItems is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumClosureItems));
        }

        if (maximumArchiveBytes is < 1 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArchiveBytes));
        }

        ScopeId = scopeId;
        FlowInstanceId = flowInstanceId;
        MaximumClosureItems = maximumClosureItems;
        MaximumArchiveBytes = maximumArchiveBytes;
    }

    /// <summary>Gets the application-authorized owner scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the one Flow candidate.</summary>
    public DurableFlowInstanceId FlowInstanceId { get; }

    /// <summary>Gets the maximum inventory count, from one through 10,000.</summary>
    public int MaximumClosureItems { get; }

    /// <summary>Gets the maximum canonical archive size, from one byte through 64 MiB.</summary>
    public int MaximumArchiveBytes { get; }
}

/// <summary>Reports a bounded Flow retention decision and its reproducible source facts.</summary>
public sealed record DurableRetentionAssessment
{
    /// <summary>Initializes a retention assessment.</summary>
    public DurableRetentionAssessment(
        DurableScopeId scopeId,
        DurableFlowInstanceId flowInstanceId,
        DurableRetentionAssessmentStatus status,
        DurableRetentionAssessmentReason reason,
        DurableRetentionDigest? closureDigest,
        DurableRetentionDigest? sourceWatermark,
        int closureItemCount,
        long archiveByteCount)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(flowInstanceId.Value, nameof(flowInstanceId), 200);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (closureItemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(closureItemCount));
        }

        if (archiveByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(archiveByteCount));
        }

        var isSafe = status == DurableRetentionAssessmentStatus.Safe;
        if (isSafe != (reason == DurableRetentionAssessmentReason.Safe))
        {
            throw new ArgumentException("Only a safe assessment may use the Safe reason.", nameof(reason));
        }

        if (isSafe && closureItemCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(closureItemCount));
        }

        if (isSafe && (closureDigest is null || sourceWatermark is null))
        {
            throw new ArgumentException("A safe assessment requires an exact closure and source watermark.");
        }

        ScopeId = scopeId;
        FlowInstanceId = flowInstanceId;
        Status = status;
        Reason = reason;
        ClosureDigest = closureDigest;
        SourceWatermark = sourceWatermark;
        ClosureItemCount = closureItemCount;
        ArchiveByteCount = archiveByteCount;
    }

    /// <summary>Gets the assessed scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the assessed Flow.</summary>
    public DurableFlowInstanceId FlowInstanceId { get; }

    /// <summary>Gets whether the provider proved safety, found a blocker, or could not prove the closure.</summary>
    public DurableRetentionAssessmentStatus Status { get; }

    /// <summary>Gets the privacy-safe deterministic explanation.</summary>
    public DurableRetentionAssessmentReason Reason { get; }

    /// <summary>Gets the canonical closure digest when the source could be inventoried.</summary>
    public DurableRetentionDigest? ClosureDigest { get; }

    /// <summary>Gets the current source watermark when the source could be inventoried.</summary>
    public DurableRetentionDigest? SourceWatermark { get; }

    /// <summary>Gets the canonical inventory item count.</summary>
    public int ClosureItemCount { get; }

    /// <summary>Gets the exact canonical archive byte count when available.</summary>
    public long ArchiveByteCount { get; }
}

/// <summary>Represents the append-only lifecycle state projected for a frozen retention manifest.</summary>
public enum DurableRetentionManifestState
{
    /// <summary>The safe source set was frozen and awaits an archive receipt.</summary>
    Frozen = 0,
    /// <summary>An adopter asserted that it wrote the package to an external archive.</summary>
    ArchiveReceiptRecorded = 1,
    /// <summary>The package was proven to correspond to the current frozen source set.</summary>
    Verified = 2,
    /// <summary>A verified source is held and cannot be purged.</summary>
    Held = 3,
    /// <summary>The separately authorized purge completed.</summary>
    Purged = 4,
}

/// <summary>Reports immutable manifest facts together with its current lifecycle projection.</summary>
public sealed record DurableRetentionManifest
{
    /// <summary>Initializes a manifest projection.</summary>
    public DurableRetentionManifest(
        DurableRetentionManifestId manifestId,
        DurableScopeId scopeId,
        DurableFlowInstanceId flowInstanceId,
        DurableRetentionDigest closureDigest,
        DurableRetentionDigest sourceWatermark,
        int closureItemCount,
        long archiveByteCount,
        DurableRetentionManifestState state,
        long lifecycleSequence,
        DateTimeOffset createdAtUtc)
    {
        ProviderContractValidation.Require(manifestId.Value, nameof(manifestId), 200);
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(flowInstanceId.Value, nameof(flowInstanceId), 200);
        ArgumentNullException.ThrowIfNull(closureDigest);
        ArgumentNullException.ThrowIfNull(sourceWatermark);
        if (closureItemCount is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(closureItemCount));
        }

        if (archiveByteCount is < 0 or > 64L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(archiveByteCount));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lifecycleSequence);
        ManifestId = manifestId;
        ScopeId = scopeId;
        FlowInstanceId = flowInstanceId;
        ClosureDigest = closureDigest;
        SourceWatermark = sourceWatermark;
        ClosureItemCount = closureItemCount;
        ArchiveByteCount = archiveByteCount;
        State = state;
        LifecycleSequence = lifecycleSequence;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    /// <summary>Gets the immutable manifest identity.</summary>
    public DurableRetentionManifestId ManifestId { get; }

    /// <summary>Gets the owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the single Flow selected by this manifest.</summary>
    public DurableFlowInstanceId FlowInstanceId { get; }

    /// <summary>Gets the immutable canonical closure digest.</summary>
    public DurableRetentionDigest ClosureDigest { get; }

    /// <summary>Gets the immutable source watermark recorded when the manifest was created.</summary>
    public DurableRetentionDigest SourceWatermark { get; }

    /// <summary>Gets the frozen inventory count.</summary>
    public int ClosureItemCount { get; }

    /// <summary>Gets the frozen canonical archive size.</summary>
    public long ArchiveByteCount { get; }

    /// <summary>Gets the current event-derived lifecycle state.</summary>
    public DurableRetentionManifestState State { get; }

    /// <summary>Gets the compare-and-swap sequence for the projected lifecycle state.</summary>
    public long LifecycleSequence { get; }

    /// <summary>Gets when immutable manifest facts were recorded.</summary>
    public DateTimeOffset CreatedAtUtc { get; }
}

/// <summary>Requests creation of an immutable manifest from a successful bounded assessment.</summary>
public sealed record DurableRetentionManifestCreateRequest
{
    /// <summary>Initializes a manifest-create command.</summary>
    public DurableRetentionManifestCreateRequest(
        DurableCommandId commandId,
        DurableRetentionAssessment assessment)
    {
        ProviderContractValidation.Require(commandId, nameof(commandId));
        Assessment = assessment ?? throw new ArgumentNullException(nameof(assessment));
        if (assessment.Status != DurableRetentionAssessmentStatus.Safe)
        {
            throw new ArgumentException("Only a safe assessment can create a retention manifest.", nameof(assessment));
        }

        CommandId = commandId;
        Fingerprint = RetentionCommandFingerprints.Create(
            "appsurface.durable.flow.retention.manifest-create.v1",
            assessment.ScopeId.Value,
            assessment.FlowInstanceId.Value,
            assessment.ClosureDigest!.SchemaId,
            assessment.ClosureDigest.Sha256,
            assessment.SourceWatermark!.SchemaId,
            assessment.SourceWatermark.Sha256,
            assessment.ClosureItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            assessment.ArchiveByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Gets the idempotent command identity.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the successful assessment whose source facts must still match.</summary>
    public DurableRetentionAssessment Assessment { get; }

    /// <summary>Gets the versioned semantic command fingerprint.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>Identifies an idempotent manifest-create result.</summary>
public enum DurableRetentionManifestCreateOutcome
{
    /// <summary>A new immutable manifest was recorded.</summary>
    Created = 0,
    /// <summary>The same command was retried and returned its originally created manifest.</summary>
    Duplicate = 1,
}

/// <summary>Reports an immutable-manifest creation result.</summary>
public sealed record DurableRetentionManifestCreateResult
{
    /// <summary>Initializes a manifest-create result.</summary>
    public DurableRetentionManifestCreateResult(
        DurableRetentionManifestCreateOutcome outcome,
        DurableRetentionManifest manifest)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        Outcome = outcome;
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    }

    /// <summary>Gets whether this command created or replayed the manifest.</summary>
    public DurableRetentionManifestCreateOutcome Outcome { get; }

    /// <summary>Gets the immutable manifest and its current projection.</summary>
    public DurableRetentionManifest Manifest { get; }
}

/// <summary>Represents a content-addressed adopter assertion about a durable-flow archive package.</summary>
public sealed record DurableArchiveReceiptV1
{
    /// <summary>Initializes an archive receipt assertion.</summary>
    public DurableArchiveReceiptV1(
        string receiptId,
        DurableRetentionDigest packageDigest,
        DurableRetentionDigest closureDigest,
        int recordCount)
    {
        ReceiptId = ProviderContractValidation.Require(receiptId, nameof(receiptId), 200);
        PackageDigest = packageDigest ?? throw new ArgumentNullException(nameof(packageDigest));
        ClosureDigest = closureDigest ?? throw new ArgumentNullException(nameof(closureDigest));
        if (recordCount is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(recordCount));
        }

        RecordCount = recordCount;
    }

    /// <summary>Gets the adopter-owned opaque receipt identity; it is not a URI or external-storage claim.</summary>
    public string ReceiptId { get; }

    /// <summary>Gets the <c>DFA1</c> package digest claimed by the archive writer.</summary>
    public DurableRetentionDigest PackageDigest { get; }

    /// <summary>Gets the frozen closure digest named by the receipt.</summary>
    public DurableRetentionDigest ClosureDigest { get; }

    /// <summary>Gets the canonical package record count.</summary>
    public int RecordCount { get; }
}

/// <summary>Returns a reproducible <c>DFA1</c> archive package without writing to external storage.</summary>
public sealed record DurableArchivePackageV1
{
    /// <summary>Initializes a verified canonical archive package.</summary>
    public DurableArchivePackageV1(
        DurableRetentionManifest manifest,
        ReadOnlyMemory<byte> bytes,
        DurableRetentionDigest packageDigest,
        int recordCount)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        PackageDigest = packageDigest ?? throw new ArgumentNullException(nameof(packageDigest));
        if (bytes.Length is < 1 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        if (recordCount != manifest.ClosureItemCount)
        {
            throw new ArgumentException("The package record count must equal the frozen manifest inventory.", nameof(recordCount));
        }

        var copy = bytes.ToArray();
        var actual = Convert.ToHexStringLower(SHA256.HashData(copy));
        if (!string.Equals(actual, packageDigest.Sha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("The archive package digest does not match its canonical bytes.", nameof(packageDigest));
        }

        Bytes = copy;
        RecordCount = recordCount;
    }

    /// <summary>Gets the manifest whose exact source facts the package represents.</summary>
    public DurableRetentionManifest Manifest { get; }

    /// <summary>Gets the reproducible package bytes. The caller writes these bytes to application-owned storage.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Gets the package's canonical SHA-256 digest.</summary>
    public DurableRetentionDigest PackageDigest { get; }

    /// <summary>Gets the manifest-ordered record count.</summary>
    public int RecordCount { get; }
}

/// <summary>Identifies an idempotent lifecycle mutation result.</summary>
public enum DurableRetentionMutationOutcome
{
    /// <summary>The command appended a lifecycle event and changed the projection.</summary>
    Applied = 0,
    /// <summary>The exact completed command was replayed without appending another event.</summary>
    Duplicate = 1,
    /// <summary>A non-destructive command found a manifest that had already been purged.</summary>
    AlreadyPurged = 2,
}

/// <summary>Reports a successful lifecycle command.</summary>
public sealed record DurableRetentionMutationResult
{
    /// <summary>Initializes a lifecycle mutation result.</summary>
    public DurableRetentionMutationResult(
        DurableRetentionManifestId manifestId,
        DurableRetentionMutationOutcome outcome,
        DurableRetentionManifestState state,
        long lifecycleSequence)
    {
        ProviderContractValidation.Require(manifestId.Value, nameof(manifestId), 200);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lifecycleSequence);
        ManifestId = manifestId;
        Outcome = outcome;
        State = state;
        LifecycleSequence = lifecycleSequence;
    }

    /// <summary>Gets the affected manifest.</summary>
    public DurableRetentionManifestId ManifestId { get; }

    /// <summary>Gets whether the mutation was applied, duplicated, or found after purge.</summary>
    public DurableRetentionMutationOutcome Outcome { get; }

    /// <summary>Gets the projected lifecycle state after the command.</summary>
    public DurableRetentionManifestState State { get; }

    /// <summary>Gets the projected lifecycle sequence after the command.</summary>
    public long LifecycleSequence { get; }
}

/// <summary>Requests one idempotent, compare-and-swap retention lifecycle mutation.</summary>
public abstract record DurableRetentionMutationRequest
{
    /// <summary>Initializes an audited retention mutation.</summary>
    protected DurableRetentionMutationRequest(
        DurableScopeId scopeId,
        DurableRetentionManifestId manifestId,
        DurableCommandId commandId,
        string actorId,
        string reasonCode,
        long expectedLifecycleSequence,
        DurableCommandFingerprint fingerprint)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(manifestId.Value, nameof(manifestId), 200);
        ProviderContractValidation.Require(commandId, nameof(commandId));
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (expectedLifecycleSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLifecycleSequence));
        }

        ScopeId = scopeId;
        ManifestId = manifestId;
        CommandId = commandId;
        ActorId = ProviderContractValidation.Require(actorId, nameof(actorId), 200);
        ReasonCode = ProviderContractValidation.Require(reasonCode, nameof(reasonCode), 120);
        ExpectedLifecycleSequence = expectedLifecycleSequence;
        Fingerprint = fingerprint;
    }

    /// <summary>Gets the application-authorized scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the exact target manifest.</summary>
    public DurableRetentionManifestId ManifestId { get; }

    /// <summary>Gets the idempotent command identity.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the privacy-safe authorized actor identity.</summary>
    public string ActorId { get; }

    /// <summary>Gets the privacy-safe audit reason.</summary>
    public string ReasonCode { get; }

    /// <summary>Gets the required current lifecycle sequence.</summary>
    public long ExpectedLifecycleSequence { get; }

    /// <summary>Gets the versioned semantic fingerprint.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>Records an adopter assertion that it stored one <c>DFA1</c> package externally.</summary>
public sealed record DurableRetentionRecordArchiveReceiptRequest : DurableRetentionMutationRequest
{
    /// <summary>Initializes an archive-receipt command.</summary>
    public DurableRetentionRecordArchiveReceiptRequest(
        DurableScopeId scopeId,
        DurableRetentionManifestId manifestId,
        DurableCommandId commandId,
        string actorId,
        string reasonCode,
        long expectedLifecycleSequence,
        DurableArchiveReceiptV1 receipt)
        : base(
            scopeId,
            manifestId,
            commandId,
            actorId,
            reasonCode,
            expectedLifecycleSequence,
            CreateFingerprint(scopeId, manifestId, actorId, reasonCode, expectedLifecycleSequence, receipt))
    {
        Receipt = receipt;
    }

    private static DurableCommandFingerprint CreateFingerprint(
        DurableScopeId scopeId,
        DurableRetentionManifestId manifestId,
        string actorId,
        string reasonCode,
        long expectedLifecycleSequence,
        DurableArchiveReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return RetentionCommandFingerprints.Create(
            "appsurface.durable.flow.retention.archive-receipt.v1",
            scopeId.Value,
            manifestId.Value,
            actorId,
            reasonCode,
            expectedLifecycleSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            receipt.ReceiptId,
            receipt.PackageDigest.SchemaId,
            receipt.PackageDigest.Sha256,
            receipt.ClosureDigest.SchemaId,
            receipt.ClosureDigest.Sha256,
            receipt.RecordCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Gets the opaque external-archive assertion to record.</summary>
    public DurableArchiveReceiptV1 Receipt { get; }
}

/// <summary>Requests source-correspondence verification for a recorded archive receipt.</summary>
public sealed record DurableRetentionVerifyArchiveRequest : DurableRetentionMutationRequest
{
    /// <summary>Initializes a source-correspondence verification command.</summary>
    public DurableRetentionVerifyArchiveRequest(
        DurableScopeId scopeId,
        DurableRetentionManifestId manifestId,
        DurableCommandId commandId,
        string actorId,
        string reasonCode,
        long expectedLifecycleSequence)
        : base(
            scopeId,
            manifestId,
            commandId,
            actorId,
            reasonCode,
            expectedLifecycleSequence,
            RetentionCommandFingerprints.Create(
            "appsurface.durable.flow.retention.verify.v1",
            scopeId.Value,
            manifestId.Value,
            actorId,
            reasonCode,
            expectedLifecycleSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)))
    {
    }
}

/// <summary>Requests an explicit hold placement or release on a verified retention manifest.</summary>
public sealed record DurableRetentionHoldRequest : DurableRetentionMutationRequest
{
    /// <summary>Initializes a hold command.</summary>
    public DurableRetentionHoldRequest(
        DurableScopeId scopeId,
        DurableRetentionManifestId manifestId,
        DurableCommandId commandId,
        string actorId,
        string reasonCode,
        long expectedLifecycleSequence,
        bool placeHold)
        : base(
            scopeId,
            manifestId,
            commandId,
            actorId,
            reasonCode,
            expectedLifecycleSequence,
            RetentionCommandFingerprints.Create(
                "appsurface.durable.flow.retention.hold.v1",
                scopeId.Value,
                manifestId.Value,
                actorId,
                reasonCode,
                expectedLifecycleSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                placeHold ? "place" : "release"))
    {
        PlaceHold = placeHold;
    }

    /// <summary>Gets whether this command places rather than releases the hold.</summary>
    public bool PlaceHold { get; }
}

/// <summary>Requests the separately authorized, irreversible purge of a verified and unheld manifest.</summary>
public sealed record DurableRetentionPurgeRequest : DurableRetentionMutationRequest
{
    /// <summary>Initializes a purge command.</summary>
    public DurableRetentionPurgeRequest(
        DurableScopeId scopeId,
        DurableRetentionManifestId manifestId,
        DurableCommandId commandId,
        string actorId,
        string reasonCode,
        long expectedLifecycleSequence)
        : base(
            scopeId,
            manifestId,
            commandId,
            actorId,
            reasonCode,
            expectedLifecycleSequence,
            RetentionCommandFingerprints.Create(
            "appsurface.durable.flow.retention.purge.v1",
            scopeId.Value,
            manifestId.Value,
            actorId,
            reasonCode,
            expectedLifecycleSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)))
    {
    }
}

/// <summary>Provides application-authorized verified retention lifecycle operations for one Flow at a time.</summary>
/// <remarks>
/// The application owns authorization, retention cadence, external archive transport, encryption, availability, and
/// compliance. This API never accepts a date-range delete, archive URI, arbitrary SQL, continuation token, or a
/// multi-Flow manifest. Verification proves only that the claimed package corresponds to the frozen source set.
/// </remarks>
public interface IDurableFlowRetentionClient
{
    /// <summary>Assesses one bounded Flow closure without mutating source rows.</summary>
    ValueTask<DurableOperationResult<DurableRetentionAssessment>> AssessAsync(
        DurableRetentionAssessmentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Freezes a still-matching successful assessment as an immutable manifest.</summary>
    ValueTask<DurableOperationResult<DurableRetentionManifestCreateResult>> CreateManifestAsync(
        DurableRetentionManifestCreateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one scope-isolated manifest lifecycle projection.</summary>
    ValueTask<DurableOperationResult<DurableRetentionManifest>> GetManifestAsync(
        DurableScopeId scopeId,
        DurableRetentionManifestId manifestId,
        CancellationToken cancellationToken = default);

    /// <summary>Builds a reproducible package from a still-matching immutable manifest without external I/O.</summary>
    ValueTask<DurableOperationResult<DurableArchivePackageV1>> BuildArchivePackageAsync(
        DurableScopeId scopeId,
        DurableRetentionManifestId manifestId,
        CancellationToken cancellationToken = default);

    /// <summary>Records an adopter-provided archive receipt; this does not verify external storage.</summary>
    ValueTask<DurableOperationResult<DurableRetentionMutationResult>> RecordArchiveReceiptAsync(
        DurableRetentionRecordArchiveReceiptRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Rebuilds the source package and verifies source correspondence with the receipt.</summary>
    ValueTask<DurableOperationResult<DurableRetentionMutationResult>> VerifyArchiveAsync(
        DurableRetentionVerifyArchiveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Places or releases an application-owned hold on a verified manifest.</summary>
    ValueTask<DurableOperationResult<DurableRetentionMutationResult>> SetHoldAsync(
        DurableRetentionHoldRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically records separate purge authorization and deletes only verified manifest-covered source rows.</summary>
    ValueTask<DurableOperationResult<DurableRetentionMutationResult>> PurgeAsync(
        DurableRetentionPurgeRequest request,
        CancellationToken cancellationToken = default);
}

internal static class RetentionCommandFingerprints
{
    internal static DurableCommandFingerprint Create(string schemaId, params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, schemaId);
        foreach (var value in values)
        {
            Append(hash, value);
        }

        return new DurableCommandFingerprint(schemaId, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
