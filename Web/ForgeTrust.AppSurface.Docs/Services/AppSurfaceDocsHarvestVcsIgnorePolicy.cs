using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Docs.Models;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Services;

internal sealed class AppSurfaceDocsHarvestVcsIgnorePolicy
{
    private const int MaxSamples = 20;
    private readonly string _repositoryRoot;
    private readonly bool _enabled;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Lazy<IgnoreRuleSet>> _ruleSets = new(StringComparer.Ordinal);
    private readonly VcsIgnoreDiagnosticsCollector _diagnostics = new(MaxSamples);

    public AppSurfaceDocsHarvestVcsIgnorePolicy(
        string repositoryRoot,
        AppSurfaceDocsHarvestVcsIgnoreOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _enabled = options.Enabled;
        _logger = logger;
        AllowMatcher = new AppSurfaceDocsHarvestPathMatcher(options.AllowGlobs ?? []);
    }

    public AppSurfaceDocsHarvestPathMatcher AllowMatcher { get; }

    public AppSurfaceDocsHarvestVcsIgnoreMatch? EvaluateFile(
        string relativePath,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        if (!_enabled)
        {
            return null;
        }

        var match = Evaluate(relativePath, isDirectory: false);
        if (match is null || !match.Ignored)
        {
            return match;
        }

        if (AllowMatcher.HasPatterns && AllowMatcher.MatchFirst(relativePath) is not null)
        {
            return null;
        }

        _diagnostics.RecordExclusion(sourceKind, relativePath, match);
        return match;
    }

    public bool ShouldPruneDirectory(
        string relativeDirectory,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        if (!_enabled)
        {
            return false;
        }

        var normalizedDirectory = relativeDirectory.TrimEnd('/');
        if (string.IsNullOrEmpty(normalizedDirectory))
        {
            return false;
        }

        var match = Evaluate(normalizedDirectory, isDirectory: true);
        var directoryItselfIgnored = match is { Ignored: true };
        if (!directoryItselfIgnored)
        {
            match = EvaluateDirectorySubtree(normalizedDirectory);
        }

        if (match is not { Ignored: true })
        {
            return false;
        }

        if (AllowMatcher.HasPatterns && AllowMatcher.MatchDirectoryOrDescendant(normalizedDirectory) is not null)
        {
            return false;
        }

        if (directoryItselfIgnored
            ? CouldAnyNegationMatchDirectory(normalizedDirectory)
            : CouldAnyNegationReachDirectoryOrDescendant(normalizedDirectory))
        {
            return false;
        }

        _diagnostics.RecordExclusion(sourceKind, normalizedDirectory, match);
        return true;
    }

    public AppSurfaceDocsHarvestVcsIgnoreDiagnostics GetDiagnostics()
    {
        return _diagnostics.CreateSnapshot(_enabled);
    }

    public IReadOnlyList<DocHarvestDiagnostic> CreateHealthDiagnostics()
    {
        var diagnostics = GetDiagnostics();
        var results = new List<DocHarvestDiagnostic>();
        foreach (var warning in diagnostics.Warnings)
        {
            results.Add(
                new DocHarvestDiagnostic(
                    DocHarvestDiagnosticCodes.VcsIgnoreWarning,
                    DocHarvestDiagnosticSeverity.Warning,
                    HarvesterType: null,
                    warning.Problem,
                    warning.Cause,
                    warning.Fix));
        }

        var totalExclusions = diagnostics.ExclusionCountsBySourceKind.Values.Sum();
        if (_enabled && totalExclusions > 0)
        {
            var counts = string.Join(
                ", ",
                diagnostics.ExclusionCountsBySourceKind
                    .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}: {pair.Value}"));
            var sampleText = string.Join(
                "; ",
                diagnostics.ExclusionSamples.Select(
                    sample => $"{sample.CandidatePath} by {sample.SourcePath}:{sample.LineNumber} '{sample.Pattern}'"));
            results.Add(
                new DocHarvestDiagnostic(
                    DocHarvestDiagnosticCodes.VcsIgnoreSummary,
                    DocHarvestDiagnosticSeverity.Information,
                    HarvesterType: null,
                    $"Repository-owned Git ignore rules excluded {totalExclusions} AppSurface Docs harvest candidate(s).",
                    string.IsNullOrEmpty(sampleText)
                        ? $"Counts by source kind: {counts}."
                        : $"Counts by source kind: {counts}. Samples: {sampleText}.",
                    "Use AppSurfaceDocs:Harvest:Paths:VcsIgnore:AllowGlobs for intentional public docs under ignored paths, or disable VCS ignore integration for this host."));
        }

        return results;
    }

    private AppSurfaceDocsHarvestVcsIgnoreMatch? Evaluate(string relativePath, bool isDirectory)
    {
        var normalizedPath = relativePath.Trim('/');
        var result = default(AppSurfaceDocsHarvestVcsIgnoreMatch);
        foreach (var rule in GetApplicableRules(normalizedPath))
        {
            if (!rule.Matches(normalizedPath, isDirectory))
            {
                continue;
            }

            result = new AppSurfaceDocsHarvestVcsIgnoreMatch(
                Ignored: !rule.IsNegated,
                rule.SourcePath,
                rule.LineNumber,
                rule.Pattern,
                rule.IsNegated);
        }

        if (result is { Ignored: false } && !isDirectory)
        {
            var ignoredParent = GetAncestorDirectories(normalizedPath)
                .Select(parent => Evaluate(parent, isDirectory: true))
                .LastOrDefault(parentMatch => parentMatch is { Ignored: true });
            if (ignoredParent is not null)
            {
                return ignoredParent;
            }
        }

        return result;
    }

    private AppSurfaceDocsHarvestVcsIgnoreMatch? EvaluateDirectorySubtree(string relativeDirectory)
    {
        var normalizedDirectory = relativeDirectory.Trim('/');
        var result = default(AppSurfaceDocsHarvestVcsIgnoreMatch);
        foreach (var rule in GetApplicableRules(normalizedDirectory).Where(rule => rule.MatchesDirectorySubtree(normalizedDirectory)))
        {
            result = new AppSurfaceDocsHarvestVcsIgnoreMatch(
                Ignored: !rule.IsNegated,
                rule.SourcePath,
                rule.LineNumber,
                rule.Pattern,
                rule.IsNegated);
        }

        return result;
    }

    private IEnumerable<AppSurfaceDocsHarvestVcsIgnoreRule> GetApplicableRules(string relativePath)
    {
        foreach (var rule in LoadRulesForDirectory(string.Empty).Rules)
        {
            yield return rule;
        }

        foreach (var directory in GetAncestorDirectories(relativePath))
        {
            foreach (var rule in LoadRulesForDirectory(directory).Rules)
            {
                yield return rule;
            }
        }
    }

    private bool CouldAnyNegationReachDirectoryOrDescendant(string relativeDirectory)
    {
        if (LoadRulesForDirectory(string.Empty).Rules.Any(
                rule => rule.IsNegated && rule.CouldMatchDirectoryOrDescendant(relativeDirectory)))
        {
            return true;
        }

        return GetAncestorDirectories(relativeDirectory)
            .Append(relativeDirectory)
            .Any(
                directory => LoadRulesForDirectory(directory).Rules.Any(
                    rule => rule.IsNegated && rule.CouldMatchDirectoryOrDescendant(relativeDirectory)));
    }

    private bool CouldAnyNegationMatchDirectory(string relativeDirectory)
    {
        return GetApplicableRules(relativeDirectory)
            .Any(rule => rule.IsNegated && rule.Matches(relativeDirectory, isDirectory: true));
    }

    private IgnoreRuleSet LoadRulesForDirectory(string relativeDirectory)
    {
        var normalizedDirectory = relativeDirectory.Trim('/');
        var lazy = _ruleSets.GetOrAdd(
            normalizedDirectory,
            key => new Lazy<IgnoreRuleSet>(() => LoadRulesForDirectoryCore(key), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private IgnoreRuleSet LoadRulesForDirectoryCore(string relativeDirectory)
    {
        if (IsNestedRepositoryBoundary(relativeDirectory))
        {
            return new IgnoreRuleSet([]);
        }

        if (!TryGetRepositoryDirectoryPath(relativeDirectory, out var fullDirectory))
        {
            return new IgnoreRuleSet([]);
        }

        var ignorePath = Path.Join(fullDirectory, ".gitignore");
        if (!File.Exists(ignorePath))
        {
            return new IgnoreRuleSet([]);
        }

        try
        {
            var sourcePath = string.IsNullOrEmpty(relativeDirectory)
                ? ".gitignore"
                : $"{relativeDirectory}/.gitignore";
            var lines = File.ReadAllLines(ignorePath);
            var rules = new List<AppSurfaceDocsHarvestVcsIgnoreRule>();
            for (var index = 0; index < lines.Length; index++)
            {
                if (TryParseRule(lines[index], sourcePath, index + 1, relativeDirectory, out var rule))
                {
                    rules.Add(rule);
                }
            }

            _diagnostics.RecordIgnoreFile(sourcePath);
            return new IgnoreRuleSet(rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var sourcePath = string.IsNullOrEmpty(relativeDirectory)
                ? ".gitignore"
                : $"{relativeDirectory}/.gitignore";
            _diagnostics.RecordWarning(
                sourcePath,
                "AppSurface Docs could not read a repository-owned Git ignore file.",
                $"The file '{sourcePath}' could not be read while building harvest path policy.",
                "Fix filesystem permissions or remove the unreadable ignore file if it is not needed for docs harvesting.");
            _logger.LogWarning(ex, "AppSurface Docs could not read Git ignore file {IgnorePath}.", sourcePath);
            return new IgnoreRuleSet([]);
        }
    }

    private bool IsNestedRepositoryBoundary(string relativeDirectory)
    {
        if (string.IsNullOrEmpty(relativeDirectory))
        {
            return false;
        }

        if (!TryGetRepositoryDirectoryPath(relativeDirectory, out var fullDirectory))
        {
            return false;
        }

        var gitPath = Path.Join(fullDirectory, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
    }

    private bool TryGetRepositoryDirectoryPath(string relativeDirectory, out string fullDirectory)
    {
        var repositoryRoot = Path.GetFullPath(_repositoryRoot);
        var directorySegment = relativeDirectory.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(directorySegment))
        {
            fullDirectory = repositoryRoot;
            return false;
        }

        var candidateDirectory = directorySegment.Length == 0
            ? repositoryRoot
            : Path.GetFullPath(Path.Join(repositoryRoot, directorySegment));
        var repositoryRootPrefix = Path.TrimEndingDirectorySeparator(repositoryRoot) + Path.DirectorySeparatorChar;
        if (!candidateDirectory.Equals(repositoryRoot, StringComparison.Ordinal)
            && !candidateDirectory.StartsWith(repositoryRootPrefix, StringComparison.Ordinal))
        {
            fullDirectory = repositoryRoot;
            return false;
        }

        fullDirectory = candidateDirectory;
        return true;
    }

    private static IEnumerable<string> GetAncestorDirectories(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
        {
            yield break;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (builder.Length > 0)
            {
                builder.Append('/');
            }

            builder.Append(segments[index]);
            yield return builder.ToString();
        }
    }

    private static bool TryParseRule(
        string line,
        string sourcePath,
        int lineNumber,
        string baseDirectory,
        out AppSurfaceDocsHarvestVcsIgnoreRule rule)
    {
        rule = default!;
        var pattern = TrimUnescapedTrailingSpaces(line);
        if (pattern.Length == 0)
        {
            return false;
        }

        if (pattern[0] == '#')
        {
            return false;
        }

        var isNegated = false;
        if (pattern[0] == '!')
        {
            isNegated = true;
            pattern = pattern[1..];
        }
        else if (pattern.StartsWith("\\#", StringComparison.Ordinal) || pattern.StartsWith("\\!", StringComparison.Ordinal))
        {
            pattern = pattern[1..];
        }

        if (pattern.Length == 0)
        {
            return false;
        }

        var rootRelative = pattern.StartsWith("/", StringComparison.Ordinal);
        var directoryOnly = pattern.EndsWith("/", StringComparison.Ordinal);
        pattern = pattern.Trim('/');
        if (pattern.Length == 0)
        {
            return false;
        }

        pattern = UnescapePattern(pattern);
        rule = new AppSurfaceDocsHarvestVcsIgnoreRule(
            sourcePath,
            lineNumber,
            line,
            baseDirectory.Trim('/'),
            isNegated,
            directoryOnly,
            rootRelative,
            pattern);
        return true;
    }

    private static string TrimUnescapedTrailingSpaces(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ')
        {
            var slashCount = 0;
            for (var index = end - 2; index >= 0 && line[index] == '\\'; index--)
            {
                slashCount++;
            }

            if (slashCount % 2 == 1)
            {
                break;
            }

            end--;
        }

        return line[..end];
    }

    private static string UnescapePattern(string pattern)
    {
        var builder = new StringBuilder(pattern.Length);
        var escaping = false;
        foreach (var character in pattern)
        {
            if (escaping)
            {
                builder.Append(character);
                escaping = false;
                continue;
            }

            if (character == '\\')
            {
                escaping = true;
                continue;
            }

            builder.Append(character);
        }

        if (escaping)
        {
            builder.Append('\\');
        }

        return builder.ToString();
    }

    private sealed record IgnoreRuleSet(IReadOnlyList<AppSurfaceDocsHarvestVcsIgnoreRule> Rules);
}

internal sealed record AppSurfaceDocsHarvestVcsIgnoreRule(
    string SourcePath,
    int LineNumber,
    string Pattern,
    string BaseDirectory,
    bool IsNegated,
    bool DirectoryOnly,
    bool RootRelative,
    string NormalizedPattern)
{
    private readonly Regex? _regex = TryCreateRegex(NormalizedPattern);
    private readonly bool _hasSlash = NormalizedPattern.Contains('/', StringComparison.Ordinal);

    public bool Matches(string repositoryRelativePath, bool isDirectory)
    {
        if (_regex is null)
        {
            return false;
        }

        if (!TryGetPathRelativeToBase(repositoryRelativePath, out var relativePath))
        {
            return false;
        }

        if (_hasSlash || RootRelative)
        {
            return DirectoryOnly
                ? MatchesDirectoryPattern(relativePath)
                : _regex.IsMatch(relativePath);
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var candidateSegments = DirectoryOnly && !isDirectory
            ? segments.SkipLast(1)
            : segments;
        return candidateSegments.Any(segment => _regex.IsMatch(segment));
    }

    public bool MatchesDirectorySubtree(string repositoryRelativeDirectory)
    {
        return NormalizedPattern.EndsWith("/**", StringComparison.Ordinal)
               && Matches($"{repositoryRelativeDirectory.TrimEnd('/')}/_", isDirectory: false);
    }

    public bool CouldMatchDirectoryOrDescendant(string repositoryRelativeDirectory)
    {
        if (!TryGetPathRelativeToBase(repositoryRelativeDirectory, out var relativeDirectory))
        {
            return false;
        }

        if (_hasSlash || RootRelative)
        {
            var literalPrefix = GetLiteralPrefix(NormalizedPattern);
            return literalPrefix.Length == 0
                   || literalPrefix.StartsWith(relativeDirectory, StringComparison.Ordinal)
                   || relativeDirectory.StartsWith(literalPrefix, StringComparison.Ordinal);
        }

        return true;
    }

    private bool MatchesDirectoryPattern(string relativePath)
    {
        if (_regex is null)
        {
            return false;
        }

        if (_regex.IsMatch(relativePath))
        {
            return true;
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var count = 1; count < segments.Length; count++)
        {
            var parent = string.Join('/', segments.Take(count));
            if (_regex.IsMatch(parent))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetPathRelativeToBase(string repositoryRelativePath, out string relativePath)
    {
        if (BaseDirectory.Length == 0)
        {
            relativePath = repositoryRelativePath.Trim('/');
            return true;
        }

        if (repositoryRelativePath.Equals(BaseDirectory, StringComparison.Ordinal))
        {
            relativePath = string.Empty;
            return true;
        }

        if (repositoryRelativePath.StartsWith($"{BaseDirectory}/", StringComparison.Ordinal))
        {
            relativePath = repositoryRelativePath[(BaseDirectory.Length + 1)..];
            return true;
        }

        relativePath = string.Empty;
        return false;
    }

    private static Regex? TryCreateRegex(string pattern)
    {
        try
        {
            return CreateRegex(pattern);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Regex CreateRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    builder.Append(".*");
                    index++;
                }
                else
                {
                    builder.Append("[^/]*");
                }

                continue;
            }

            if (character == '?')
            {
                builder.Append("[^/]");
                continue;
            }

            if (character == '[')
            {
                var end = pattern.IndexOf(']', index + 1);
                if (end > index)
                {
                    builder.Append(pattern[index..(end + 1)]);
                    index = end;
                    continue;
                }
            }

            builder.Append(Regex.Escape(character.ToString()));
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static string GetLiteralPrefix(string pattern)
    {
        var special = pattern.IndexOfAny(['*', '?', '[']);
        return (special < 0 ? pattern : pattern[..special]).TrimEnd('/');
    }
}

internal sealed record AppSurfaceDocsHarvestVcsIgnoreMatch(
    bool Ignored,
    string SourcePath,
    int LineNumber,
    string Pattern,
    bool IsNegated);

internal sealed record AppSurfaceDocsHarvestVcsIgnoreExclusionSample(
    AppSurfaceDocsHarvestSourceKind SourceKind,
    string CandidatePath,
    string SourcePath,
    int LineNumber,
    string Pattern);

internal sealed record AppSurfaceDocsHarvestVcsIgnoreWarning(
    string SourcePath,
    string Problem,
    string Cause,
    string Fix);

internal sealed record AppSurfaceDocsHarvestVcsIgnoreDiagnostics(
    bool Enabled,
    int IgnoreFileCount,
    IReadOnlyList<string> IgnoreFileSamples,
    IReadOnlyDictionary<AppSurfaceDocsHarvestSourceKind, int> ExclusionCountsBySourceKind,
    IReadOnlyList<AppSurfaceDocsHarvestVcsIgnoreExclusionSample> ExclusionSamples,
    IReadOnlyList<AppSurfaceDocsHarvestVcsIgnoreWarning> Warnings);

internal sealed class VcsIgnoreDiagnosticsCollector
{
    private readonly int _maxSamples;
    private readonly object _sync = new();
    private readonly HashSet<string> _ignoreFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<AppSurfaceDocsHarvestSourceKind, int> _exclusionCounts = [];
    private readonly List<AppSurfaceDocsHarvestVcsIgnoreExclusionSample> _samples = [];
    private readonly List<AppSurfaceDocsHarvestVcsIgnoreWarning> _warnings = [];

    public VcsIgnoreDiagnosticsCollector(int maxSamples)
    {
        _maxSamples = maxSamples;
    }

    public void RecordIgnoreFile(string sourcePath)
    {
        lock (_sync)
        {
            _ignoreFiles.Add(sourcePath);
        }
    }

    public void RecordExclusion(
        AppSurfaceDocsHarvestSourceKind sourceKind,
        string candidatePath,
        AppSurfaceDocsHarvestVcsIgnoreMatch match)
    {
        lock (_sync)
        {
            _exclusionCounts[sourceKind] = _exclusionCounts.GetValueOrDefault(sourceKind) + 1;
            if (_samples.Count >= _maxSamples)
            {
                return;
            }

            _samples.Add(
                new AppSurfaceDocsHarvestVcsIgnoreExclusionSample(
                    sourceKind,
                    candidatePath,
                    match.SourcePath,
                    match.LineNumber,
                    match.Pattern));
        }
    }

    public void RecordWarning(string sourcePath, string problem, string cause, string fix)
    {
        lock (_sync)
        {
            if (_warnings.Count >= _maxSamples)
            {
                return;
            }

            _warnings.Add(new AppSurfaceDocsHarvestVcsIgnoreWarning(sourcePath, problem, cause, fix));
        }
    }

    public AppSurfaceDocsHarvestVcsIgnoreDiagnostics CreateSnapshot(bool enabled)
    {
        lock (_sync)
        {
            return new AppSurfaceDocsHarvestVcsIgnoreDiagnostics(
                enabled,
                _ignoreFiles.Count,
                _ignoreFiles.OrderBy(path => path, StringComparer.Ordinal).Take(_maxSamples).ToArray(),
                new Dictionary<AppSurfaceDocsHarvestSourceKind, int>(_exclusionCounts),
                _samples.ToArray(),
                _warnings.ToArray());
        }
    }
}
