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
