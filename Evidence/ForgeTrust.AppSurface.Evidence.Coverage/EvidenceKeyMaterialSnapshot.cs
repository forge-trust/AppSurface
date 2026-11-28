using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Owns a bounded, ordinally ordered copy of local producer key material.</summary>
/// <remarks>Only the validating factory can create a snapshot from caller-owned entries.</remarks>
internal sealed class EvidenceKeyMaterialSnapshot : ReadOnlyDictionary<string, string>
{
    internal const int MaximumKeys = 64;
    internal const int MaximumKeyLength = 128;
    internal const int MaximumValueBytes = 4 * 1024;
    internal const int MaximumTotalBytes = 256 * 1024;

    /// <summary>Gets the already-validated empty material for producers without a key-material callback.</summary>
    internal static EvidenceKeyMaterialSnapshot NoMaterial { get; } = new(new SortedDictionary<string, string>(StringComparer.Ordinal));

    private EvidenceKeyMaterialSnapshot(SortedDictionary<string, string> values) : base(values) { }

    /// <summary>Validates and captures a single enumeration of raw material, or reuses a previously validated snapshot.</summary>
    /// <param name="material">The caller's material map.</param>
    /// <param name="cancellationToken">Cancellation observed while inspecting entries and their Unicode text.</param>
    /// <returns>The immutable entries in the order used for producer-key hashing.</returns>
    /// <exception cref="EvidenceKeyMaterialValidationException">The map contains invalid, duplicate, or oversized entries.</exception>
    internal static EvidenceKeyMaterialSnapshot Capture(IReadOnlyDictionary<string, string>? material, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (material is EvidenceKeyMaterialSnapshot existing) return existing;
        if (material is null)
            throw new EvidenceKeyMaterialValidationException("The key-material callback returned no material map. Return a non-null map within the declared limits.");
        var reportedCount = material.Count;
        if (reportedCount > MaximumKeys)
            throw new EvidenceKeyMaterialValidationException(
                $"Key material contains {FormatNumber(reportedCount)} keys; limit is {FormatNumber(MaximumKeys)}. Remove keys and retry.");

        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var totalBytes = 0L;
        foreach (var (key, value) in material)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observedCount = values.Count + 1;
            if (observedCount > MaximumKeys)
                throw new EvidenceKeyMaterialValidationException(
                    $"More than {FormatNumber(MaximumKeys)} key-material entries were observed ({FormatNumber(observedCount)} observed). Remove keys and retry.");
            if (string.IsNullOrEmpty(key))
                throw new EvidenceKeyMaterialValidationException("A key-material key name has length 0; minimum is 1. Provide a non-empty key name.");
            if (key.Length > MaximumKeyLength)
                throw new EvidenceKeyMaterialValidationException(
                    $"A key-material key name is {FormatNumber(key.Length)} UTF-16 code units; limit is {FormatNumber(MaximumKeyLength)}. Shorten key names and retry.");
            if (!TryCountUtf8Bytes(key, cancellationToken, out var keyBytes))
                throw new EvidenceKeyMaterialValidationException("A key-material key name contains malformed UTF-16. Use well-formed Unicode text.");
            if (value is null)
                throw new EvidenceKeyMaterialValidationException("A key-material entry has a null value. Return non-null values within the declared limits.");
            if (!TryCountUtf8Bytes(value, cancellationToken, out var valueBytes))
                throw new EvidenceKeyMaterialValidationException("A key-material value contains malformed UTF-16. Use well-formed Unicode text.");
            if (values.ContainsKey(key))
                throw new EvidenceKeyMaterialValidationException("Key-material key names must be unique. Return a map with unique key names.");
            if (valueBytes > MaximumValueBytes)
                throw new EvidenceKeyMaterialValidationException(
                    $"A key-material value is {FormatNumber(valueBytes)} UTF-8 bytes; limit is {FormatNumber(MaximumValueBytes)}. Reduce the value and retry.");
            totalBytes += keyBytes + valueBytes;
            if (totalBytes > MaximumTotalBytes)
                throw new EvidenceKeyMaterialValidationException(
                    $"Key material totals {FormatNumber(totalBytes)} UTF-8 bytes; limit is {FormatNumber(MaximumTotalBytes)}. Reduce key and value sizes and retry.");
            values.Add(key, value);
        }
        return new EvidenceKeyMaterialSnapshot(values);
    }

    private static bool TryCountUtf8Bytes(string value, CancellationToken cancellationToken, out long bytes)
    {
        bytes = 0;
        for (var index = 0; index < value.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Rune.TryGetRuneAt(value, index, out var rune)) return false;
            bytes += rune.Utf8SequenceLength;
            index += rune.Utf16SequenceLength;
        }
        return true;
    }

    private static string FormatNumber(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Reports invalid producer key material without including any raw key or value.</summary>
/// <param name="diagnostic">The bounded, framework-generated validation detail.</param>
internal sealed class EvidenceKeyMaterialValidationException(string diagnostic) : Exception(diagnostic);
