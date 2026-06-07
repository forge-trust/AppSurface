using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Runs .NET test projects with Coverlet and produces merged private coverage artifacts.
/// </summary>
/// <remarks>
/// The command is a public package-consumer orchestrator for repositories that already own their
/// test instrumentation. It discovers or accepts test projects, runs <c>dotnet test</c> with
/// Coverlet MSBuild properties, preserves logs and per-project Cobertura files, merges coverage via
/// AppSurface's package-owned ReportGenerator dependency, and writes artifacts that
/// <c>appsurface coverage gate</c> can evaluate without additional path translation.
/// </remarks>
[Command("coverage run", Description = "Run instrumented .NET tests and merge Cobertura coverage locally.")]
internal sealed partial class CoverageRunCommand : ICommand
{
    private readonly CoverageRunWorkflow _workflow;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverageRunCommand"/> class.
    /// </summary>
    /// <param name="workflow">Coverage execution workflow.</param>
    public CoverageRunCommand(CoverageRunWorkflow workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    /// <summary>
    /// Gets or sets the solution used for discovery. Supports <c>.sln</c> and <c>.slnx</c>.
    /// </summary>
    [CommandOption("solution", Description = "Solution file used for test project discovery. Supports .sln and .slnx.")]
    public string? SolutionPath { get; set; }

    /// <summary>
    /// Gets or sets explicit test project paths. Repeat this option to skip solution discovery.
    /// </summary>
    [CommandOption("test-project", Description = "Repeatable explicit test project path. Skips solution discovery when supplied.")]
    public string[] TestProjects { get; set; } = [];

    /// <summary>
    /// Gets or sets the coverage output directory.
    /// </summary>
    [CommandOption("output", Description = "Coverage output directory. Defaults to TestResults/coverage-merged.")]
    public string OutputDirectory { get; set; } = Path.Join("TestResults", "coverage-merged");

    /// <summary>
    /// Gets or sets the build and test configuration.
    /// </summary>
    [CommandOption("configuration", Description = "Build/test configuration. Defaults to Debug.")]
    public string Configuration { get; set; } = "Debug";

    /// <summary>
    /// Gets or sets the maximum number of non-exclusive projects that may run at once.
    /// </summary>
    [CommandOption("parallelism", Description = "Positive integer for non-exclusive test project concurrency. Defaults to 1.")]
    public int Parallelism { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether build and test commands should pass <c>--no-restore</c>.
    /// </summary>
    [CommandOption("no-restore", Description = "Pass --no-restore to build and test commands.")]
    public bool NoRestore { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to build once before running tests.
    /// </summary>
    [CommandOption("build", Description = "Build the solution once before tests, including explicit-project runs.")]
    public bool Build { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to skip the solution build before tests.
    /// </summary>
    [CommandOption("no-build", Description = "Skip the solution build before tests.")]
    public bool NoBuild { get; set; }

    /// <summary>
    /// Gets or sets the optional Coverlet include filter.
    /// </summary>
    [CommandOption("include", Description = "Coverlet include filter. Omit for Coverlet's project defaults.")]
    public string? IncludeFilter { get; set; }

    /// <summary>
    /// Gets or sets the Coverlet exclude filter.
    /// </summary>
    [CommandOption("exclude", Description = "Coverlet exclude filter. Defaults to [*.Tests]*,[*.IntegrationTests]*.")]
    public string? ExcludeFilter { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether discovery and scheduling should be printed without running tests.
    /// </summary>
    [CommandOption("dry-run", Description = "Print discovery, scheduling, and artifact paths without running tests.")]
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether projects should be listed without running tests.
    /// </summary>
    [CommandOption("list-projects", Description = "List selected and skipped projects without running tests.")]
    public bool ListProjects { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether automatic integration/Playwright exclusive classification is disabled.
    /// </summary>
    [CommandOption("no-discover-exclusive", Description = "Do not auto-classify integration or Playwright projects as exclusive.")]
    public bool NoDiscoverExclusive { get; set; }

    /// <summary>
    /// Gets or sets projects that should run exclusively. Repeat this option for multiple projects.
    /// </summary>
    [CommandOption("exclusive-test-project", Description = "Repeatable project path or file name that should run exclusively.")]
    public string[] ExclusiveTestProjects { get; set; } = [];

    /// <summary>
    /// Gets or sets test logger arguments forwarded to <c>dotnet test</c>. Repeat this option for multiple loggers.
    /// </summary>
    [CommandOption("logger", Description = "Repeatable dotnet test logger value, such as junit;LogFilePath=... .")]
    public string[] Loggers { get; set; } = [];

    /// <summary>
    /// Gets or sets extra arguments appended to every <c>dotnet test</c> invocation.
    /// </summary>
    [CommandOption("test-argument", Description = "Repeatable extra argument token appended to every dotnet test invocation.")]
    public string[] TestArguments { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether existing owned output should not be cleaned before the run.
    /// </summary>
    [CommandOption("no-clean", Description = "Do not clean existing AppSurface-owned output before the run.")]
    public bool NoClean { get; set; }

    /// <summary>
    /// Gets or sets the <c>dotnet test</c> verbosity.
    /// </summary>
    [CommandOption("verbosity", Description = "dotnet test verbosity. Defaults to minimal.")]
    public string Verbosity { get; set; } = "minimal";

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await ExecuteAsync(console, console.RegisterCancellationHandler());
    }

    /// <summary>
    /// Executes the coverage run with an explicit cancellation token.
    /// </summary>
    /// <param name="console">Console used for user-visible output.</param>
    /// <param name="cancellationToken">Cancellation token for process and artifact IO.</param>
    /// <returns>A task that completes when the run finishes.</returns>
    internal async ValueTask ExecuteAsync(IConsole console, CancellationToken cancellationToken)
    {
        var request = CreateRequest();
        var result = await _workflow.RunAsync(request, console, cancellationToken);
        if (!result.Success)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV120",
                "Coverage run failed.",
                "One or more test, merge, or artifact steps returned a failure.",
                "Open the per-project logs and timings.json listed above.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }
    }

    private CoverageRunRequest CreateRequest()
    {
        if (Parallelism <= 0)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV101",
                "--parallelism must be a positive integer.",
                $"Received {Parallelism.ToString(CultureInfo.InvariantCulture)}.",
                "Pass --parallelism 1 or another positive integer.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        if (Build && NoBuild)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV101",
                "--build and --no-build cannot be used together.",
                "The command cannot both force and skip the solution build.",
                "Choose either --build, --no-build, or neither.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        return new CoverageRunRequest(
            SolutionPath,
            TestProjects,
            OutputDirectory,
            Configuration,
            Parallelism,
            NoRestore,
            Build,
            NoBuild,
            IncludeFilter,
            ExcludeFilter ?? "[*.Tests]*,[*.IntegrationTests]*",
            DryRun || ListProjects,
            NoDiscoverExclusive,
            ExclusiveTestProjects,
            Loggers,
            TestArguments,
            !NoClean,
            Verbosity);
    }
}

/// <summary>
/// Request for running and merging coverage from one or more .NET test projects.
/// </summary>
internal sealed record CoverageRunRequest(
    string? SolutionPath,
    IReadOnlyList<string> TestProjects,
    string OutputDirectory,
    string Configuration,
    int Parallelism,
    bool NoRestore,
    bool Build,
    bool NoBuild,
    string? IncludeFilter,
    string ExcludeFilter,
    bool DryRun,
    bool NoDiscoverExclusive,
    IReadOnlyList<string> ExclusiveTestProjects,
    IReadOnlyList<string> Loggers,
    IReadOnlyList<string> TestArguments,
    bool Clean,
    string Verbosity);

/// <summary>
/// Result of a public coverage run.
/// </summary>
/// <param name="Success">Whether all required test, merge, and artifact steps succeeded.</param>
/// <param name="OutputDirectory">Absolute output directory.</param>
/// <param name="CoveragePath">Absolute merged Cobertura path.</param>
internal sealed record CoverageRunResult(bool Success, string OutputDirectory, string CoveragePath);

/// <summary>
/// Coordinates public coverage run discovery, scheduling, test execution, merge, and artifacts.
/// </summary>
internal sealed class CoverageRunWorkflow
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
    };

    private readonly ICoverageRunProcessRunner _processRunner;
    private readonly ICoverageRunReportGenerator _reportGenerator;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverageRunWorkflow"/> class.
    /// </summary>
    /// <param name="processRunner">Process runner for dotnet commands.</param>
    /// <param name="reportGenerator">Package-owned ReportGenerator wrapper.</param>
    /// <param name="timeProvider">Time provider used for timings.</param>
    public CoverageRunWorkflow(
        ICoverageRunProcessRunner processRunner,
        ICoverageRunReportGenerator reportGenerator,
        TimeProvider timeProvider)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Runs the coverage workflow.
    /// </summary>
    /// <param name="request">Coverage run request.</param>
    /// <param name="console">Console used for user-visible output.</param>
    /// <param name="cancellationToken">Cancellation token for process and artifact IO.</param>
    /// <returns>Coverage run result.</returns>
    public async Task<CoverageRunResult> RunAsync(
        CoverageRunRequest request,
        IConsole console,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(console);

        var currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        var resolution = await ResolveProjectsAsync(request, currentDirectory, cancellationToken);
        var outputDirectory = ResolveUserPath(request.OutputDirectory, currentDirectory);

        await PrintDiscoveryAsync(console, request, resolution, outputDirectory);
        if (request.DryRun)
        {
            CoverageRunOutputGuard.Validate(outputDirectory, resolution.SolutionDirectory, resolution.Projects);
            return new CoverageRunResult(true, outputDirectory, Path.Join(outputDirectory, "coverage.cobertura.xml"));
        }

        CoverageRunOutputGuard.Prepare(outputDirectory, resolution.SolutionDirectory, resolution.Projects, request.Clean);
        var runStarted = _timeProvider.GetTimestamp();
        var buildSeconds = await BuildIfNeededAsync(request, resolution, console, cancellationToken);
        var projectResults = await RunScheduledProjectsAsync(request, resolution, outputDirectory, console, cancellationToken);
        await ReplayLogsAsync(projectResults, console, cancellationToken);

        var coverageFiles = Directory.Exists(Path.Join(outputDirectory, "projects"))
            ? Directory.EnumerateFiles(Path.Join(outputDirectory, "projects"), "coverage.cobertura.xml", SearchOption.AllDirectories).ToArray()
            : [];
        var failedProjects = projectResults.Where(result => result.ExitCode != 0).ToArray();
        if (failedProjects.Length > 0 && coverageFiles.Length == 0)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV120",
                "Coverage run failed before coverage artifacts were produced.",
                $"{failedProjects.Length.ToString(CultureInfo.InvariantCulture)} test project(s) exited nonzero.",
                "Open the listed project log, fix the test/build failure, then rerun coverage run.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics",
                failedProjects[0].LogFile);
        }

        if (coverageFiles.Length == 0)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV103",
                "No coverage files were produced.",
                "The selected test projects ran without writing Coverlet Cobertura artifacts.",
                "Add coverlet.msbuild to each selected test project, then rerun coverage run.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#add-coverlet-first",
                projectResults.Select(result => result.LogFile).FirstOrDefault(File.Exists));
        }

        var mergeStarted = _timeProvider.GetTimestamp();
        var mergeDirectory = Path.Join(outputDirectory, "reportgenerator");
        Directory.CreateDirectory(mergeDirectory);
        var merge = await _reportGenerator.MergeAsync(coverageFiles, mergeDirectory, cancellationToken);
        var mergeSeconds = ElapsedSeconds(mergeStarted);
        if (merge.ExitCode != 0 || !File.Exists(merge.CoberturaPath))
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV104",
                "Coverage merge failed.",
                $"ReportGenerator exit code: {merge.ExitCode.ToString(CultureInfo.InvariantCulture)}.",
                "Inspect per-project Cobertura files and rerun with --dry-run to verify selected projects.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        var mergedCoveragePath = Path.Join(outputDirectory, "coverage.cobertura.xml");
        File.Copy(merge.CoberturaPath, mergedCoveragePath, overwrite: true);
        if (File.Exists(merge.SummaryPath))
        {
            File.Copy(merge.SummaryPath, Path.Join(outputDirectory, "reportgenerator-summary.txt"), overwrite: true);
        }

        await WriteSummaryAsync(outputDirectory, mergedCoveragePath, console, cancellationToken);
        await WriteTimingsAsync(
            request,
            resolution,
            outputDirectory,
            mergedCoveragePath,
            buildSeconds,
            mergeSeconds,
            ElapsedSeconds(runStarted),
            merge.ExitCode,
            projectResults,
            cancellationToken);

        await console.Output.WriteLineAsync($"Coverage artifacts: {outputDirectory}");
        await console.Output.WriteLineAsync($"Next: appsurface coverage gate --coverage {mergedCoveragePath} --min-line <percent> --min-branch <percent>");

        return new CoverageRunResult(projectResults.All(result => result.ExitCode == 0), outputDirectory, mergedCoveragePath);
    }

    private async Task<CoverageProjectResolution> ResolveProjectsAsync(
        CoverageRunRequest request,
        string currentDirectory,
        CancellationToken cancellationToken)
    {
        var explicitProjects = request.TestProjects.Where(project => !string.IsNullOrWhiteSpace(project)).ToArray();
        if (explicitProjects.Length > 0)
        {
            var explicitProjectModels = await CreateProjectsAsync(
                explicitProjects,
                skippedProjects: [],
                solutionDirectory: currentDirectory,
                request,
                cancellationToken);
            return new CoverageProjectResolution(null, currentDirectory, explicitProjectModels, []);
        }

        var solutionPath = ResolveSolutionPath(request.SolutionPath, currentDirectory);
        var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? currentDirectory;
        var list = await _processRunner.RunAsync("dotnet", ["sln", solutionPath, "list"], solutionDirectory, cancellationToken);
        if (list.ExitCode != 0)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV102",
                "Failed to list solution projects.",
                $"dotnet sln exited with {list.ExitCode.ToString(CultureInfo.InvariantCulture)}.",
                "Pass --test-project for explicit project selection, or fix the solution file.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        var included = new List<string>();
        var skipped = new List<CoverageSkippedProject>();
        foreach (var candidate in list.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()))
        {
            if (!candidate.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsDiscoveredTestProject(candidate))
            {
                included.Add(candidate);
            }
            else
            {
                skipped.Add(new CoverageSkippedProject(candidate, "project name does not match *Tests.csproj or *IntegrationTests.csproj"));
            }
        }

        var projects = await CreateProjectsAsync(included, skipped, solutionDirectory, request, cancellationToken);
        return new CoverageProjectResolution(solutionPath, solutionDirectory, projects, skipped);
    }

    private static string ResolveSolutionPath(string? requestedSolution, string currentDirectory)
    {
        if (!string.IsNullOrWhiteSpace(requestedSolution))
        {
            var resolved = ResolveUserPath(requestedSolution.Trim(), currentDirectory);
            if (!File.Exists(resolved))
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV102",
                    "Solution file not found.",
                    $"Path: {resolved}.",
                    "Pass --solution with an existing .sln or .slnx file, or pass --test-project.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }

            if (!resolved.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                && !resolved.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV102",
                    "--solution must point to a .sln or .slnx file.",
                    $"Path: {resolved}.",
                    "Pass a .sln/.slnx path or use repeated --test-project options.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }

            return resolved;
        }

        var solutions = Directory.EnumerateFiles(currentDirectory, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(currentDirectory, "*.slnx", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return solutions.Length switch
        {
            1 => solutions[0],
            0 => throw CoverageRunDiagnostics.Create(
                "ASCOV102",
                "No solution file was found.",
                $"Directory: {currentDirectory}.",
                "Pass --solution or one or more --test-project options.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics"),
            _ => throw CoverageRunDiagnostics.Create(
                "ASCOV102",
                "Multiple solution files were found.",
                string.Join(Environment.NewLine, solutions.Select(path => $"  - {path}")),
                "Pass --solution with the intended .sln or .slnx file.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics"),
        };
    }

    private static async Task<IReadOnlyList<CoverageRunProject>> CreateProjectsAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<CoverageSkippedProject> skippedProjects,
        string solutionDirectory,
        CoverageRunRequest request,
        CancellationToken cancellationToken)
    {
        if (projectPaths.Count == 0)
        {
            var skippedHint = skippedProjects.Count == 0
                ? "No .csproj entries were returned by discovery."
                : $"Skipped {skippedProjects.Count.ToString(CultureInfo.InvariantCulture)} non-test project(s).";
            throw CoverageRunDiagnostics.Create(
                "ASCOV105",
                "No test projects were selected.",
                skippedHint,
                "Pass one or more --test-project options, or rename test projects to match *Tests.csproj.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        var projects = new List<CoverageRunProject>();
        foreach (var project in projectPaths)
        {
            var fullPath = ResolveUserPath(project, solutionDirectory);
            if (!File.Exists(fullPath))
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV101",
                    "Test project file not found.",
                    $"Path: {fullPath}.",
                    "Pass an existing test project path.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }

            var projectContents = await File.ReadAllTextAsync(fullPath, cancellationToken);
            var relative = Path.IsPathRooted(project) ? Path.GetRelativePath(solutionDirectory, fullPath) : project;
            var isExclusive = IsExclusive(relative, fullPath, projectContents, request);
            projects.Add(new CoverageRunProject(relative, fullPath, string.Empty, isExclusive));
        }

        return AllocateProjectSlugs(projects);
    }

    private static IReadOnlyList<CoverageRunProject> AllocateProjectSlugs(IReadOnlyList<CoverageRunProject> projects)
    {
        return projects
            .Select(project =>
            {
                var name = SanitizePathSegment(Path.GetFileNameWithoutExtension(project.FullPath));
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(project.FullPath).ToUpperInvariant())))[..8].ToLowerInvariant();
                return project with { Slug = $"{name}-{hash}" };
            })
            .ToArray();
    }

    private static bool IsDiscoveredTestProject(string projectPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(projectPath);
        return fileName.EndsWith("Tests", StringComparison.Ordinal)
            || fileName.EndsWith(".Tests", StringComparison.Ordinal)
            || fileName.EndsWith(".IntegrationTests", StringComparison.Ordinal);
    }

    private static bool IsExclusive(
        string relativePath,
        string fullPath,
        string projectContents,
        CoverageRunRequest request)
    {
        if (request.ExclusiveTestProjects.Any(value => MatchesProject(value, relativePath, fullPath)))
        {
            return true;
        }

        if (request.NoDiscoverExclusive)
        {
            return false;
        }

        var normalized = Normalize(relativePath);
        return normalized.EndsWith(".IntegrationTests.csproj", StringComparison.Ordinal)
            || normalized.Contains("/IntegrationTests/", StringComparison.Ordinal)
            || projectContents.Contains("Microsoft.Playwright", StringComparison.OrdinalIgnoreCase)
            || projectContents.Contains("Playwright", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesProject(string candidate, string relativePath, string fullPath)
    {
        return string.Equals(candidate, relativePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, Path.GetFileName(fullPath), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> BuildIfNeededAsync(
        CoverageRunRequest request,
        CoverageProjectResolution resolution,
        IConsole console,
        CancellationToken cancellationToken)
    {
        var shouldBuild = request.Build || (!request.NoBuild && resolution.SolutionPath is not null && request.TestProjects.Count == 0);
        if (!shouldBuild)
        {
            await console.Output.WriteLineAsync("Build: skipped; test projects will build as needed.");
            return 0;
        }

        if (resolution.SolutionPath is null)
        {
            return await BuildExplicitProjectsAsync(request, resolution, console, cancellationToken);
        }

        await console.Output.WriteLineAsync($"Building {resolution.SolutionPath}...");
        var started = _timeProvider.GetTimestamp();
        var args = new List<string> { "build", resolution.SolutionPath, "--configuration", request.Configuration, "-v", "minimal" };
        if (request.NoRestore)
        {
            args.Add("--no-restore");
        }

        var result = await _processRunner.RunAsync("dotnet", args, resolution.SolutionDirectory, cancellationToken);
        await console.Output.WriteAsync(result.Output);
        if (result.ExitCode != 0)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV110",
                "Solution build failed.",
                $"dotnet build exited with {result.ExitCode.ToString(CultureInfo.InvariantCulture)}.",
                "Fix the build failure or pass --no-build when projects should build independently.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        return ElapsedSeconds(started);
    }

    private async Task<long> BuildExplicitProjectsAsync(
        CoverageRunRequest request,
        CoverageProjectResolution resolution,
        IConsole console,
        CancellationToken cancellationToken)
    {
        await console.Output.WriteLineAsync($"Building {resolution.Projects.Count.ToString(CultureInfo.InvariantCulture)} selected test project(s)...");
        var started = _timeProvider.GetTimestamp();
        foreach (var project in resolution.Projects)
        {
            var args = new List<string> { "build", project.FullPath, "--configuration", request.Configuration, "-v", "minimal" };
            if (request.NoRestore)
            {
                args.Add("--no-restore");
            }

            var result = await _processRunner.RunAsync("dotnet", args, resolution.SolutionDirectory, cancellationToken);
            await console.Output.WriteAsync(result.Output);
            if (result.ExitCode != 0)
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV110",
                    "Test project build failed.",
                    $"dotnet build exited with {result.ExitCode.ToString(CultureInfo.InvariantCulture)} for {project.RelativePath}.",
                    "Fix the build failure or omit --build so dotnet test builds the project.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }
        }

        return ElapsedSeconds(started);
    }

    private async Task<IReadOnlyList<CoverageProjectRunResult>> RunScheduledProjectsAsync(
        CoverageRunRequest request,
        CoverageProjectResolution resolution,
        string outputDirectory,
        IConsole console,
        CancellationToken cancellationToken)
    {
        var results = new CoverageProjectRunResult[resolution.Projects.Count];
        var active = new List<Task<CoverageProjectRunResult>>();

        for (var i = 0; i < resolution.Projects.Count; i++)
        {
            var project = resolution.Projects[i];
            if (project.IsExclusive)
            {
                await DrainActiveAsync(active, results);
                results[i] = await RunProjectAsync(request, resolution, outputDirectory, project, i, console, cancellationToken);
                continue;
            }

            while (active.Count >= request.Parallelism)
            {
                await DrainOneAsync(active, results);
            }

            active.Add(RunProjectAsync(request, resolution, outputDirectory, project, i, console, cancellationToken));
        }

        await DrainActiveAsync(active, results);
        return results;
    }

    private static async Task DrainOneAsync(List<Task<CoverageProjectRunResult>> active, CoverageProjectRunResult[] results)
    {
        var completed = await Task.WhenAny(active);
        active.Remove(completed);
        var result = await completed;
        results[result.Index] = result;
    }

    private static async Task DrainActiveAsync(List<Task<CoverageProjectRunResult>> active, CoverageProjectRunResult[] results)
    {
        while (active.Count > 0)
        {
            await DrainOneAsync(active, results);
        }
    }

    private async Task<CoverageProjectRunResult> RunProjectAsync(
        CoverageRunRequest request,
        CoverageProjectResolution resolution,
        string outputDirectory,
        CoverageRunProject project,
        int index,
        IConsole console,
        CancellationToken cancellationToken)
    {
        var projectOutputDirectory = Path.Join(outputDirectory, "projects", project.Slug);
        Directory.CreateDirectory(projectOutputDirectory);
        var logFile = Path.Join(projectOutputDirectory, "dotnet-test.log");
        var started = _timeProvider.GetTimestamp();

        await console.Output.WriteLineAsync(
            $"[{index + 1}/{resolution.Projects.Count}] starting {project.RelativePath}{(project.IsExclusive ? " (exclusive)" : string.Empty)}");

        var args = CreateTestArguments(request, project, projectOutputDirectory);
        var processResult = await _processRunner.RunAsync("dotnet", args, resolution.SolutionDirectory, cancellationToken, outputFile: logFile);
        var seconds = ElapsedSeconds(started);

        await console.Output.WriteLineAsync(
            $"[{index + 1}/{resolution.Projects.Count}] finished {project.RelativePath} in {seconds}s (exit {processResult.ExitCode})");
        if (processResult.ExitCode != 0)
        {
            await console.Error.WriteLineAsync($"Test run failed for {project.RelativePath}; log: {logFile}");
        }

        return new CoverageProjectRunResult(index, project, seconds, processResult.ExitCode, logFile);
    }

    private static IReadOnlyList<string> CreateTestArguments(
        CoverageRunRequest request,
        CoverageRunProject project,
        string projectOutputDirectory)
    {
        var args = new List<string>
        {
            "test",
            project.FullPath,
            "--configuration",
            request.Configuration,
            "-v",
            request.Verbosity,
        };
        foreach (var logger in request.Loggers)
        {
            args.Add($"--logger:{logger}");
        }

        args.Add("/p:CollectCoverage=true");
        args.Add($"/p:CoverletOutput={Path.Join(projectOutputDirectory, "coverage")}");
        args.Add("/p:CoverletOutputFormat=cobertura");
        if (!string.IsNullOrWhiteSpace(request.IncludeFilter))
        {
            args.Add($"/p:Include={request.IncludeFilter}");
        }

        if (!string.IsNullOrWhiteSpace(request.ExcludeFilter))
        {
            args.Add($"/p:Exclude={request.ExcludeFilter.Replace(",", "%2c", StringComparison.Ordinal)}");
        }

        if (request.NoRestore)
        {
            args.Add("--no-restore");
        }

        args.AddRange(request.TestArguments);
        return args;
    }

    private static async Task ReplayLogsAsync(IReadOnlyList<CoverageProjectRunResult> results, IConsole console, CancellationToken cancellationToken)
    {
        foreach (var result in results.OrderBy(result => result.Index))
        {
            await console.Output.WriteLineAsync($"--- BEGIN {result.Project.RelativePath} ---");
            if (File.Exists(result.LogFile))
            {
                var log = await File.ReadAllTextAsync(result.LogFile, cancellationToken);
                if (!string.IsNullOrEmpty(log))
                {
                    const int maxReplayCharacters = 80_000;
                    await console.Output.WriteAsync(log.Length > maxReplayCharacters
                        ? log[..maxReplayCharacters] + Environment.NewLine + "[log truncated; see full log on disk]" + Environment.NewLine
                        : log);
                }
            }

            await console.Output.WriteLineAsync($"--- END {result.Project.RelativePath} (exit {result.ExitCode}) ---");
        }
    }

    private static async Task PrintDiscoveryAsync(
        IConsole console,
        CoverageRunRequest request,
        CoverageProjectResolution resolution,
        string outputDirectory)
    {
        await console.Output.WriteLineAsync($"Coverage run inputs");
        await console.Output.WriteLineAsync($"  Solution: {resolution.SolutionPath ?? "(explicit test projects)"}");
        await console.Output.WriteLineAsync($"  Output: {outputDirectory}");
        await console.Output.WriteLineAsync($"  Configuration: {request.Configuration}");
        await console.Output.WriteLineAsync($"  Parallelism: {request.Parallelism.ToString(CultureInfo.InvariantCulture)}");
        await console.Output.WriteLineAsync($"  Include: {request.IncludeFilter ?? "(Coverlet default)"}");
        await console.Output.WriteLineAsync($"  Exclude: {request.ExcludeFilter}");
        await console.Output.WriteLineAsync($"Discovered {resolution.Projects.Count.ToString(CultureInfo.InvariantCulture)} test project(s).");

        foreach (var project in resolution.Projects)
        {
            await console.Output.WriteLineAsync(
                $"  include {(project.IsExclusive ? "exclusive" : "parallel ")} {project.RelativePath} -> projects/{project.Slug}");
        }

        foreach (var skipped in resolution.SkippedProjects)
        {
            await console.Output.WriteLineAsync($"  skip {skipped.ProjectPath}: {skipped.Reason}");
        }
    }

    private static async Task WriteSummaryAsync(
        string outputDirectory,
        string coveragePath,
        IConsole console,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(coveragePath);
        using var reader = XmlReader.Create(stream, ReaderSettings);
        var root = XDocument.Load(reader).Root;
        if (root is null || !string.Equals(root.Name.LocalName, "coverage", StringComparison.Ordinal))
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV106",
                "Merged Cobertura file is malformed.",
                $"Path: {coveragePath}.",
                "Regenerate coverage and inspect ReportGenerator output.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        var linesCovered = ReadDecimal(root, "lines-covered");
        var linesValid = ReadDecimal(root, "lines-valid");
        var branchesCovered = ReadDecimal(root, "branches-covered");
        var branchesValid = ReadDecimal(root, "branches-valid");
        var linePercent = linesValid > 0 ? linesCovered * 100m / linesValid : ReadRate(root, "line-rate") * 100m;
        var branchPercent = branchesValid > 0 ? branchesCovered * 100m / branchesValid : ReadRate(root, "branch-rate") * 100m;
        var summary = FormattableString.Invariant($"""
            Coverage run summary
            Line coverage: {linePercent:0.00}%
            Branch coverage: {branchPercent:0.00}%
            Cobertura: {coveragePath}
            Timings: {Path.Join(outputDirectory, "timings.json")}
            """);

        await File.WriteAllTextAsync(Path.Join(outputDirectory, "summary.txt"), summary, cancellationToken);
        await console.Output.WriteLineAsync(summary);
    }

    private static decimal ReadDecimal(XElement root, string name)
    {
        return decimal.TryParse(root.Attribute(name)?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    private static decimal ReadRate(XElement root, string name)
    {
        return decimal.TryParse(root.Attribute(name)?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    private static async Task WriteTimingsAsync(
        CoverageRunRequest request,
        CoverageProjectResolution resolution,
        string outputDirectory,
        string coveragePath,
        long buildSeconds,
        long mergeSeconds,
        long totalSeconds,
        int mergeExitCode,
        IReadOnlyList<CoverageProjectRunResult> projectResults,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            solution = resolution.SolutionPath,
            configuration = request.Configuration,
            buildSolution = buildSeconds > 0,
            parallelism = request.Parallelism,
            durations = new
            {
                solutionBuildSeconds = buildSeconds,
                coverageMergeSeconds = mergeSeconds,
                totalSeconds,
            },
            merge = new
            {
                exitCode = mergeExitCode,
            },
            artifacts = new
            {
                coverageFiles = projectResults.Count,
                cobertura = coveragePath,
            },
            projects = projectResults
                .OrderBy(result => result.Index)
                .Select(result => new
                {
                    project = result.Project.RelativePath,
                    slug = result.Project.Slug,
                    seconds = result.Seconds,
                    exitCode = result.ExitCode,
                    exclusive = result.Project.IsExclusive,
                    log = result.LogFile,
                }),
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Join(outputDirectory, "timings.json"), json + Environment.NewLine, cancellationToken);
    }

    private long ElapsedSeconds(long started)
    {
        return (long)_timeProvider.GetElapsedTime(started).TotalSeconds;
    }

    private static string ResolveUserPath(string path, string currentDirectory)
    {
        try
        {
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, currentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV101",
                "Path value is invalid.",
                ex.Message,
                "Pass a valid filesystem path.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var sanitized = string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-'));
        return string.IsNullOrWhiteSpace(sanitized) ? "project" : sanitized;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

internal sealed record CoverageProjectResolution(
    string? SolutionPath,
    string SolutionDirectory,
    IReadOnlyList<CoverageRunProject> Projects,
    IReadOnlyList<CoverageSkippedProject> SkippedProjects);

internal sealed record CoverageRunProject(
    string RelativePath,
    string FullPath,
    string Slug,
    bool IsExclusive);

internal sealed record CoverageSkippedProject(string ProjectPath, string Reason);

internal sealed record CoverageProjectRunResult(
    int Index,
    CoverageRunProject Project,
    long Seconds,
    int ExitCode,
    string LogFile);

/// <summary>
/// Runs external processes for the coverage run workflow.
/// </summary>
internal interface ICoverageRunProcessRunner
{
    /// <summary>
    /// Runs a process and returns its exit code plus captured output.
    /// </summary>
    Task<CoverageRunProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        string? outputFile = null);
}

/// <summary>
/// Result of running a coverage workflow process.
/// </summary>
internal sealed record CoverageRunProcessResult(int ExitCode, string Output);

internal sealed class SystemCoverageRunProcessRunner : ICoverageRunProcessRunner
{
    public async Task<CoverageRunProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        string? outputFile = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw CoverageRunDiagnostics.Create(
                "ASCOV110",
                "Failed to start dotnet.",
                $"Command: {fileName}.",
                "Verify the .NET SDK is installed and available on PATH.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        var output = standardOutput + standardError;
        if (outputFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? workingDirectory);
            await File.WriteAllTextAsync(outputFile, output, cancellationToken);
        }

        return new CoverageRunProcessResult(process.ExitCode, output);
    }
}

/// <summary>
/// Runs AppSurface's package-owned ReportGenerator dependency.
/// </summary>
internal interface ICoverageRunReportGenerator
{
    /// <summary>
    /// Merges Cobertura coverage files into one ReportGenerator output directory.
    /// </summary>
    Task<CoverageRunMergeResult> MergeAsync(
        IReadOnlyList<string> coverageFiles,
        string outputDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of a ReportGenerator merge.
/// </summary>
internal sealed record CoverageRunMergeResult(int ExitCode, string CoberturaPath, string SummaryPath);

internal sealed class CoverageRunReportGenerator : ICoverageRunReportGenerator
{
    private readonly ICoverageRunProcessRunner _processRunner;
    private readonly IReportGeneratorPackageLocator _locator;

    public CoverageRunReportGenerator(ICoverageRunProcessRunner processRunner, IReportGeneratorPackageLocator locator)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    public async Task<CoverageRunMergeResult> MergeAsync(
        IReadOnlyList<string> coverageFiles,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var reportGeneratorDll = _locator.ResolveReportGeneratorDll();
        var reports = string.Join(';', coverageFiles);
        var result = await _processRunner.RunAsync(
            "dotnet",
            [reportGeneratorDll, $"-reports:{reports}", $"-targetdir:{outputDirectory}", "-reporttypes:Cobertura;TextSummary"],
            Directory.GetCurrentDirectory(),
            cancellationToken);

        return new CoverageRunMergeResult(
            result.ExitCode,
            Path.Join(outputDirectory, "Cobertura.xml"),
            Path.Join(outputDirectory, "Summary.txt"));
    }
}

/// <summary>
/// Locates the ReportGenerator package restored as a dependency of the AppSurface CLI package.
/// </summary>
internal interface IReportGeneratorPackageLocator
{
    /// <summary>
    /// Resolves the ReportGenerator DLL entrypoint.
    /// </summary>
    string ResolveReportGeneratorDll();
}

internal sealed class ReportGeneratorPackageLocator : IReportGeneratorPackageLocator
{
    internal const string Version = "5.5.10";

    public string ResolveReportGeneratorDll()
    {
        var roots = new[]
            {
                Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
            }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var target in new[] { "net10.0", "net8.0" })
            {
                var candidate = Path.Join(root, "reportgenerator", Version, "tools", target, "ReportGenerator.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw CoverageRunDiagnostics.Create(
            "ASCOV114",
            "ReportGenerator package dependency was not found.",
            $"Expected ReportGenerator {Version} under the NuGet package cache.",
            "Restore or reinstall ForgeTrust.AppSurface.Cli so its package-owned dependencies are present.",
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
    }
}

internal static class CoverageRunOutputGuard
{
    private const string MarkerFileName = ".appsurface-coverage-output";

    public static void Validate(
        string outputDirectory,
        string solutionDirectory,
        IReadOnlyList<CoverageRunProject> projects)
    {
        ValidateCore(outputDirectory, solutionDirectory, projects);
    }

    public static void Prepare(
        string outputDirectory,
        string solutionDirectory,
        IReadOnlyList<CoverageRunProject> projects,
        bool clean)
    {
        ValidateCore(outputDirectory, solutionDirectory, projects);

        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var marker = Path.Join(output, MarkerFileName);
        if (clean && File.Exists(marker))
        {
            DeleteKnownOutput(output);
        }

        File.WriteAllText(marker, "AppSurface coverage output directory" + Environment.NewLine);
        Directory.CreateDirectory(Path.Join(output, "projects"));
    }

    private static void ValidateCore(
        string outputDirectory,
        string solutionDirectory,
        IReadOnlyList<CoverageRunProject> projects)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV109",
                "--output must point to a coverage artifact directory.",
                "The output path was blank.",
                "Pass a dedicated output directory such as TestResults/coverage-merged.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        var output = Path.GetFullPath(outputDirectory);
        if (File.Exists(output))
        {
            throw UnsafeOutput($"--output points to a file: {output}");
        }

        var comparison = GetPathComparison();
        var trimmedOutput = Trim(output);
        var root = Path.GetPathRoot(output);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(trimmedOutput, Trim(root), comparison))
        {
            throw UnsafeOutput("--output must not be a filesystem root.");
        }

        var current = Trim(Path.GetFullPath(Directory.GetCurrentDirectory()));
        if (string.Equals(trimmedOutput, current, comparison))
        {
            throw UnsafeOutput("--output must not be the current working directory.");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && string.Equals(trimmedOutput, Trim(home), comparison))
        {
            throw UnsafeOutput("--output must not be the user home directory.");
        }

        var solution = Trim(Path.GetFullPath(solutionDirectory));
        if (string.Equals(trimmedOutput, solution, comparison))
        {
            throw UnsafeOutput("--output must not be the solution directory.");
        }

        foreach (var projectDirectory in projects.Select(project => Path.GetDirectoryName(project.FullPath)))
        {
            if (projectDirectory is not null && string.Equals(trimmedOutput, Trim(projectDirectory), comparison))
            {
                throw UnsafeOutput("--output must not be a test project directory.");
            }
        }

        var marker = Path.Join(output, MarkerFileName);
        if (Directory.Exists(output))
        {
            var entries = Directory.EnumerateFileSystemEntries(output)
                .Where(path => !string.Equals(Path.GetFileName(path), MarkerFileName, StringComparison.Ordinal))
                .ToArray();
            if (entries.Length > 0 && !File.Exists(marker))
            {
                throw UnsafeOutput("--output already contains files and is not marked as AppSurface-owned.");
            }
        }
    }

    private static void DeleteKnownOutput(string output)
    {
        foreach (var path in new[] { "coverage.cobertura.xml", "coverage.json", "summary.txt", "timings.json", "reportgenerator-summary.txt" }
            .Select(file => Path.Join(output, file)))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        foreach (var junitFile in Directory.EnumerateFiles(output, "junit-*.xml", SearchOption.TopDirectoryOnly))
        {
            File.Delete(junitFile);
        }

        foreach (var path in new[] { "projects", "reportgenerator" }.Select(directory => Path.Join(output, directory)))
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static CommandException UnsafeOutput(string cause)
    {
        return CoverageRunDiagnostics.Create(
            "ASCOV109",
            "Coverage output path is unsafe.",
            cause,
            "Use a dedicated artifact directory, for example TestResults/coverage-merged.",
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}

internal static class CoverageRunDiagnostics
{
    public static CommandException Create(
        string code,
        string problem,
        string cause,
        string fix,
        string docs,
        string? logPath = null)
    {
        var builder = new StringBuilder();
        builder.Append(code).Append(' ').Append(problem);
        builder.Append(" Cause: ").Append(cause);
        builder.Append(" Fix: ").Append(fix);
        builder.Append(" Docs: ").Append(docs).Append('.');
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            builder.Append(" Log: ").Append(logPath).Append('.');
        }

        return new CommandException(builder.ToString());
    }
}
