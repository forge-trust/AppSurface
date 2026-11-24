using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Defines the path pattern grammar shared by producer input planning and inventory selection.</summary>
internal static class EvidenceProducerInputPattern
{
    /// <summary>Checks for a repository-relative pattern with <c>*</c> inside a segment and <c>**</c> as a whole segment.</summary>
    /// <param name="pattern">The declared input pattern.</param>
    /// <param name="cancellationToken">Cancellation for validation during input inventory.</param>
    /// <returns>Whether the pattern is valid.</returns>
    internal static bool IsValid(string? pattern, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(pattern) || pattern.StartsWith('/') || Path.IsPathRooted(pattern)
            || pattern.IndexOfAny(['\\', ':', '?', '[', ']', '{', '}']) >= 0)
            return false;

        for (var index = 0; index < pattern.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Rune.TryGetRuneAt(pattern, index, out var rune) || Rune.IsControl(rune))
                return false;
            index += rune.Utf16SequenceLength;
        }

        foreach (var segment in pattern.Split('/'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Length == 0 || segment is "." or ".."
                || segment != "**" && segment.Contains("**", StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
