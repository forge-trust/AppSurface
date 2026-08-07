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
    /// <summary>Caps process-local records so an authenticated trigger cannot grow the sample's memory without bound.</summary>
    public const int MaximumRecordCount = 128;

    private readonly ConcurrentDictionary<string, CanaryProofRecord> _records = new(StringComparer.Ordinal);
    private int _recordSlotCount;

    /// <summary>
    /// Records the newest evidence for a marker, or returns <see langword="null"/> when a new marker would exceed the local bound.
    /// </summary>
    public CanaryProofRecord? Record(CanaryProofRecord candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        while (true)
        {
            if (!_records.TryGetValue(candidate.MarkerFingerprint, out var existing))
            {
                if (!TryReserveRecordSlot())
                {
                    if (_records.TryGetValue(candidate.MarkerFingerprint, out existing))
                    {
                        continue;
                    }

                    return null;
                }

                if (_records.TryAdd(candidate.MarkerFingerprint, candidate))
                {
                    return candidate;
                }

                Interlocked.Decrement(ref _recordSlotCount);
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

    private bool TryReserveRecordSlot()
    {
        while (true)
        {
            var currentCount = Volatile.Read(ref _recordSlotCount);
            if (currentCount >= MaximumRecordCount)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _recordSlotCount, currentCount + 1, currentCount) == currentCount)
            {
                return true;
            }
        }
    }
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
