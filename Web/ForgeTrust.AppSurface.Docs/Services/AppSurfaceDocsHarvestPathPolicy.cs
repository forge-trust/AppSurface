using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Applies AppSurface Docs harvest path rules for source-backed Markdown, C#, and JavaScript documentation.
/// </summary>
/// <remarks>
/// Policy order is intentionally fixed: built-in source candidates are checked first, then global includes,
/// source-specific includes, default exclusion groups and their allows, global excludes, and source-specific excludes.
/// Default groups protect build output, hidden directories, test projects, and C# source under <c>examples</c>. Directory
/// pruning uses the same defaults and clear <c>/**</c> subtree excludes, but keeps a default-excluded subtree only when a
/// configured group allow can match that directory or one of its descendants.
/// </remarks>
internal sealed class AppSurfaceDocsHarvestPathPolicy
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

    private readonly ILogger<AppSurfaceDocsHarvestPathPolicy> _logger;
    private readonly AppSurfaceDocsHarvestPathMatcher _globalIncludeMatcher;
    private readonly AppSurfaceDocsHarvestPathMatcher _globalExcludeMatcher;
    private readonly SourceScopePolicy _markdownPolicy;
    private readonly SourceScopePolicy _csharpPolicy;
    private readonly SourceScopePolicy _javascriptPolicy;
    private readonly HashSet<AppSurfaceDocsHarvestDefaultExclusionGroup> _globalDisabledGroups;
    private readonly Dictionary<AppSurfaceDocsHarvestDefaultExclusionGroup, AppSurfaceDocsHarvestPathMatcher> _globalAllowMatchers;

    /// <summary>
    /// Initializes a path policy from normalized AppSurface Docs options.
    /// </summary>
    public AppSurfaceDocsHarvestPathPolicy(
        AppSurfaceDocsOptions options,
        ILogger<AppSurfaceDocsHarvestPathPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var harvest = options.Harvest ?? new AppSurfaceDocsHarvestOptions();
        var paths = harvest.Paths ?? new AppSurfaceDocsHarvestPathOptions();
        _globalIncludeMatcher = new AppSurfaceDocsHarvestPathMatcher(paths.IncludeGlobs ?? []);
        _globalExcludeMatcher = new AppSurfaceDocsHarvestPathMatcher(paths.ExcludeGlobs ?? []);
        _globalDisabledGroups = CreateDisabledGroupSet(paths.DefaultExclusions?.DisabledGroups);
        _globalAllowMatchers = CreateAllowMatchers(paths.DefaultExclusions?.AllowGlobs);
        _markdownPolicy = CreateScopePolicy(
            harvest.Markdown,
            AppSurfaceDocsHarvestSourceKind.Markdown);
        _csharpPolicy = CreateScopePolicy(
            harvest.CSharp,
            AppSurfaceDocsHarvestSourceKind.CSharp);
        _javascriptPolicy = CreateScopePolicy(
            harvest.JavaScript,
            AppSurfaceDocsHarvestSourceKind.JavaScript);
    }

    private AppSurfaceDocsHarvestPathPolicy(AppSurfaceDocsOptions options)
        : this(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance)
    {
    }

    /// <summary>
    /// Creates a policy with package defaults and no configured include or exclude globs.
    /// </summary>
    public static AppSurfaceDocsHarvestPathPolicy CreateDefault()
    {
        return new AppSurfaceDocsHarvestPathPolicy(new AppSurfaceDocsOptions());
    }

    /// <summary>
    /// Evaluates a repository-relative path and returns the include/exclude decision with rule trace details.
    /// </summary>
    /// <remarks>
    /// The input must be a normalized relative candidate. Invalid paths are excluded with
    /// <see cref="AppSurfaceDocsHarvestPathDecisionCode.ExcludedByInvalidPath"/>. Include misses use the <c>Miss</c> codes,
    /// while configured exclude and default-group denials use their corresponding <c>ExcludedBy*</c> codes.
    /// </remarks>
    public AppSurfaceDocsHarvestPathDecision Evaluate(
        string relativePath,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var trace = new List<AppSurfaceDocsHarvestPathRuleTrace>();
        if (!AppSurfaceDocsHarvestPathPatternValidator.TryNormalizeCandidatePath(relativePath, out var normalizedPath))
        {
            _logger.LogWarning(
                "AppSurface Docs excluded invalid harvest candidate path '{RelativePath}'.",
                relativePath);
            return CreateDecision(
                included: false,
                normalizedPath,
                sourceKind,
                AppSurfaceDocsHarvestPathDecisionCode.ExcludedByInvalidPath,
                trace,
                []);
        }

        if (!IsBaseCandidate(normalizedPath, sourceKind))
        {
            return CreateDecision(
                included: false,
                normalizedPath,
                sourceKind,
                AppSurfaceDocsHarvestPathDecisionCode.ExcludedByBaseCandidate,
                trace,
                []);
        }

        var includeCode = AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultCandidate;
        if (_globalIncludeMatcher.HasPatterns)
        {
            var matchedPattern = _globalIncludeMatcher.MatchFirst(normalizedPath);
            trace.Add(
                new AppSurfaceDocsHarvestPathRuleTrace(
                    AppSurfaceDocsHarvestPathDecisionCode.IncludedByGlobalInclude,
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
                    AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalIncludeMiss,
                    trace,
                    []);
            }

            includeCode = AppSurfaceDocsHarvestPathDecisionCode.IncludedByGlobalInclude;
        }

        var sourcePolicy = GetSourcePolicy(sourceKind);
        if (sourcePolicy.IncludeMatcher.HasPatterns)
        {
            var matchedPattern = sourcePolicy.IncludeMatcher.MatchFirst(normalizedPath);
            trace.Add(
                new AppSurfaceDocsHarvestPathRuleTrace(
                    AppSurfaceDocsHarvestPathDecisionCode.IncludedBySourceInclude,
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
                    AppSurfaceDocsHarvestPathDecisionCode.ExcludedBySourceIncludeMiss,
                    trace,
                    []);
            }

            includeCode = AppSurfaceDocsHarvestPathDecisionCode.IncludedBySourceInclude;
        }

        var matchedDefaultGroups = GetMatchingDefaultGroups(
            normalizedPath,
            sourceKind,
            treatLastSegmentAsDirectory: false);
        foreach (var group in matchedDefaultGroups)
        {
            trace.Add(
                new AppSurfaceDocsHarvestPathRuleTrace(
                    AppSurfaceDocsHarvestPathDecisionCode.ExcludedByDefaultGroup,
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
            var unallowedDefaultGroups = new List<AppSurfaceDocsHarvestDefaultExclusionGroup>();
            foreach (var group in enabledDefaultGroups)
            {
                var allowPattern = MatchDefaultGroupAllow(group, normalizedPath, sourcePolicy);
                trace.Add(
                    new AppSurfaceDocsHarvestPathRuleTrace(
                        AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow,
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
                    AppSurfaceDocsHarvestPathDecisionCode.ExcludedByDefaultGroup,
                    trace,
                    matchedDefaultGroups.Select(group => group.ToString()).ToArray());
            }

            includeCode = AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow;
        }

        if (_globalExcludeMatcher.HasPatterns)
        {
            var matchedPattern = _globalExcludeMatcher.MatchFirst(normalizedPath);
            trace.Add(
                new AppSurfaceDocsHarvestPathRuleTrace(
                    AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalExclude,
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
                    AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalExclude,
                    trace,
                    matchedDefaultGroups.Select(group => group.ToString()).ToArray());
            }
        }

        if (sourcePolicy.ExcludeMatcher.HasPatterns)
        {
            var matchedPattern = sourcePolicy.ExcludeMatcher.MatchFirst(normalizedPath);
            trace.Add(
                new AppSurfaceDocsHarvestPathRuleTrace(
                    AppSurfaceDocsHarvestPathDecisionCode.ExcludedBySourceExclude,
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
                    AppSurfaceDocsHarvestPathDecisionCode.ExcludedBySourceExclude,
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

    /// <summary>
    /// Returns whether <paramref name="relativePath"/> is included by <see cref="Evaluate"/>.
    /// </summary>
    public bool ShouldIncludeFilePath(
        string relativePath,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return Evaluate(relativePath, sourceKind).Included;
    }

    /// <summary>
    /// Enumerates candidate files under <paramref name="rootPath"/> while pruning policy-excluded subtrees.
    /// </summary>
    /// <remarks>
    /// The <paramref name="searchPattern"/> is passed to <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
    /// for each visited directory. Cancellation is observed between directory visits.
    /// </remarks>
    public IEnumerable<string> EnumerateCandidateFiles(
        string rootPath,
        AppSurfaceDocsHarvestSourceKind sourceKind,
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

    /// <summary>
    /// Returns whether a normalized repository-relative directory can be skipped during traversal.
    /// </summary>
    /// <remarks>
    /// Invalid directories are pruned. Default-excluded directories are pruned unless the matched group has an allow
    /// glob that can apply to a file inside the directory or a descendant. Configured subtree excludes prune only when a
    /// <c>/**</c> pattern matches the directory.
    /// </remarks>
    public bool ShouldPruneDirectory(
        string relativeDirectory,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        if (!AppSurfaceDocsHarvestPathPatternValidator.TryNormalizeCandidatePath(relativeDirectory, out var normalizedDirectory))
        {
            _logger.LogWarning(
                "AppSurface Docs pruned invalid harvest candidate directory '{RelativeDirectory}'.",
                relativeDirectory);
            return true;
        }

        var sourcePolicy = GetSourcePolicy(sourceKind);
        var matchedDefaultGroups = GetMatchingDefaultGroups(
            normalizedDirectory,
            sourceKind,
            treatLastSegmentAsDirectory: true)
            .Where(group => !IsDefaultGroupDisabled(group, sourcePolicy));

        if (matchedDefaultGroups.Any(group => !HasDefaultGroupAllowPatternForDescendants(group, normalizedDirectory, sourcePolicy)))
        {
            return true;
        }

        return _globalExcludeMatcher.MatchDirectorySubtree(normalizedDirectory) is not null
               || sourcePolicy.ExcludeMatcher.MatchDirectorySubtree(normalizedDirectory) is not null;
    }

    /// <summary>
    /// Returns whether <paramref name="groupId"/> is a supported named default exclusion group.
    /// </summary>
    /// <remarks>
    /// Matching is case-insensitive but name-only; numeric enum values are rejected so configuration stays stable.
    /// </remarks>
    public static bool IsKnownDefaultGroupId(string? groupId)
    {
        return TryParseDefaultGroupName(groupId, out _);
    }

    /// <summary>
    /// Normalizes a named default exclusion group to canonical casing, or returns the trimmed input when unsupported.
    /// </summary>
    public static string NormalizeDefaultGroupId(string? groupId)
    {
        var trimmedGroupId = groupId?.Trim() ?? string.Empty;

        return TryParseDefaultGroupName(trimmedGroupId, out var group)
            ? group.ToString()
            : trimmedGroupId;
    }

    private static SourceScopePolicy CreateScopePolicy(
        AppSurfaceDocsMarkdownHarvestOptions? options,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return new SourceScopePolicy(
            sourceKind.ToString(),
            new AppSurfaceDocsHarvestPathMatcher(options?.IncludeGlobs ?? []),
            new AppSurfaceDocsHarvestPathMatcher(options?.ExcludeGlobs ?? []),
            CreateDisabledGroupSet(options?.DefaultExclusions?.DisabledGroups),
            CreateAllowMatchers(options?.DefaultExclusions?.AllowGlobs));
    }

    private static SourceScopePolicy CreateScopePolicy(
        AppSurfaceDocsCSharpHarvestOptions? options,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return new SourceScopePolicy(
            sourceKind.ToString(),
            new AppSurfaceDocsHarvestPathMatcher(options?.IncludeGlobs ?? []),
            new AppSurfaceDocsHarvestPathMatcher(options?.ExcludeGlobs ?? []),
            CreateDisabledGroupSet(options?.DefaultExclusions?.DisabledGroups),
            CreateAllowMatchers(options?.DefaultExclusions?.AllowGlobs));
    }

    private static SourceScopePolicy CreateScopePolicy(
        AppSurfaceDocsJavaScriptHarvestOptions? options,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return new SourceScopePolicy(
            sourceKind.ToString(),
            new AppSurfaceDocsHarvestPathMatcher(options?.IncludeGlobs ?? []),
            new AppSurfaceDocsHarvestPathMatcher(options?.ExcludeGlobs ?? []),
            CreateDisabledGroupSet(options?.DefaultExclusions?.DisabledGroups),
            CreateAllowMatchers(options?.DefaultExclusions?.AllowGlobs));
    }

    private static HashSet<AppSurfaceDocsHarvestDefaultExclusionGroup> CreateDisabledGroupSet(
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

    private static Dictionary<AppSurfaceDocsHarvestDefaultExclusionGroup, AppSurfaceDocsHarvestPathMatcher> CreateAllowMatchers(
        Dictionary<string, string[]>? allowGlobs)
    {
        var matchers = new Dictionary<AppSurfaceDocsHarvestDefaultExclusionGroup, AppSurfaceDocsHarvestPathMatcher>();
        foreach (var (groupId, patterns) in allowGlobs ?? [])
        {
            if (TryParseDefaultGroupName(groupId, out var group))
            {
                matchers[group] = new AppSurfaceDocsHarvestPathMatcher(patterns ?? []);
            }
        }

        return matchers;
    }

    private static AppSurfaceDocsHarvestPathDecision CreateDecision(
        bool included,
        string normalizedPath,
        AppSurfaceDocsHarvestSourceKind sourceKind,
        AppSurfaceDocsHarvestPathDecisionCode code,
        IReadOnlyList<AppSurfaceDocsHarvestPathRuleTrace> trace,
        string[] matchedDefaultGroups)
    {
        return new AppSurfaceDocsHarvestPathDecision(
            included,
            normalizedPath,
            sourceKind,
            code,
            trace,
            matchedDefaultGroups);
    }

    private static bool TryParseDefaultGroupName(
        string? groupId,
        out AppSurfaceDocsHarvestDefaultExclusionGroup group)
    {
        group = default;

        if (string.IsNullOrWhiteSpace(groupId))
        {
            return false;
        }

        var matchingName = Enum.GetNames<AppSurfaceDocsHarvestDefaultExclusionGroup>()
            .FirstOrDefault(name => name.Equals(groupId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matchingName is null)
        {
            return false;
        }

        group = Enum.Parse<AppSurfaceDocsHarvestDefaultExclusionGroup>(matchingName);
        return true;
    }

    private static bool IsBaseCandidate(
        string normalizedPath,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return sourceKind switch
        {
            AppSurfaceDocsHarvestSourceKind.Markdown => normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                                                   || normalizedPath.Equals("LICENSE", StringComparison.OrdinalIgnoreCase),
            AppSurfaceDocsHarvestSourceKind.CSharp => normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
            AppSurfaceDocsHarvestSourceKind.JavaScript => normalizedPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static AppSurfaceDocsHarvestDefaultExclusionGroup[] GetMatchingDefaultGroups(
        string normalizedPath,
        AppSurfaceDocsHarvestSourceKind sourceKind,
        bool treatLastSegmentAsDirectory)
    {
        var directorySegments = GetDirectorySegments(normalizedPath, treatLastSegmentAsDirectory);
        var groups = new List<AppSurfaceDocsHarvestDefaultExclusionGroup>();
        if (directorySegments.Any(segment => BuildOutputDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            groups.Add(AppSurfaceDocsHarvestDefaultExclusionGroup.BuildOutput);
        }

        if (directorySegments.Any(segment => segment.StartsWith(".", StringComparison.Ordinal)))
        {
            groups.Add(AppSurfaceDocsHarvestDefaultExclusionGroup.HiddenDirectories);
        }

        if (directorySegments.Any(IsTestProjectDirectorySegment))
        {
            groups.Add(AppSurfaceDocsHarvestDefaultExclusionGroup.TestProjects);
        }

        if (sourceKind == AppSurfaceDocsHarvestSourceKind.CSharp
            && directorySegments.Any(segment => segment.Equals("examples", StringComparison.OrdinalIgnoreCase)))
        {
            groups.Add(AppSurfaceDocsHarvestDefaultExclusionGroup.CSharpExampleSource);
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

    private SourceScopePolicy GetSourcePolicy(AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return sourceKind switch
        {
            AppSurfaceDocsHarvestSourceKind.Markdown => _markdownPolicy,
            AppSurfaceDocsHarvestSourceKind.CSharp => _csharpPolicy,
            AppSurfaceDocsHarvestSourceKind.JavaScript => _javascriptPolicy,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, null)
        };
    }

    private bool IsDefaultGroupDisabled(
        AppSurfaceDocsHarvestDefaultExclusionGroup group,
        SourceScopePolicy sourcePolicy)
    {
        return _globalDisabledGroups.Contains(group)
               || sourcePolicy.DisabledGroups.Contains(group);
    }

    private string? MatchDefaultGroupAllow(
        AppSurfaceDocsHarvestDefaultExclusionGroup group,
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

    private bool HasDefaultGroupAllowPatternForDescendants(
        AppSurfaceDocsHarvestDefaultExclusionGroup group,
        string normalizedDirectory,
        SourceScopePolicy sourcePolicy)
    {
        return _globalAllowMatchers.TryGetValue(group, out var globalMatcher)
               && globalMatcher.MatchDirectoryOrDescendant(normalizedDirectory) is not null
               || sourcePolicy.AllowMatchers.TryGetValue(group, out var sourceMatcher)
               && sourceMatcher.MatchDirectoryOrDescendant(normalizedDirectory) is not null;
    }

    private sealed class SourceScopePolicy(
        string scopeName,
        AppSurfaceDocsHarvestPathMatcher includeMatcher,
        AppSurfaceDocsHarvestPathMatcher excludeMatcher,
        HashSet<AppSurfaceDocsHarvestDefaultExclusionGroup> disabledGroups,
        Dictionary<AppSurfaceDocsHarvestDefaultExclusionGroup, AppSurfaceDocsHarvestPathMatcher> allowMatchers)
    {
        public string ScopeName { get; } = scopeName;

        public AppSurfaceDocsHarvestPathMatcher IncludeMatcher { get; } = includeMatcher;

        public AppSurfaceDocsHarvestPathMatcher ExcludeMatcher { get; } = excludeMatcher;

        public HashSet<AppSurfaceDocsHarvestDefaultExclusionGroup> DisabledGroups { get; } = disabledGroups;

        public Dictionary<AppSurfaceDocsHarvestDefaultExclusionGroup, AppSurfaceDocsHarvestPathMatcher> AllowMatchers { get; } = allowMatchers;
    }
}
