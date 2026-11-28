using System.Buffers;
using System.Security.Cryptography;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Describes where an artifact stream stopped matching its recorded length.</summary>
internal enum EvidenceArtifactLengthStatus
{
    /// <summary>The stream supplied exactly the recorded bytes.</summary>
    Exact,

    /// <summary>The recorded length was negative or differed from the initial stream length.</summary>
    InitialMismatch,

    /// <summary>The stream ended before delivering the recorded bytes.</summary>
    Short,

    /// <summary>The stream supplied another byte after delivering the recorded bytes.</summary>
    Long,
}

/// <summary>Checks a caller-owned artifact stream and optionally copies the verified-length bytes to staging.</summary>
internal static class EvidenceArtifactStream
{
    private const int ChunkBytes = 64 * 1024;

    /// <summary>Hashes the recorded number of bytes, then verifies end of stream with a one-byte probe.</summary>
    /// <param name="input">A readable stream exposing its length, at the beginning of the artifact.</param>
    /// <param name="expectedLength">Recorded artifact length.</param>
    /// <param name="cancellationToken">Cancellation observed at each read boundary.</param>
    /// <param name="destination">Optional staging stream to receive the hashed bytes without taking ownership.</param>
    /// <returns>Length status and the lowercase SHA-256 digest when the length is exact.</returns>
    /// <remarks>A length mismatch or I/O failure may leave partial bytes in the caller's staging stream.
    /// The caller owns disposal, cleanup, and size limits specific to its artifact store.</remarks>
    internal static async Task<(EvidenceArtifactLengthStatus Status, string? Digest)> HashAsync(
        Stream input, long expectedLength, CancellationToken cancellationToken, Stream? destination = null)
    {
        if (expectedLength < 0 || input.Length != expectedLength)
            return (EvidenceArtifactLengthStatus.InitialMismatch, null);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            var remaining = expectedLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkBytes, remaining)), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0) return (EvidenceArtifactLengthStatus.Short, null);
                hash.AppendData(buffer, 0, count);
                if (destination is not null)
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remaining -= count;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var extra = await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return extra == 0
                ? (EvidenceArtifactLengthStatus.Exact, Convert.ToHexStringLower(hash.GetHashAndReset()))
                : (EvidenceArtifactLengthStatus.Long, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
