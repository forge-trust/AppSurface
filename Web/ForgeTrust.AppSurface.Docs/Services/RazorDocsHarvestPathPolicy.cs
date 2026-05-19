using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Services;

internal sealed class RazorDocsHarvestPathPolicy
{
    private static readonly string[] TestProjectSuffixes =
    [
        ".Tests",
        ".UnitTests",
        ".IntegrationTests",
        ".FunctionalTests",
        ".E2ETests",
        "-Tests",
        "_Tests"
    ];

    private static readonly string[] BuildOutputDirectoryNames =
    [
        "node_modules",
        "bin",
        "obj"
    ];

    private readonly ILogger<RazorDocsHarvestPathPolicy> _logger;
    private readonly RazorDocsHarvestPathMatcher _globalIncludeMatcher;
    private readonly RazorDocsHarvestPathMatcher _globalExcludeMatcher;
    private readonly SourceScopePolicy _markdownPolicy;
    private readonly SourceScopePolicy _csharpPolicy;
    private readonly HashSet<RazorDocsHarvestDefaultExclusionGroup> _globalDisabledGroups;
    private readonly Dictionary<RazorDocsHarvestDefaultExclusionGroup, RazorDocsHarvestPathMatcher> _globalAllowMatchers;

    public RazorDocsHarvestPathPolicy(
        RazorDocsOptions options,
        ILogger<RazorDocsHarvestPathPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var harvest = options.Harvest ?? new RazorDocsHarvestOptions();
        var paths = harvest.Paths ?? new RazorDocsHarvestPathOptions();
        _globalIncludeMatcher = new RazorDocsHarvestPathMatcher(paths.IncludeGlobs ?? []);
        _globalExcludeMatcher = new RazorDocsHarvestPathMatcher(paths.ExcludeGlobs ?? []);
        _globalDisabledGroups = CreateDisabledGroupSet(paths.DefaultExclusions?.DisabledGroups);
        _globalAllowMatchers = CreateAllowMatchers(paths.DefaultExclusions?.AllowGlobs);
        _markdownPolicy = CreateScopePolicy(
            harvest.Markdown,
            RazorDocsHarvestSourceKind.Markdown);
        _csharpPolicy = CreateScopePolicy(
            harvest.CSharp,
            RazorDocsHarvestSourceKind.CSharp);
    }

    private RazorDocsHarvestPathPolicy(RazorDocsOptions options)
        : this(options, NullLogger<RazorDocsHarvestPathPolicy>.Instance)
    {
    }

    public static RazorDocsHarvestPathPolicy CreateDefault()
    {
        return new RazorDocsHarvestPathPolicy(new RazorDocsOptions());
    }

    public RazorDocsHarvestPathDecision Evaluate(
        string relativePath,
        RazorDocsHarvestSourceKind sourceKind)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var trace = new List<RazorDocsHarvestPathRuleTrace>();
        if (!RazorDocsHarvestPathPatternValidator.TryNormalizeCandidatePath(relativePath, out var normalizedPath))
        {
            _logger.LogWarning(
                "RazorDocs excluded invalid harvest candidate path '{RelativePath}'.",
                relativePath);
            return CreateDecision(
                included: false,
                normalizedPath,
                sourceKind,
                RazorDocsHarvestPathDecisionCode.ExcludedByInvalidPath,
                trace,
                []);
        }

        if (!IsBaseCandidate(normalizedPath, sourceKind))
        {
            return CreateDecision(
                included: false,
                normalizedPath,
                sourceKind,
                RazorDocsHarvestPathDecisionCode.ExcludedByBaseCandidate,
                trace,
                []);
        }

        var includeCode = RazorDocsHarvestPathDecisionCode.IncludedByDefaultCandidate;
        if (_globalIncludeMatcher.HasPatterns)
        {
            var matchedPattern = _globalIncludeMatcher.MatchFirst(normalizedPath);
            trace.Add(
                new RazorDocsHarvestPathRuleTrace(
                    RazorDocsHarvestPathDecisionCode.IncludedByGlobalInclude,
                    "global",
                    matchedPattern,
                    null,
                    matchedPattern is not null));
            if (matchedPattern is null)
            {
                return CreateDecision(
                    included: false,
                    normalizedPath,
                    sourceKind,
                    RazorDocsHarvestPathDecisionCode.ExcludedByGlobalIncludeMiss,
                    trace,
                    []);
            }

            includeCode = RazorDocsHarvestPathDecisionCode.IncludedByGlobalInclude;
        }

        var sourcePolicy = GetSourcePolicy(sourceKind);
        if (sourcePolicy.IncludeMatcher.HasPatterns)
        {
            var matchedPattern = sourcePolicy.IncludeMatcher.MatchFirst(normalizedPath);
            trace.Add(
                new RazorDocsHarvestPathRuleTrace(
                    RazorDocsHarvestPathDecisionCode.IncludedBySourceInclude,
                    sourcePolicy.ScopeName,
                    matchedPattern,
                    null,
                    matchedPattern is not null));
            if (matchedPattern is null)
            {
                return CreateDecision(
                    included: false,
                    normalizedPath,
                    sourceKind,
                    RazorDocsHarvestPathDecisionCode.ExcludedBySourceIncludeMiss,
                    trace,
                    []);
            }

            includeCode = RazorDocsHarvestPathDecisionCode.IncludedBySourceInclude;
        }

        var matchedDefaultGroups = GetMatchingDefaultGroups(
            normalizedPath,
            sourceKind,
            treatLastSegmentAsDirectory: false);
        foreach (var group in matchedDefaultGroups)
        {
            trace.Add(
                new RazorDocsHarvestPathRuleTrace(
                    RazorDocsHarvestPathDecisionCode.ExcludedByDefaultGroup,
                    "default",
                    null,
                    group.ToString(),
                    Matched: true));
        }

        var enabledDefaultGroups = matchedDefaultGroups
            .Where(group => !IsDefaultGroupDisabled(group, sourcePolicy))
            .ToArray();
        if (enabledDefaultGroups.Length > 0)
        {
            var unallowedDefaultGroups = new List<RazorDocsHarvestDefaultExclusionGroup>();
            foreach (var group in enabledDefaultGroups)
            {
                var allowPattern = MatchDefaultGroupAllow(group, normalizedPath, sourcePolicy);
                trace.Add(
                    new RazorDocsHarvestPathRuleTrace(
                        RazorDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow,
                        "default-allow",
                        allowPattern,
                        group.ToString(),
                        allowPattern is not null));

                if (allowPattern is null)
                {
                    unallowedDefaultGroups.Add(group);
                }
            }

            if (unallowedDefaultGroups.Count > 0)
            {
                return CreateDecision(
                    included: false,
                    normalizedPath,
                    sourceKind,
                    RazorDocsHarvestPathDecisionCode.ExcludedByDefaultGroup,
                    trace,
                    matchedDefaultGroups.Select(group => group.ToString()).ToArray());
            }

            includeCode = RazorDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow;
        }

        if (_globalExcludeMatcher.HasPatterns)
        {
            var matchedPattern = _globalExcludeMatcher.MatchFirst(normalizedPath);
            trace.Add(
                new RazorDocsHarvestPathRuleTrace(
                    RazorDocsHarvestPathDecisionCode.ExcludedByGlobalExclude,
                    "global",
                    matchedPattern,
                    null,
                    matchedPattern is not null));
            if (matchedPattern is not null)
            {
                return CreateDecision(
                    included: false,
                    normalizedPath,
                    sourceKind,
                    RazorDocsHarvestPathDecisionCode.ExcludedByGlobalExclude,
                    trace,
                    matchedDefaultGroups.Select(group => group.ToString()).ToArray());
            }
        }

        if (sourcePolicy.ExcludeMatcher.HasPatterns)
        {
            var matchedPattern = sourcePolicy.ExcludeMatcher.MatchFirst(normalizedPath);
            trace.Add(
                new RazorDocsHarvestPathRuleTrace(
                    RazorDocsHarvestPathDecisionCode.ExcludedBySourceExclude,
                    sourcePolicy.ScopeName,
                    matchedPattern,
                    null,
                    matchedPattern is not null));
            if (matchedPattern is not null)
            {
                return CreateDecision(
                    included: false,
                    normalizedPath,
                    sourceKind,
                    RazorDocsHarvestPathDecisionCode.ExcludedBySourceExclude,
                    trace,
                    matchedDefaultGroups.Select(group => group.ToString()).ToArray());
            }
        }

        return CreateDecision(
            included: true,
            normalizedPath,
            sourceKind,
            includeCode,
            trace,
            matchedDefaultGroups.Select(group => group.ToString()).ToArray());
    }

    public bool ShouldIncludeFilePath(
        string relativePath,
        RazorDocsHarvestSourceKind sourceKind)
    {
        return Evaluate(relativePath, sourceKind).Included;
    }

    public IEnumerable<string> EnumerateCandidateFiles(
        string rootPath,
        RazorDocsHarvestSourceKind sourceKind,
        string searchPattern,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(searchPattern);

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDirectory = pendingDirectories.Pop();
            foreach (var file in Directory.EnumerateFiles(currentDirectory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }

            foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
            {
                var relativeDirectory = Path.GetRelativePath(rootPath, directory).Replace('\\', '/');
                if (ShouldPruneDirectory(relativeDirectory, sourceKind))
                {
                    continue;
                }

                pendingDirectories.Push(directory);
            }
        }
    }

    public bool ShouldPruneDirectory(
        string relativeDirectory,
        RazorDocsHarvestSourceKind sourceKind)
    {
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        if (!RazorDocsHarvestPathPatternValidator.TryNormalizeCandidatePath(relativeDirectory, out var normalizedDirectory))
        {
            _logger.LogWarning(
                "RazorDocs pruned invalid harvest candidate directory '{RelativeDirectory}'.",
                relativeDirectory);
            return true;
        }

        var sourcePolicy = GetSourcePolicy(sourceKind);
        var matchedDefaultGroups = GetMatchingDefaultGroups(
            normalizedDirectory,
            sourceKind,
            treatLastSegmentAsDirectory: true)
            .Where(group => !IsDefaultGroupDisabled(group, sourcePolicy));

        foreach (var group in matchedDefaultGroups.Where(group => !HasAnyDefaultGroupAllowPattern(group, sourcePolicy)))
        {
            return true;
        }

        return _globalExcludeMatcher.MatchDirectorySubtree(normalizedDirectory) is not null
               || sourcePolicy.ExcludeMatcher.MatchDirectorySubtree(normalizedDirectory) is not null;
    }

    public static bool IsKnownDefaultGroupId(string? groupId)
    {
        return TryParseDefaultGroupName(groupId, out _);
    }

    public static string NormalizeDefaultGroupId(string? groupId)
    {
        var trimmedGroupId = groupId?.Trim() ?? string.Empty;

        return TryParseDefaultGroupName(trimmedGroupId, out var group)
            ? group.ToString()
            : trimmedGroupId;
    }

    private static SourceScopePolicy CreateScopePolicy(
        RazorDocsMarkdownHarvestOptions? options,
        RazorDocsHarvestSourceKind sourceKind)
    {
        return new SourceScopePolicy(
            sourceKind.ToString(),
            new RazorDocsHarvestPathMatcher(options?.IncludeGlobs ?? []),
            new RazorDocsHarvestPathMatcher(options?.ExcludeGlobs ?? []),
            CreateDisabledGroupSet(options?.DefaultExclusions?.DisabledGroups),
            CreateAllowMatchers(options?.DefaultExclusions?.AllowGlobs));
    }

    private static SourceScopePolicy CreateScopePolicy(
        RazorDocsCSharpHarvestOptions? options,
        RazorDocsHarvestSourceKind sourceKind)
    {
        return new SourceScopePolicy(
            sourceKind.ToString(),
            new RazorDocsHarvestPathMatcher(options?.IncludeGlobs ?? []),
            new RazorDocsHarvestPathMatcher(options?.ExcludeGlobs ?? []),
            CreateDisabledGroupSet(options?.DefaultExclusions?.DisabledGroups),
            CreateAllowMatchers(options?.DefaultExclusions?.AllowGlobs));
    }

    private static HashSet<RazorDocsHarvestDefaultExclusionGroup> CreateDisabledGroupSet(
        IEnumerable<string>? disabledGroups)
    {
        return (disabledGroups ?? [])
            .Select(groupId => new
            {
                Parsed = TryParseDefaultGroupName(groupId, out var group),
                Group = group
            })
            .Where(result => result.Parsed)
            .Select(result => result.Group)
            .ToHashSet();
    }

    private static Dictionary<RazorDocsHarvestDefaultExclusionGroup, RazorDocsHarvestPathMatcher> CreateAllowMatchers(
        Dictionary<string, string[]>? allowGlobs)
    {
        var matchers = new Dictionary<RazorDocsHarvestDefaultExclusionGroup, RazorDocsHarvestPathMatcher>();
        foreach (var (groupId, patterns) in allowGlobs ?? [])
        {
            if (TryParseDefaultGroupName(groupId, out var group))
            {
                matchers[group] = new RazorDocsHarvestPathMatcher(patterns ?? []);
            }
        }

        return matchers;
    }

    private static RazorDocsHarvestPathDecision CreateDecision(
        bool included,
        string normalizedPath,
        RazorDocsHarvestSourceKind sourceKind,
        RazorDocsHarvestPathDecisionCode code,
        IReadOnlyList<RazorDocsHarvestPathRuleTrace> trace,
        string[] matchedDefaultGroups)
    {
        return new RazorDocsHarvestPathDecision(
            included,
            normalizedPath,
            sourceKind,
            code,
            trace,
            matchedDefaultGroups);
    }

    private static bool TryParseDefaultGroupName(
        string? groupId,
        out RazorDocsHarvestDefaultExclusionGroup group)
    {
        group = default;

        if (string.IsNullOrWhiteSpace(groupId))
        {
            return false;
        }

        var matchingName = Enum.GetNames<RazorDocsHarvestDefaultExclusionGroup>()
            .FirstOrDefault(name => name.Equals(groupId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matchingName is null)
        {
            return false;
        }

        group = Enum.Parse<RazorDocsHarvestDefaultExclusionGroup>(matchingName);
        return true;
    }

    private static bool IsBaseCandidate(
        string normalizedPath,
        RazorDocsHarvestSourceKind sourceKind)
    {
        return sourceKind switch
        {
            RazorDocsHarvestSourceKind.Markdown => normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                                                   || normalizedPath.Equals("LICENSE", StringComparison.OrdinalIgnoreCase),
            RazorDocsHarvestSourceKind.CSharp => normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static RazorDocsHarvestDefaultExclusionGroup[] GetMatchingDefaultGroups(
        string normalizedPath,
        RazorDocsHarvestSourceKind sourceKind,
        bool treatLastSegmentAsDirectory)
    {
        var directorySegments = GetDirectorySegments(normalizedPath, treatLastSegmentAsDirectory);
        var groups = new List<RazorDocsHarvestDefaultExclusionGroup>();
        if (directorySegments.Any(segment => BuildOutputDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            groups.Add(RazorDocsHarvestDefaultExclusionGroup.BuildOutput);
        }

        if (directorySegments.Any(segment => segment.StartsWith(".", StringComparison.Ordinal)))
        {
            groups.Add(RazorDocsHarvestDefaultExclusionGroup.HiddenDirectories);
        }

        if (directorySegments.Any(IsTestProjectDirectorySegment))
        {
            groups.Add(RazorDocsHarvestDefaultExclusionGroup.TestProjects);
        }

        if (sourceKind == RazorDocsHarvestSourceKind.CSharp
            && directorySegments.Any(segment => segment.Equals("examples", StringComparison.OrdinalIgnoreCase)))
        {
            groups.Add(RazorDocsHarvestDefaultExclusionGroup.CSharpExampleSource);
        }

        return groups.ToArray();
    }

    private static string[] GetDirectorySegments(
        string normalizedPath,
        bool treatLastSegmentAsDirectory)
    {
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return treatLastSegmentAsDirectory
            ? segments
            : segments[..^1];
    }

    private static bool IsTestProjectDirectorySegment(string segment)
    {
        return segment.Equals("Test", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("Tests", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("UnitTests", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("IntegrationTests", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("FunctionalTests", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("E2ETests", StringComparison.OrdinalIgnoreCase)
               || TestProjectSuffixes.Any(suffix => segment.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private SourceScopePolicy GetSourcePolicy(RazorDocsHarvestSourceKind sourceKind)
    {
        return sourceKind switch
        {
            RazorDocsHarvestSourceKind.Markdown => _markdownPolicy,
            RazorDocsHarvestSourceKind.CSharp => _csharpPolicy,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, null)
        };
    }

    private bool IsDefaultGroupDisabled(
        RazorDocsHarvestDefaultExclusionGroup group,
        SourceScopePolicy sourcePolicy)
    {
        return _globalDisabledGroups.Contains(group)
               || sourcePolicy.DisabledGroups.Contains(group);
    }

    private string? MatchDefaultGroupAllow(
        RazorDocsHarvestDefaultExclusionGroup group,
        string normalizedPath,
        SourceScopePolicy sourcePolicy)
    {
        if (_globalAllowMatchers.TryGetValue(group, out var globalMatcher))
        {
            var globalPattern = globalMatcher.MatchFirst(normalizedPath);
            if (globalPattern is not null)
            {
                return globalPattern;
            }
        }

        return sourcePolicy.AllowMatchers.TryGetValue(group, out var sourceMatcher)
            ? sourceMatcher.MatchFirst(normalizedPath)
            : null;
    }

    private bool HasAnyDefaultGroupAllowPattern(
        RazorDocsHarvestDefaultExclusionGroup group,
        SourceScopePolicy sourcePolicy)
    {
        return _globalAllowMatchers.TryGetValue(group, out var globalMatcher) && globalMatcher.HasPatterns
               || sourcePolicy.AllowMatchers.TryGetValue(group, out var sourceMatcher) && sourceMatcher.HasPatterns;
    }

    private sealed class SourceScopePolicy(
        string scopeName,
        RazorDocsHarvestPathMatcher includeMatcher,
        RazorDocsHarvestPathMatcher excludeMatcher,
        HashSet<RazorDocsHarvestDefaultExclusionGroup> disabledGroups,
        Dictionary<RazorDocsHarvestDefaultExclusionGroup, RazorDocsHarvestPathMatcher> allowMatchers)
    {
        public string ScopeName { get; } = scopeName;

        public RazorDocsHarvestPathMatcher IncludeMatcher { get; } = includeMatcher;

        public RazorDocsHarvestPathMatcher ExcludeMatcher { get; } = excludeMatcher;

        public HashSet<RazorDocsHarvestDefaultExclusionGroup> DisabledGroups { get; } = disabledGroups;

        public Dictionary<RazorDocsHarvestDefaultExclusionGroup, RazorDocsHarvestPathMatcher> AllowMatchers { get; } = allowMatchers;
    }
}
