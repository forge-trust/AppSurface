using System.Buffers;
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
using CliWrap;
using CliWrap.Buffered;
using CliWrap.Exceptions;
using CliCommand = CliWrap.Cli;

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
    /// Gets or sets solution-discovered test project patterns that should not run.
    /// </summary>
    [CommandOption("exclude-test-project", Description = "Repeatable solution-relative test project glob to exclude from coverage execution.")]
    public string[] ExcludeTestProjects { get; set; } = [];

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
    /// Gets or sets the project scheduling mode.
    /// </summary>
    [CommandOption("schedule", Description = "Project scheduling mode: input-order or longest-first. Defaults to input-order.")]
    public string Schedule { get; set; } = "input-order";

    /// <summary>
    /// Gets or sets an explicit timings file used by <c>--schedule longest-first</c>.
    /// </summary>
    [CommandOption("schedule-timings", Description = "timings.json file used by --schedule longest-first instead of the output directory's previous timings.")]
    public string? ScheduleTimings { get; set; }

    /// <summary>
    /// Gets or sets non-exclusive projects that should be scheduled before duration-sorted projects.
    /// </summary>
    [CommandOption("priority-test-project", Description = "Repeatable non-exclusive project path or file name to run first when --schedule longest-first is used.")]
    public string[] PriorityTestProjects { get; set; } = [];

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
    /// Gets or sets the managed test result artifact format.
    /// </summary>
    [CommandOption("test-results", Description = "Managed test result format. Only junit is supported in this release.")]
    public string? TestResults { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether slow-test diagnostic artifacts should be written.
    /// </summary>
    [CommandOption("slow-test-diagnostics", Description = "Write slow-test diagnostics from managed JUnit test results.")]
    public bool SlowTestDiagnostics { get; set; }

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
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics",
                result.FailureLogPath ?? result.CoveragePath);
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

        var excludeTestProjects = CoverageProjectExclusionMatcher.NormalizePatterns(ExcludeTestProjects);
        if (excludeTestProjects.Count > 0 && TestProjects.Any(project => !string.IsNullOrWhiteSpace(project)))
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV101",
                "--exclude-test-project cannot be combined with --test-project.",
                "Project exclusion applies only to solution discovery, while --test-project is an explicit project selection.",
                "Use --solution with --exclude-test-project, or remove the exclusion and keep the explicit project list.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        var scheduleMode = ParseScheduleMode();
        var priorityTestProjects = PriorityTestProjects.Where(project => !string.IsNullOrWhiteSpace(project)).ToArray();
        if (scheduleMode == CoverageRunScheduleMode.InputOrder && !string.IsNullOrWhiteSpace(ScheduleTimings))
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV101",
                "--schedule-timings requires --schedule longest-first.",
                "A timings file has no effect when projects run in input order.",
                "Pass --schedule longest-first with --schedule-timings, or remove --schedule-timings.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        if (scheduleMode == CoverageRunScheduleMode.InputOrder && priorityTestProjects.Length > 0)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV101",
                "--priority-test-project requires --schedule longest-first.",
                "Priority projects have no effect when projects run in input order.",
                "Pass --schedule longest-first with --priority-test-project, or remove the priority option.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        var testResults = ParseTestResults();
        if (SlowTestDiagnostics && testResults == CoverageRunTestResultFormat.None)
        {
            testResults = CoverageRunTestResultFormat.Junit;
        }

        return new CoverageRunRequest(
            SolutionPath,
            TestProjects,
            excludeTestProjects,
            OutputDirectory,
            Configuration,
            Parallelism,
            scheduleMode,
            ScheduleTimings,
            priorityTestProjects,
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
            testResults,
            SlowTestDiagnostics,
            !NoClean,
            Verbosity);
    }

    private CoverageRunScheduleMode ParseScheduleMode()
    {
        if (string.IsNullOrWhiteSpace(Schedule)
            || string.Equals(Schedule, "input-order", StringComparison.OrdinalIgnoreCase))
        {
            return CoverageRunScheduleMode.InputOrder;
        }

        if (string.Equals(Schedule, "longest-first", StringComparison.OrdinalIgnoreCase))
        {
            return CoverageRunScheduleMode.LongestFirst;
        }

        throw CoverageRunDiagnostics.Create(
            "ASCOV101",
            "--schedule must be input-order or longest-first.",
            $"Received '{Schedule}'.",
            "Use --schedule input-order or --schedule longest-first.",
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
    }

    private CoverageRunTestResultFormat ParseTestResults()
    {
        if (string.IsNullOrWhiteSpace(TestResults))
        {
            return CoverageRunTestResultFormat.None;
        }

        if (string.Equals(TestResults, "junit", StringComparison.OrdinalIgnoreCase))
        {
            return CoverageRunTestResultFormat.Junit;
        }

        throw CoverageRunDiagnostics.Create(
            "ASCOV111",
            "--test-results supports only junit in this release.",
            $"Received '{TestResults}'. TRX and TUnit-compatible result parsing are planned in #491.",
            "Use --test-results junit, omit --test-results, or keep passing custom loggers with --logger.",
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
    }
}

/// <summary>
/// Request for running and merging coverage from one or more .NET test projects.
/// </summary>
/// <remarks>
/// The request is the internal contract behind the public <c>coverage run</c> command. Paths are
/// interpreted relative to the current working directory for explicit-project runs, or relative to
/// the solution directory after solution discovery. The workflow never mutates consumer projects:
/// Coverlet must already be referenced by each selected test project, logger arguments are forwarded
/// verbatim, and extra test arguments are appended after AppSurface's required coverage properties.
/// </remarks>
/// <param name="SolutionPath">Optional <c>.sln</c> or <c>.slnx</c> path used for discovery.</param>
/// <param name="TestProjects">Explicit test project paths. Supplying at least one path skips solution discovery.</param>
/// <param name="ExcludeTestProjects">Normalized solution-discovered test project glob patterns that should not run.</param>
/// <param name="OutputDirectory">Coverage artifact directory. Existing content must be AppSurface-owned unless cleaning is disabled.</param>
/// <param name="Configuration">Build and test configuration forwarded to <c>dotnet build</c> and <c>dotnet test</c>.</param>
/// <param name="Parallelism">Maximum concurrent non-exclusive test projects.</param>
/// <param name="ScheduleMode">Project scheduling mode used before process execution.</param>
/// <param name="ScheduleTimingsPath">Optional explicit timings file for duration-aware scheduling.</param>
/// <param name="PriorityTestProjects">Non-exclusive project path or file-name matches scheduled ahead of duration-sorted projects.</param>
/// <param name="NoRestore">Whether build and test commands should pass <c>--no-restore</c>.</param>
/// <param name="Build">Whether to build once before tests, including explicit-project runs.</param>
/// <param name="NoBuild">Whether to skip AppSurface's build phase and forward <c>--no-build</c> to <c>dotnet test</c>.</param>
/// <param name="IncludeFilter">Optional Coverlet include filter.</param>
/// <param name="ExcludeFilter">Coverlet exclude filter. Commas are escaped before forwarding to MSBuild.</param>
/// <param name="DryRun">Whether to print discovery and artifact paths without running tests or cleaning output.</param>
/// <param name="NoDiscoverExclusive">Whether automatic integration/Playwright exclusive classification is disabled.</param>
/// <param name="ExclusiveTestProjects">Explicit project path or file-name matches that should run exclusively.</param>
/// <param name="Loggers">Logger values forwarded to <c>dotnet test</c> as repeatable <c>--logger:</c> arguments.</param>
/// <param name="TestArguments">Extra tokens appended to each <c>dotnet test</c> invocation.</param>
/// <param name="TestResults">Managed test result artifact format.</param>
/// <param name="SlowTestDiagnostics">Whether slow-test diagnostic artifacts should be written.</param>
/// <param name="Clean">Whether AppSurface-owned output is cleaned before writing new artifacts.</param>
/// <param name="Verbosity">Verbosity forwarded to <c>dotnet test</c>.</param>
internal sealed record CoverageRunRequest(
    string? SolutionPath,
    IReadOnlyList<string> TestProjects,
    IReadOnlyList<string> ExcludeTestProjects,
    string OutputDirectory,
    string Configuration,
    int Parallelism,
    CoverageRunScheduleMode ScheduleMode,
    string? ScheduleTimingsPath,
    IReadOnlyList<string> PriorityTestProjects,
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
    CoverageRunTestResultFormat TestResults,
    bool SlowTestDiagnostics,
    bool Clean,
    string Verbosity);

/// <summary>
/// Scheduling modes supported by <c>coverage run</c>.
/// </summary>
internal enum CoverageRunScheduleMode
{
    /// <summary>
    /// Preserve the discovered or explicitly supplied project order.
    /// </summary>
    InputOrder,

    /// <summary>
    /// Use prior timings to start longer non-exclusive projects first within each exclusive barrier segment.
    /// </summary>
    LongestFirst,
}

/// <summary>
/// Managed test result artifact format for <c>coverage run</c>.
/// </summary>
internal enum CoverageRunTestResultFormat
{
    /// <summary>
    /// Do not create AppSurface-managed test result artifacts.
    /// </summary>
    None,

    /// <summary>
    /// Create top-level JUnit XML artifacts with stable AppSurface-owned names.
    /// </summary>
    Junit,
}

/// <summary>
/// Result of a public coverage run.
/// </summary>
/// <remarks>
/// A result is returned only after discovery, test execution, merge, summary, and timings steps have
/// finished. Diagnostic failures throw <see cref="CommandException"/> before this result is created.
/// When tests fail after writing coverage, <see cref="Success"/> is <see langword="false"/> while
/// merged artifacts are still available for inspection.
/// </remarks>
/// <param name="Success">Whether all required test, merge, and artifact steps succeeded.</param>
/// <param name="OutputDirectory">Absolute output directory.</param>
/// <param name="CoveragePath">Absolute merged Cobertura path.</param>
/// <param name="FailureLogPath">Primary failing project log path, when a project failed.</param>
internal sealed record CoverageRunResult(bool Success, string OutputDirectory, string CoveragePath, string? FailureLogPath = null);

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

        if (request.SlowTestDiagnostics && request.TestResults == CoverageRunTestResultFormat.None)
        {
            request = request with { TestResults = CoverageRunTestResultFormat.Junit };
        }

        var currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        var resolution = await ResolveProjectsAsync(request, currentDirectory, cancellationToken);
        var outputDirectory = ResolveUserPath(request.OutputDirectory, currentDirectory);
        var schedulePlan = await CreateSchedulePlanAsync(request, resolution, outputDirectory, currentDirectory, cancellationToken);

        await PrintDiscoveryAsync(console, request, resolution, outputDirectory, schedulePlan);
        if (request.DryRun)
        {
            CoverageRunOutputGuard.Validate(outputDirectory, resolution.SolutionDirectory, resolution.Projects);
            return new CoverageRunResult(true, outputDirectory, Path.Join(outputDirectory, "coverage.cobertura.xml"));
        }

        CoverageRunOutputGuard.Prepare(outputDirectory, resolution.SolutionDirectory, resolution.Projects, request.Clean);
        var runStarted = _timeProvider.GetTimestamp();
        var build = await BuildIfNeededAsync(request, resolution, console, cancellationToken);
        var projectResults = await RunScheduledProjectsAsync(request, resolution, schedulePlan, outputDirectory, build.TestsShouldSkipBuild, console, cancellationToken);
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

        var diagnostics = await RunSlowTestDiagnosticsAsync(
            request,
            outputDirectory,
            projectResults,
            () => ElapsedSeconds(runStarted),
            console,
            cancellationToken);

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

        await WriteSummaryAsync(outputDirectory, mergedCoveragePath, request, diagnostics, console, cancellationToken);
        await WriteTimingsAsync(
            request,
            resolution,
            outputDirectory,
            mergedCoveragePath,
            build.Seconds,
            mergeSeconds,
            ElapsedSeconds(runStarted),
            merge.ExitCode,
            coverageFiles.Length,
            projectResults,
            schedulePlan,
            diagnostics,
            cancellationToken);

        await console.Output.WriteLineAsync($"Coverage artifacts: {outputDirectory}");
        await console.Output.WriteLineAsync($"Next: appsurface coverage gate --coverage {mergedCoveragePath} --min-line <percent> --min-branch <percent>");

        var failureLogPath = projectResults.FirstOrDefault(result => result.ExitCode != 0)?.LogFile;
        return new CoverageRunResult(projectResults.All(result => result.ExitCode == 0), outputDirectory, mergedCoveragePath, failureLogPath);
    }

    private async Task<CoverageProjectResolution> ResolveProjectsAsync(
        CoverageRunRequest request,
        string currentDirectory,
        CancellationToken cancellationToken)
    {
        var exclusionPatterns = CoverageProjectExclusionMatcher.NormalizePatterns(request.ExcludeTestProjects);
        var explicitProjects = request.TestProjects.Where(project => !string.IsNullOrWhiteSpace(project)).ToArray();
        if (explicitProjects.Length > 0 && exclusionPatterns.Count > 0)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV101",
                "--exclude-test-project cannot be combined with --test-project.",
                "Project exclusion applies only to solution discovery, while --test-project is an explicit project selection.",
                "Use --solution with --exclude-test-project, or remove the exclusion and keep the explicit project list.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        request = request with { ExcludeTestProjects = exclusionPatterns };
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
        var exclusionMatches = exclusionPatterns.ToDictionary(
            pattern => pattern,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in list.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => Normalize(line.Trim())))
        {
            if (!candidate.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsDiscoveredTestProject(candidate))
            {
                var matchedPatterns = exclusionPatterns
                    .Where(pattern => CoverageProjectExclusionMatcher.IsMatch(pattern, candidate))
                    .ToArray();
                if (matchedPatterns.Length == 0)
                {
                    included.Add(candidate);
                    continue;
                }

                foreach (var pattern in matchedPatterns)
                {
                    exclusionMatches[pattern].Add(candidate);
                }

                skipped.Add(new CoverageSkippedProject(
                    candidate,
                    $"matched --exclude-test-project pattern(s): {string.Join(", ", matchedPatterns.Select(pattern => $"'{pattern}'"))}",
                    matchedPatterns));
            }
            else
            {
                skipped.Add(new CoverageSkippedProject(candidate, "project name does not match *Tests.csproj or *IntegrationTests.csproj"));
            }
        }

        var unmatchedPatterns = exclusionPatterns
            .Where(pattern => exclusionMatches[pattern].Count == 0)
            .ToArray();
        if (unmatchedPatterns.Length > 0)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV112",
                "One or more --exclude-test-project patterns did not match a discovered test project.",
                string.Join(Environment.NewLine, unmatchedPatterns.Select(pattern => $"  - {pattern}")),
                "Run with --list-projects, then update or remove each stale exclusion pattern.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        foreach (var excludedProject in skipped.Where(project => project.ExclusionPatterns is not null))
        {
            var fullPath = ResolveUserPath(excludedProject.ProjectPath, solutionDirectory);
            var exclusiveConflict = request.ExclusiveTestProjects.FirstOrDefault(
                value => !string.IsNullOrWhiteSpace(value) && MatchesProject(value, excludedProject.ProjectPath, fullPath));
            if (exclusiveConflict is not null)
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV101",
                    "--exclusive-test-project cannot target an excluded test project.",
                    $"Project: {excludedProject.ProjectPath}. Exclusive selector: {exclusiveConflict}.",
                    "Remove either the exclusion pattern or the exclusive project selector.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }
        }

        if (exclusionPatterns.Count > 0)
        {
            var excludedPriority = request.PriorityTestProjects.FirstOrDefault(priority =>
                !string.IsNullOrWhiteSpace(priority)
                && skipped.Any(project => project.ExclusionPatterns is not null
                    && MatchesProject(priority, project.ProjectPath, ResolveUserPath(project.ProjectPath, solutionDirectory))));
            if (excludedPriority is not null)
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV101",
                    "--priority-test-project did not match any selected test project.",
                    $"Project: {excludedPriority}.",
                    "Pass a selected non-exclusive project path or file name.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
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
            var excludedProjects = skippedProjects
                .Where(project => project.ExclusionPatterns is not null)
                .ToArray();
            var skippedHint = excludedProjects.Length > 0
                ? DescribeAllExcludedProjects(request.ExcludeTestProjects, excludedProjects)
                : skippedProjects.Count == 0
                    ? "No .csproj entries were returned by discovery."
                    : $"Skipped {skippedProjects.Count.ToString(CultureInfo.InvariantCulture)} non-test project(s).";
            throw CoverageRunDiagnostics.Create(
                "ASCOV105",
                "No test projects were selected.",
                skippedHint,
                excludedProjects.Length > 0
                    ? "Remove or narrow --exclude-test-project patterns so at least one discovered test project remains."
                    : "Pass one or more --test-project options, or rename test projects to match *Tests.csproj.",
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

    private static string DescribeAllExcludedProjects(
        IReadOnlyList<string> patterns,
        IReadOnlyList<CoverageSkippedProject> excludedProjects)
    {
        var lines = new List<string>
        {
            $"Excluded {excludedProjects.Count.ToString(CultureInfo.InvariantCulture)} discovered test project(s) using {patterns.Count.ToString(CultureInfo.InvariantCulture)} pattern(s).",
        };
        foreach (var pattern in patterns)
        {
            var matchedProjects = excludedProjects
                .Where(project => project.ExclusionPatterns!.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                .Select(project => project.ProjectPath)
                .ToArray();
            lines.Add($"  - {pattern}: {matchedProjects.Length.ToString(CultureInfo.InvariantCulture)} match(es): {string.Join(", ", matchedProjects)}");
        }

        return string.Join(Environment.NewLine, lines);
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
        var normalizedCandidate = NormalizeProjectKey(candidate);
        return string.Equals(normalizedCandidate, NormalizeProjectKey(relativePath), StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCandidate, NormalizeProjectKey(fullPath), StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCandidate, NormalizeProjectKey(Path.GetFileName(fullPath)), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CoverageRunSchedulePlan> CreateSchedulePlanAsync(
        CoverageRunRequest request,
        CoverageProjectResolution resolution,
        string outputDirectory,
        string currentDirectory,
        CancellationToken cancellationToken)
    {
        if (request.ScheduleMode == CoverageRunScheduleMode.InputOrder)
        {
            var inputEntries = resolution.Projects
                .Select((project, index) => new CoverageRunScheduleEntry(
                    OriginalIndex: index,
                    ExecutionIndex: index,
                    project,
                    ScheduledSeconds: null,
                    DurationSource: "none",
                    ScheduleReason: "input-order"))
                .ToArray();
            return new CoverageRunSchedulePlan(
                Mode: "input-order",
                TimingSource: new CoverageRunScheduleTimingSource("none", null, "not-used"),
                Warnings: [],
                inputEntries);
        }

        var priorityRanks = ValidatePriorityProjects(request, resolution);
        var timingSource = await LoadPriorTimingsAsync(request, outputDirectory, currentDirectory, cancellationToken);
        var entries = new List<CoverageRunScheduleEntry>(resolution.Projects.Count);
        var executionIndex = 0;
        var index = 0;
        while (index < resolution.Projects.Count)
        {
            var project = resolution.Projects[index];
            if (project.IsExclusive)
            {
                entries.Add(new CoverageRunScheduleEntry(
                    OriginalIndex: index,
                    ExecutionIndex: executionIndex++,
                    project,
                    ScheduledSeconds: null,
                    DurationSource: "none",
                    ScheduleReason: "exclusive-barrier"));
                index++;
                continue;
            }

            var segment = new List<CoverageRunIndexedProject>();
            while (index < resolution.Projects.Count && !resolution.Projects[index].IsExclusive)
            {
                segment.Add(new CoverageRunIndexedProject(index, resolution.Projects[index]));
                index++;
            }

            foreach (var item in SortScheduleSegment(segment, timingSource.TimingsByProject, priorityRanks))
            {
                var hasTiming = timingSource.TimingsByProject.TryGetValue(NormalizeProjectKey(item.Project.RelativePath), out var scheduledSeconds);
                var isPriority = priorityRanks.ContainsKey(item.OriginalIndex);
                entries.Add(new CoverageRunScheduleEntry(
                    item.OriginalIndex,
                    executionIndex++,
                    item.Project,
                    hasTiming ? scheduledSeconds : null,
                    hasTiming ? timingSource.DurationSource : "none",
                    isPriority ? "priority" : hasTiming ? "prior-timing" : "unknown-timing"));
            }
        }

        return new CoverageRunSchedulePlan(
            Mode: "longest-first",
            TimingSource: new CoverageRunScheduleTimingSource(
                timingSource.Kind,
                timingSource.Path,
                timingSource.Status),
            timingSource.Warnings,
            entries);
    }

    private static Dictionary<int, int> ValidatePriorityProjects(
        CoverageRunRequest request,
        CoverageProjectResolution resolution)
    {
        var priorities = request.PriorityTestProjects
            .Where(project => !string.IsNullOrWhiteSpace(project))
            .Select(project => project.Trim())
            .ToArray();
        var duplicate = priorities
            .GroupBy(NormalizeProjectKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV101",
                "--priority-test-project contains a duplicate project.",
                $"Project: {duplicate.First()}.",
                "Pass each priority project once.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }

        var ranks = new Dictionary<int, int>();
        for (var rank = 0; rank < priorities.Length; rank++)
        {
            var priority = priorities[rank];
            var matches = resolution.Projects
                .Select((project, originalIndex) => new CoverageRunIndexedProject(originalIndex, project))
                .Where(item => MatchesProject(priority, item.Project.RelativePath, item.Project.FullPath))
                .ToArray();
            if (matches.Length == 0)
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV101",
                    "--priority-test-project did not match any selected test project.",
                    $"Project: {priority}.",
                    "Pass a selected non-exclusive project path or file name.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }

            if (matches.Any(match => match.Project.IsExclusive))
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV101",
                    "--priority-test-project cannot target an exclusive project.",
                    $"Project: {priority}.",
                    "Exclusive projects remain scheduling barriers; remove the priority flag for that project.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }

            if (matches.Length > 1)
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV101",
                    "--priority-test-project matched more than one selected project.",
                    $"Project: {priority}.",
                    "Pass the relative path for the intended non-exclusive project.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }

            if (!ranks.TryAdd(matches[0].OriginalIndex, rank))
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV101",
                    "--priority-test-project contains a duplicate project.",
                    $"Project: {priority}.",
                    "Pass each priority project once.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }

        }

        return ranks;
    }

    private static IOrderedEnumerable<CoverageRunIndexedProject> SortScheduleSegment(
        IReadOnlyList<CoverageRunIndexedProject> segment,
        IReadOnlyDictionary<string, long> timingsByProject,
        IReadOnlyDictionary<int, int> priorityRanks)
    {
        return segment
            .OrderBy(item => priorityRanks.TryGetValue(item.OriginalIndex, out var priorityRank) ? priorityRank : int.MaxValue)
            .ThenBy(item => priorityRanks.ContainsKey(item.OriginalIndex) ? 0 : timingsByProject.ContainsKey(NormalizeProjectKey(item.Project.RelativePath)) ? 1 : 2)
            .ThenByDescending(item => timingsByProject.TryGetValue(NormalizeProjectKey(item.Project.RelativePath), out var seconds) ? seconds : long.MinValue)
            .ThenBy(item => item.OriginalIndex);
    }

    private static async Task<CoverageRunPriorTimingLoad> LoadPriorTimingsAsync(
        CoverageRunRequest request,
        string outputDirectory,
        string currentDirectory,
        CancellationToken cancellationToken)
    {
        var explicitPath = !string.IsNullOrWhiteSpace(request.ScheduleTimingsPath);
        var path = explicitPath
            ? ResolveUserPath(request.ScheduleTimingsPath!, currentDirectory)
            : Path.Join(outputDirectory, "timings.json");
        var kind = explicitPath ? "explicit" : "inferred";

        if (!File.Exists(path))
        {
            if (explicitPath)
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV101",
                    "--schedule-timings file was not found.",
                    $"Path: {path}.",
                    "Pass an existing timings.json file or omit --schedule-timings.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }

            return new CoverageRunPriorTimingLoad(
                kind,
                path,
                "missing",
                "none",
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
                [$"Schedule warning: no prior timings found at {path}; unmeasured projects keep input order."]);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var timings = ReadPriorTimings(document, explicitPath, path);
            if (timings.Count == 0)
            {
                throw new InvalidDataException("No project timings were found.");
            }

            return new CoverageRunPriorTimingLoad(kind, path, "loaded", kind, timings, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            if (explicitPath)
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV101",
                    "--schedule-timings file could not be read.",
                    $"{path}: {ex.Message}",
                    "Pass a valid coverage run timings.json file.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }

            return new CoverageRunPriorTimingLoad(
                kind,
                path,
                "unusable",
                "none",
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
                [$"Schedule warning: prior timings at {path} were unusable ({ex.Message}); unmeasured projects keep input order."]);
        }
    }

    private static Dictionary<string, long> ReadPriorTimings(JsonDocument document, bool failOnDuplicate, string path)
    {
        if (!document.RootElement.TryGetProperty("projects", out var projects)
            || projects.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The timings file must contain a projects array.");
        }

        var timings = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.Object
                || !project.TryGetProperty("project", out var projectPath)
                || projectPath.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(projectPath.GetString())
                || !project.TryGetProperty("seconds", out var secondsElement)
                || !secondsElement.TryGetInt64(out var seconds))
            {
                throw new InvalidDataException("Every project timing must include string project and integer seconds fields.");
            }

            var key = NormalizeProjectKey(projectPath.GetString()!);
            if (failOnDuplicate && timings.ContainsKey(key))
            {
                throw new InvalidDataException($"Duplicate project timing '{projectPath.GetString()}' in {path}.");
            }

            timings[key] = seconds;
        }

        return timings;
    }

    private async Task<CoverageRunBuildResult> BuildIfNeededAsync(
        CoverageRunRequest request,
        CoverageProjectResolution resolution,
        IConsole console,
        CancellationToken cancellationToken)
    {
        var shouldBuild = request.Build || (!request.NoBuild && resolution.SolutionPath is not null && request.TestProjects.Count == 0);
        if (!shouldBuild)
        {
            await console.Output.WriteLineAsync(request.NoBuild
                ? "Build: skipped by --no-build; test projects will run with --no-build."
                : "Build: skipped; test projects will build as needed.");
            return new CoverageRunBuildResult(0, request.NoBuild);
        }

        if (resolution.SolutionPath is null)
        {
            return new CoverageRunBuildResult(await BuildExplicitProjectsAsync(request, resolution, console, cancellationToken), TestsShouldSkipBuild: true);
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

        return new CoverageRunBuildResult(ElapsedSeconds(started), TestsShouldSkipBuild: true);
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
        CoverageRunSchedulePlan schedulePlan,
        string outputDirectory,
        bool skipBuildDuringTests,
        IConsole console,
        CancellationToken cancellationToken)
    {
        var results = new CoverageProjectRunResult[resolution.Projects.Count];
        var active = new List<Task<CoverageProjectRunResult>>();

        foreach (var entry in schedulePlan.Entries.OrderBy(entry => entry.ExecutionIndex))
        {
            if (entry.Project.IsExclusive)
            {
                await DrainActiveAsync(active, results);
                results[entry.OriginalIndex] = await RunProjectAsync(request, resolution, outputDirectory, entry, skipBuildDuringTests, console, cancellationToken);
                continue;
            }

            while (active.Count >= request.Parallelism)
            {
                await DrainOneAsync(active, results);
            }

            active.Add(RunProjectAsync(request, resolution, outputDirectory, entry, skipBuildDuringTests, console, cancellationToken));
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
        CoverageRunScheduleEntry entry,
        bool skipBuildDuringTests,
        IConsole console,
        CancellationToken cancellationToken)
    {
        var project = entry.Project;
        var index = entry.OriginalIndex;
        var projectOutputDirectory = Path.Join(outputDirectory, "projects", project.Slug);
        Directory.CreateDirectory(projectOutputDirectory);
        var logFile = Path.Join(projectOutputDirectory, "dotnet-test.log");
        var started = _timeProvider.GetTimestamp();

        await console.Output.WriteLineAsync(
            $"[{entry.ExecutionIndex + 1}/{resolution.Projects.Count}] starting {project.RelativePath}{(project.IsExclusive ? " (exclusive)" : string.Empty)}");

        var testResults = CreateTestResultArtifacts(request, outputDirectory, project, index);
        var args = CreateTestArguments(request, project, projectOutputDirectory, testResults, skipBuildDuringTests);
        var processResult = await _processRunner.RunAsync("dotnet", args, resolution.SolutionDirectory, cancellationToken, outputFile: logFile);
        var seconds = ElapsedSeconds(started);

        await console.Output.WriteLineAsync(
            $"[{entry.ExecutionIndex + 1}/{resolution.Projects.Count}] finished {project.RelativePath} in {seconds}s (exit {processResult.ExitCode})");
        if (processResult.ExitCode != 0)
        {
            await console.Error.WriteLineAsync($"Test run failed for {project.RelativePath}; log: {logFile}");
        }

        return new CoverageProjectRunResult(
            index,
            entry.ExecutionIndex,
            project,
            entry.ScheduledSeconds,
            entry.DurationSource,
            entry.ScheduleReason,
            seconds,
            processResult.ExitCode,
            logFile,
            testResults);
    }

    private static IReadOnlyList<string> CreateTestArguments(
        CoverageRunRequest request,
        CoverageRunProject project,
        string projectOutputDirectory,
        IReadOnlyList<CoverageRunTestResultArtifact> testResults,
        bool skipBuildDuringTests)
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
        foreach (var artifact in testResults.Where(artifact => artifact.Format == CoverageRunTestResultFormat.Junit))
        {
            args.Add($"--logger:junit;LogFilePath={artifact.Path}");
        }

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

        if (skipBuildDuringTests)
        {
            args.Add("--no-build");
        }

        args.AddRange(request.TestArguments);
        return args;
    }

    private static IReadOnlyList<CoverageRunTestResultArtifact> CreateTestResultArtifacts(
        CoverageRunRequest request,
        string outputDirectory,
        CoverageRunProject project,
        int index)
    {
        return request.TestResults switch
        {
            CoverageRunTestResultFormat.None => [],
            CoverageRunTestResultFormat.Junit =>
            [
                new CoverageRunTestResultArtifact(
                    CoverageRunTestResultFormat.Junit,
                    project.RelativePath,
                    Path.Join(outputDirectory, $"junit-coverage-{index + 1}-{project.Slug}.xml"),
                    "pending"),
            ],
            _ => throw new UnreachableException(),
        };
    }

    private static async Task ReplayLogsAsync(IReadOnlyList<CoverageProjectRunResult> results, IConsole console, CancellationToken cancellationToken)
    {
        foreach (var result in results.OrderBy(result => result.Index))
        {
            await console.Output.WriteLineAsync($"--- BEGIN {result.Project.RelativePath} ---");
            if (File.Exists(result.LogFile))
            {
                const int maxReplayCharacters = 80_000;
                var log = await ReadLogPrefixAsync(result.LogFile, maxReplayCharacters, cancellationToken);
                if (!string.IsNullOrEmpty(log.Text))
                {
                    await console.Output.WriteAsync(log.Truncated
                        ? log.Text + Environment.NewLine + "[log truncated; see full log on disk]" + Environment.NewLine
                        : log.Text);
                }
            }

            await console.Output.WriteLineAsync($"--- END {result.Project.RelativePath} (exit {result.ExitCode}) ---");
        }
    }

    private static async Task<CoverageLogPrefix> ReadLogPrefixAsync(
        string logFile,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(logFile);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxCharacters + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var truncated = total > maxCharacters;
        return new CoverageLogPrefix(new string(buffer, 0, Math.Min(total, maxCharacters)), truncated);
    }

    private static async Task PrintDiscoveryAsync(
        IConsole console,
        CoverageRunRequest request,
        CoverageProjectResolution resolution,
        string outputDirectory,
        CoverageRunSchedulePlan schedulePlan)
    {
        await console.Output.WriteLineAsync($"Coverage run inputs");
        await console.Output.WriteLineAsync($"  Solution: {resolution.SolutionPath ?? "(explicit test projects)"}");
        await console.Output.WriteLineAsync($"  Output: {outputDirectory}");
        await console.Output.WriteLineAsync($"  Configuration: {request.Configuration}");
        await console.Output.WriteLineAsync($"  Parallelism: {request.Parallelism.ToString(CultureInfo.InvariantCulture)}");
        await console.Output.WriteLineAsync($"  Schedule: {schedulePlan.Mode}");
        if (!string.Equals(schedulePlan.TimingSource.Kind, "none", StringComparison.Ordinal))
        {
            await console.Output.WriteLineAsync($"  Schedule timings: {schedulePlan.TimingSource.Status} {schedulePlan.TimingSource.Path}");
        }

        await console.Output.WriteLineAsync($"  Include: {request.IncludeFilter ?? "(Coverlet default)"}");
        await console.Output.WriteLineAsync($"  Exclude: {request.ExcludeFilter}");
        await console.Output.WriteLineAsync($"  Managed test results: {DescribeTestResults(request)}");
        await console.Output.WriteLineAsync($"Discovered {resolution.Projects.Count.ToString(CultureInfo.InvariantCulture)} test project(s).");

        foreach (var warning in schedulePlan.Warnings)
        {
            await console.Error.WriteLineAsync(warning);
        }

        foreach (var project in resolution.Projects)
        {
            await console.Output.WriteLineAsync(
                $"  include {(project.IsExclusive ? "exclusive" : "parallel ")} {project.RelativePath} -> projects/{project.Slug}");
        }

        await console.Output.WriteLineAsync("Planned execution order:");
        foreach (var entry in schedulePlan.Entries.OrderBy(entry => entry.ExecutionIndex))
        {
            var scheduled = entry.ScheduledSeconds.HasValue
                ? $" scheduled {entry.ScheduledSeconds.Value.ToString(CultureInfo.InvariantCulture)}s"
                : string.Empty;
            await console.Output.WriteLineAsync(
                $"  execution {entry.ExecutionIndex + 1}: original {entry.OriginalIndex + 1} {(entry.Project.IsExclusive ? "exclusive" : "parallel ")} {entry.Project.RelativePath} ({entry.ScheduleReason}{scheduled})");
        }

        foreach (var skipped in resolution.SkippedProjects)
        {
            await console.Output.WriteLineAsync($"  skip {skipped.ProjectPath}: {skipped.Reason}");
        }
    }

    private static async Task WriteSummaryAsync(
        string outputDirectory,
        string coveragePath,
        CoverageRunRequest request,
        CoverageRunSlowTestDiagnosticsRun? diagnostics,
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
            Managed test results: {DescribeTestResults(request)}
            Cobertura: {coveragePath}
            Timings: {Path.Join(outputDirectory, "timings.json")}
            """);

        if (diagnostics is not null)
        {
            summary += Environment.NewLine + FormattableString.Invariant($"""
                Slow-test diagnostics: {diagnostics.MarkdownPath}
                Slow-test diagnostics JSON: {diagnostics.JsonPath}
                Slow-test diagnostics overhead: {diagnostics.AggregationSeconds}s ({diagnostics.AggregationPercent:0.00}%)
                Slow-test diagnostics warnings: {diagnostics.WarningCount}
                Slow-test diagnostics metadata complete: {diagnostics.MetadataComplete}
                """);
        }

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
        int coverageFileCount,
        IReadOnlyList<CoverageProjectRunResult> projectResults,
        CoverageRunSchedulePlan schedulePlan,
        CoverageRunSlowTestDiagnosticsRun? diagnostics,
        CancellationToken cancellationToken)
    {
        var testResultArtifacts = projectResults
            .SelectMany(result => result.TestResults.Select(artifact => artifact with
            {
                ParserStatus = ResolveParserStatus(artifact, diagnostics),
            }))
            .ToArray();
        var junitArtifacts = testResultArtifacts
            .Where(artifact => artifact.Format == CoverageRunTestResultFormat.Junit)
            .ToArray();
        var payload = new
        {
            solution = resolution.SolutionPath,
            configuration = request.Configuration,
            buildSolution = buildSeconds > 0,
            parallelism = request.Parallelism,
            schedule = new
            {
                mode = schedulePlan.Mode,
                timingSource = new
                {
                    kind = schedulePlan.TimingSource.Kind,
                    path = schedulePlan.TimingSource.Path,
                    status = schedulePlan.TimingSource.Status,
                },
                warnings = schedulePlan.Warnings,
            },
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
                coverageFiles = coverageFileCount,
                cobertura = coveragePath,
                junitFiles = junitArtifacts.Count(artifact => File.Exists(artifact.Path)),
                testResults = testResultArtifacts.Select(artifact => new
                {
                    format = artifact.Format.ToString().ToLowerInvariant(),
                    project = artifact.Project,
                    path = artifact.Path,
                    parserStatus = artifact.ParserStatus,
                }),
                diagnostics = diagnostics is null
                    ? null
                    : new
                    {
                        schemaVersion = CoverageRunSlowTestDiagnosticsWriter.SchemaVersion,
                        markdown = diagnostics.MarkdownPath,
                        json = diagnostics.JsonPath,
                    },
            },
            diagnostics = diagnostics is null
                ? null
                : new
                {
                    slowTests = new
                    {
                        warningCount = diagnostics.WarningCount,
                        metadataComplete = diagnostics.MetadataComplete,
                        aggregationSeconds = diagnostics.AggregationSeconds,
                        aggregationPercent = diagnostics.AggregationPercent,
                        markdown = diagnostics.MarkdownPath,
                        json = diagnostics.JsonPath,
                    },
                },
            projects = projectResults
                .OrderBy(result => result.Index)
                .Select(result => new
                {
                    project = result.Project.RelativePath,
                    slug = result.Project.Slug,
                    originalIndex = result.Index,
                    executionIndex = result.ExecutionIndex,
                    scheduledSeconds = result.ScheduledSeconds,
                    durationSource = result.DurationSource,
                    scheduleReason = result.ScheduleReason,
                    seconds = result.Seconds,
                    exitCode = result.ExitCode,
                    exclusive = result.Project.IsExclusive,
                    log = result.LogFile,
                    testResults = result.TestResults.Select(artifact => new
                    {
                        format = artifact.Format.ToString().ToLowerInvariant(),
                        path = artifact.Path,
                        parserStatus = ResolveParserStatus(artifact, diagnostics),
                    }),
                }),
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Join(outputDirectory, "timings.json"), json + Environment.NewLine, cancellationToken);
    }

    private static string ResolveParserStatus(
        CoverageRunTestResultArtifact artifact,
        CoverageRunSlowTestDiagnosticsRun? diagnostics)
    {
        if (diagnostics is not null && diagnostics.ParserStatuses.TryGetValue(artifact.Path, out var parserStatus))
        {
            return parserStatus;
        }

        return File.Exists(artifact.Path) ? "available" : "missing";
    }

    private async Task<CoverageRunSlowTestDiagnosticsRun?> RunSlowTestDiagnosticsAsync(
        CoverageRunRequest request,
        string outputDirectory,
        IReadOnlyList<CoverageProjectRunResult> projectResults,
        Func<long> getTotalSeconds,
        IConsole console,
        CancellationToken cancellationToken)
    {
        if (!request.SlowTestDiagnostics)
        {
            return null;
        }

        var diagnosticStarted = _timeProvider.GetTimestamp();
        try
        {
            var report = await CoverageRunSlowTestDiagnosticsWriter.CollectAsync(projectResults, cancellationToken);
            var diagnostics = await CoverageRunSlowTestDiagnosticsWriter.WriteAsync(
                outputDirectory,
                report,
                () => ElapsedSeconds(diagnosticStarted),
                aggregationSeconds => CalculateAggregationPercent(aggregationSeconds, getTotalSeconds()),
                cancellationToken);

            await console.Output.WriteLineAsync(FormattableString.Invariant(
                $"Slow-test diagnostics: {diagnostics.MarkdownPath} ({diagnostics.AggregationSeconds}s, {diagnostics.AggregationPercent:0.00}% overhead)"));
            return diagnostics;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or XmlException)
        {
            var aggregationSeconds = ElapsedSeconds(diagnosticStarted);
            var diagnostics = new CoverageRunSlowTestDiagnosticsRun(
                Path.Join(outputDirectory, CoverageRunSlowTestDiagnosticsWriter.MarkdownFileName),
                Path.Join(outputDirectory, CoverageRunSlowTestDiagnosticsWriter.JsonFileName),
                aggregationSeconds,
                CalculateAggregationPercent(aggregationSeconds, getTotalSeconds()),
                WarningCount: 1,
                MetadataComplete: false,
                projectResults
                    .SelectMany(result => result.TestResults)
                    .ToDictionary(artifact => artifact.Path, _ => "diagnosticsFailed", StringComparer.Ordinal));
            await console.Error.WriteLineAsync(FormattableString.Invariant(
                $"Slow-test diagnostics failed after {diagnostics.AggregationSeconds}s ({diagnostics.AggregationPercent:0.00}% overhead): {ex.Message}"));
            return diagnostics;
        }
    }

    private static decimal CalculateAggregationPercent(long aggregationSeconds, long totalSeconds)
    {
        return totalSeconds <= 0 ? 0 : aggregationSeconds * 100m / totalSeconds;
    }

    private static string DescribeTestResults(CoverageRunRequest request)
    {
        if (request.TestResults == CoverageRunTestResultFormat.None)
        {
            return "none";
        }

        return request.SlowTestDiagnostics
            ? "junit (enabled for slow-test diagnostics)"
            : "junit";
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

    private static string NormalizeProjectKey(string path)
    {
        var normalized = Normalize(path.Trim());
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.ToUpperInvariant();
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

/// <summary>
/// Validates and matches the solution-relative segment globs accepted by
/// <c>--exclude-test-project</c>.
/// </summary>
internal static class CoverageProjectExclusionMatcher
{
    /// <summary>
    /// Validates, normalizes, and de-duplicates exclusion patterns while preserving command-line order.
    /// </summary>
    /// <param name="patterns">Raw repeatable command option values.</param>
    /// <returns>Normalized patterns using forward-slash separators.</returns>
    public static IReadOnlyList<string> NormalizePatterns(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var normalizedPatterns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in patterns)
        {
            var normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
            ValidatePattern(normalized);
            if (!seen.Add(normalized))
            {
                throw InvalidPattern(
                    normalized,
                    "The normalized pattern duplicates an earlier --exclude-test-project value.",
                    "Pass each exclusion pattern once.");
            }

            normalizedPatterns.Add(normalized);
        }

        return normalizedPatterns;
    }

    /// <summary>
    /// Gets whether a normalized exclusion pattern matches a solution-relative project path.
    /// </summary>
    /// <param name="pattern">Validated normalized pattern.</param>
    /// <param name="projectPath">Solution-relative path reported by <c>dotnet sln list</c>.</param>
    /// <returns><see langword="true" /> when the project is excluded by the pattern.</returns>
    public static bool IsMatch(string pattern, string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        var normalizedProject = projectPath.Trim().Replace('\\', '/');
        while (normalizedProject.StartsWith("./", StringComparison.Ordinal))
        {
            normalizedProject = normalizedProject[2..];
        }

        var patternSegments = pattern.Split('/');
        var pathSegments = pattern.Contains('/')
            ? normalizedProject.Split('/')
            : [normalizedProject.Split('/')[^1]];
        var cache = new Dictionary<(int PatternIndex, int PathIndex), bool>();
        return MatchSegments(patternSegments, 0, pathSegments, 0, cache);
    }

    private static void ValidatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw InvalidPattern(
                pattern,
                "The exclusion pattern is empty or whitespace.",
                "Pass a solution-relative project path or segment glob.");
        }

        if (pattern.StartsWith("/", StringComparison.Ordinal)
            || (pattern.Length >= 2 && char.IsAsciiLetter(pattern[0]) && pattern[1] == ':'))
        {
            throw InvalidPattern(
                pattern,
                "Rooted, UNC, and drive-qualified exclusion patterns are not allowed.",
                "Pass a path relative to the solution, such as tests/**/Browser*.Tests.csproj.");
        }

        if (pattern.EndsWith("/", StringComparison.Ordinal) || pattern.Contains("//", StringComparison.Ordinal))
        {
            throw InvalidPattern(
                pattern,
                "Leading, trailing, and repeated path separators are not allowed.",
                "Use one separator between non-empty path segments.");
        }

        var ordinarySegmentSeen = false;
        foreach (var segment in pattern.Split('/'))
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal))
            {
                throw InvalidPattern(
                    pattern,
                    "Dot path segments are not allowed.",
                    "Remove the dot segment and keep the pattern solution-relative.");
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (ordinarySegmentSeen)
                {
                    throw InvalidPattern(
                        pattern,
                        "Parent path segments are allowed only at the start of a pattern.",
                        "Use leading ../ segments only for projects outside the solution directory.");
                }

                continue;
            }

            ordinarySegmentSeen = true;
            if (segment.Contains("**", StringComparison.Ordinal)
                && !string.Equals(segment, "**", StringComparison.Ordinal))
            {
                throw InvalidPattern(
                    pattern,
                    "A double-star wildcard must be a complete path segment.",
                    "Use segment globs such as tests/**/Browser*.Tests.csproj.");
            }
        }

        if (!ordinarySegmentSeen)
        {
            throw InvalidPattern(
                pattern,
                "A parent-only exclusion pattern does not identify a project.",
                "Follow leading ../ segments with a project path or segment glob.");
        }
    }

    private static bool MatchSegments(
        IReadOnlyList<string> patternSegments,
        int patternIndex,
        IReadOnlyList<string> pathSegments,
        int pathIndex,
        IDictionary<(int PatternIndex, int PathIndex), bool> cache)
    {
        var key = (patternIndex, pathIndex);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        bool matches;
        if (patternIndex == patternSegments.Count)
        {
            matches = pathIndex == pathSegments.Count;
        }
        else if (string.Equals(patternSegments[patternIndex], "**", StringComparison.Ordinal))
        {
            matches = Enumerable.Range(pathIndex, pathSegments.Count - pathIndex + 1)
                .Any(nextPathIndex => MatchSegments(patternSegments, patternIndex + 1, pathSegments, nextPathIndex, cache));
        }
        else
        {
            matches = pathIndex < pathSegments.Count
                && MatchSegment(patternSegments[patternIndex], pathSegments[pathIndex])
                && MatchSegments(patternSegments, patternIndex + 1, pathSegments, pathIndex + 1, cache);
        }

        cache[key] = matches;
        return matches;
    }

    private static bool MatchSegment(string pattern, string value)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        }

        var chunks = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
        if (chunks.Length == 0)
        {
            return true;
        }

        var chunkIndex = 0;
        var valueIndex = 0;
        if (pattern[0] != '*')
        {
            if (!value.StartsWith(chunks[0], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            valueIndex = chunks[0].Length;
            chunkIndex++;
        }

        var trailingChunkIndex = pattern[^1] == '*' ? chunks.Length : chunks.Length - 1;
        for (; chunkIndex < trailingChunkIndex; chunkIndex++)
        {
            var matchIndex = value.IndexOf(chunks[chunkIndex], valueIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return false;
            }

            valueIndex = matchIndex + chunks[chunkIndex].Length;
        }

        if (pattern[^1] == '*')
        {
            return true;
        }

        var trailingChunk = chunks[^1];
        return value.Length - trailingChunk.Length >= valueIndex
            && value.EndsWith(trailingChunk, StringComparison.OrdinalIgnoreCase);
    }

    private static CommandException InvalidPattern(string pattern, string cause, string fix)
    {
        var received = string.IsNullOrWhiteSpace(pattern) ? "(empty)" : pattern;
        return CoverageRunDiagnostics.Create(
            "ASCOV101",
            "--exclude-test-project contains an invalid pattern.",
            $"Pattern: {received}. {cause}",
            fix,
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
    }
}

/// <summary>
/// Planned project execution order for a coverage run.
/// </summary>
/// <param name="Mode">Public scheduling mode name written to console output and timings.</param>
/// <param name="TimingSource">Source used to load prior project durations.</param>
/// <param name="Warnings">Non-fatal scheduling warnings.</param>
/// <param name="Entries">Execution entries in original-index-compatible project space.</param>
internal sealed record CoverageRunSchedulePlan(
    string Mode,
    CoverageRunScheduleTimingSource TimingSource,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CoverageRunScheduleEntry> Entries);

/// <summary>
/// Prior-duration source used to build a schedule.
/// </summary>
/// <param name="Kind">Source kind: none, inferred, or explicit.</param>
/// <param name="Path">Timings file path when one was considered.</param>
/// <param name="Status">Read status for the source.</param>
internal sealed record CoverageRunScheduleTimingSource(string Kind, string? Path, string Status);

/// <summary>
/// One project's execution slot in the schedule.
/// </summary>
/// <param name="OriginalIndex">Project index from discovery or explicit input order.</param>
/// <param name="ExecutionIndex">Index used to launch the process.</param>
/// <param name="Project">Project scheduled for this slot.</param>
/// <param name="ScheduledSeconds">Prior duration used for sorting, when available.</param>
/// <param name="DurationSource">Where <paramref name="ScheduledSeconds"/> came from.</param>
/// <param name="ScheduleReason">Human-readable reason for the chosen slot.</param>
internal sealed record CoverageRunScheduleEntry(
    int OriginalIndex,
    int ExecutionIndex,
    CoverageRunProject Project,
    long? ScheduledSeconds,
    string DurationSource,
    string ScheduleReason);

/// <summary>
/// Prior timing data loaded before output cleanup.
/// </summary>
/// <param name="Kind">Source kind: inferred or explicit.</param>
/// <param name="Path">Source file path.</param>
/// <param name="Status">Load status.</param>
/// <param name="DurationSource">Per-project duration source value.</param>
/// <param name="TimingsByProject">Project durations keyed by normalized relative project path.</param>
/// <param name="Warnings">Non-fatal load warnings.</param>
internal sealed record CoverageRunPriorTimingLoad(
    string Kind,
    string Path,
    string Status,
    string DurationSource,
    IReadOnlyDictionary<string, long> TimingsByProject,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Project paired with its discovery/input index.
/// </summary>
/// <param name="OriginalIndex">Project index from discovery or explicit input order.</param>
/// <param name="Project">Project metadata.</param>
internal sealed record CoverageRunIndexedProject(int OriginalIndex, CoverageRunProject Project);

/// <summary>
/// Resolved project set used by the coverage runner after discovery or explicit selection.
/// </summary>
/// <param name="SolutionPath">Absolute solution path when discovery was solution-based; otherwise <see langword="null"/>.</param>
/// <param name="SolutionDirectory">Directory used as the working directory for build, test, and discovery commands.</param>
/// <param name="Projects">Selected test projects in scheduled order.</param>
/// <param name="SkippedProjects">Projects discovered from the solution but excluded from coverage execution.</param>
internal sealed record CoverageProjectResolution(
    string? SolutionPath,
    string SolutionDirectory,
    IReadOnlyList<CoverageRunProject> Projects,
    IReadOnlyList<CoverageSkippedProject> SkippedProjects);

/// <summary>
/// Selected test project and scheduling metadata.
/// </summary>
/// <param name="RelativePath">Project path as displayed to users and recorded in timings.</param>
/// <param name="FullPath">Absolute project path passed to <c>dotnet test</c>.</param>
/// <param name="Slug">Stable artifact directory segment allocated from the project path.</param>
/// <param name="IsExclusive">Whether the project must run outside parallel batches.</param>
internal sealed record CoverageRunProject(
    string RelativePath,
    string FullPath,
    string Slug,
    bool IsExclusive);

/// <summary>
/// Project that solution discovery intentionally did not include.
/// </summary>
/// <param name="ProjectPath">Path reported by <c>dotnet sln list</c>.</param>
/// <param name="Reason">Human-readable reason the project was skipped.</param>
/// <param name="ExclusionPatterns">Normalized exclusion patterns that matched this test project, when applicable.</param>
internal sealed record CoverageSkippedProject(
    string ProjectPath,
    string Reason,
    IReadOnlyList<string>? ExclusionPatterns = null);

/// <summary>
/// Per-project test execution result.
/// </summary>
/// <param name="Index">Original discovery or explicit input index.</param>
/// <param name="ExecutionIndex">Project process launch index.</param>
/// <param name="Project">Project metadata.</param>
/// <param name="ScheduledSeconds">Prior duration used for scheduling, when available.</param>
/// <param name="DurationSource">Source used for <paramref name="ScheduledSeconds"/>.</param>
/// <param name="ScheduleReason">Reason the project received its execution slot.</param>
/// <param name="Seconds">Elapsed whole seconds for the project test command.</param>
/// <param name="ExitCode">Test process exit code.</param>
/// <param name="LogFile">Per-project log file path.</param>
/// <param name="TestResults">Managed test result artifacts requested for this project.</param>
internal sealed record CoverageProjectRunResult(
    int Index,
    int ExecutionIndex,
    CoverageRunProject Project,
    long? ScheduledSeconds,
    string DurationSource,
    string ScheduleReason,
    long Seconds,
    int ExitCode,
    string LogFile,
    IReadOnlyList<CoverageRunTestResultArtifact> TestResults);

/// <summary>
/// Managed test result artifact requested by <c>coverage run</c>.
/// </summary>
/// <param name="Format">Artifact format.</param>
/// <param name="Project">Project that owns the artifact.</param>
/// <param name="Path">Absolute artifact path.</param>
/// <param name="ParserStatus">Best-effort parser status recorded in timings.</param>
internal sealed record CoverageRunTestResultArtifact(
    CoverageRunTestResultFormat Format,
    string Project,
    string Path,
    string ParserStatus);

/// <summary>
/// Build phase result used to coordinate later <c>dotnet test</c> arguments.
/// </summary>
/// <param name="Seconds">Elapsed whole seconds spent in the AppSurface build phase.</param>
/// <param name="TestsShouldSkipBuild">Whether test commands should pass <c>--no-build</c>.</param>
internal sealed record CoverageRunBuildResult(long Seconds, bool TestsShouldSkipBuild);

/// <summary>
/// Bounded prefix of a project log replayed to the console.
/// </summary>
/// <param name="Text">Log text prefix.</param>
/// <param name="Truncated">Whether additional log content remains on disk.</param>
internal sealed record CoverageLogPrefix(string Text, bool Truncated);

/// <summary>
/// Runs external processes for the coverage run workflow.
/// </summary>
/// <remarks>
/// Implementations must preserve command output for diagnostics and honor cancellation promptly.
/// When <c>outputFile</c> is supplied, implementations should stream output to that file during
/// process execution and may return an empty <see cref="CoverageRunProcessResult.Output"/>.
/// </remarks>
internal interface ICoverageRunProcessRunner
{
    /// <summary>
    /// Runs a process and returns its exit code plus captured output.
    /// </summary>
    /// <param name="fileName">Executable name or path.</param>
    /// <param name="arguments">Argument tokens passed without shell interpolation.</param>
    /// <param name="workingDirectory">Working directory for the child process.</param>
    /// <param name="cancellationToken">Cancellation token that should terminate the process tree.</param>
    /// <param name="outputFile">Optional log file that receives captured standard output and error.</param>
    /// <returns>Process exit code and captured output.</returns>
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
/// <param name="ExitCode">Child process exit code.</param>
/// <param name="Output">Captured standard output followed by captured standard error.</param>
internal sealed record CoverageRunProcessResult(int ExitCode, string Output);

/// <summary>
/// Default process runner used by the public coverage command.
/// </summary>
/// <remarks>
/// The runner delegates tokenized argument escaping and cancellation to CliWrap. It buffers
/// short-lived discovery/build commands, streams per-project test logs to disk, and disables
/// non-zero exit-code validation so coverage workflow failures remain ordinary command results.
/// </remarks>
internal sealed class CliWrapCoverageRunProcessRunner : ICoverageRunProcessRunner
{
    /// <inheritdoc />
    public async Task<CoverageRunProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        string? outputFile = null)
    {
        try
        {
            return outputFile is null
                ? await RunBufferedAsync(fileName, arguments, workingDirectory, cancellationToken)
                : await RunStreamingAsync(fileName, arguments, workingDirectory, outputFile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCommandLaunchFailure(ex))
        {
            if (outputFile is not null)
            {
                await TryAppendFailureLogAsync(
                    outputFile,
                    $"Failed to start command '{fileName}': {ex.Message}{Environment.NewLine}",
                    cancellationToken);
            }

            throw CoverageRunDiagnostics.Create(
                "ASCOV110",
                "Failed to start dotnet.",
                $"Command: {fileName}. {ex.Message}",
                "Verify the .NET SDK is installed and available on PATH.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
        }
    }

    private static async Task<CoverageRunProcessResult> RunBufferedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        BufferedCommandResult result = await CliCommand.Wrap(fileName)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        var output = result.StandardOutput + result.StandardError;

        return new CoverageRunProcessResult(result.ExitCode, output);
    }

    private static async Task<CoverageRunProcessResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string outputFile,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            outputFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        using var writeGate = new SemaphoreSlim(1, 1);

        var result = await CliCommand.Wrap(fileName)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Create((source, token) => CopyPipeToFileAsync(source, stream, writeGate, token)))
            .WithStandardErrorPipe(PipeTarget.Create((source, token) => CopyPipeToFileAsync(source, stream, writeGate, token)))
            .ExecuteAsync(cancellationToken);

        return new CoverageRunProcessResult(result.ExitCode, string.Empty);
    }

    private static async Task CopyPipeToFileAsync(
        Stream source,
        Stream target,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return;
                }

                await writeGate.WaitAsync(cancellationToken);
                try
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsCommandLaunchFailure(Exception exception)
    {
        return exception is CliWrapException
            or Win32Exception
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or IOException
            or InvalidOperationException;
    }

    private static async Task TryAppendFailureLogAsync(
        string outputFile,
        string output,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(outputFile, output, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // The thrown ASCOV110 diagnostic remains the user-visible failure when the log path is also unavailable.
        }
    }
}

/// <summary>
/// Runs AppSurface's package-owned ReportGenerator dependency.
/// </summary>
/// <remarks>
/// The merge contract accepts one or more per-project Cobertura files and writes ReportGenerator
/// output into a caller-owned directory. Implementations must not read consumer tool manifests or
/// require repositories to install ReportGenerator themselves.
/// </remarks>
internal interface ICoverageRunReportGenerator
{
    /// <summary>
    /// Merges Cobertura coverage files into one ReportGenerator output directory.
    /// </summary>
    /// <param name="coverageFiles">Cobertura files emitted by instrumented test projects.</param>
    /// <param name="outputDirectory">ReportGenerator output directory for this run.</param>
    /// <param name="cancellationToken">Cancellation token for the merge process.</param>
    /// <returns>Merge exit code plus expected Cobertura and text-summary paths.</returns>
    Task<CoverageRunMergeResult> MergeAsync(
        IReadOnlyList<string> coverageFiles,
        string outputDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of a ReportGenerator merge.
/// </summary>
/// <param name="ExitCode">ReportGenerator process exit code.</param>
/// <param name="CoberturaPath">Expected merged Cobertura file path.</param>
/// <param name="SummaryPath">Expected ReportGenerator text summary path.</param>
internal sealed record CoverageRunMergeResult(int ExitCode, string CoberturaPath, string SummaryPath);

/// <summary>
/// ReportGenerator-backed merge implementation for <c>coverage run</c>.
/// </summary>
internal sealed class CoverageRunReportGenerator : ICoverageRunReportGenerator
{
    private readonly ICoverageRunProcessRunner _processRunner;
    private readonly IReportGeneratorPackageLocator _locator;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverageRunReportGenerator"/> class.
    /// </summary>
    /// <param name="processRunner">Process runner used to execute ReportGenerator through <c>dotnet</c>.</param>
    /// <param name="locator">Locator for the AppSurface-owned ReportGenerator assembly.</param>
    public CoverageRunReportGenerator(ICoverageRunProcessRunner processRunner, IReportGeneratorPackageLocator locator)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <inheritdoc />
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
/// <remarks>
/// Packaged local tools carry ReportGenerator under the CLI base directory. Source and development
/// runs may instead rely on NuGet's package cache, so the locator probes the pinned dependency first
/// and then other installed ReportGenerator package versions as a compatibility fallback.
/// </remarks>
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
    private static readonly string[] TargetFrameworks = ["net10.0", "net9.0", "net8.0"];
    private readonly string _packageBaseDirectory;
    private readonly IReadOnlyList<string?> _packageRoots;

    public ReportGeneratorPackageLocator()
        : this(AppContext.BaseDirectory, ResolvePackageRoots())
    {
    }

    internal ReportGeneratorPackageLocator(string packageBaseDirectory)
        : this(packageBaseDirectory, ResolvePackageRoots())
    {
    }

    internal ReportGeneratorPackageLocator(string packageBaseDirectory, IReadOnlyList<string?> packageRoots)
    {
        _packageBaseDirectory = packageBaseDirectory ?? throw new ArgumentNullException(nameof(packageBaseDirectory));
        _packageRoots = packageRoots ?? throw new ArgumentNullException(nameof(packageRoots));
    }

    public string ResolveReportGeneratorDll()
    {
        foreach (var target in TargetFrameworks)
        {
            var packageOwned = Path.Join(_packageBaseDirectory, "reportgenerator", target, "ReportGenerator.dll");
            if (File.Exists(packageOwned))
            {
                return packageOwned;
            }
        }

        var roots = _packageRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var candidate in EnumerateNuGetCacheCandidates(root).Where(File.Exists))
            {
                return candidate;
            }
        }

        throw CoverageRunDiagnostics.Create(
            "ASCOV114",
            "ReportGenerator package dependency was not found.",
            $"Expected ReportGenerator {Version} under the NuGet package cache.",
            "Restore or reinstall ForgeTrust.AppSurface.Cli so its package-owned dependencies are present.",
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
    }

    private static IEnumerable<string> EnumerateNuGetCacheCandidates(string root)
    {
        foreach (var target in TargetFrameworks)
        {
            yield return Path.Join(root, "reportgenerator", Version, "tools", target, "ReportGenerator.dll");
        }

        var packageDirectory = Path.Join(root, "reportgenerator");
        if (!Directory.Exists(packageDirectory))
        {
            yield break;
        }

        foreach (var versionDirectory in Directory.EnumerateDirectories(packageDirectory)
            .Select(path => new { Path = path, Name = Path.GetFileName(path) })
            .OrderByDescending(item => ParsePackageVersionOrZero(item.Name))
            .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path))
        {
            if (string.Equals(Path.GetFileName(versionDirectory), Version, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var target in TargetFrameworks)
            {
                yield return Path.Join(versionDirectory, "tools", target, "ReportGenerator.dll");
            }
        }
    }

    private static global::System.Version ParsePackageVersionOrZero(string? value)
    {
        return global::System.Version.TryParse(value, out var version) ? version : new global::System.Version(0, 0);
    }

    private static string?[] ResolvePackageRoots()
    {
        return
        [
            Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
        ];
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
        var legacyOwned = !File.Exists(marker) && IsLegacyOwnedOutput(EnumerateOutputEntries(output));
        if (clean && (File.Exists(marker) || legacyOwned))
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

        if (projects
            .Select(project => Path.GetDirectoryName(project.FullPath))
            .OfType<string>()
            .Any(projectDirectory => string.Equals(trimmedOutput, Trim(projectDirectory), comparison)))
        {
            throw UnsafeOutput("--output must not be a test project directory.");
        }

        var marker = Path.Join(output, MarkerFileName);
        if (Directory.Exists(output))
        {
            var entries = Directory.EnumerateFileSystemEntries(output)
                .Where(path => !string.Equals(Path.GetFileName(path), MarkerFileName, StringComparison.Ordinal))
                .ToArray();
            if (entries.Length > 0 && !File.Exists(marker) && !IsLegacyOwnedOutput(entries))
            {
                throw UnsafeOutput("--output already contains files and is not marked as AppSurface-owned.");
            }
        }
    }

    private static string[] EnumerateOutputEntries(string output)
    {
        return Directory.Exists(output)
            ? Directory.EnumerateFileSystemEntries(output)
                .Where(path => !string.Equals(Path.GetFileName(path), MarkerFileName, StringComparison.Ordinal))
                .ToArray()
            : [];
    }

    private static bool IsLegacyOwnedOutput(IReadOnlyList<string> entries)
    {
        return entries.Count > 0 && entries.All(IsKnownOutputEntry);
    }

    private static bool IsKnownOutputEntry(string path)
    {
        var name = Path.GetFileName(path);
        if (Directory.Exists(path))
        {
            return string.Equals(name, "projects", StringComparison.Ordinal)
                || string.Equals(name, "reportgenerator", StringComparison.Ordinal);
        }

        return string.Equals(name, "coverage.cobertura.xml", StringComparison.Ordinal)
            || string.Equals(name, "coverage.json", StringComparison.Ordinal)
            || string.Equals(name, "coverage-gate.json", StringComparison.Ordinal)
            || string.Equals(name, "coverage-gate.md", StringComparison.Ordinal)
            || string.Equals(name, "summary.txt", StringComparison.Ordinal)
            || string.Equals(name, "timings.json", StringComparison.Ordinal)
            || string.Equals(name, "reportgenerator-summary.txt", StringComparison.Ordinal)
            || string.Equals(name, CoverageRunSlowTestDiagnosticsWriter.MarkdownFileName, StringComparison.Ordinal)
            || string.Equals(name, CoverageRunSlowTestDiagnosticsWriter.JsonFileName, StringComparison.Ordinal)
            || name.StartsWith("junit-", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal)
            || name.StartsWith("test-results-", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal);
    }

    private static void DeleteKnownOutput(string output)
    {
        foreach (var path in new[]
            {
                "coverage.cobertura.xml",
                "coverage.json",
                "coverage-gate.json",
                "coverage-gate.md",
                "summary.txt",
                "timings.json",
                "reportgenerator-summary.txt",
                CoverageRunSlowTestDiagnosticsWriter.MarkdownFileName,
                CoverageRunSlowTestDiagnosticsWriter.JsonFileName,
            }
            .Select(file => Path.Join(output, file))
            .Where(File.Exists))
        {
            File.Delete(path);
        }

        foreach (var junitFile in Directory.EnumerateFiles(output, "junit-*.xml", SearchOption.TopDirectoryOnly))
        {
            File.Delete(junitFile);
        }

        foreach (var testResultFile in Directory.EnumerateFiles(output, "test-results-*.xml", SearchOption.TopDirectoryOnly))
        {
            File.Delete(testResultFile);
        }

        foreach (var path in new[] { "projects", "reportgenerator" }
            .Select(directory => Path.Join(output, directory))
            .Where(Directory.Exists))
        {
            Directory.Delete(path, recursive: true);
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
        builder.Append(" Docs: ").Append(docs);
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            builder.Append(" Log: ").Append(logPath);
        }

        return new CommandException(builder.ToString());
    }
}
