using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Web;

namespace NamedCanaryLab;

/// <summary>Identifies the candidate and environment to which application proof is bound.</summary>
internal sealed record CanaryProofIdentity(string Candidate, string Environment);

/// <summary>Stores only the bounded local facts required to evaluate a named canary.</summary>
internal sealed record CanaryProofRecord(
    CanaryProofIdentity Identity,
    string MarkerFingerprint,
    DateTimeOffset ObservedAt,
    AppSurfaceCanaryStatus Status);

/// <summary>Holds development-only proof records without persisting raw markers or payloads.</summary>
internal sealed class CanaryLabProofStore
{
    private readonly ConcurrentDictionary<string, CanaryProofRecord> _records = new(StringComparer.Ordinal);

    public CanaryProofRecord Record(CanaryProofRecord candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        while (true)
        {
            if (!_records.TryGetValue(candidate.MarkerFingerprint, out var existing))
            {
                if (_records.TryAdd(candidate.MarkerFingerprint, candidate))
                {
                    return candidate;
                }

                continue;
            }

            if (candidate.ObservedAt <= existing.ObservedAt)
            {
                return existing;
            }

            if (_records.TryUpdate(candidate.MarkerFingerprint, candidate, existing))
            {
                return candidate;
            }
        }
    }

    public bool TryRead(string markerFingerprint, out CanaryProofRecord proof) =>
        _records.TryGetValue(markerFingerprint, out proof!);
}

/// <summary>Creates internal lookup fingerprints without retaining opaque marker values.</summary>
internal static class CanaryLabMarkerFingerprint
{
    public static string Create(string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker))).ToLowerInvariant();
    }
}
