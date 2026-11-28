using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Defines the path pattern grammar shared by producer input planning and inventory selection.</summary>
internal static class EvidenceProducerInputPattern
{
    private const string RecursiveWildcard = "**";

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
                || segment != RecursiveWildcard && segment.Contains(RecursiveWildcard, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>Checks whether a validated pattern matches the complete repository-relative path.</summary>
    /// <param name="pattern">A validated producer input pattern.</param>
    /// <param name="path">A forward-slash-delimited repository-relative path.</param>
    /// <param name="token">Cancellation during matching.</param>
    /// <returns>Whether the pattern consumes the entire path.</returns>
    internal static bool MatchesPath(string pattern, string path, CancellationToken token)
        => MatchPattern(pattern, path, allowPatternPrefix: false, token);

    /// <summary>Checks whether a validated pattern matches a path or could match content beneath it.</summary>
    /// <param name="pattern">A validated producer input pattern.</param>
    /// <param name="path">A forward-slash-delimited repository-relative path.</param>
    /// <param name="token">Cancellation during matching.</param>
    /// <returns>Whether the pattern can consume the path or a descendant of it.</returns>
    internal static bool MayMatchPathOrDescendant(string pattern, string path, CancellationToken token)
        => MatchPattern(pattern, path, allowPatternPrefix: true, token);

    private static bool MatchPattern(string pattern, string path, bool allowPatternPrefix, CancellationToken token)
    {
        var patternSegments = pattern.Split('/');
        var pathSegments = path.Split('/');
        var reachable = new bool[pathSegments.Length + 1];
        reachable[0] = true;
        var pathMatchedByPatternPrefix = false;
        foreach (var part in patternSegments)
        {
            token.ThrowIfCancellationRequested();
            var next = new bool[reachable.Length];
            if (part == RecursiveWildcard)
            {
                var reachablePrefix = false;
                for (var index = 0; index < reachable.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    reachablePrefix |= reachable[index];
                    next[index] = reachablePrefix;
                }
            }
            else
            {
                for (var index = 0; index < pathSegments.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    next[index + 1] = reachable[index] && SegmentMatches(part, pathSegments[index], token);
                }
            }
            reachable = next;
            pathMatchedByPatternPrefix |= allowPatternPrefix && reachable[^1];
        }

        return reachable[^1] || pathMatchedByPatternPrefix;
    }

    private static bool SegmentMatches(string pattern, string value, CancellationToken token)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var lastWildcardIndex = -1;
        var retryValueIndex = 0;
        while (valueIndex < value.Length)
        {
            token.ThrowIfCancellationRequested();
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == value[valueIndex] || pattern[patternIndex] == '*'))
            {
                if (pattern[patternIndex] == '*')
                {
                    lastWildcardIndex = patternIndex++;
                    retryValueIndex = valueIndex;
                }
                else
                {
                    patternIndex++;
                    valueIndex++;
                }
            }
            else if (lastWildcardIndex >= 0)
            {
                patternIndex = lastWildcardIndex + 1;
                valueIndex = ++retryValueIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }
}
