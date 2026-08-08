using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Web;

namespace NamedCanaryLab;

/// <summary>Identifies the candidate and environment to which application proof is bound.</summary>
/// <param name="Candidate">Deployment candidate that produced the proof.</param>
/// <param name="Environment">Deployment environment that produced the proof.</param>
internal sealed record CanaryProofIdentity(string Candidate, string Environment);

/// <summary>Stores only the bounded local facts required to evaluate a named canary.</summary>
/// <param name="Identity">Candidate and environment to which the proof is bound.</param>
/// <param name="MarkerFingerprint">One-way lookup fingerprint for the opaque marker.</param>
/// <param name="ObservedAt">UTC instant when the local workflow observed the proof.</param>
/// <param name="Status">Outcome recorded by the local synthetic workflow.</param>
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
    /// Records evidence for a marker. A newer record replaces older evidence, an older record leaves the current one intact,
    /// and a new marker returns <see langword="null"/> when it would exceed the local bound.
    /// </summary>
    /// <param name="candidate">Candidate evidence to retain when its marker has capacity or a newer observation time.</param>
    /// <returns>The retained record, or <see langword="null"/> only when a new marker cannot be accommodated.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="candidate"/> is <see langword="null"/>.</exception>
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

    /// <summary>Reads the retained proof for a marker fingerprint without triggering new work.</summary>
    /// <param name="markerFingerprint">Fingerprint produced by <see cref="CanaryLabMarkerFingerprint.Create"/>.</param>
    /// <param name="proof">Retained proof when the fingerprint is present; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a proof is present; otherwise <see langword="false"/>.</returns>
    public bool TryRead(string markerFingerprint, [MaybeNullWhen(false)] out CanaryProofRecord proof) =>
        _records.TryGetValue(markerFingerprint, out proof);

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
    /// <summary>Creates a lowercase SHA-256 hexadecimal fingerprint for a nonblank opaque marker.</summary>
    /// <param name="marker">Opaque marker value to fingerprint without retaining it.</param>
    /// <returns>Lowercase hexadecimal SHA-256 fingerprint.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="marker"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public static string Create(string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(marker)));
    }
}
