using Microsoft.Extensions.FileSystemGlobbing;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Evaluates normalized repository-relative paths against ordered harvest glob patterns.
/// </summary>
/// <remarks>
/// Matching is case-insensitive. <see cref="MatchFirst"/> is order-dependent and returns the first configured pattern
/// that matches the candidate. Directory-subtree helpers are intentionally conservative and only reason about patterns
/// that end in <c>/**</c>; callers should validate and normalize paths before invoking the matcher.
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

        private static bool HasGlobSyntax(string pattern)
        {
            return pattern.IndexOfAny(['*', '?', '[', '{']) >= 0;
        }
    }
}
