using System.Diagnostics;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageRunTests
{
    [Fact]
    public async Task RunAsync_DryRun_ShouldListSlnxDiscoveryAndUniqueProjectSlugs()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/First/Foo.Tests.csproj", "<Project />");
        repo.WriteFile("tests/Second/Foo.Tests.csproj", "<Project><PackageReference Include=\"Microsoft.Playwright\" /></Project>");
        repo.WriteFile("src/App/App.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        var priorCoverage = repo.WriteFile("TestResults/coverage-merged/coverage.cobertura.xml", "old coverage");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                Project(s)
                ----------
                tests/First/Foo.Tests.csproj
                tests/Second/Foo.Tests.csproj
                src/App/App.csproj
                """
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(SolutionPath: solution, DryRun: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(priorCoverage));
        Assert.Equal("old coverage", File.ReadAllText(priorCoverage));
        Assert.False(Directory.Exists(Path.Join(repo.Path, "TestResults", "coverage-merged", "projects")));
        Assert.Single(runner.Commands);
        Assert.Equal("sln", runner.Commands[0].Arguments[0]);
        var output = console.ReadOutputString();
        Assert.Contains("Sample.slnx", output, StringComparison.Ordinal);
        Assert.Contains("include parallel", output, StringComparison.Ordinal);
        Assert.Contains("include exclusive", output, StringComparison.Ordinal);
        Assert.Contains("skip src/App/App.csproj", output, StringComparison.Ordinal);
        var includeLines = output.Split(Environment.NewLine).Where(line => line.Contains("projects/Foo.Tests-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, includeLines.Length);
        Assert.NotEqual(includeLines[0], includeLines[1]);
    }

    [Fact]
    public async Task RunAsync_DryRun_ShouldExcludeDiscoveredProjectsBeforeReadingProjectFiles()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Unit/Unit.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                Project(s)
                ----------
                tests/Unit/Unit.Tests.csproj
                tests/e2e/Browser.Playwright.Tests.csproj
                """
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            SolutionPath: solution,
            ExcludeTestProjects: ["Browser*.Tests.csproj", "tests/**/Browser*.Tests.csproj"],
            DryRun: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(runner.Commands);
        var output = console.ReadOutputString();
        Assert.Contains("include parallel  tests/Unit/Unit.Tests.csproj", output, StringComparison.Ordinal);
        Assert.Contains("skip tests/e2e/Browser.Playwright.Tests.csproj", output, StringComparison.Ordinal);
        Assert.Contains(
            "matched --exclude-test-project pattern(s): 'Browser*.Tests.csproj', 'tests/**/Browser*.Tests.csproj'",
            output,
            StringComparison.Ordinal);
        Assert.Equal(1, output.Split("skip tests/e2e/Browser.Playwright.Tests.csproj", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task RunAsync_ShouldRunIncludedProjectAndCreateNoExcludedProjectArtifacts()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Unit/Unit.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                Project(s)
                ----------
                tests/Unit/Unit.Tests.csproj
                tests/e2e/Browser.Playwright.Tests.csproj
                """
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            SolutionPath: solution,
            ExcludeTestProjects: ["tests/**/Browser*.Tests.csproj"]);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var test = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
        Assert.Contains(test.Arguments, argument => NormalizeTestPath(argument).EndsWith("tests/Unit/Unit.Tests.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(
            runner.Commands,
            command => command.Arguments.Any(argument => NormalizeTestPath(argument).EndsWith("tests/e2e/Browser.Playwright.Tests.csproj", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(Path.Join(result.OutputDirectory, "projects")),
            directory => directory.Contains("Browser.Playwright.Tests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ShouldFailUnmatchedExclusionBeforeOutputCleanupOrBuild()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Unit/Unit.Tests.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        var prior = repo.WriteFile("TestResults/coverage-merged/prior.txt", "preserve");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = "tests/Unit/Unit.Tests.csproj",
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            SolutionPath: solution,
            ExcludeTestProjects: ["tests/**/Browser*.Tests.csproj"]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV112", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tests/**/Browser*.Tests.csproj", exception.Message, StringComparison.Ordinal);
        Assert.Equal("preserve", File.ReadAllText(prior));
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task RunAsync_ShouldNotCountNonTestProjectAsExclusionMatch()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Unit/Unit.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                src/Browser.csproj
                tests/Unit/Unit.Tests.csproj
                """,
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            SolutionPath: solution,
            ExcludeTestProjects: ["Browser.csproj"],
            DryRun: true);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV112", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReportStableDetailsWhenAllTestProjectsAreExcluded()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                tests/e2e/Browser.Tests.csproj
                tests/e2e/Browser.Playwright.Tests.csproj
                """,
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            SolutionPath: solution,
            ExcludeTestProjects: ["tests/**/Browser*.Tests.csproj"],
            PriorityTestProjects: ["   "],
            DryRun: true);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV105", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Excluded 2 discovered test project(s) using 1 pattern(s).", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tests/**/Browser*.Tests.csproj: 2 match(es)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tests/e2e/Browser.Tests.csproj, tests/e2e/Browser.Playwright.Tests.csproj", exception.Message, StringComparison.Ordinal);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task RunAsync_ShouldExcludeExternalSolutionProjectUsingLeadingParentPattern()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("app/Sample.slnx", "<Solution />");
        repo.WriteFile("app/tests/Unit.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                tests/Unit.Tests.csproj
                ../Shared/Shared.Tests.csproj
                """,
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            SolutionPath: solution,
            ExcludeTestProjects: ["../Shared/*.Tests.csproj"],
            DryRun: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("skip ../Shared/Shared.Tests.csproj", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectExplicitProjectSelectionWithDiscoveryExclusion()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Unit.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: [project],
            ExcludeTestProjects: ["Browser.Tests.csproj"]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be combined", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectExclusiveSelectorForExcludedProject()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Unit.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                tests/Unit.Tests.csproj
                tests/Browser.Tests.csproj
                """,
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            SolutionPath: solution,
            ExcludeTestProjects: ["Browser.Tests.csproj"],
            ExclusiveTestProjects: ["Browser.Tests.csproj"],
            DryRun: true);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot target an excluded", exception.Message, StringComparison.Ordinal);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectPrioritySelectorWhenEveryProjectIsExcluded()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = "tests/Browser.Tests.csproj",
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            SolutionPath: solution,
            ExcludeTestProjects: ["Browser.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            PriorityTestProjects: ["Browser.Tests.csproj"],
            DryRun: true);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("did not match any selected", exception.Message, StringComparison.Ordinal);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectPrioritySelectorForExcludedProjectWhenAnotherProjectRemains()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Unit.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                src/App.csproj
                tests/Unit.Tests.csproj
                tests/Browser.Tests.csproj
                """,
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            SolutionPath: solution,
            ExcludeTestProjects: ["Browser.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            PriorityTestProjects: ["Browser.Tests.csproj"],
            DryRun: true);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("did not match any selected", exception.Message, StringComparison.Ordinal);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task RunAsync_ShouldIgnoreBlankExclusiveSelectorWhenCheckingExcludedProjects()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Unit.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                tests/Unit.Tests.csproj
                tests/Browser.Tests.csproj
                """,
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            SolutionPath: solution,
            ExcludeTestProjects: ["Browser.Tests.csproj"],
            ExclusiveTestProjects: ["   "],
            DryRun: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("include parallel  tests/Unit.Tests.csproj", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRunExplicitProjectsAndWriteMergedArtifacts()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var reportGenerator = new RecordingReportGenerator();
        var workflow = CreateWorkflow(runner, reportGenerator);
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: [project],
            IncludeFilter: "[Sample]*",
            Loggers: ["trx"],
            TestArguments: ["--filter", "Category=Unit"]);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Join(repo.Path, "TestResults", "coverage-merged", "coverage.cobertura.xml")));
        Assert.True(File.Exists(Path.Join(repo.Path, "TestResults", "coverage-merged", "summary.txt")));
        Assert.True(File.Exists(Path.Join(repo.Path, "TestResults", "coverage-merged", "timings.json")));
        Assert.Single(reportGenerator.CoverageFiles);
        var testCommand = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
        Assert.Contains("--logger:trx", testCommand.Arguments);
        Assert.Contains("/p:Include=[Sample]*", testCommand.Arguments);
        Assert.Contains("/p:Exclude=[*.Tests]*%2c[*.IntegrationTests]*", testCommand.Arguments);
        Assert.Contains("--filter", testCommand.Arguments);
        Assert.DoesNotContain("--no-build", testCommand.Arguments);
        Assert.DoesNotContain("[ForgeTrust.AppSurface.", string.Join(" ", testCommand.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain("build", runner.Commands.Select(command => command.Arguments.FirstOrDefault()));
    }

    [Fact]
    public async Task RunAsync_TestResultsJunit_ShouldWriteManagedArtifactsAndTimings()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], TestResults: CoverageRunTestResultFormat.Junit);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var testCommand = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
        var junitLogger = Assert.Single(testCommand.Arguments, argument => argument.StartsWith("--logger:junit;LogFilePath=", StringComparison.Ordinal));
        Assert.Contains("junit-coverage-1-Sample.Tests-", junitLogger, StringComparison.Ordinal);
        var junitPath = junitLogger["--logger:junit;LogFilePath=".Length..];
        Assert.True(File.Exists(junitPath));
        Assert.DoesNotContain("GitHubActions", junitLogger, StringComparison.Ordinal);
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"junitFiles\": 1", timings, StringComparison.Ordinal);
        Assert.Contains("\"format\": \"junit\"", timings, StringComparison.Ordinal);
        Assert.Contains("\"parserStatus\": \"available\"", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TestResultsJunit_ShouldRecordMissingManagedArtifactWithoutDiagnostics()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner { WriteJunitFiles = false };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], TestResults: CoverageRunTestResultFormat.Junit);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"junitFiles\": 0", timings, StringComparison.Ordinal);
        Assert.Contains("\"parserStatus\": \"missing\"", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TestResultsInvalidInternalValue_ShouldThrowUnreachableDiagnostic()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], TestResults: (CoverageRunTestResultFormat)999);

        await Assert.ThrowsAsync<UnreachableException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_TestResultsJunit_ShouldAcceptCaseInsensitiveFormat()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var command = new CoverageRunCommand(CreateWorkflow(runner, new RecordingReportGenerator()))
        {
            TestProjects = [project],
            TestResults = "JUNIT",
        };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console, CancellationToken.None);

        var testCommand = Assert.Single(runner.Commands, recorded => recorded.Arguments.FirstOrDefault() == "test");
        Assert.Contains(testCommand.Arguments, argument => argument.StartsWith("--logger:junit;LogFilePath=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_SlowTestDiagnostics_ShouldImplyJunitAndWriteDiagnostics()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], SlowTestDiagnostics: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Join(result.OutputDirectory, "slow-test-diagnostics.md")));
        Assert.True(File.Exists(Path.Join(result.OutputDirectory, "slow-test-diagnostics.json")));
        Assert.Contains("Managed test results: junit (enabled for slow-test diagnostics)", console.ReadOutputString(), StringComparison.Ordinal);
        var testCommand = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
        Assert.Contains(testCommand.Arguments, argument => argument.StartsWith("--logger:junit;LogFilePath=", StringComparison.Ordinal));
        var summary = File.ReadAllText(Path.Join(result.OutputDirectory, "summary.txt"));
        Assert.Contains("Managed test results: junit (enabled for slow-test diagnostics)", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("timings.jsonSlow-test", summary, StringComparison.Ordinal);
        Assert.Contains("Slow-test diagnostics warnings: 0", summary, StringComparison.Ordinal);
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"warningCount\": 0", timings, StringComparison.Ordinal);
        Assert.Contains("\"metadataComplete\": true", timings, StringComparison.Ordinal);
        Assert.Contains("\"aggregationSeconds\"", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_SlowTestDiagnostics_ShouldImplyManagedJunitResults()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var command = new CoverageRunCommand(CreateWorkflow(runner, new RecordingReportGenerator()))
        {
            TestProjects = [project],
            SlowTestDiagnostics = true,
        };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console, CancellationToken.None);

        var testCommand = Assert.Single(runner.Commands, recorded => recorded.Arguments.FirstOrDefault() == "test");
        Assert.Contains(testCommand.Arguments, argument => argument.StartsWith("--logger:junit;LogFilePath=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ShouldOmitWhitespaceExcludeFilterAndReplayTruncatedLogs()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            TestOutput = new string('x', 80_050)
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], ExcludeFilter: " ");

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var testCommand = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
        Assert.DoesNotContain(testCommand.Arguments, argument => argument.StartsWith("/p:Exclude=", StringComparison.Ordinal));
        Assert.Contains("[log truncated; see full log on disk]", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Build_ShouldBuildExplicitProjectsBeforeTests()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], Build: true, NoRestore: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var buildCommand = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "build");
        var testCommand = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
        Assert.Same(buildCommand, runner.Commands[0]);
        Assert.Same(testCommand, runner.Commands[1]);
        Assert.Contains(project, buildCommand.Arguments);
        Assert.Contains("--no-restore", buildCommand.Arguments);
        Assert.Contains("--no-restore", testCommand.Arguments);
        Assert.Contains("--no-build", testCommand.Arguments);
    }

    [Fact]
    public async Task RunAsync_NoBuild_ShouldSkipSolutionBuildAndRunDiscoveredProjects()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                Project(s)
                ----------
                tests/Sample.Tests/Sample.Tests.csproj
                """
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(SolutionPath: solution, NoBuild: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(runner.Commands, command => command.Arguments.FirstOrDefault() == "sln");
        var testCommand = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
        Assert.Contains("--no-build", testCommand.Arguments);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "build");
    }

    [Fact]
    public async Task RunAsync_ShouldBuildDiscoveredSolutionBeforeTests()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                Project(s)
                ----------
                tests/Sample.Tests/Sample.Tests.csproj
                """
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(SolutionPath: solution);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var buildCommand = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "build");
        var testCommand = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
        Assert.Contains(solution, buildCommand.Arguments);
        Assert.Contains("--no-build", testCommand.Arguments);
    }

    [Fact]
    public async Task RunAsync_ShouldWriteActualCoverageFileCountToTimings()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var first = repo.WriteFile("tests/First.Tests/First.Tests.csproj", "<Project />");
        var second = repo.WriteFile("tests/Second.Tests/Second.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            ProjectsWithoutCoverage = { second },
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [first, second]);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var timings = File.ReadAllText(Path.Join(repo.Path, "TestResults", "coverage-merged", "timings.json"));
        Assert.Contains("\"coverageFiles\": 1", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldDiscoverSingleImplicitSolution()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                Project(s)
                ----------
                tests/Sample.Tests/Sample.Tests.csproj
                """
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(NoBuild: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var list = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "sln");
        Assert.EndsWith("Sample.slnx", list.Arguments[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoDiscoverExclusive_ShouldLeavePlaywrightProjectParallel()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Browser.Tests/Browser.Tests.csproj", "<Project><PackageReference Include=\"Microsoft.Playwright\" /></Project>");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], DryRun: true, NoDiscoverExclusive: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("include parallel", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldCleanOwnedOutputByDefault()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        var oldCoverage = repo.WriteFile("TestResults/coverage-merged/coverage.cobertura.xml", "old coverage");
        var oldJunit = repo.WriteFile("TestResults/coverage-merged/junit-old.xml", "old junit");
        var oldProjectArtifact = repo.WriteFile("TestResults/coverage-merged/projects/old/coverage.cobertura.xml", "old project");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project]);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(oldJunit));
        Assert.False(File.Exists(oldProjectArtifact));
        Assert.NotEqual("old coverage", File.ReadAllText(oldCoverage));
    }

    [Fact]
    public async Task RunAsync_ShouldRejectMissingExplicitTestProject()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: ["tests/Missing.Tests/Missing.Tests.csproj"]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Test project file not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReportSolutionListFailure()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner { SlnExitCode = 9 };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(SolutionPath: solution);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV102", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Failed to list solution projects", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectMissingSolutionPath()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(SolutionPath: "missing.slnx");

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV102", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Solution file not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectUnsupportedSolutionExtension()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.txt", "not a solution");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(SolutionPath: solution);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV102", exception.Message, StringComparison.Ordinal);
        Assert.Contains(".sln or .slnx", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectMissingImplicitSolution()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest();

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV102", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No solution file was found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectAmbiguousImplicitSolutions()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("A.sln", string.Empty);
        repo.WriteFile("B.slnx", string.Empty);
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest();

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV102", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Multiple solution files were found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectDiscoveryWithNoTestProjects()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("src/App/App.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                Project(s)
                ----------
                src/App/App.csproj
                """
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(SolutionPath: solution);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV105", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Skipped 1 non-test project", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldExplainWhenDiscoveryReturnsNoProjectEntries()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = "Project(s)\n----------",
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(SolutionPath: solution);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV105", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No .csproj entries were returned by discovery", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectCurrentDirectoryOutput()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], OutputDirectory: ".");

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Empty(runner.Commands);
        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("current working directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectUnsafeOutputPaths()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        var outputFile = repo.WriteFile("coverage-output.txt", "not a directory");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();

        var blankException = Assert.Throws<CommandException>(() => CoverageRunOutputGuard.Validate(string.Empty, repo.Path, []));
        Assert.Contains("ASCOV109", blankException.Message, StringComparison.Ordinal);
        Assert.Contains("output path was blank", blankException.Message, StringComparison.Ordinal);
        var solutionDirectory = Directory.CreateDirectory(Path.Join(repo.Path, "solution")).FullName;
        var solutionException = Assert.Throws<CommandException>(() => CoverageRunOutputGuard.Validate(solutionDirectory, solutionDirectory, []));
        Assert.Contains("ASCOV109", solutionException.Message, StringComparison.Ordinal);
        Assert.Contains("solution directory", solutionException.Message, StringComparison.Ordinal);
        await AssertUnsafeOutputAsync(workflow, console, project, outputFile, "points to a file");
        await AssertUnsafeOutputAsync(workflow, console, project, Path.GetDirectoryName(project)!, "test project directory");
        await AssertUnsafeOutputAsync(workflow, console, project, Path.GetPathRoot(repo.Path)!, "filesystem root");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && !string.Equals(Path.GetPathRoot(home), home, StringComparison.Ordinal))
        {
            var homeException = Assert.Throws<CommandException>(() => CoverageRunOutputGuard.Validate(home, solutionDirectory, []));
            Assert.Contains("ASCOV109", homeException.Message, StringComparison.Ordinal);
            Assert.Contains("user home directory", homeException.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunAsync_ShouldRejectInvalidOutputPath()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], OutputDirectory: "bad\0path");

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Path value is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoClean_ShouldPreserveKnownOwnedOutput()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        var oldJunit = repo.WriteFile("TestResults/coverage-merged/junit-old.xml", "old junit");
        var oldProjectArtifact = repo.WriteFile("TestResults/coverage-merged/projects/old/coverage.cobertura.xml", "old project");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], Clean: false);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(oldJunit));
        Assert.True(File.Exists(oldProjectArtifact));
    }

    [Fact]
    public async Task RunAsync_Clean_ShouldDeleteStaleManagedResultsAndDiagnostics()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        var oldJunit = repo.WriteFile("TestResults/coverage-merged/junit-coverage-1-old.xml", "old junit");
        var oldTestResult = repo.WriteFile("TestResults/coverage-merged/test-results-old.xml", "old test result");
        var oldMarkdown = repo.WriteFile("TestResults/coverage-merged/slow-test-diagnostics.md", "old diagnostics");
        var oldJson = repo.WriteFile("TestResults/coverage-merged/slow-test-diagnostics.json", "{}");
        var oldGateMarkdown = repo.WriteFile("TestResults/coverage-merged/coverage-gate.md", "old gate");
        var oldGateJson = repo.WriteFile("TestResults/coverage-merged/coverage-gate.json", "{}");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project]);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(oldJunit));
        Assert.False(File.Exists(oldTestResult));
        Assert.False(File.Exists(oldMarkdown));
        Assert.False(File.Exists(oldJson));
        Assert.False(File.Exists(oldGateMarkdown));
        Assert.False(File.Exists(oldGateJson));
    }

    [Fact]
    public async Task RunAsync_Clean_ShouldTakeOverLegacyCoverageOutputWithoutMarker()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        var oldJunit = repo.WriteFile("TestResults/coverage-merged/junit-all-1-Sample.Tests.xml", "old junit");
        var oldCoverage = repo.WriteFile("TestResults/coverage-merged/coverage.cobertura.xml", "old coverage");
        var oldGate = repo.WriteFile("TestResults/coverage-merged/coverage-gate.json", "{}");
        var oldProjectArtifact = repo.WriteFile("TestResults/coverage-merged/projects/old/coverage.cobertura.xml", "old project");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project]);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(oldJunit));
        Assert.False(File.Exists(oldGate));
        Assert.False(File.Exists(oldProjectArtifact));
        Assert.NotEqual("old coverage", File.ReadAllText(oldCoverage));
        Assert.True(File.Exists(Path.Join(result.OutputDirectory, ".appsurface-coverage-output")));
    }

    [Fact]
    public async Task RunAsync_SlowTestDiagnostics_ShouldRecordJunitParseFailuresInTimings()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner { JunitContent = "<testsuite>" };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], SlowTestDiagnostics: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"parserStatus\": \"parseFailed\"", timings, StringComparison.Ordinal);
        Assert.Contains("\"warningCount\": 1", timings, StringComparison.Ordinal);
        Assert.Contains("\"metadataComplete\": false", timings, StringComparison.Ordinal);
        var diagnostics = File.ReadAllText(Path.Join(result.OutputDirectory, "slow-test-diagnostics.md"));
        Assert.Contains("Project metadata complete: False", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Failed to parse JUnit XML", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SlowTestDiagnostics_ShouldMarkMetadataIncompleteForJunitMetadataWarnings()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            JunitContent = """
                <testsuite tests="1" failures="0" skipped="0">
                  <testcase classname="SampleTests" name="MissingTime" />
                </testsuite>
                """,
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], SlowTestDiagnostics: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"parserStatus\": \"parsed\"", timings, StringComparison.Ordinal);
        Assert.Contains("\"warningCount\": 1", timings, StringComparison.Ordinal);
        Assert.Contains("\"metadataComplete\": false", timings, StringComparison.Ordinal);
        var diagnostics = File.ReadAllText(Path.Join(result.OutputDirectory, "slow-test-diagnostics.md"));
        Assert.Contains("Project metadata complete: False", diagnostics, StringComparison.Ordinal);
        Assert.Contains("is missing time", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SlowTestDiagnostics_ShouldWarnWhenDiagnosticsCannotBeWritten()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        Directory.CreateDirectory(Path.Join(repo.Path, "TestResults", "coverage-merged", "slow-test-diagnostics.md"));
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], SlowTestDiagnostics: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Slow-test diagnostics failed", console.ReadErrorString(), StringComparison.Ordinal);
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"warningCount\": 1", timings, StringComparison.Ordinal);
        Assert.Contains("\"metadataComplete\": false", timings, StringComparison.Ordinal);
        Assert.Contains("\"parserStatus\": \"diagnosticsFailed\"", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SlowTestDiagnostics_ShouldWarnWhenExistingDiagnosticsPathCannotBeReused()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        Directory.CreateDirectory(Path.Join(repo.Path, "TestResults", "coverage-merged", "slow-test-diagnostics.json"));
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], SlowTestDiagnostics: true, Clean: false);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Slow-test diagnostics failed", console.ReadErrorString(), StringComparison.Ordinal);
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"parserStatus\": \"diagnosticsFailed\"", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldRecordEmptyMetadataAndRewriteFinalOverhead()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");

        var report = await CoverageRunSlowTestDiagnosticsWriter.CollectAsync([], CancellationToken.None);
        var calls = 0;
        var diagnostics = await CoverageRunSlowTestDiagnosticsWriter.WriteAsync(
            repo.Path,
            report,
            () => calls++ == 0 ? 1 : 2,
            seconds => seconds * 10m,
            CancellationToken.None);

        Assert.False(report.MetadataComplete);
        Assert.Contains(report.Warnings, warning => warning.Contains("No project metadata was available", StringComparison.Ordinal));
        Assert.Equal(2, diagnostics.AggregationSeconds);
        Assert.Equal(20m, diagnostics.AggregationPercent);
        var markdown = File.ReadAllText(diagnostics.MarkdownPath);
        Assert.Contains("No project timing metadata was available.", markdown, StringComparison.Ordinal);
        Assert.Contains("No JUnit test cases were available.", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "Diagnostic aggregation overhead: 2s (20.00% of elapsed runner time at diagnostics generation)",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldParseStatusesAndMetadataWarnings()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var junit = repo.WriteFile(
            "junit.xml",
            """
            <testsuite tests="5" failures="1" errors="1" skipped="1">
              <testcase classname="Pipe|Class" name="Fail&#10;Name" time="3"><failure /></testcase>
              <testcase classname="ErrorClass" name="ErrorName" time="2"><error /></testcase>
              <testcase classname="SkipClass" name="SkipName" time="1"><skipped /></testcase>
              <testcase time="-1" />
              <testcase classname="BadTime" name="Nan" time="NaN" />
            </testsuite>
            """);
        var result = CreateProjectRunResult(repo.Path, junit);

        var report = await CoverageRunSlowTestDiagnosticsWriter.CollectAsync([result], CancellationToken.None);
        var diagnostics = await CoverageRunSlowTestDiagnosticsWriter.WriteAsync(
            repo.Path,
            report,
            () => 0,
            _ => 0,
            CancellationToken.None);

        Assert.False(report.MetadataComplete);
        Assert.Equal("parsed", diagnostics.ParserStatuses[junit]);
        Assert.Contains(report.TestCases, test => test.Status == "failed");
        Assert.Contains(report.TestCases, test => test.Status == "error");
        Assert.Contains(report.TestCases, test => test.Status == "skipped");
        Assert.Contains(report.Warnings, warning => warning.Contains("missing classname", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("missing name", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("invalid time '-1'", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("invalid time 'NaN'", StringComparison.Ordinal));
        var markdown = File.ReadAllText(diagnostics.MarkdownPath);
        Assert.Contains("Pipe\\|Class.Fail Name", markdown, StringComparison.Ordinal);
        Assert.Contains("| 3 | failed |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 2 | error |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 1 | skipped |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldWarnWhenProjectHasMultipleManagedJunitArtifacts()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var firstJunit = repo.WriteFile("first.xml", "<testsuite><testcase classname=\"First\" name=\"UsesFirst\" time=\"1\" /></testsuite>");
        var secondJunit = repo.WriteFile("second.xml", "<testsuite><testcase classname=\"Second\" name=\"Ignored\" time=\"2\" /></testsuite>");
        var result = CreateProjectRunResult(repo.Path, [firstJunit, secondJunit]);

        var report = await CoverageRunSlowTestDiagnosticsWriter.CollectAsync([result], CancellationToken.None);

        Assert.False(report.MetadataComplete);
        Assert.Equal(1, report.JunitFileCount);
        Assert.Equal("parsed", Assert.Single(report.Projects).ParserStatus);
        Assert.Contains(report.Warnings, warning => warning.Contains("multiple managed JUnit artifacts", StringComparison.Ordinal));
        Assert.Contains(report.TestCases, test => test.Name == "UsesFirst");
        Assert.DoesNotContain(report.TestCases, test => test.Name == "Ignored");
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldRecordMissingJunitFiles()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var missingDirectory = Directory.CreateDirectory(Path.Join(repo.Path, "missing")).FullName;
        var missingJunit = Path.Join(missingDirectory, "junit.xml");
        var result = CreateProjectRunResult(repo.Path, missingJunit);

        var report = await CoverageRunSlowTestDiagnosticsWriter.CollectAsync([result], CancellationToken.None);

        Assert.False(report.MetadataComplete);
        Assert.Equal(0, report.JunitFileCount);
        Assert.Equal("missing", Assert.Single(report.Projects).ParserStatus);
        Assert.Contains(report.Warnings, warning => warning.Contains("JUnit file was not created", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldRecordMissingJunitDirectories()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var missingJunit = Path.Join(repo.Path, "missing", "junit.xml");
        var result = CreateProjectRunResult(repo.Path, missingJunit);

        var report = await CoverageRunSlowTestDiagnosticsWriter.CollectAsync([result], CancellationToken.None);

        Assert.False(report.MetadataComplete);
        Assert.Equal("missing", Assert.Single(report.Projects).ParserStatus);
        Assert.Contains(report.Warnings, warning => warning.Contains("JUnit file was not created", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldRecordProjectsWithoutManagedJunit()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var result = CreateProjectRunResult(repo.Path, junitPath: null);

        var report = await CoverageRunSlowTestDiagnosticsWriter.CollectAsync([result], CancellationToken.None);

        Assert.False(report.MetadataComplete);
        Assert.Equal(0, report.JunitFileCount);
        Assert.Equal("notRequested", Assert.Single(report.Projects).ParserStatus);
        Assert.Contains(report.Warnings, warning => warning.Contains("No managed JUnit file was requested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldRecordDirectoryJunitReadFailures()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var junitDirectory = Path.Join(repo.Path, "junit-as-directory.xml");
        Directory.CreateDirectory(junitDirectory);
        var result = CreateProjectRunResult(repo.Path, junitDirectory);

        var report = await CoverageRunSlowTestDiagnosticsWriter.CollectAsync([result], CancellationToken.None);

        Assert.False(report.MetadataComplete);
        Assert.Equal("readFailed", Assert.Single(report.Projects).ParserStatus);
        Assert.Contains(report.Warnings, warning => warning.Contains("Failed to", StringComparison.Ordinal)
            && warning.Contains("JUnit XML", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldRecordIoJunitReadFailures()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var longName = new string('x', 4096) + ".xml";
        var result = CreateProjectRunResult(repo.Path, Path.Join(repo.Path, longName));

        var report = await CoverageRunSlowTestDiagnosticsWriter.CollectAsync([result], CancellationToken.None);

        Assert.False(report.MetadataComplete);
        Assert.Equal("readFailed", Assert.Single(report.Projects).ParserStatus);
        Assert.Contains(report.Warnings, warning => warning.Contains("Failed to read JUnit XML", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Clean_ShouldRejectUnmarkedOutputWithUnknownFiles()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/custom.txt", "not ours");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not marked as AppSurface-owned", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReportMergeFailure()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(
            new RecordingCoverageRunProcessRunner(),
            new RecordingReportGenerator { ExitCode = 42, WriteMergedCoverage = false });
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV104", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ReportGenerator exit code: 42", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectMalformedMergedCoverage()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(
            new RecordingCoverageRunProcessRunner(),
            new RecordingReportGenerator { MergedCoverage = "<not-coverage />" });
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV106", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Merged Cobertura file is malformed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldSummarizeCoverageRatesWhenValidCountsAreMissing()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(
            new RecordingCoverageRunProcessRunner(),
            new RecordingReportGenerator
            {
                MergedCoverage = "<coverage line-rate=\"0.75\" branch-rate=\"0.5\" />"
            });
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project]);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var summary = File.ReadAllText(Path.Join(result.OutputDirectory, "summary.txt"));
        Assert.Contains("Line coverage: 75.00%", summary, StringComparison.Ordinal);
        Assert.Contains("Branch coverage: 50.00%", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectMissingCoverageWithFixAndLogPath()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner { WriteCoverageFiles = false };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV103", exception.Message, StringComparison.Ordinal);
        Assert.Contains("coverlet.msbuild", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Log:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReportTestFailureBeforeMissingCoverage()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner { TestExitCode = 1, WriteCoverageFiles = false };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV120", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exited nonzero", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Log:", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Add coverlet.msbuild", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldBuildSolutionAndReportFailure()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            BuildExitCode = 7,
            SlnListOutput = """
                Project(s)
                ----------
                tests/Sample.Tests/Sample.Tests.csproj
                """
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(SolutionPath: solution, NoRestore: true);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV110", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Solution build failed", exception.Message, StringComparison.Ordinal);
        var build = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "build");
        Assert.Contains("--no-restore", build.Arguments);
    }

    [Fact]
    public async Task RunAsync_ShouldBuildExplicitProjectsAndReportFailure()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner { BuildExitCode = 7 };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project], Build: true);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV110", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Test project build failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldScheduleExclusiveProjectsBetweenParallelBatches()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var first = repo.WriteFile("tests/First.Tests/First.Tests.csproj", "<Project />");
        var second = repo.WriteFile("tests/Second.Tests/Second.Tests.csproj", "<Project />");
        var browser = repo.WriteFile("tests/Browser.Tests/Browser.Tests.csproj", "<Project><PackageReference Include=\"Microsoft.Playwright\" /></Project>");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            TestDelays =
            {
                [first] = TimeSpan.FromMilliseconds(40),
                [second] = TimeSpan.FromMilliseconds(40),
            },
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [first, second, browser], Parallelism: 2);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var tests = runner.Commands.Where(command => command.Arguments.FirstOrDefault() == "test").ToArray();
        Assert.Equal([first, second, browser], tests.Select(command => command.Arguments[1]).ToArray());
        Assert.True(tests[2].StartedAt >= tests[0].FinishedAt);
        Assert.True(tests[2].StartedAt >= tests[1].FinishedAt);
        Assert.Contains("(exclusive)", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldDrainParallelBatchWhenParallelismLimitIsReached()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var first = repo.WriteFile("tests/First.Tests/First.Tests.csproj", "<Project />");
        var second = repo.WriteFile("tests/Second.Tests/Second.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            TestDelays =
            {
                [first] = TimeSpan.FromMilliseconds(40),
            },
        };
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [first, second], Parallelism: 1);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var tests = runner.Commands.Where(command => command.Arguments.FirstOrDefault() == "test").ToArray();
        Assert.Equal([first, second], tests.Select(command => command.Arguments[1]).ToArray());
        Assert.True(tests[1].StartedAt >= tests[0].FinishedAt);
    }

    [Fact]
    public async Task RunAsync_LongestFirst_ShouldRunMeasuredProjectsFirstWithinExclusiveSegments()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/First.Tests/First.Tests.csproj", "<Project />");
        repo.WriteFile("tests/Slow.Tests/Slow.Tests.csproj", "<Project />");
        repo.WriteFile("tests/Browser.Tests/Browser.Tests.csproj", "<Project><PackageReference Include=\"Microsoft.Playwright\" /></Project>");
        repo.WriteFile("tests/After.Tests/After.Tests.csproj", "<Project />");
        var priorTimings = repo.WriteFile("prior-timings.json", """
            {
              "projects": [
                { "project": "tests/First.Tests/First.Tests.csproj", "seconds": 5 },
                { "project": ".\\tests\\Slow.Tests\\Slow.Tests.csproj", "seconds": 50 },
                { "project": "./tests/After.Tests/After.Tests.csproj", "seconds": 90 }
              ]
            }
            """);
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects:
            [
                "tests/First.Tests/First.Tests.csproj",
                "tests/Slow.Tests/Slow.Tests.csproj",
                "tests/Browser.Tests/Browser.Tests.csproj",
                "tests/After.Tests/After.Tests.csproj",
            ],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            ScheduleTimingsPath: priorTimings,
            NoDiscoverExclusive: true,
            ExclusiveTestProjects: [" ./tests/Browser.Tests/Browser.Tests.csproj "]);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var tests = runner.Commands.Where(command => command.Arguments.FirstOrDefault() == "test").ToArray();
        Assert.Equal(["Slow.Tests", "First.Tests", "Browser.Tests", "After.Tests"], tests.Select(command => Path.GetFileNameWithoutExtension(command.Arguments[1])).ToArray());
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"mode\": \"longest-first\"", timings, StringComparison.Ordinal);
        Assert.Contains("\"originalIndex\": 1", timings, StringComparison.Ordinal);
        Assert.Contains("\"executionIndex\": 0", timings, StringComparison.Ordinal);
        Assert.Contains("\"scheduleReason\": \"prior-timing\"", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_LongestFirst_ShouldKeepJunitArtifactsOnOriginalIndex()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/First.Tests/First.Tests.csproj", "<Project />");
        repo.WriteFile("tests/Slow.Tests/Slow.Tests.csproj", "<Project />");
        var priorTimings = repo.WriteFile("prior-timings.json", """
            {
              "projects": [
                { "project": "tests/First.Tests/First.Tests.csproj", "seconds": 5 },
                { "project": "tests/Slow.Tests/Slow.Tests.csproj", "seconds": 50 }
              ]
            }
            """);
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/First.Tests/First.Tests.csproj", "tests/Slow.Tests/Slow.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            ScheduleTimingsPath: priorTimings,
            TestResults: CoverageRunTestResultFormat.Junit);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var tests = runner.Commands.Where(command => command.Arguments.FirstOrDefault() == "test").ToArray();
        Assert.Equal(["Slow.Tests", "First.Tests"], tests.Select(command => Path.GetFileNameWithoutExtension(command.Arguments[1])).ToArray());
        Assert.Contains(tests[0].Arguments, argument => argument.Contains("junit-coverage-2-Slow.Tests-", StringComparison.Ordinal));
        Assert.Contains(tests[1].Arguments, argument => argument.Contains("junit-coverage-1-First.Tests-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_LongestFirst_ShouldReadInferredTimingsBeforeClean()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/First.Tests/First.Tests.csproj", "<Project />");
        repo.WriteFile("tests/Slow.Tests/Slow.Tests.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        repo.WriteFile("TestResults/coverage-merged/timings.json", """
            {
              "projects": [
                { "project": "tests/First.Tests/First.Tests.csproj", "seconds": 5 },
                { "project": "tests/Slow.Tests/Slow.Tests.csproj", "seconds": 50 }
              ]
            }
            """);
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/First.Tests/First.Tests.csproj", "tests/Slow.Tests/Slow.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var tests = runner.Commands.Where(command => command.Arguments.FirstOrDefault() == "test").ToArray();
        Assert.Equal(["Slow.Tests", "First.Tests"], tests.Select(command => Path.GetFileNameWithoutExtension(command.Arguments[1])).ToArray());
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"kind\": \"inferred\"", timings, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"loaded\"", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_LongestFirst_MissingInferredTimings_ShouldWarnAndUseInputOrderForUnknownProjects()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/First.Tests/First.Tests.csproj", "<Project />");
        repo.WriteFile("tests/Second.Tests/Second.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/First.Tests/First.Tests.csproj", "tests/Second.Tests/Second.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var tests = runner.Commands.Where(command => command.Arguments.FirstOrDefault() == "test").ToArray();
        Assert.Equal(["First.Tests", "Second.Tests"], tests.Select(command => Path.GetFileNameWithoutExtension(command.Arguments[1])).ToArray());
        Assert.Contains("Schedule warning", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_LongestFirst_UnusableInferredTimings_ShouldWarnAndUseInputOrder()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/timings.json", """
            {
              "projects": []
            }
            """);
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/Sample.Tests/Sample.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("were unusable (No project timings were found.)", console.ReadErrorString(), StringComparison.Ordinal);
        var test = Assert.Single(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
        Assert.Equal("Sample.Tests.csproj", Path.GetFileName(test.Arguments[1]));
    }

    [Fact]
    public async Task RunAsync_LongestFirst_ExplicitMalformedTimings_ShouldThrowBeforeTests()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        var priorTimings = repo.WriteFile("prior-timings.json", "{ not-json");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/Sample.Tests/Sample.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            ScheduleTimingsPath: priorTimings);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--schedule-timings file could not be read", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
    }

    [Theory]
    [InlineData("{}", "must contain a projects array")]
    [InlineData("{\"projects\": {}}", "must contain a projects array")]
    [InlineData("{\"projects\": [null]}", "must include string project and integer seconds fields")]
    public async Task RunAsync_LongestFirst_ExplicitInvalidTimingShape_ShouldThrowBeforeTests(
        string timingsJson,
        string expectedMessage)
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        var priorTimings = repo.WriteFile("prior-timings.json", timingsJson);
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/Sample.Tests/Sample.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            ScheduleTimingsPath: priorTimings);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
    }

    [Fact]
    public async Task RunAsync_LongestFirst_MissingExplicitTimings_ShouldThrowBeforeTests()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/Sample.Tests/Sample.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            ScheduleTimingsPath: "missing-timings.json");

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--schedule-timings file was not found", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
    }

    [Fact]
    public async Task RunAsync_LongestFirst_DuplicateExplicitTimings_ShouldThrowBeforeTests()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        var priorTimings = repo.WriteFile("prior-timings.json", """
            {
              "projects": [
                { "project": "tests/Sample.Tests/Sample.Tests.csproj", "seconds": 5 },
                { "project": "tests/Sample.Tests/Sample.Tests.csproj", "seconds": 10 }
              ]
            }
            """);
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/Sample.Tests/Sample.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            ScheduleTimingsPath: priorTimings);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Duplicate project timing", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
    }

    [Fact]
    public async Task RunAsync_LongestFirst_PriorityProjects_ShouldRunBeforeMeasuredProjects()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Fast.Tests/Fast.Tests.csproj", "<Project />");
        repo.WriteFile("tests/Slow.Tests/Slow.Tests.csproj", "<Project />");
        var priorTimings = repo.WriteFile("prior-timings.json", """
            {
              "projects": [
                { "project": "tests/Fast.Tests/Fast.Tests.csproj", "seconds": 1 },
                { "project": "tests/Slow.Tests/Slow.Tests.csproj", "seconds": 90 }
              ]
            }
            """);
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/Fast.Tests/Fast.Tests.csproj", "tests/Slow.Tests/Slow.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            ScheduleTimingsPath: priorTimings,
            PriorityTestProjects: [" Fast.Tests.csproj ", " .\\tests\\Slow.Tests\\Slow.Tests.csproj "]);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        var tests = runner.Commands.Where(command => command.Arguments.FirstOrDefault() == "test").ToArray();
        Assert.Equal(["Fast.Tests", "Slow.Tests"], tests.Select(command => Path.GetFileNameWithoutExtension(command.Arguments[1])).ToArray());
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"scheduleReason\": \"priority\"", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_LongestFirst_PriorityExclusiveProject_ShouldThrowBeforeTests()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Browser.Tests/Browser.Tests.csproj", "<Project><PackageReference Include=\"Microsoft.Playwright\" /></Project>");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/Browser.Tests/Browser.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            PriorityTestProjects: ["Browser.Tests.csproj"]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot target an exclusive project", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
    }

    [Fact]
    public async Task RunAsync_LongestFirst_UnmatchedPriorityProject_ShouldThrowBeforeTests()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/Sample.Tests/Sample.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            PriorityTestProjects: ["Missing.Tests.csproj"]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("did not match any selected test project", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
    }

    [Fact]
    public async Task RunAsync_LongestFirst_DuplicatePriorityProject_ShouldThrowBeforeTests()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/Sample.Tests/Sample.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            PriorityTestProjects: ["Sample.Tests.csproj", "Sample.Tests.csproj"]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("contains a duplicate project", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
    }

    [Fact]
    public async Task RunAsync_LongestFirst_DuplicatePriorityProjectAlias_ShouldThrowBeforeTests()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/Sample.Tests/Sample.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            PriorityTestProjects: ["tests/Sample.Tests/Sample.Tests.csproj", "Sample.Tests.csproj"]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("contains a duplicate project", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
    }

    [Fact]
    public async Task RunAsync_LongestFirst_AmbiguousPriorityProject_ShouldThrowBeforeTests()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        _ = repo.WriteFile("tests/First/Sample.Tests.csproj", "<Project />");
        _ = repo.WriteFile("tests/Second/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/First/Sample.Tests.csproj", "tests/Second/Sample.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            PriorityTestProjects: ["Sample.Tests.csproj"]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("matched more than one selected project", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
    }

    [Fact]
    public async Task RunAsync_DryRun_LongestFirst_ShouldPrintPlannedSchedule()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/First.Tests/First.Tests.csproj", "<Project />");
        repo.WriteFile("tests/Slow.Tests/Slow.Tests.csproj", "<Project />");
        var priorTimings = repo.WriteFile("prior-timings.json", """
            {
              "projects": [
                { "project": "tests/First.Tests/First.Tests.csproj", "seconds": 5 },
                { "project": "tests/Slow.Tests/Slow.Tests.csproj", "seconds": 50 }
              ]
            }
            """);
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var workflow = CreateWorkflow(runner, new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(
            TestProjects: ["tests/First.Tests/First.Tests.csproj", "tests/Slow.Tests/Slow.Tests.csproj"],
            ScheduleMode: CoverageRunScheduleMode.LongestFirst,
            ScheduleTimingsPath: priorTimings,
            DryRun: true);

        var result = await workflow.RunAsync(request, console, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(runner.Commands);
        var output = console.ReadOutputString();
        Assert.Contains("Schedule: longest-first", output, StringComparison.Ordinal);
        Assert.Contains("Planned execution order", output, StringComparison.Ordinal);
        Assert.Contains("execution 1: original 2", output, StringComparison.Ordinal);
        Assert.Contains("prior-timing scheduled 50s", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectPopulatedOutputWithoutOwnershipMarker()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        repo.WriteFile("TestResults/coverage-merged/user-file.txt", "mine");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();
        var request = CreateRequest(TestProjects: [project]);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not marked as AppSurface-owned", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldForwardPublicOptionsAndThrowWhenWorkflowFails()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var project = repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner { TestExitCode = 1 };
        var command = new CoverageRunCommand(CreateWorkflow(runner, new RecordingReportGenerator()))
        {
            TestProjects = [project],
            OutputDirectory = "custom-output",
            Configuration = "Release",
            Parallelism = 2,
            NoRestore = true,
            IncludeFilter = "[Sample]*",
            ExcludeFilter = "[Generated]*",
            NoDiscoverExclusive = true,
            ExclusiveTestProjects = ["Sample.Tests.csproj"],
            Loggers = ["trx"],
            TestArguments = ["--filter", "Category=Fast"],
            NoClean = true,
            Verbosity = "normal",
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV120", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Log:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet-test.log", exception.Message, StringComparison.Ordinal);
        var test = Assert.Single(runner.Commands, recorded => recorded.Arguments.FirstOrDefault() == "test");
        Assert.Contains("--configuration", test.Arguments);
        Assert.Contains("Release", test.Arguments);
        Assert.Contains("--logger:trx", test.Arguments);
        Assert.Contains("/p:Include=[Sample]*", test.Arguments);
        Assert.Contains("/p:Exclude=[Generated]*", test.Arguments);
        Assert.Contains("--filter", test.Arguments);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNormalizeAndForwardRepeatedProjectExclusions()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var solution = repo.WriteFile("Sample.slnx", "<Solution />");
        repo.WriteFile("tests/Unit.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            SlnListOutput = """
                tests/Unit.Tests.csproj
                tests/Browser.Tests.csproj
                """,
        };
        var command = new CoverageRunCommand(CreateWorkflow(runner, new RecordingReportGenerator()))
        {
            SolutionPath = solution,
            ExcludeTestProjects = ["  tests\\Browser.Tests.csproj  ", "Browser*.Tests.csproj"],
            DryRun = true,
        };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console, CancellationToken.None);

        var output = console.ReadOutputString();
        Assert.Contains(
            "matched --exclude-test-project pattern(s): 'tests/Browser.Tests.csproj', 'Browser*.Tests.csproj'",
            output,
            StringComparison.Ordinal);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectExplicitProjectsCombinedWithExclusionsBeforeRunningCommands()
    {
        var runner = new RecordingCoverageRunProcessRunner();
        var command = new CoverageRunCommand(CreateWorkflow(runner, new RecordingReportGenerator()))
        {
            TestProjects = ["tests/Unit.Tests.csproj"],
            ExcludeTestProjects = ["Browser.Tests.csproj"],
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be combined", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectInvalidParallelismWithDiagnosticTemplate()
    {
        var command = new CoverageRunCommand(CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator()))
        {
            Parallelism = 0
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Cause:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Fix:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Docs:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectUnsupportedTestResultsBeforeRunningTests()
    {
        var runner = new RecordingCoverageRunProcessRunner();
        var command = new CoverageRunCommand(CreateWorkflow(runner, new RecordingReportGenerator()))
        {
            TestResults = "trx"
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV111", exception.Message, StringComparison.Ordinal);
        Assert.Contains("#491", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectUnsupportedScheduleModeBeforeRunningTests()
    {
        var runner = new RecordingCoverageRunProcessRunner();
        var command = new CoverageRunCommand(CreateWorkflow(runner, new RecordingReportGenerator()))
        {
            Schedule = "fastest",
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--schedule must be input-order or longest-first", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ExecuteAsync_LongestFirst_ShouldCreateAndRunSchedule()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var command = new CoverageRunCommand(CreateWorkflow(runner, new RecordingReportGenerator()))
        {
            TestProjects = ["tests/Sample.Tests/Sample.Tests.csproj"],
            Schedule = "longest-first",
            DryRun = true,
        };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console, CancellationToken.None);

        Assert.Contains("Schedule: longest-first", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectScheduleTimingsWithoutLongestFirst()
    {
        var runner = new RecordingCoverageRunProcessRunner();
        var command = new CoverageRunCommand(CreateWorkflow(runner, new RecordingReportGenerator()))
        {
            ScheduleTimings = "prior-timings.json",
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--schedule-timings requires --schedule longest-first", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectPriorityProjectWithoutLongestFirst()
    {
        var runner = new RecordingCoverageRunProcessRunner();
        var command = new CoverageRunCommand(CreateWorkflow(runner, new RecordingReportGenerator()))
        {
            PriorityTestProjects = ["Sample.Tests.csproj"],
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--priority-test-project requires --schedule longest-first", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public void CoverageRunDiagnostics_ShouldLeaveDocsAndLogPathsCopyable()
    {
        var exception = CoverageRunDiagnostics.Create(
            "ASCOV999",
            "Problem.",
            "Cause.",
            "Fix.",
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics",
            "/tmp/appsurface/dotnet-test.log");

        Assert.Contains("Docs: Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics Log:", exception.Message, StringComparison.Ordinal);
        Assert.EndsWith("Log: /tmp/appsurface/dotnet-test.log", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("coverage-run-diagnostics.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-test.log.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectConflictingBuildOptions()
    {
        var command = new CoverageRunCommand(CreateWorkflow(new RecordingCoverageRunProcessRunner(), new RecordingReportGenerator()))
        {
            Build = true,
            NoBuild = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--build and --no-build cannot be used together", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructors_ShouldRejectMissingDependencies()
    {
        var runner = new RecordingCoverageRunProcessRunner();
        var reportGenerator = new RecordingReportGenerator();
        var locator = new RecordingReportGeneratorPackageLocator("/packages/reportgenerator/ReportGenerator.dll");

        Assert.Throws<ArgumentNullException>(() => new CoverageRunCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunWorkflow(null!, reportGenerator, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunWorkflow(runner, null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunWorkflow(runner, reportGenerator, null!));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunReportGenerator(null!, locator));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunReportGenerator(runner, null!));
    }

    [Fact]
    public async Task CoverageRunReportGenerator_ShouldInvokePackageOwnedReportGenerator()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner();
        var locator = new RecordingReportGeneratorPackageLocator("/packages/reportgenerator/ReportGenerator.dll");
        var reportGenerator = new CoverageRunReportGenerator(runner, locator);
        var output = Path.Join(repo.Path, "reportgenerator");

        var result = await reportGenerator.MergeAsync(["a.xml", "b.xml"], output, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Path.Join(output, "Cobertura.xml"), result.CoberturaPath);
        Assert.Equal(Path.Join(output, "Summary.txt"), result.SummaryPath);
        var command = Assert.Single(runner.Commands);
        Assert.Equal("dotnet", command.FileName);
        Assert.Contains("/packages/reportgenerator/ReportGenerator.dll", command.Arguments);
        Assert.Contains("-reports:a.xml;b.xml", command.Arguments);
        Assert.Contains($"-targetdir:{output}", command.Arguments);
    }

    [Fact]
    public void ReportGeneratorPackageLocator_ShouldResolvePackagedDependency()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var dll = repo.WriteFile(Path.Join("reportgenerator", "net10.0", "ReportGenerator.dll"), "fake");
        var locator = new ReportGeneratorPackageLocator(repo.Path);

        var resolved = locator.ResolveReportGeneratorDll();

        Assert.Equal(dll, resolved);
    }

    [Fact]
    public void ReportGeneratorPackageLocator_ShouldResolvePackagedFallbackTarget()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var dll = repo.WriteFile(Path.Join("reportgenerator", "net9.0", "ReportGenerator.dll"), "fake");
        var locator = new ReportGeneratorPackageLocator(repo.Path, []);

        var resolved = locator.ResolveReportGeneratorDll();

        Assert.Equal(dll, resolved);
    }

    [Fact]
    public void ReportGeneratorPackageLocator_ShouldResolveNuGetPackageCacheDependency()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var dll = repo.WriteFile(
            Path.Join("reportgenerator", ReportGeneratorPackageLocator.Version, "tools", "net8.0", "ReportGenerator.dll"),
            "fake");
        var locator = new ReportGeneratorPackageLocator("/missing-package-base", [repo.Path]);

        var resolved = locator.ResolveReportGeneratorDll();

        Assert.Equal(dll, resolved);
    }

    [Fact]
    public void ReportGeneratorPackageLocator_ShouldPreferPinnedNuGetCacheDependency()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var pinned = repo.WriteFile(
            Path.Join("reportgenerator", ReportGeneratorPackageLocator.Version, "tools", "net8.0", "ReportGenerator.dll"),
            "fake");
        repo.WriteFile(Path.Join("reportgenerator", "9.9.9", "tools", "net10.0", "ReportGenerator.dll"), "fake");
        var locator = new ReportGeneratorPackageLocator("/missing-package-base", [repo.Path]);

        var resolved = locator.ResolveReportGeneratorDll();

        Assert.Equal(pinned, resolved);
    }

    [Fact]
    public void ReportGeneratorPackageLocator_ShouldResolveUnpinnedNuGetCacheDependency()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var dll = repo.WriteFile(Path.Join("reportgenerator", "5.5.11", "tools", "net9.0", "ReportGenerator.dll"), "fake");
        var locator = new ReportGeneratorPackageLocator("/missing-package-base", [repo.Path]);

        var resolved = locator.ResolveReportGeneratorDll();

        Assert.Equal(dll, resolved);
    }

    [Fact]
    public void ReportGeneratorPackageLocator_ShouldOrderUnpinnedNuGetVersionsSemantically()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        repo.WriteFile(Path.Join("reportgenerator", "5.5.9", "tools", "net9.0", "ReportGenerator.dll"), "fake");
        var newer = repo.WriteFile(Path.Join("reportgenerator", "5.5.11", "tools", "net9.0", "ReportGenerator.dll"), "fake");
        var locator = new ReportGeneratorPackageLocator("/missing-package-base", [repo.Path]);

        var resolved = locator.ResolveReportGeneratorDll();

        Assert.Equal(newer, resolved);
    }

    [Fact]
    public void ReportGeneratorPackageLocator_ShouldThrowDiagnosticWhenDependencyIsMissing()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        using var cache = TempDirectory.Create("appsurface-coverage-run-cache-");
        var locator = new ReportGeneratorPackageLocator(repo.Path, [cache.Path]);

        var exception = Assert.Throws<CommandException>(locator.ResolveReportGeneratorDll);

        Assert.Contains("ASCOV114", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ReportGenerator package dependency was not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliWrapCoverageRunProcessRunner_ShouldStreamProcessOutputToLog()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new CliWrapCoverageRunProcessRunner();
        var outputFile = Path.Join(repo.Path, "logs", "dotnet.log");

        var result = await runner.RunAsync(
            "dotnet",
            ["--version"],
            repo.Path,
            CancellationToken.None,
            outputFile);

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Output));
        Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(outputFile)));
    }

    [Fact]
    public async Task CliWrapCoverageRunProcessRunner_ShouldRunBufferedWithoutOutputFile()
    {
        var runner = new CliWrapCoverageRunProcessRunner();

        var result = await runner.RunAsync("dotnet", ["--version"], Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.Output);
    }

    [Fact]
    public async Task CliWrapCoverageRunProcessRunner_ShouldWrapStartFailureInDiagnostic()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var runner = new CliWrapCoverageRunProcessRunner();
        var outputFile = Path.Join(repo.Path, "logs", "dotnet-test.log");

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => runner.RunAsync(
                "definitely-not-a-real-dotnet-command",
                [],
                repo.Path,
                CancellationToken.None,
                outputFile));

        Assert.Contains("ASCOV110", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Failed to start dotnet", exception.Message, StringComparison.Ordinal);
        Assert.Contains("definitely-not-a-real-dotnet-command", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Failed to start command 'definitely-not-a-real-dotnet-command'", File.ReadAllText(outputFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliWrapCoverageRunProcessRunner_ShouldWrapStartFailureWhenFailureLogCannotBeWritten()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var outputFile = Path.Join(repo.Path, "log-as-directory");
        Directory.CreateDirectory(outputFile);
        var runner = new CliWrapCoverageRunProcessRunner();

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => runner.RunAsync(
                "definitely-not-a-real-dotnet-command",
                ["--version"],
                repo.Path,
                CancellationToken.None,
                outputFile));

        Assert.Contains("ASCOV110", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliWrapCoverageRunProcessRunner_ShouldCancelAndKillProcessTree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repo = TempDirectory.Create("appsurface-coverage-run-");
        var runner = new CliWrapCoverageRunProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync("/bin/sh", ["-c", "sleep 30"], repo.Path, cancellation.Token));
    }

    private static async Task AssertUnsafeOutputAsync(
        CoverageRunWorkflow workflow,
        IConsole console,
        string project,
        string outputDirectory,
        string expectedCause)
    {
        var request = CreateRequest(TestProjects: [project], OutputDirectory: outputDirectory);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.RunAsync(request, console, CancellationToken.None));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedCause, exception.Message, StringComparison.Ordinal);
    }

    private static CoverageRunWorkflow CreateWorkflow(
        ICoverageRunProcessRunner runner,
        ICoverageRunReportGenerator reportGenerator)
        => new(runner, reportGenerator, TimeProvider.System);

    private static CoverageProjectRunResult CreateProjectRunResult(string repoPath, string? junitPath)
        => CreateProjectRunResult(repoPath, junitPath is null ? [] : [junitPath]);

    private static CoverageProjectRunResult CreateProjectRunResult(string repoPath, IReadOnlyList<string> junitPaths)
        => new(
            0,
            0,
            new CoverageRunProject(
                "tests/Sample.Tests/Sample.Tests.csproj",
                Path.Join(repoPath, "tests", "Sample.Tests", "Sample.Tests.csproj"),
                "Sample.Tests",
                IsExclusive: false),
            ScheduledSeconds: null,
            DurationSource: "none",
            ScheduleReason: "input-order",
            Seconds: 7,
            ExitCode: 0,
            LogFile: Path.Join(repoPath, "dotnet-test.log"),
            TestResults: junitPaths
                .Select(path => new CoverageRunTestResultArtifact(
                        CoverageRunTestResultFormat.Junit,
                        "tests/Sample.Tests/Sample.Tests.csproj",
                        path,
                        "pending"))
                .ToArray());

    private static CoverageRunRequest CreateRequest(
        string? SolutionPath = null,
        IReadOnlyList<string>? TestProjects = null,
        IReadOnlyList<string>? ExcludeTestProjects = null,
        string OutputDirectory = "TestResults/coverage-merged",
        string Configuration = "Debug",
        int Parallelism = 1,
        CoverageRunScheduleMode ScheduleMode = CoverageRunScheduleMode.InputOrder,
        string? ScheduleTimingsPath = null,
        IReadOnlyList<string>? PriorityTestProjects = null,
        bool NoRestore = false,
        bool Build = false,
        bool NoBuild = false,
        string? IncludeFilter = null,
        string ExcludeFilter = "[*.Tests]*,[*.IntegrationTests]*",
        bool DryRun = false,
        bool NoDiscoverExclusive = false,
        IReadOnlyList<string>? ExclusiveTestProjects = null,
        IReadOnlyList<string>? Loggers = null,
        IReadOnlyList<string>? TestArguments = null,
        CoverageRunTestResultFormat TestResults = CoverageRunTestResultFormat.None,
        bool SlowTestDiagnostics = false,
        bool Clean = true,
        string Verbosity = "minimal")
        => new(
            SolutionPath,
            TestProjects ?? [],
            ExcludeTestProjects ?? [],
            OutputDirectory,
            Configuration,
            Parallelism,
            ScheduleMode,
            ScheduleTimingsPath,
            PriorityTestProjects ?? [],
            NoRestore,
            Build,
            NoBuild,
            IncludeFilter,
            ExcludeFilter,
            DryRun,
            NoDiscoverExclusive,
            ExclusiveTestProjects ?? [],
            Loggers ?? [],
            TestArguments ?? [],
            TestResults,
            SlowTestDiagnostics,
            Clean,
            Verbosity);

    private static IDisposable PushCurrentDirectory(string path)
    {
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(path);
        return new DelegateDisposable(() => Directory.SetCurrentDirectory(previous));
    }

    private static string NormalizeTestPath(string path) => path.Replace('\\', '/');

    private sealed class RecordingCoverageRunProcessRunner : ICoverageRunProcessRunner
    {
        public string SlnListOutput { get; init; } = string.Empty;
        public bool WriteCoverageFiles { get; init; } = true;
        public bool WriteJunitFiles { get; init; } = true;
        public int SlnExitCode { get; init; }
        public int BuildExitCode { get; init; }
        public int TestExitCode { get; init; }
        public string TestOutput { get; init; } = "test output";
        public string JunitContent { get; init; } = """
            <testsuite tests="2" failures="0" skipped="0">
              <testcase classname="SampleTests" name="Fast" time="0.1" />
              <testcase classname="SampleTests" name="Slow" time="1.25" />
            </testsuite>
            """;
        public Dictionary<string, TimeSpan> TestDelays { get; } = [];
        public HashSet<string> ProjectsWithoutCoverage { get; } = [];
        public List<RecordedCommand> Commands { get; } = [];

        public async Task<CoverageRunProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            string? outputFile = null)
        {
            var command = new RecordedCommand(fileName, arguments.ToArray(), workingDirectory, outputFile, DateTimeOffset.UtcNow);
            Commands.Add(command);
            if (arguments is ["sln", ..])
            {
                command.Finish();
                return new CoverageRunProcessResult(SlnExitCode, SlnListOutput);
            }

            if (arguments.FirstOrDefault() == "build")
            {
                command.Finish();
                return new CoverageRunProcessResult(BuildExitCode, "build output");
            }

            if (arguments.FirstOrDefault() == "test")
            {
                if (TestDelays.TryGetValue(arguments[1], out var delay))
                {
                    await Task.Delay(delay, cancellationToken);
                }

                if (outputFile is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
                    await File.WriteAllTextAsync(outputFile, TestOutput, cancellationToken);
                }

                if (WriteCoverageFiles && !ProjectsWithoutCoverage.Contains(arguments[1]))
                {
                    var coverletOutput = arguments.Single(argument => argument.StartsWith("/p:CoverletOutput=", StringComparison.Ordinal))["/p:CoverletOutput=".Length..];
                    var projectDirectory = Path.GetDirectoryName(coverletOutput)!;
                    Directory.CreateDirectory(projectDirectory);
                    await File.WriteAllTextAsync(
                        Path.Join(projectDirectory, "coverage.cobertura.xml"),
                        "<coverage lines-covered=\"8\" lines-valid=\"10\" branches-covered=\"2\" branches-valid=\"4\" />",
                        cancellationToken);
                }

                foreach (var junitLogger in arguments.Where(argument => WriteJunitFiles && argument.StartsWith("--logger:junit;LogFilePath=", StringComparison.Ordinal)))
                {
                    var junitFile = junitLogger["--logger:junit;LogFilePath=".Length..];
                    Directory.CreateDirectory(Path.GetDirectoryName(junitFile)!);
                    await File.WriteAllTextAsync(
                        junitFile,
                        JunitContent,
                        cancellationToken);
                }

                command.Finish();
                return new CoverageRunProcessResult(TestExitCode, TestOutput);
            }

            command.Finish();
            return new CoverageRunProcessResult(0, string.Empty);
        }
    }

    private sealed class RecordingReportGenerator : ICoverageRunReportGenerator
    {
        public List<string> CoverageFiles { get; } = [];
        public int ExitCode { get; init; }
        public bool WriteMergedCoverage { get; init; } = true;
        public string MergedCoverage { get; init; } = "<coverage lines-covered=\"8\" lines-valid=\"10\" branches-covered=\"2\" branches-valid=\"4\" />";

        public async Task<CoverageRunMergeResult> MergeAsync(
            IReadOnlyList<string> coverageFiles,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            CoverageFiles.AddRange(coverageFiles);
            Directory.CreateDirectory(outputDirectory);
            var cobertura = Path.Join(outputDirectory, "Cobertura.xml");
            var summary = Path.Join(outputDirectory, "Summary.txt");
            if (WriteMergedCoverage)
            {
                await File.WriteAllTextAsync(
                    cobertura,
                    MergedCoverage,
                    cancellationToken);
            }

            await File.WriteAllTextAsync(summary, "reportgenerator summary", cancellationToken);
            return new CoverageRunMergeResult(ExitCode, cobertura, summary);
        }
    }

    private sealed class RecordingReportGeneratorPackageLocator(string path) : IReportGeneratorPackageLocator
    {
        public string ResolveReportGeneratorDll() => path;
    }

    private sealed class RecordedCommand(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        string? OutputFile,
        DateTimeOffset StartedAt)
    {
        public string FileName { get; } = FileName;
        public IReadOnlyList<string> Arguments { get; } = Arguments;
        public string WorkingDirectory { get; } = WorkingDirectory;
        public string? OutputFile { get; } = OutputFile;
        public DateTimeOffset StartedAt { get; } = StartedAt;
        public DateTimeOffset FinishedAt { get; private set; }

        public void Finish() => FinishedAt = DateTimeOffset.UtcNow;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create(string prefix)
        {
            var path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                System.IO.Path.GetFileName(prefix) + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public string WriteFile(string relativePath, string contents)
        {
            var path = TestPathUtils.PathUnder(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
