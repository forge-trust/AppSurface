using Microsoft.Extensions.FileSystemGlobbing;

namespace ForgeTrust.AppSurface.Docs.Services;

internal sealed class RazorDocsHarvestPathMatcher
{
    private readonly PatternMatcher[] _matchers;

    public RazorDocsHarvestPathMatcher(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        _matchers = patterns
            .Select(pattern => new PatternMatcher(pattern))
            .ToArray();
    }

    public bool HasPatterns => _matchers.Length > 0;

    public string? MatchFirst(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        return _matchers.FirstOrDefault(matcher => matcher.Matches(relativePath))?.Pattern;
    }

    public string? MatchDirectorySubtree(string relativeDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        var sentinelPath = $"{relativeDirectory.TrimEnd('/')}/_";
        foreach (var matcher in _matchers.Where(matcher => matcher.Pattern.EndsWith("/**", StringComparison.Ordinal)))
        {
            if (matcher.Matches(sentinelPath))
            {
                return matcher.Pattern;
            }
        }

        return null;
    }

    private sealed class PatternMatcher
    {
        private readonly Matcher _matcher = new(StringComparison.OrdinalIgnoreCase);

        public PatternMatcher(string pattern)
        {
            Pattern = pattern;
            _matcher.AddInclude(pattern);
        }

        public string Pattern { get; }

        public bool Matches(string relativePath)
        {
            return _matcher.Match(relativePath).HasMatches;
        }
    }
}
