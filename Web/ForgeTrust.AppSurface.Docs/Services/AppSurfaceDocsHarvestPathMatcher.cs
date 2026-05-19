using Microsoft.Extensions.FileSystemGlobbing;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Evaluates normalized repository-relative paths against ordered harvest glob patterns.
/// </summary>
/// <remarks>
/// Matching is case-insensitive. <see cref="MatchFirst"/> is order-dependent and returns the first configured pattern
/// that matches the candidate. Directory helpers are conservative because pruning must never skip a file that a later
/// allow pattern could include; callers should validate and normalize paths before invoking the matcher.
/// </remarks>
internal sealed class AppSurfaceDocsHarvestPathMatcher
{
    private readonly PatternMatcher[] _matchers;

    public AppSurfaceDocsHarvestPathMatcher(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        _matchers = patterns
            .Select(pattern => new PatternMatcher(pattern))
            .ToArray();
    }

    /// <summary>
    /// Gets a value indicating whether any patterns were configured.
    /// </summary>
    public bool HasPatterns => _matchers.Length > 0;

    /// <summary>
    /// Returns the first configured pattern that matches <paramref name="relativePath"/>, or null when none match.
    /// </summary>
    public string? MatchFirst(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        return _matchers.FirstOrDefault(matcher => matcher.Matches(relativePath))?.Pattern;
    }

    /// <summary>
    /// Returns the first <c>/**</c> subtree pattern that matches <paramref name="relativeDirectory"/> itself.
    /// </summary>
    /// <remarks>
    /// The matcher probes a sentinel child path under the directory. This keeps clear subtree excludes fast, but patterns
    /// that do not end in <c>/**</c> are ignored because they may describe only files or a narrower leaf match.
    /// </remarks>
    public string? MatchDirectorySubtree(string relativeDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        var sentinelPath = $"{relativeDirectory.TrimEnd('/')}/_";
        return _matchers
            .Where(matcher => matcher.Pattern.EndsWith("/**", StringComparison.Ordinal))
            .FirstOrDefault(matcher => matcher.Matches(sentinelPath))?.Pattern;
    }

    /// <summary>
    /// Returns the first <c>/**</c> pattern that could match the directory or one of its descendants.
    /// </summary>
    public string? MatchDirectoryOrDescendantSubtree(string relativeDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        var normalizedDirectory = relativeDirectory.TrimEnd('/');
        return _matchers
            .Where(matcher => matcher.Pattern.EndsWith("/**", StringComparison.Ordinal))
            .FirstOrDefault(matcher => matcher.MatchesDirectoryOrDescendant(normalizedDirectory))?.Pattern;
    }

    /// <summary>
    /// Returns the first pattern that could match a file inside <paramref name="relativeDirectory"/> or a descendant.
    /// </summary>
    /// <remarks>
    /// This is intentionally broader than <see cref="MatchDirectoryOrDescendantSubtree"/> so file-level allow globs
    /// such as <c>.github/workflows/*.yml</c> keep the containing default-excluded directories enumerable.
    /// </remarks>
    public string? MatchDirectoryOrDescendant(string relativeDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        var normalizedDirectory = relativeDirectory.TrimEnd('/');
        return _matchers
            .FirstOrDefault(matcher => matcher.CouldMatchDirectoryOrDescendant(normalizedDirectory))?.Pattern;
    }

    /// <summary>
    /// Wraps a single configured glob pattern and exposes case-insensitive match helpers.
    /// </summary>
    private sealed class PatternMatcher
    {
        private readonly Matcher _matcher = new(StringComparison.OrdinalIgnoreCase);

        public PatternMatcher(string pattern)
        {
            Pattern = pattern;
            _matcher.AddInclude(pattern);
        }

        /// <summary>
        /// Gets the original configured pattern.
        /// </summary>
        public string Pattern { get; }

        /// <summary>
        /// Returns whether <paramref name="relativePath"/> matches <see cref="Pattern"/>.
        /// </summary>
        public bool Matches(string relativePath)
        {
            return _matcher.Match(relativePath).HasMatches;
        }

        /// <summary>
        /// Returns whether <see cref="Pattern"/> can apply to the directory or any descendant path.
        /// </summary>
        public bool MatchesDirectoryOrDescendant(string relativeDirectory)
        {
            if (Matches($"{relativeDirectory}/_"))
            {
                return true;
            }

            var subtreeRoot = Pattern[..^3].TrimEnd('/');
            return !HasGlobSyntax(subtreeRoot)
                   && (subtreeRoot.Equals(relativeDirectory, StringComparison.OrdinalIgnoreCase)
                       || subtreeRoot.StartsWith($"{relativeDirectory}/", StringComparison.OrdinalIgnoreCase)
                       || relativeDirectory.StartsWith($"{subtreeRoot}/", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns whether <see cref="Pattern"/> could match any file under <paramref name="relativeDirectory"/>.
        /// </summary>
        public bool CouldMatchDirectoryOrDescendant(string relativeDirectory)
        {
            if (Matches($"{relativeDirectory}/_"))
            {
                return true;
            }

            var literalDirectoryPrefix = GetLiteralDirectoryPrefix(Pattern);
            if (literalDirectoryPrefix.Length == 0)
            {
                return Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() is { } firstSegment
                       && HasGlobSyntax(firstSegment);
            }

            return literalDirectoryPrefix.Equals(relativeDirectory, StringComparison.OrdinalIgnoreCase)
                   || literalDirectoryPrefix.StartsWith($"{relativeDirectory}/", StringComparison.OrdinalIgnoreCase)
                   || relativeDirectory.StartsWith($"{literalDirectoryPrefix}/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGlobSyntax(string pattern)
        {
            return pattern.IndexOfAny(['*', '?', '[', '{']) >= 0;
        }

        private static string GetLiteralDirectoryPrefix(string pattern)
        {
            var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var prefixSegments = new List<string>();

            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                if (HasGlobSyntax(segment) || index == segments.Length - 1)
                {
                    break;
                }

                prefixSegments.Add(segment);
            }

            return string.Join('/', prefixSegments);
        }
    }
}
