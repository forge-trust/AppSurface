using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ForgeTrust.AppSurface.CoverageArtifacts;

namespace ForgeTrust.AppSurface.CoverageRunner;

/// <summary>
/// Coordinates solution coverage project discovery, scheduling, test execution, and artifact merging.
/// </summary>
/// <remarks>
/// This type is the implementation behind <c>scripts/coverage-solution.sh</c>. It preserves the
/// script contract while moving the scheduling logic into testable C# code: normal runs discover
/// projects with <c>dotnet sln list</c>, optionally build the solution, run selected test projects,
/// replay captured logs in solution order, merge Cobertura files with ReportGenerator, and write
/// <c>coverage.cobertura.xml</c>, <c>summary.txt</c>, <c>timings.json</c>, top-level
/// <c>junit-*.xml</c> files, <c>slow-test-diagnostics.md</c>,
/// <c>slow-test-diagnostics.json</c>, and per-project <c>dotnet-test.log</c> files under the
/// output directory. Merge-only runs skip discovery/build/test and only merge an existing artifact
/// tree before writing a partial diagnostic report from copied JUnit files.
/// </remarks>
internal sealed class CoverageRunnerApplication
{
    private static readonly XmlReaderSettings CoberturaReaderSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    private const string Usage = """
        ForgeTrust.AppSurface.CoverageRunner

        Usage:
          scripts/coverage-solution.sh [solution] [output]
          scripts/coverage-solution.sh --group <name> [--output <dir>] [--solution <path>]
          scripts/coverage-solution.sh --list-groups
          scripts/coverage-solution.sh --merge-only <source-dir> [--output <dir>]

        Groups:
          all, core, tools, web, docs, razorwire, integration

        Environment:
          TEST_GROUP             Test group to run. Defaults to all.
          BUILD_CONFIGURATION    Test configuration. Defaults to Debug.
          BUILD_SOLUTION         true or false. Defaults to true for all, false for named groups.
          BUILD_NO_RESTORE       true/false. Adds --no-restore to build and test commands after a prior restore.
          COVERAGE_PARALLELISM   Positive integer. Defaults to 1. Exclusive projects run alone.
          INCLUDE_FILTER         Coverlet include filter.
          EXCLUDE_FILTER         Coverlet exclude filter.
        """;

    private readonly ICommandRunner _commandRunner;
    private readonly IClock _clock;
    private readonly TextWriter _standardOut;
    private readonly TextWriter _standardError;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverageRunnerApplication"/> class.
    /// </summary>
    /// <param name="commandRunner">External command runner.</param>
    /// <param name="clock">Clock for duration measurements.</param>
    /// <param name="standardOut">Standard output writer.</param>
    /// <param name="standardError">Standard error writer.</param>
    public CoverageRunnerApplication(
        ICommandRunner commandRunner,
        IClock clock,
        TextWriter standardOut,
        TextWriter standardError)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _standardOut = standardOut ?? throw new ArgumentNullException(nameof(standardOut));
        _standardError = standardError ?? throw new ArgumentNullException(nameof(standardError));
    }

    /// <summary>
    /// Runs the coverage workflow described by script-compatible arguments and environment
    /// variables.
    /// </summary>
    /// <param name="args">
    /// Command-line arguments accepted by <c>scripts/coverage-solution.sh</c>, including
    /// positional <c>[solution] [output]</c>, <c>--group</c>, <c>--list-groups</c>,
    /// <c>--merge-only</c>, <c>--output</c>, <c>--solution</c>, <c>--build-solution</c>, and
    /// <c>--skip-solution-build</c>.
    /// </param>
    /// <param name="currentDirectory">
    /// Directory used to find the repository root and, through the wrapper-provided
    /// <c>COVERAGE_CALLER_DIRECTORY</c>, resolve user-supplied relative paths.
    /// </param>
    /// <param name="environment">
    /// Environment variables. Defaults are <c>TEST_GROUP=all</c>,
    /// <c>BUILD_CONFIGURATION=Debug</c>, <c>BUILD_SOLUTION=true</c> for <c>all</c> and
    /// <c>false</c> for named groups, <c>BUILD_NO_RESTORE=false</c>, and
    /// <c>COVERAGE_PARALLELISM=1</c>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token forwarded to external command execution and file writes. Cancellation
    /// aborts the active operation rather than writing a partial success summary.
    /// </param>
    /// <returns>
    /// <c>0</c> for success, <c>2</c> for invalid invocation, or the first failing test/merge
    /// command exit code. Test failures do not stop scheduling; all selected projects run before
    /// the aggregate non-zero exit is returned.
    /// </returns>
    /// <remarks>
    /// Non-exclusive projects run through a bounded queue up to <c>COVERAGE_PARALLELISM</c>.
    /// Projects classified as integration tests or Playwright users are a conservative first
    /// rollout: they wait for active work to drain, run alone, and then release the queue. In
    /// merge-only mode the source directory must not equal, contain, or be contained by the output
    /// directory because the output directory is cleaned before writing merged artifacts.
    /// </remarks>
    public async Task<int> RunAsync(
        string[] args,
        string currentDirectory,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken = default)
    {
        var parse = CoverageRunnerOptions.Parse(args, currentDirectory, environment);
        if (parse.ShowUsage)
        {
            var writer = parse.ExitCode == 0 ? _standardOut : _standardError;
            if (parse.ErrorMessage is not null)
            {
                await writer.WriteLineAsync(parse.ErrorMessage);
            }

            await writer.WriteLineAsync(Usage);
        }

        if (parse.Options is null)
        {
            return parse.ExitCode;
        }

        var options = parse.Options;
        if (options.ListGroups)
        {
            foreach (var group in CoverageRunnerOptions.GetValidGroups())
            {
                await _standardOut.WriteLineAsync(group);
            }

            return 0;
        }

        if (options.MergeOnly)
        {
            return await RunMergeOnlyAsync(options, cancellationToken);
        }

        if (!File.Exists(options.SolutionPath))
        {
            await _standardError.WriteLineAsync($"Solution not found: {options.SolutionPath}");
            return 1;
        }

        return await RunTestsAsync(options, cancellationToken);
    }

    private async Task<int> RunMergeOnlyAsync(CoverageRunnerOptions options, CancellationToken cancellationToken)
    {
        if (options.MergeSourceDirectory is null || !Directory.Exists(options.MergeSourceDirectory))
        {
            await _standardError.WriteLineAsync($"Merge source directory not found: {options.MergeSourceDirectory}");
            return 1;
        }

        if (DirectoriesOverlap(options.MergeSourceDirectory, options.OutputDirectory))
        {
            await _standardError.WriteLineAsync("--merge-only source directory must not overlap the output directory.");
            return 2;
        }

        PrepareOutputDirectory(options);
        var totalTimer = _clock.StartTimer();
        var mergeTimer = _clock.StartTimer();
        var mergeExit = await MergeCoverageFilesAsync(options, options.MergeSourceDirectory, cancellationToken);
        var mergeSeconds = mergeTimer.ElapsedSeconds;
        if (mergeExit != 0)
        {
            return mergeExit;
        }

        var junitCount = CopyJunitFiles(options.MergeSourceDirectory, options.OutputDirectory);
        var coverageCount = CoverageFileCountForMerge(options.MergeSourceDirectory);
        var diagnostics = await RunSlowTestDiagnosticsAsync(options, [], () => totalTimer.ElapsedSeconds, cancellationToken);
        var totalSeconds = totalTimer.ElapsedSeconds;
        if (!await WriteSummaryAsync(options, diagnostics, cancellationToken))
        {
            return 1;
        }

        await WriteTimingsAsync(options, totalSeconds, 0, mergeSeconds, mergeExit, junitCount, coverageCount, [], diagnostics, cancellationToken);
        return 0;
    }

    private async Task<int> RunTestsAsync(CoverageRunnerOptions options, CancellationToken cancellationToken)
    {
        PrepareOutputDirectory(options);
        var totalTimer = _clock.StartTimer();
        var solutionDirectory = Path.GetDirectoryName(options.SolutionPath) ?? options.RepositoryRoot;
        var testProjects = await DiscoverTestProjectsAsync(options, solutionDirectory, cancellationToken);
        if (testProjects.Count == 0)
        {
            await _standardError.WriteLineAsync($"No test projects found for group '{options.GroupName}' in {options.SolutionPath}");
            return 1;
        }

        var buildSeconds = await BuildSolutionIfNeededAsync(options, cancellationToken);
        if (buildSeconds < 0)
        {
            return 1;
        }

        var results = await RunScheduledProjectsAsync(options, testProjects, solutionDirectory, cancellationToken);
        await ReplayLogsAsync(results, cancellationToken);

        var projectsOutputDirectory = Path.Join(options.OutputDirectory, "projects");
        var mergeTimer = _clock.StartTimer();
        var mergeExit = await MergeCoverageFilesAsync(options, projectsOutputDirectory, cancellationToken);
        var mergeSeconds = mergeTimer.ElapsedSeconds;
        var junitCount = Directory.EnumerateFiles(options.OutputDirectory, "junit-*.xml", SearchOption.TopDirectoryOnly).Count();
        var coverageCount = Directory.EnumerateFiles(projectsOutputDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories)
            .Count(path => !IsCollectorRawPath(path));
        var diagnostics = await RunSlowTestDiagnosticsAsync(options, results, () => totalTimer.ElapsedSeconds, cancellationToken);
        var totalSeconds = totalTimer.ElapsedSeconds;
        var summaryExit = 0;
        if (mergeExit == 0 && !await WriteSummaryAsync(options, diagnostics, cancellationToken))
        {
            summaryExit = 1;
        }

        await WriteTimingsAsync(options, totalSeconds, buildSeconds, mergeSeconds, mergeExit, junitCount, coverageCount, results, diagnostics, cancellationToken);

        var failures = results.Where(result => result.ExitCode != 0).ToArray();
        if (failures.Length == 0 && mergeExit == 0 && summaryExit == 0)
        {
            return 0;
        }

        if (failures.Length > 0)
        {
            await _standardError.WriteLineAsync();
            await _standardError.WriteLineAsync("One or more test projects failed:");
            foreach (var failure in failures)
            {
                await _standardError.WriteLineAsync($"  - {failure.Project.RelativePath} (exit {failure.ExitCode})");
            }
        }

        return failures.Length > 0 ? failures[0].ExitCode : mergeExit != 0 ? mergeExit : summaryExit;
    }

    private async Task<long> BuildSolutionIfNeededAsync(CoverageRunnerOptions options, CancellationToken cancellationToken)
    {
        if (!options.BuildSolution)
        {
            await _standardOut.WriteLineAsync("Skipping solution build; each selected test project will build its own graph.");
            return 0;
        }

        await _standardOut.WriteLineAsync("Building solution...");
        var buildTimer = _clock.StartTimer();
        var buildArgs = new List<string> { "build", options.SolutionPath, "--configuration", options.BuildConfiguration, "-v", "minimal" };
        if (options.BuildNoRestore)
        {
            buildArgs.Add("--no-restore");
        }

        var build = await _commandRunner.RunAsync("dotnet", buildArgs, options.RepositoryRoot, cancellationToken);
        await _standardOut.WriteAsync(build.Output);
        if (build.ExitCode == 0)
        {
            return buildTimer.ElapsedSeconds;
        }

        await _standardError.WriteLineAsync($"Build failed for {options.SolutionPath}");
        return -1;
    }

    private async Task<IReadOnlyList<TestProject>> DiscoverTestProjectsAsync(
        CoverageRunnerOptions options,
        string solutionDirectory,
        CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync("dotnet", ["sln", options.SolutionPath, "list"], options.RepositoryRoot, cancellationToken);
        if (result.ExitCode != 0)
        {
            await _standardError.WriteAsync(result.Output);
            return [];
        }

        var projects = new List<TestProject>();
        foreach (var project in result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()))
        {
            if (!project.EndsWith(".csproj", StringComparison.Ordinal) || !IsTestProjectPath(project))
            {
                continue;
            }

            var group = TestProjectClassifier.GetGroup(project);
            if (!string.Equals(options.GroupName, "all", StringComparison.Ordinal) && !string.Equals(group, options.GroupName, StringComparison.Ordinal))
            {
                continue;
            }

            var fullPath = Path.IsPathRooted(project) ? project : Path.GetFullPath(project, solutionDirectory);
            var projectContents = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, cancellationToken) : string.Empty;
            projects.Add(new TestProject(
                project,
                fullPath,
                group,
                TestProjectClassifier.CreateSlug(fullPath),
                TestProjectClassifier.IsExclusive(project, projectContents)));
        }

        return projects;
    }

    private static bool IsTestProjectPath(string projectPath)
    {
        return projectPath.EndsWith("Tests.csproj", StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<ProjectRunResult>> RunScheduledProjectsAsync(
        CoverageRunnerOptions options,
        IReadOnlyList<TestProject> projects,
        string solutionDirectory,
        CancellationToken cancellationToken)
    {
        var results = new ProjectRunResult[projects.Count];
        var active = new List<Task<ProjectRunResult>>();
        ExceptionDispatchInfo? terminalFailure = null;

        for (var i = 0; i < projects.Count; i++)
        {
            if (terminalFailure is not null)
            {
                break;
            }

            var project = projects[i];
            if (project.IsExclusive)
            {
                terminalFailure = await DrainActiveAsync(active, results, terminalFailure);
                if (terminalFailure is not null)
                {
                    break;
                }

                terminalFailure = await CompleteProjectAsync(
                    RunProjectAsync(options, project, i, projects.Count, solutionDirectory, cancellationToken),
                    results,
                    terminalFailure);
                continue;
            }

            while (active.Count >= options.Parallelism && terminalFailure is null)
            {
                terminalFailure = await DrainOneAsync(active, results, terminalFailure);
            }

            if (terminalFailure is not null)
            {
                break;
            }

            active.Add(RunProjectAsync(options, project, i, projects.Count, solutionDirectory, cancellationToken));
        }

        terminalFailure = await DrainActiveAsync(active, results, terminalFailure);
        if (terminalFailure is not null)
        {
            terminalFailure.Throw();
        }

        return results;
    }

    private static async Task<ExceptionDispatchInfo?> DrainOneAsync(
        List<Task<ProjectRunResult>> active,
        ProjectRunResult[] results,
        ExceptionDispatchInfo? terminalFailure)
    {
        var completed = await Task.WhenAny(active);
        active.Remove(completed);
        return await CompleteProjectAsync(completed, results, terminalFailure);
    }

    private static async Task<ExceptionDispatchInfo?> DrainActiveAsync(
        List<Task<ProjectRunResult>> active,
        ProjectRunResult[] results,
        ExceptionDispatchInfo? terminalFailure)
    {
        while (active.Count > 0)
        {
            terminalFailure = await DrainOneAsync(active, results, terminalFailure);
        }

        return terminalFailure;
    }

    private static async Task<ExceptionDispatchInfo?> CompleteProjectAsync(
        Task<ProjectRunResult> task,
        ProjectRunResult[] results,
        ExceptionDispatchInfo? terminalFailure)
    {
        try
        {
            var result = await task;
            results[result.Index] = result;
        }
        catch (Exception ex)
        {
            terminalFailure ??= ExceptionDispatchInfo.Capture(ex);
        }

        return terminalFailure;
    }

    private async Task<ProjectRunResult> RunProjectAsync(
        CoverageRunnerOptions options,
        TestProject project,
        int index,
        int count,
        string solutionDirectory,
        CancellationToken cancellationToken)
    {
        var projectSlug = RequirePathSegment(project.Slug, nameof(project.Slug));
        var groupName = RequirePathSegment(options.GroupName, nameof(options.GroupName));
        var projectOutputDirectory = Path.Join(options.OutputDirectory, "projects", projectSlug);
        Directory.CreateDirectory(projectOutputDirectory);
        var junitFile = Path.Join(options.OutputDirectory, $"junit-{groupName}-{index + 1}-{projectSlug}.xml");
        var logFile = Path.Join(projectOutputDirectory, "dotnet-test.log");

        await _standardOut.WriteLineAsync($"[{index + 1}/{count}][{options.GroupName}] starting dotnet test {project.RelativePath}{(project.IsExclusive ? " (exclusive)" : string.Empty)}");

        var invocation = CreateTestInvocation(options, project, projectOutputDirectory, junitFile);
        var timer = _clock.StartTimer();
        var result = await RunProjectCommandAsync(project, invocation.Arguments, solutionDirectory, logFile, cancellationToken);
        var artifactFailure = await NormalizeCollectorCoverageAsync(
            projectOutputDirectory,
            invocation.RawResultsDirectory,
            cancellationToken);
        var exitCode = result.ExitCode;
        if (exitCode == 0 && artifactFailure is not null)
        {
            exitCode = 1;
            await _standardError.WriteLineAsync($"Coverage artifact invalid for {project.RelativePath}: {artifactFailure}");
        }

        var seconds = timer.ElapsedSeconds;

        await _standardOut.WriteLineAsync($"[{index + 1}/{count}][{options.GroupName}] finished {project.RelativePath} in {seconds}s (exit {exitCode})");
        if (exitCode != 0)
        {
            await _standardError.WriteLineAsync($"Test run failed for {project.RelativePath} (exit {exitCode})");
        }

        return new ProjectRunResult(index, project, seconds, exitCode, junitFile, logFile);
    }

    private async Task<CommandResult> RunProjectCommandAsync(
        TestProject project,
        IReadOnlyList<string> args,
        string solutionDirectory,
        string logFile,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _commandRunner.RunAsync("dotnet", args, solutionDirectory, cancellationToken, outputFile: logFile);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            var output = $"Failed to run dotnet test for {project.RelativePath}: {ex.Message}{Environment.NewLine}";
            await File.WriteAllTextAsync(logFile, output, cancellationToken);
            return new CommandResult(1, output);
        }
    }

    private static CollectorTestInvocation CreateTestInvocation(
        CoverageRunnerOptions options,
        TestProject project,
        string projectOutputDirectory,
        string junitFile)
    {
        var args = new List<string>
        {
            "test",
            project.FullPath,
            "--configuration",
            options.BuildConfiguration,
            "-v",
            "minimal",
            "--logger:GitHubActions;report-warnings=false",
            $"--logger:junit;LogFilePath={junitFile}",
        };

        if (options.BuildSolution)
        {
            args.Add("--no-build");
        }

        if (options.BuildNoRestore)
        {
            args.Add("--no-restore");
        }

        var rawResultsDirectory = Path.Join(projectOutputDirectory, "collector-results", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(rawResultsDirectory) || File.Exists(rawResultsDirectory))
        {
            throw new IOException($"Collector invocation directory collision: {rawResultsDirectory}");
        }

        Directory.CreateDirectory(rawResultsDirectory);
        args.Add("--collect:XPlat Code Coverage");
        args.Add("--results-directory");
        args.Add(rawResultsDirectory);
        args.Add("--");
        args.Add("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura");
        args.Add($"DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include={options.IncludeFilter}");
        args.Add($"DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude={options.ExcludeFilter}");

        return new CollectorTestInvocation(args, rawResultsDirectory);
    }

    /// <summary>
    /// Validates and atomically normalizes exactly one Cobertura attachment from one collector invocation.
    /// </summary>
    /// <remarks>
    /// The invocation directory is deliberately retained from argument construction so artifacts from stale
    /// sibling invocations cannot satisfy or invalidate the current test run. Artifact content is copied from
    /// the shared no-follow reader into a private staging file, validated there, and committed with one rename.
    /// </remarks>
    internal static async Task<string?> NormalizeCollectorCoverageAsync(
        string projectOutputDirectory,
        string rawResultsDirectory,
        CancellationToken cancellationToken)
    {
        string[] candidates;
        try
        {
            candidates = EnumerateCollectorArtifacts(rawResultsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"collector results are unreadable ({ex.GetType().Name})";
        }

        if (candidates.Length == 0)
        {
            return "zero Cobertura files were produced";
        }

        if (candidates.Length > 1)
        {
            return $"multiple Cobertura files were produced ({candidates.Length})";
        }

        var staged = Path.Join(projectOutputDirectory, $".coverage.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var input = CoverageRunArtifactReader.OpenRegularFile(
                projectOutputDirectory,
                rawResultsDirectory,
                candidates[0]))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            await using (var stagedInput = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            using (var reader = XmlReader.Create(stagedInput, CoberturaReaderSettings))
            {
                var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
                if (!string.Equals(document.Root?.Name.LocalName, "coverage", StringComparison.Ordinal))
                {
                    return "the Cobertura document root is not 'coverage'";
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, Path.Join(projectOutputDirectory, "coverage.cobertura.xml"), overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return $"the Cobertura file is unreadable or malformed ({ex.GetType().Name})";
        }
        finally
        {
            if (File.Exists(staged))
            {
                try
                {
                    File.Delete(staged);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string[] EnumerateCollectorArtifacts(string rawResultsDirectory)
    {
        if (!Directory.Exists(rawResultsDirectory))
        {
            return [];
        }

        var candidates = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(rawResultsDirectory));
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("symbolic link or reparse point encountered in collector results");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if (string.Equals(Path.GetFileName(entry), "coverage.cobertura.xml", StringComparison.Ordinal))
                {
                    candidates.Add(Path.GetFullPath(entry));
                }
            }
        }

        return candidates.OrderBy(path => path, GetPathComparer()).ToArray();
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record CollectorTestInvocation(IReadOnlyList<string> Arguments, string RawResultsDirectory);

    private async Task ReplayLogsAsync(IReadOnlyList<ProjectRunResult> results, CancellationToken cancellationToken)
    {
        foreach (var result in results.OrderBy(result => result.Index))
        {
            await _standardOut.WriteLineAsync($"--- BEGIN {result.Project.RelativePath} ---");
            var output = File.Exists(result.LogFile)
                ? await File.ReadAllTextAsync(result.LogFile, cancellationToken)
                : string.Empty;
            if (!string.IsNullOrEmpty(output))
            {
                await _standardOut.WriteAsync(output);
                if (!output.EndsWith('\n'))
                {
                    await _standardOut.WriteLineAsync();
                }
            }

            await _standardOut.WriteLineAsync($"--- END {result.Project.RelativePath} (exit {result.ExitCode}) ---");
        }
    }

    private async Task<int> MergeCoverageFilesAsync(CoverageRunnerOptions options, string sourceDirectory, CancellationToken cancellationToken)
    {
        var coverageFiles = GetCoverageFilesForMerge(sourceDirectory);
        if (coverageFiles.Count == 0)
        {
            await _standardError.WriteLineAsync($"No Cobertura coverage files found under {sourceDirectory}");
            return 1;
        }

        var toolRestore = await _commandRunner.RunAsync("dotnet", ["tool", "restore"], options.RepositoryRoot, cancellationToken);
        if (toolRestore.ExitCode != 0)
        {
            await _standardError.WriteAsync(toolRestore.Output);
            await _standardError.WriteLineAsync("Failed to restore local .NET tools");
            return toolRestore.ExitCode;
        }

        var mergeDirectory = Path.Join(options.OutputDirectory, "reportgenerator");
        Directory.CreateDirectory(mergeDirectory);
        var reports = string.Join(';', coverageFiles);
        var merge = await _commandRunner.RunAsync(
            "dotnet",
            ["tool", "run", "reportgenerator", "--", $"-reports:{reports}", $"-targetdir:{mergeDirectory}", "-reporttypes:Cobertura;TextSummary"],
            options.RepositoryRoot,
            cancellationToken);

        if (merge.ExitCode != 0)
        {
            await _standardError.WriteAsync(merge.Output);
            await _standardError.WriteLineAsync("Coverage merge failed");
            return merge.ExitCode;
        }

        var mergedCoverage = Path.Join(mergeDirectory, "Cobertura.xml");
        if (!File.Exists(mergedCoverage))
        {
            await _standardError.WriteLineAsync($"ReportGenerator did not create {mergedCoverage}");
            return 1;
        }

        var canonicalCoverage = Path.Join(options.OutputDirectory, "coverage.cobertura.xml");
        var commitFailure = await CommitCoberturaAsync(mergedCoverage, canonicalCoverage, cancellationToken);
        if (commitFailure is not null)
        {
            if (commitFailure.Contains("numeric coverage attributes", StringComparison.Ordinal))
            {
                await _standardError.WriteLineAsync($"Failed to parse numeric coverage attributes from {mergedCoverage}");
            }
            else
            {
                await _standardError.WriteLineAsync($"Merged Cobertura artifact is invalid or could not be committed: {commitFailure}");
            }

            return 1;
        }

        var reportGeneratorSummary = Path.Join(mergeDirectory, "Summary.txt");
        if (File.Exists(reportGeneratorSummary))
        {
            await CopyCanonicalTextAsync(
                reportGeneratorSummary,
                Path.Join(options.OutputDirectory, "reportgenerator-summary.txt"),
                cancellationToken);
        }

        return 0;
    }

    private static IReadOnlyList<string> GetCoverageFilesForMerge(string sourceDirectory)
    {
        var shardCoverageFiles = Directory.Exists(sourceDirectory)
            ? Directory.EnumerateFiles(sourceDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories)
                .Where(path => !IsProjectCoveragePath(path, GetPathComparison()))
                .ToArray()
            : [];

        if (shardCoverageFiles.Length > 0)
        {
            return shardCoverageFiles;
        }

        return Directory.Exists(sourceDirectory)
            ? Directory.EnumerateFiles(sourceDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories)
                .Where(path => !IsCollectorRawPath(path))
                .ToArray()
            : [];
    }

    private static bool IsCollectorRawPath(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains("collector-results", StringComparer.Ordinal);

    private static int CoverageFileCountForMerge(string sourceDirectory)
    {
        return GetCoverageFilesForMerge(sourceDirectory).Count;
    }

    private static int CopyJunitFiles(string sourceDirectory, string outputDirectory)
    {
        var copied = 0;
        foreach (var junitFile in Directory.EnumerateFiles(sourceDirectory, "junit-*.xml", SearchOption.AllDirectories))
        {
            File.Copy(junitFile, Path.Join(outputDirectory, Path.GetFileName(junitFile)), overwrite: true);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// Writes the human-readable coverage summary from the merged Cobertura artifact.
    /// </summary>
    /// <param name="options">Runner options that identify the output directory and group.</param>
    /// <param name="diagnostics">
    /// Optional slow-test diagnostics metadata included for discoverability and overhead accounting.
    /// </param>
    /// <param name="cancellationToken">Cancellation token used for summary file reads and writes.</param>
    /// <returns>
    /// <c>true</c> when the summary was written; <c>false</c> when the merged coverage artifact is
    /// missing or cannot be parsed.
    /// </returns>
    /// <remarks>
    /// This method is internal so tests can verify summary failure behavior without depending on a
    /// failing ReportGenerator invocation that would stop before summary generation.
    /// </remarks>
    internal async Task<bool> WriteSummaryAsync(
        CoverageRunnerOptions options,
        SlowTestDiagnosticsRun? diagnostics,
        CancellationToken cancellationToken)
    {
        var coveragePath = Path.Join(options.OutputDirectory, "coverage.cobertura.xml");
        if (!File.Exists(coveragePath))
        {
            await _standardError.WriteLineAsync($"Merged Cobertura file was not created: {coveragePath}");
            return false;
        }

        var coverageText = await File.ReadAllTextAsync(coveragePath, cancellationToken);
        if (!TryReadIntAttribute(coverageText, "lines-covered", out var linesCovered)
            || !TryReadIntAttribute(coverageText, "lines-valid", out var linesValid)
            || !TryReadIntAttribute(coverageText, "branches-covered", out var branchesCovered)
            || !TryReadIntAttribute(coverageText, "branches-valid", out var branchesValid))
        {
            await _standardError.WriteLineAsync($"Failed to parse numeric coverage attributes from {coveragePath}");
            return false;
        }

        var lineRate = linesValid == 0 ? 0 : linesCovered * 100m / linesValid;
        var branchRate = branchesValid == 0 ? 0 : branchesCovered * 100m / branchesValid;
        var timingsPath = Path.Join(options.OutputDirectory, "timings.json");
        var diagnosticsPath = diagnostics?.MarkdownPath ?? Path.Join(options.OutputDirectory, SlowTestDiagnosticsWriter.MarkdownFileName);
        var diagnosticsSeconds = diagnostics?.AggregationSeconds ?? 0;
        var diagnosticsPercent = diagnostics?.AggregationPercent ?? 0;
        var diagnosticsWarnings = diagnostics?.WarningCount ?? 0;
        var summary = FormattableString.Invariant($"""
            Solution coverage summary
            Group: {options.GroupName}
            Line coverage: {lineRate:0.00}% ({linesCovered}/{linesValid})
            Branch coverage: {branchRate:0.00}% ({branchesCovered}/{branchesValid})
            Cobertura: {coveragePath}
            Timings: {timingsPath}
            Slow test diagnostics: {diagnosticsPath}
            Diagnostic aggregation overhead: {diagnosticsSeconds}s ({diagnosticsPercent:0.00}% of total runner time)
            Diagnostic warnings: {diagnosticsWarnings}
            """);
        var summaryPath = Path.Join(options.OutputDirectory, "summary.txt");
        try
        {
            await WriteCanonicalTextAsync(summaryPath, summary, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _standardError.WriteLineAsync($"Failed to write coverage summary {summaryPath}: {ex.Message}");
            return false;
        }

        await _standardOut.WriteLineAsync(summary);
        return true;
    }

    /// <summary>
    /// Stages, validates, and atomically replaces a canonical Cobertura artifact.
    /// </summary>
    /// <param name="sourcePath">ReportGenerator output to stage.</param>
    /// <param name="destinationPath">Canonical output path to replace.</param>
    /// <param name="cancellationToken">Cancellation token observed before the atomic replacement.</param>
    /// <param name="beforeCommit">Optional test seam invoked after validation and before replacement.</param>
    /// <returns><see langword="null"/> on success; otherwise a stable failure description.</returns>
    internal static async Task<string?> CommitCoberturaAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken,
        Action? beforeCommit = null)
    {
        var stagedPath = CreateSiblingStagingPath(destinationPath);
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var staged = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(staged, cancellationToken);
                await staged.FlushAsync(cancellationToken);
            }

            await using (var staged = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            using (var reader = XmlReader.Create(staged, CoberturaReaderSettings))
            {
                var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
                var root = document.Root;
                if (root is null || !string.Equals(root.Name.LocalName, "coverage", StringComparison.Ordinal))
                {
                    return "the document root is not 'coverage'";
                }

                if (!HasNumericCoverageAttribute(root, "lines-covered")
                    || !HasNumericCoverageAttribute(root, "lines-valid")
                    || !HasNumericCoverageAttribute(root, "branches-covered")
                    || !HasNumericCoverageAttribute(root, "branches-valid"))
                {
                    return "required numeric coverage attributes are missing or invalid";
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            beforeCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedPath, destinationPath, overwrite: true);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return $"the Cobertura file is unreadable or malformed ({ex.GetType().Name})";
        }
        finally
        {
            TryDeleteStagingFile(stagedPath);
        }
    }

    /// <summary>
    /// Writes a sibling staging file and atomically replaces the requested canonical text artifact.
    /// </summary>
    /// <param name="destinationPath">Canonical output path to replace.</param>
    /// <param name="contents">Complete artifact contents.</param>
    /// <param name="cancellationToken">Cancellation token observed before the atomic replacement.</param>
    /// <param name="beforeCommit">Optional test seam invoked after staging and before replacement.</param>
    internal static async Task WriteCanonicalTextAsync(
        string destinationPath,
        string contents,
        CancellationToken cancellationToken,
        Action? beforeCommit = null)
    {
        var stagedPath = CreateSiblingStagingPath(destinationPath);
        try
        {
            await File.WriteAllTextAsync(stagedPath, contents, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            beforeCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteStagingFile(stagedPath);
        }
    }

    /// <summary>
    /// Reads a complete text artifact and atomically replaces a canonical sibling-path copy.
    /// </summary>
    /// <param name="sourcePath">Text artifact to read before staging begins.</param>
    /// <param name="destinationPath">Canonical output path to replace.</param>
    /// <param name="cancellationToken">Cancellation token observed while reading, staging, and before replacement.</param>
    /// <param name="beforeCommit">Optional test seam invoked after staging and before replacement.</param>
    /// <remarks>
    /// Reading the source before staging ensures a read failure leaves both the canonical artifact
    /// and its directory free of partial temporary files.
    /// </remarks>
    internal static async Task CopyCanonicalTextAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken,
        Action? beforeCommit = null)
    {
        var contents = await File.ReadAllTextAsync(sourcePath, cancellationToken);
        await WriteCanonicalTextAsync(destinationPath, contents, cancellationToken, beforeCommit);
    }

    private static bool HasNumericCoverageAttribute(XElement root, string name)
        => int.TryParse(root.Attribute(name)?.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _);

    private static string CreateSiblingStagingPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException($"Canonical artifact path has no directory: {destinationPath}");
        return Path.Join(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
    }

    private static void TryDeleteStagingFile(string stagedPath)
    {
        if (!File.Exists(stagedPath))
        {
            return;
        }

        try
        {
            File.Delete(stagedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes the coverage summary without slow-test diagnostic metadata.
    /// </summary>
    /// <param name="options">Runner options that identify the output directory and group.</param>
    /// <param name="cancellationToken">Cancellation token used for summary file reads and writes.</param>
    /// <returns><c>true</c> when the summary was written; otherwise <c>false</c>.</returns>
    internal Task<bool> WriteSummaryAsync(CoverageRunnerOptions options, CancellationToken cancellationToken)
    {
        return WriteSummaryAsync(options, diagnostics: null, cancellationToken);
    }

    private static bool TryReadIntAttribute(string text, string attributeName, out int value)
    {
        value = 0;
        var needle = attributeName + "=\"";
        var start = text.IndexOf(needle, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += needle.Length;
        var end = text.IndexOf('"', start);
        return end > start && int.TryParse(text[start..end], out value);
    }

    private static async Task WriteTimingsAsync(
        CoverageRunnerOptions options,
        long totalSeconds,
        long buildSeconds,
        long mergeSeconds,
        int mergeExitCode,
        int junitCount,
        int coverageCount,
        IReadOnlyList<ProjectRunResult> results,
        SlowTestDiagnosticsRun diagnostics,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            solution = options.SolutionPath,
            group = options.GroupName,
            configuration = options.BuildConfiguration,
            buildSolution = options.BuildSolution,
            parallelism = options.Parallelism,
            durations = new
            {
                solutionBuildSeconds = buildSeconds,
                coverageMergeSeconds = mergeSeconds,
                diagnosticAggregationSeconds = diagnostics.AggregationSeconds,
                diagnosticAggregationPercent = diagnostics.AggregationPercent,
                totalSeconds,
            },
            merge = new
            {
                exitCode = mergeExitCode,
            },
            artifacts = new
            {
                junitFiles = junitCount,
                coverageFiles = coverageCount,
                cobertura = Path.Join(options.OutputDirectory, "coverage.cobertura.xml"),
                slowTestDiagnosticsMarkdown = diagnostics.MarkdownPath,
                slowTestDiagnosticsJson = diagnostics.JsonPath,
            },
            diagnostics = new
            {
                schemaVersion = SlowTestDiagnosticsWriter.SchemaVersion,
                warningCount = diagnostics.WarningCount,
                metadataComplete = diagnostics.MetadataComplete,
                aggregationSeconds = diagnostics.AggregationSeconds,
                aggregationPercent = diagnostics.AggregationPercent,
                markdown = diagnostics.MarkdownPath,
                json = diagnostics.JsonPath,
            },
            projects = results
                .OrderBy(result => result.Index)
                .Select(result => new
                {
                    project = result.Project.RelativePath,
                    group = result.Project.Group,
                    seconds = result.Seconds,
                    exitCode = result.ExitCode,
                    exclusive = result.Project.IsExclusive,
                    junit = result.JunitFile,
                    log = result.LogFile,
                }),
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await WriteCanonicalTextAsync(
            Path.Join(options.OutputDirectory, "timings.json"),
            json + Environment.NewLine,
            cancellationToken);
    }

    private static void PrepareOutputDirectory(CoverageRunnerOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        DeleteFileIfExists(Path.Join(options.OutputDirectory, "coverage.json"));
        DeleteFileIfExists(Path.Join(options.OutputDirectory, "coverage.cobertura.xml"));
        DeleteFileIfExists(Path.Join(options.OutputDirectory, "summary.txt"));
        DeleteFileIfExists(Path.Join(options.OutputDirectory, "timings.json"));
        DeleteFileIfExists(Path.Join(options.OutputDirectory, SlowTestDiagnosticsWriter.MarkdownFileName));
        DeleteFileIfExists(Path.Join(options.OutputDirectory, SlowTestDiagnosticsWriter.JsonFileName));
        foreach (var junitFile in Directory.EnumerateFiles(options.OutputDirectory, "junit-*.xml", SearchOption.TopDirectoryOnly))
        {
            File.Delete(junitFile);
        }

        DeleteDirectoryIfExists(Path.Join(options.OutputDirectory, "projects"));
        DeleteDirectoryIfExists(Path.Join(options.OutputDirectory, "reportgenerator"));
        Directory.CreateDirectory(Path.Join(options.OutputDirectory, "projects"));
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    internal static string RequirePathSegment(string segment, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || Path.IsPathRooted(segment)
            || Path.IsPathFullyQualified(segment)
            || segment.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || segment.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Coverage runner path segment '{parameterName}' is not a safe file or directory name.");
        }

        return segment;
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Gets whether a coverage path belongs to a per-project artifact directory.
    /// </summary>
    /// <param name="path">Coverage file path to inspect.</param>
    /// <param name="comparison">Filesystem-aware comparison used for path segment matching.</param>
    /// <returns><c>true</c> when the normalized path contains a <c>projects</c> segment.</returns>
    internal static bool IsProjectCoveragePath(string path, StringComparison comparison)
    {
        return Normalize(path).Contains("/projects/", comparison);
    }

    private static bool DirectoriesOverlap(string left, string right)
        => DirectoriesOverlap(left, right, GetPathComparison());

    /// <summary>
    /// Gets whether two directory paths are equal or have an ancestor/descendant relationship
    /// after absolute-path normalization.
    /// </summary>
    /// <param name="left">First directory path to compare.</param>
    /// <param name="right">Second directory path to compare.</param>
    /// <param name="comparison">
    /// String comparison that models the target filesystem's case sensitivity. Production callers
    /// use <see cref="GetPathComparison"/>; tests can pass a deterministic comparison to verify
    /// case-sensitive and case-insensitive behavior without depending on the CI filesystem.
    /// </param>
    /// <returns>
    /// <c>true</c> when the normalized directories are equal, when <paramref name="left"/>
    /// contains <paramref name="right"/>, or when <paramref name="right"/> contains
    /// <paramref name="left"/>; otherwise <c>false</c>.
    /// </returns>
    internal static bool DirectoriesOverlap(string left, string right, StringComparison comparison)
    {
        var normalizedLeft = Normalize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)));
        var normalizedRight = Normalize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));

        return string.Equals(normalizedLeft, normalizedRight, comparison)
            || IsDirectoryAncestor(normalizedLeft, normalizedRight, comparison)
            || IsDirectoryAncestor(normalizedRight, normalizedLeft, comparison);
    }

    private static bool IsDirectoryAncestor(string ancestor, string descendant, StringComparison comparison)
    {
        var prefix = ancestor.EndsWith('/') ? ancestor : ancestor + "/";
        return descendant.StartsWith(prefix, comparison);
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private async Task<SlowTestDiagnosticsRun> RunSlowTestDiagnosticsAsync(
        CoverageRunnerOptions options,
        IReadOnlyList<ProjectRunResult> results,
        Func<long> getTotalSeconds,
        CancellationToken cancellationToken)
    {
        var diagnosticTimer = _clock.StartTimer();
        try
        {
            var report = await SlowTestDiagnosticsWriter.CollectAsync(options, results, cancellationToken);
            var diagnostics = await SlowTestDiagnosticsWriter.WriteAsync(
                options,
                report,
                () => diagnosticTimer.ElapsedSeconds,
                aggregationSeconds => CalculateAggregationPercent(aggregationSeconds, getTotalSeconds()),
                cancellationToken);

            await WriteSlowTestDiagnosticsStatusAsync(diagnostics);
            return diagnostics;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            var aggregationSeconds = diagnosticTimer.ElapsedSeconds;
            var diagnostics = new SlowTestDiagnosticsRun(
                Path.Join(options.OutputDirectory, SlowTestDiagnosticsWriter.MarkdownFileName),
                Path.Join(options.OutputDirectory, SlowTestDiagnosticsWriter.JsonFileName),
                aggregationSeconds,
                CalculateAggregationPercent(aggregationSeconds, getTotalSeconds()),
                WarningCount: 1,
                MetadataComplete: false);
            await _standardError.WriteLineAsync(FormattableString.Invariant(
                $"Slow-test diagnostics failed after {diagnostics.AggregationSeconds}s ({diagnostics.AggregationPercent:0.00}% overhead): {ex.Message}"));
            return diagnostics;
        }
    }

    private async Task WriteSlowTestDiagnosticsStatusAsync(SlowTestDiagnosticsRun diagnostics)
    {
        try
        {
            await _standardOut.WriteLineAsync(FormattableString.Invariant(
                $"Slow-test diagnostics: {diagnostics.MarkdownPath} ({diagnostics.AggregationSeconds}s, {diagnostics.AggregationPercent:0.00}% overhead)"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _standardError.WriteLineAsync($"Slow-test diagnostics status output failed: {ex.Message}");
        }
    }

    private static decimal CalculateAggregationPercent(long aggregationSeconds, long totalSeconds)
    {
        return totalSeconds <= 0 ? 0 : aggregationSeconds * 100m / totalSeconds;
    }
}

/// <summary>
/// Test project selected for coverage execution after solution discovery and group filtering.
/// </summary>
/// <param name="RelativePath">
/// Path exactly as reported by <c>dotnet sln list</c>. This value is used for stable logging and
/// timings so CI output follows solution order.
/// </param>
/// <param name="FullPath">
/// Absolute project file path passed to <c>dotnet test</c>. Relative solution entries are resolved
/// against the solution directory.
/// </param>
/// <param name="Group">
/// Coverage group from <see cref="TestProjectClassifier.GetGroup"/>. Valid scheduled groups are
/// <c>core</c>, <c>tools</c>, <c>web</c>, <c>docs</c>, <c>razorwire</c>, and
/// <c>integration</c>; unmatched paths fall back to <c>core</c>.
/// </param>
/// <param name="Slug">
/// File-system-safe artifact directory slug. The slug must be a single path segment because it is
/// used below <c>projects/</c> and in top-level JUnit file names.
/// </param>
/// <param name="IsExclusive">
/// Whether the scheduler must drain active work before running this project alone. This is used for
/// integration and Playwright projects to avoid browser/server/resource contention during the first
/// parallelism rollout.
/// </param>
internal sealed record TestProject(string RelativePath, string FullPath, string Group, string Slug, bool IsExclusive);

/// <summary>
/// Result from one project test run, including timing, process outcome, and captured log location.
/// </summary>
/// <param name="Index">
/// Original solution-order index. Log replay and <c>timings.json</c> sort by this value so parallel
/// execution does not make CI output unstable.
/// </param>
/// <param name="Project">Project metadata used to write timings, classify failures, and locate artifacts.</param>
/// <param name="Seconds">Measured project runtime in whole seconds from the injected clock.</param>
/// <param name="ExitCode">
/// Exit code returned by <c>dotnet test</c>. Non-zero values are recorded and reported after all
/// selected projects have been scheduled; they do not stop remaining projects from running.
/// </param>
/// <param name="JunitFile">
/// Full path to the project JUnit XML artifact. Diagnostics join on this recorded path instead of
/// reconstructing filenames from project slugs.
/// </param>
/// <param name="LogFile">Full path to the per-project <c>dotnet-test.log</c> file.</param>
internal sealed record ProjectRunResult(int Index, TestProject Project, long Seconds, int ExitCode, string JunitFile, string LogFile);
