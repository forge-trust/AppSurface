using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>Creates canonical versioned fingerprints for provider/operator mutation commands.</summary>
internal static class ProviderCommandFingerprints
{
    /// <summary>Hashes the ordered semantic fields of one Work operator command.</summary>
    /// <param name="schemaId">Versioned canonical encoding schema.</param>
    /// <param name="scopeId">Authorized scope identity.</param>
    /// <param name="workId">Target Work identity.</param>
    /// <param name="actorId">Authorized privacy-safe actor identity.</param>
    /// <param name="reasonCode">Privacy-safe audit reason.</param>
    /// <param name="expectedRevision">Expected aggregate revision.</param>
    /// <param name="resolution">Optional manual-resolution outcome.</param>
    /// <param name="result">Required encoded result only for an applied manual resolution.</param>
    /// <returns>A canonical fingerprint for persisted replay/conflict comparison.</returns>
    /// <remarks>Any change to field ordering or encoding requires a new <paramref name="schemaId"/>.</remarks>
    internal static DurableCommandFingerprint Create(
        string schemaId,
        DurableScopeId scopeId,
        DurableWorkId workId,
        string actorId,
        string reasonCode,
        long expectedRevision,
        DurableManualResolutionKind? resolution = null,
        DurableEncodedPayload? result = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, schemaId);
        Append(hash, scopeId.Value);
        Append(hash, workId.Value);
        Append(hash, actorId);
        Append(hash, reasonCode);
        Append(hash, expectedRevision);
        Append(hash, resolution.HasValue ? 1 : 0);
        if (resolution.HasValue)
        {
            Append(hash, (long)resolution.Value);
        }

        Append(hash, result is null ? 0 : 1);
        if (result is not null)
        {
            Append(hash, result.ContractName);
            Append(hash, result.ContractVersion);
            Append(hash, (long)result.Classification);
            Append(hash, result.RetentionPolicyId);
            Append(hash, result.Sha256);
        }

        return new DurableCommandFingerprint(schemaId, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>Hashes the ordered semantic fields of one evidence-backed Flow repair command.</summary>
    /// <param name="scopeId">Authorized scope that owns every referenced durable record.</param>
    /// <param name="instanceId">Target Flow instance identity.</param>
    /// <param name="expectedFlowRevision">Revision that must still match when the repair is applied.</param>
    /// <param name="expectedSuspensionDescriptorSha256">Digest of the expected V1 suspension descriptor.</param>
    /// <param name="action">Closed-set repair assertion that determines the fingerprint schema.</param>
    /// <param name="evidence">Payload-free child-Work evidence bound to the assertion.</param>
    /// <param name="actorId">Authorized, privacy-safe audit actor identity.</param>
    /// <param name="reasonCode">Privacy-safe audit reason.</param>
    /// <returns>A canonical fingerprint for persisted replay and collision comparison.</returns>
    /// <remarks>
    /// Command identity is deliberately excluded: it selects the idempotency record rather than its semantic content.
    /// Any change to field ordering or length-prefixed encoding requires a new action-specific schema identifier.
    /// </remarks>
    internal static DurableCommandFingerprint CreateFlowRepair(
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long expectedFlowRevision,
        string expectedSuspensionDescriptorSha256,
        DurableFlowRepairAction action,
        DurableFlowRepairEvidenceReference evidence,
        string actorId,
        string reasonCode)
    {
        var schemaId = GetFlowRepairSchemaId(action);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, schemaId);
        Append(hash, scopeId.Value);
        Append(hash, instanceId.Value);
        Append(hash, expectedFlowRevision);
        Append(hash, expectedSuspensionDescriptorSha256);
        Append(hash, (long)action);
        Append(hash, evidence.ChildWorkId.Value);
        Append(hash, evidence.ExpectedChildWorkRevision);
        Append(hash, evidence.ChildWorkHistoryEventId);
        AppendOptional(hash, evidence.ExpectedChildResultSha256);
        AppendOptional(hash, evidence.RequiredWorkOperatorCommandId?.Value);
        Append(hash, actorId);
        Append(hash, reasonCode);
        return new DurableCommandFingerprint(schemaId, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>Gets the versioned semantic fingerprint schema for one repair assertion.</summary>
    /// <param name="action">Closed-set repair assertion to map.</param>
    /// <returns>The action-specific fingerprint schema identifier.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="action"/> is undefined.</exception>
    internal static string GetFlowRepairSchemaId(DurableFlowRepairAction action) => action switch
    {
        DurableFlowRepairAction.AssertChildEffectCompleted => "appsurface.durable.flow.repair.completed.v1",
        DurableFlowRepairAction.AssertChildEffectNotApplied => "appsurface.durable.flow.repair.not-applied.v1",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Hashes the ordered fields of an accepted payload-free Flow repair receipt.</summary>
    /// <param name="scopeId">Authorized scope that owns the repair.</param>
    /// <param name="instanceId">Repaired Flow instance identity.</param>
    /// <param name="commandId">Stable repair command and receipt identity.</param>
    /// <param name="action">Accepted evidence-backed repair assertion.</param>
    /// <param name="requestFingerprint">Canonical semantic fingerprint of the request.</param>
    /// <param name="suspensionDescriptorSha256">Digest of the accepted V1 suspension descriptor.</param>
    /// <param name="evidence">Payload-free child-Work evidence accepted by the repair.</param>
    /// <param name="actorId">Privacy-safe audit actor identity.</param>
    /// <param name="reasonCode">Privacy-safe audit reason.</param>
    /// <param name="priorState">Flow state before the repair mutation.</param>
    /// <param name="priorRevision">Flow revision before the repair mutation.</param>
    /// <param name="resultingState">Flow state after the repair mutation.</param>
    /// <param name="resultingRevision">Flow revision after the repair mutation.</param>
    /// <param name="resultingFlowHistoryEventId">Append-only Flow history event created by the repair.</param>
    /// <param name="acceptedAtUtc">Accepted UTC instant, already normalized to PostgreSQL microsecond precision.</param>
    /// <returns>The lowercase hexadecimal SHA-256 receipt digest.</returns>
    /// <remarks>
    /// Field ordering and length-prefixed encoding are persisted V1 receipt semantics. Changing either requires a new
    /// receipt schema and migration rather than a silent hash change.
    /// </remarks>
    internal static string CreateFlowRepairReceipt(
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableCommandId commandId,
        DurableFlowRepairAction action,
        DurableCommandFingerprint requestFingerprint,
        string suspensionDescriptorSha256,
        DurableFlowRepairEvidenceReference evidence,
        string actorId,
        string reasonCode,
        DurableFlowState priorState,
        long priorRevision,
        DurableFlowState resultingState,
        long resultingRevision,
        long resultingFlowHistoryEventId,
        DateTimeOffset acceptedAtUtc)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "appsurface.durable.flow.repair.receipt.v1");
        Append(hash, scopeId.Value);
        Append(hash, instanceId.Value);
        Append(hash, commandId.Value);
        Append(hash, (long)action);
        Append(hash, requestFingerprint.SchemaId);
        Append(hash, requestFingerprint.Sha256);
        Append(hash, suspensionDescriptorSha256);
        Append(hash, evidence.ExpectedChildResultSha256 is null ? 1L : 0L);
        Append(hash, evidence.ChildWorkId.Value);
        Append(hash, evidence.ExpectedChildWorkRevision);
        Append(hash, evidence.ChildWorkHistoryEventId);
        AppendOptional(hash, evidence.ExpectedChildResultSha256);
        AppendOptional(hash, evidence.RequiredWorkOperatorCommandId?.Value);
        Append(hash, actorId);
        Append(hash, reasonCode);
        Append(hash, (long)priorState);
        Append(hash, priorRevision);
        Append(hash, (long)resultingState);
        Append(hash, resultingRevision);
        Append(hash, resultingFlowHistoryEventId);
        Append(hash, acceptedAtUtc.ToUniversalTime().Ticks);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendOptional(IncrementalHash hash, string? value)
    {
        Append(hash, value is null ? 0L : 1L);
        if (value is not null)
        {
            Append(hash, value);
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
