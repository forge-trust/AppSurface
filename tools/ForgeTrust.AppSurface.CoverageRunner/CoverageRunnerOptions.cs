namespace ForgeTrust.AppSurface.CoverageRunner;

/// <summary>
/// Parsed command-line and environment configuration for a coverage runner invocation.
/// </summary>
/// <remarks>
/// Command-line options have precedence over environment defaults. Positional arguments are
/// interpreted as <c>[solution] [output]</c> only when the matching named option was not supplied.
/// Relative user paths are resolved from <c>COVERAGE_CALLER_DIRECTORY</c> when the shell wrapper
/// provides it, otherwise from the current directory passed to <see cref="Parse"/>. Supported
/// groups are <c>all</c>, <c>core</c>, <c>tools</c>, <c>web</c>, <c>docs</c>,
/// <c>razorwire</c>, and <c>integration</c>. <c>--list-groups</c> is list-only and skips
/// unrelated environment validation; <c>--merge-only</c> skips project discovery, builds, and
/// tests, then uses <see cref="MergeSourceDirectory"/> as the existing artifact source.
/// </remarks>
internal sealed record CoverageRunnerOptions
{
    private static readonly string[] ValidGroups = ["all", "core", "tools", "web", "docs", "razorwire", "integration"];

    /// <summary>
    /// Gets the repository root inferred by walking upward for <c>ForgeTrust.AppSurface.slnx</c>,
    /// or the supplied current directory when no repository root can be found.
    /// </summary>
    public required string RepositoryRoot { get; init; }

    /// <summary>
    /// Gets the solution path used for test project discovery. Defaults to
    /// <c>ForgeTrust.AppSurface.slnx</c> under <see cref="RepositoryRoot"/> and can be overridden
    /// by positional argument one or <c>--solution</c>.
    /// </summary>
    public required string SolutionPath { get; init; }

    /// <summary>
    /// Gets the coverage output directory. Defaults to <c>TestResults/coverage-merged</c> under
    /// <see cref="RepositoryRoot"/> and can be overridden by positional argument two or
    /// <c>--output</c>.
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// Gets the selected test group. Defaults to <c>TEST_GROUP</c> or <c>all</c>; valid values
    /// are returned by <see cref="GetValidGroups"/>. Merge-only invocations set this to
    /// <c>merge-only</c> for summaries and timings.
    /// </summary>
    public required string GroupName { get; init; }

    /// <summary>
    /// Gets the build configuration supplied to <c>dotnet build</c> and <c>dotnet test</c>.
    /// Defaults to <c>BUILD_CONFIGURATION</c> or <c>Debug</c>.
    /// </summary>
    public required string BuildConfiguration { get; init; }

    /// <summary>
    /// Gets a value indicating whether solution build should run before test projects. Defaults
    /// to <c>true</c> for group <c>all</c> and <c>false</c> for named groups; <c>BUILD_SOLUTION</c>,
    /// <c>--build-solution</c>, and <c>--skip-solution-build</c> override that default. Merge-only
    /// always disables the build.
    /// </summary>
    public required bool BuildSolution { get; init; }

    /// <summary>
    /// Gets a value indicating whether <c>--no-restore</c> should be passed to build and test
    /// commands. Defaults to <c>true</c> only when <c>BUILD_NO_RESTORE=true</c>.
    /// </summary>
    public required bool BuildNoRestore { get; init; }

    /// <summary>
    /// Gets the coverlet include filter. Defaults to <c>[ForgeTrust.AppSurface.*]*</c>.
    /// </summary>
    public required string IncludeFilter { get; init; }

    /// <summary>
    /// Gets the coverlet exclude filter with commas escaped for MSBuild. Defaults to
    /// <c>[*.Tests]*,[*.IntegrationTests]*</c> before comma escaping.
    /// </summary>
    public required string ExcludeFilter { get; init; }

    /// <summary>
    /// Gets the maximum number of non-exclusive test projects that may run at once. Defaults to
    /// <c>COVERAGE_PARALLELISM</c> or <c>1</c>; invalid, zero, or negative values are parse
    /// failures except for list-only invocations.
    /// </summary>
    public required int Parallelism { get; init; }

    /// <summary>
    /// Gets a value indicating whether the invocation should only merge existing coverage
    /// artifacts. Merge-only mode skips solution discovery, builds, and tests.
    /// </summary>
    public required bool MergeOnly { get; init; }

    /// <summary>
    /// Gets the source directory for merge-only invocations. This value is required for
    /// <see cref="MergeOnly"/> and is resolved using the same caller-relative path rules as
    /// <see cref="SolutionPath"/> and <see cref="OutputDirectory"/>.
    /// </summary>
    public string? MergeSourceDirectory { get; init; }

    /// <summary>
    /// Gets a value indicating whether the invocation should list supported test groups and exit
    /// without validating unrelated environment values.
    /// </summary>
    public required bool ListGroups { get; init; }

    /// <summary>
    /// Parses coverage runner options from script-compatible arguments and environment variables.
    /// </summary>
    /// <param name="args">
    /// Script-compatible arguments. Named options override environment defaults and positional
    /// values; missing option values, unknown options, unexpected positional values, unknown
    /// groups, invalid <c>BUILD_SOLUTION</c>, and invalid <c>COVERAGE_PARALLELISM</c> return a
    /// failed parse result with exit code <c>2</c>.
    /// </param>
    /// <param name="currentDirectory">
    /// Current directory used for repository-root discovery and, when
    /// <c>COVERAGE_CALLER_DIRECTORY</c> is not supplied, relative path resolution.
    /// </param>
    /// <param name="environment">
    /// Environment variables used for defaults: <c>TEST_GROUP</c>, <c>BUILD_CONFIGURATION</c>,
    /// <c>BUILD_SOLUTION</c>, <c>BUILD_NO_RESTORE</c>, <c>COVERAGE_PARALLELISM</c>,
    /// <c>INCLUDE_FILTER</c>, <c>EXCLUDE_FILTER</c>, and <c>COVERAGE_CALLER_DIRECTORY</c>.
    /// </param>
    /// <returns>
    /// A parse result containing options on success, usage-only state for <c>--help</c>, or a
    /// validation error and usage flag for invalid invocations. <c>--list-groups</c> returns
    /// successful list-only options before validating unrelated environment values.
    /// </returns>
    public static CoverageRunnerParseResult Parse(
        string[] args,
        string currentDirectory,
        IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentNullException.ThrowIfNull(environment);

        var repositoryRoot = ResolveRepositoryRoot(currentDirectory);
        var callerDirectory = ReadEnvironment(environment, "COVERAGE_CALLER_DIRECTORY") ?? currentDirectory;
        var defaultSolutionPath = Path.Join(repositoryRoot, "ForgeTrust.AppSurface.slnx");
        var defaultOutputDirectory = Path.Join(repositoryRoot, "TestResults", "coverage-merged");

        var solutionPath = defaultSolutionPath;
        var outputDirectory = defaultOutputDirectory;
        var groupName = ReadEnvironment(environment, "TEST_GROUP") ?? "all";
        var buildConfiguration = ReadEnvironment(environment, "BUILD_CONFIGURATION") ?? "Debug";
        var buildNoRestore = string.Equals(ReadEnvironment(environment, "BUILD_NO_RESTORE"), "true", StringComparison.OrdinalIgnoreCase);
        var includeFilter = ReadEnvironment(environment, "INCLUDE_FILTER") ?? "[ForgeTrust.AppSurface.*]*";
        var excludeFilter = (ReadEnvironment(environment, "EXCLUDE_FILTER") ?? "[*.Tests]*,[*.IntegrationTests]*").Replace(",", "%2c", StringComparison.Ordinal);
        var buildSolutionValue = ReadEnvironment(environment, "BUILD_SOLUTION");
        var mergeOnly = false;
        var mergeSourceDirectory = (string?)null;
        var listGroups = false;
        var positionalCount = 0;

        for (var i = 0; i < args.Length;)
        {
            var argument = args[i];
            switch (argument)
            {
                case "--group":
                    if (i + 1 >= args.Length)
                    {
                        return CoverageRunnerParseResult.Fail(2, "--group requires a value");
                    }

                    groupName = args[i + 1];
                    i += 2;
                    break;
                case "--list-groups":
                    listGroups = true;
                    i++;
                    break;
                case "--merge-only":
                    if (i + 1 >= args.Length)
                    {
                        return CoverageRunnerParseResult.Fail(2, "--merge-only requires a source directory");
                    }

                    mergeOnly = true;
                    mergeSourceDirectory = args[i + 1];
                    i += 2;
                    break;
                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        return CoverageRunnerParseResult.Fail(2, "--output requires a value");
                    }

                    outputDirectory = args[i + 1];
                    i += 2;
                    break;
                case "--solution":
                    if (i + 1 >= args.Length)
                    {
                        return CoverageRunnerParseResult.Fail(2, "--solution requires a value");
                    }

                    solutionPath = args[i + 1];
                    i += 2;
                    break;
                case "--build-solution":
                    buildSolutionValue = "true";
                    i++;
                    break;
                case "--skip-solution-build":
                    buildSolutionValue = "false";
                    i++;
                    break;
                case "-h":
                case "--help":
                    return CoverageRunnerParseResult.Usage();
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        return CoverageRunnerParseResult.Fail(2, $"Unknown option: {argument}");
                    }

                    positionalCount++;
                    if (positionalCount == 1 && string.Equals(solutionPath, defaultSolutionPath, StringComparison.Ordinal))
                    {
                        solutionPath = argument;
                    }
                    else if (positionalCount == 2 && string.Equals(outputDirectory, defaultOutputDirectory, StringComparison.Ordinal))
                    {
                        outputDirectory = argument;
                    }
                    else
                    {
                        return CoverageRunnerParseResult.Fail(2, $"Unexpected argument: {argument}");
                    }

                    i++;
                    break;
            }
        }

        if (listGroups)
        {
            return CoverageRunnerParseResult.Success(new CoverageRunnerOptions
            {
                RepositoryRoot = Path.GetFullPath(repositoryRoot),
                SolutionPath = ResolveUserPath(solutionPath, callerDirectory),
                OutputDirectory = ResolveUserPath(outputDirectory, callerDirectory),
                GroupName = "all",
                BuildConfiguration = buildConfiguration,
                BuildSolution = false,
                BuildNoRestore = buildNoRestore,
                IncludeFilter = includeFilter,
                ExcludeFilter = excludeFilter,
                Parallelism = 1,
                MergeOnly = false,
                MergeSourceDirectory = null,
                ListGroups = true,
            });
        }

        if (!TryParseParallelism(ReadEnvironment(environment, "COVERAGE_PARALLELISM"), out var parallelism))
        {
            return CoverageRunnerParseResult.Fail(2, "COVERAGE_PARALLELISM must be a positive integer.");
        }

        var buildSolution = ParseBuildSolution(buildSolutionValue, groupName, mergeOnly, out var buildSolutionError);
        if (buildSolutionError is not null)
        {
            return CoverageRunnerParseResult.Fail(2, buildSolutionError);
        }

        if (mergeOnly)
        {
            groupName = "merge-only";
            buildSolution = false;
        }
        else if (!ValidGroups.Contains(groupName, StringComparer.Ordinal))
        {
            return CoverageRunnerParseResult.Fail(2, $"Unknown test group: {groupName}");
        }

        return CoverageRunnerParseResult.Success(new CoverageRunnerOptions
        {
            RepositoryRoot = Path.GetFullPath(repositoryRoot),
            SolutionPath = ResolveUserPath(solutionPath, callerDirectory),
            OutputDirectory = ResolveUserPath(outputDirectory, callerDirectory),
            GroupName = groupName,
            BuildConfiguration = buildConfiguration,
            BuildSolution = buildSolution,
            BuildNoRestore = buildNoRestore,
            IncludeFilter = includeFilter,
            ExcludeFilter = excludeFilter,
            Parallelism = parallelism,
            MergeOnly = mergeOnly,
            MergeSourceDirectory = mergeSourceDirectory is null ? null : ResolveUserPath(mergeSourceDirectory, callerDirectory),
            ListGroups = listGroups,
        });
    }

    /// <summary>
    /// Returns the supported test groups.
    /// </summary>
    /// <returns>Supported group names.</returns>
    public static IReadOnlyList<string> GetValidGroups() => ValidGroups;

    private static bool ParseBuildSolution(string? value, string groupName, bool mergeOnly, out string? error)
    {
        error = null;
        if (mergeOnly)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Equals(groupName, "all", StringComparison.Ordinal);
        }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        error = "BUILD_SOLUTION must be true or false.";
        return false;
    }

    private static bool TryParseParallelism(string? value, out int parallelism)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parallelism = 1;
            return true;
        }

        return int.TryParse(value, out parallelism) && parallelism > 0;
    }

    private static string? ReadEnvironment(IReadOnlyDictionary<string, string?> environment, string key)
    {
        return environment.TryGetValue(key, out var value) ? value : null;
    }

    private static string ResolveUserPath(string path, string callerDirectory)
    {
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, callerDirectory);
    }

    private static string ResolveRepositoryRoot(string currentDirectory)
    {
        var directory = new DirectoryInfo(currentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return currentDirectory;
    }
}

/// <summary>
/// Result of parsing command-line options.
/// </summary>
internal sealed record CoverageRunnerParseResult
{
    private CoverageRunnerParseResult(CoverageRunnerOptions? options, int exitCode, string? errorMessage, bool showUsage)
    {
        Options = options;
        ExitCode = exitCode;
        ErrorMessage = errorMessage;
        ShowUsage = showUsage;
    }

    /// <summary>
    /// Gets parsed options when parsing succeeds.
    /// </summary>
    public CoverageRunnerOptions? Options { get; }

    /// <summary>
    /// Gets the parse exit code.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Gets an error message for invalid invocations.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Gets a value indicating whether usage should be printed.
    /// </summary>
    public bool ShowUsage { get; }

    /// <summary>
    /// Creates a successful parse result.
    /// </summary>
    /// <param name="options">Parsed options.</param>
    /// <returns>A successful parse result.</returns>
    public static CoverageRunnerParseResult Success(CoverageRunnerOptions options) => new(options, 0, null, false);

    /// <summary>
    /// Creates a failed parse result.
    /// </summary>
    /// <param name="exitCode">Exit code to return.</param>
    /// <param name="errorMessage">Error message to print.</param>
    /// <returns>A failed parse result.</returns>
    public static CoverageRunnerParseResult Fail(int exitCode, string errorMessage) => new(null, exitCode, errorMessage, true);

    /// <summary>
    /// Creates a usage-only parse result.
    /// </summary>
    /// <returns>A usage-only parse result.</returns>
    public static CoverageRunnerParseResult Usage() => new(null, 0, null, true);
}
