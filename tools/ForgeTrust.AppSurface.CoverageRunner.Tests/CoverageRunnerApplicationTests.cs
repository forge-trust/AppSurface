using System.Collections.Concurrent;
using System.Text.Json;

namespace ForgeTrust.AppSurface.CoverageRunner.Tests;

public sealed class CoverageRunnerApplicationTests
{
    [Fact]
    public async Task RunAsync_ShouldWriteUsageToStandardOutForHelp()
    {
        using var workspace = TestRepo.Create();
        var output = new StringWriter();
        var error = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), output, error);

        var exitCode = await app.RunAsync(["--help"], workspace.Root, new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        Assert.Contains("ForgeTrust.AppSurface.CoverageRunner", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RunAsync_ShouldWriteParseErrorsToStandardError()
    {
        using var workspace = TestRepo.Create();
        var output = new StringWriter();
        var error = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), output, error);

        var exitCode = await app.RunAsync(["--group"], workspace.Root, new Dictionary<string, string?>());

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("--group requires a value", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldListGroups()
    {
        using var workspace = TestRepo.Create();
        var output = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), output);

        var exitCode = await app.RunAsync(["--list-groups"], workspace.Root, new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        Assert.Contains("all", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("integration", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnOneWhenSolutionIsMissing()
    {
        using var workspace = TestRepo.Create();
        var error = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), standardError: error);

        var exitCode = await app.RunAsync(
            ["--solution", "missing.slnx"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
        Assert.Contains("Solution not found:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnOneWhenSolutionListFails()
    {
        using var workspace = TestRepo.Create();
        var runner = new RecordingCommandRunner(workspace)
        {
            SlnListExitCode = 4,
            SlnListOutput = "sln list failed",
        };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
        Assert.Contains("sln list failed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnOneWhenNoProjectsMatchGroup()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var error = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "docs"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
        Assert.Contains("No test projects found for group 'docs'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldIgnoreNonTestProjectsDuringDiscovery()
    {
        using var workspace = TestRepo.Create();
        workspace.AddSolutionProject("src/Sample/Sample.csproj");
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace);
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        Assert.Single(runner.TestCommands);
    }

    [Fact]
    public async Task RunAsync_ShouldTreatMissingProjectFilesAsShareable()
    {
        using var workspace = TestRepo.Create();
        workspace.AddSolutionProject("tools/Missing.Tests/Missing.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace);
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        Assert.Single(runner.TestCommands);
    }

    [Fact]
    public async Task RunAsync_ShouldCleanExistingOutputDirectoryBeforeRunning()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Join(outputDirectory, "coverage.json"), "{}");
        File.WriteAllText(Path.Join(outputDirectory, "coverage.cobertura.xml"), "<old />");
        File.WriteAllText(Path.Join(outputDirectory, "summary.txt"), "old");
        File.WriteAllText(Path.Join(outputDirectory, "timings.json"), "{}");
        File.WriteAllText(Path.Join(outputDirectory, "junit-old.xml"), "<old />");
        Directory.CreateDirectory(Path.Join(outputDirectory, "projects", "Old.Tests"));
        File.WriteAllText(Path.Join(outputDirectory, "projects", "Old.Tests", "old.txt"), "old");
        Directory.CreateDirectory(Path.Join(outputDirectory, "reportgenerator"));
        File.WriteAllText(Path.Join(outputDirectory, "reportgenerator", "old.txt"), "old");
        var app = CreateApp(new RecordingCommandRunner(workspace));

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Path.Join(outputDirectory, "coverage.json")));
        Assert.False(File.Exists(Path.Join(outputDirectory, "junit-old.xml")));
        Assert.False(File.Exists(Path.Join(outputDirectory, "projects", "Old.Tests", "old.txt")));
        Assert.False(File.Exists(Path.Join(outputDirectory, "reportgenerator", "old.txt")));
        Assert.True(File.Exists(Path.Join(outputDirectory, "summary.txt")));
        Assert.True(File.Exists(Path.Join(outputDirectory, "timings.json")));
    }

    [Fact]
    public async Task RunAsync_ShouldBuildSolutionAndPassNoBuildToProjectTests()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace);
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--build-solution"],
            workspace.Root,
            new Dictionary<string, string?> { ["BUILD_CONFIGURATION"] = "Release", ["BUILD_NO_RESTORE"] = "true" });

        Assert.Equal(0, exitCode);
        var buildArguments = Assert.Single(runner.BuildCommands);
        Assert.Contains("build", buildArguments);
        Assert.Contains("Release", buildArguments);
        Assert.Contains("--no-restore", buildArguments);
        var testArguments = Assert.Single(runner.TestArguments);
        Assert.Contains("--no-build", testArguments);
        Assert.Contains("--no-restore", testArguments);
    }

    [Fact]
    public async Task RunAsync_ShouldStopBeforeTestsWhenSolutionBuildFails()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace) { BuildExitCode = 5 };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--build-solution"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
        Assert.Empty(runner.TestCommands);
        Assert.Contains("Build failed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRunMergeOnlyAndCopyJunitFiles()
    {
        using var workspace = TestRepo.Create();
        var sourceDirectory = Path.Join(workspace.Root, "coverage-shards");
        var outputDirectory = Path.Join(workspace.Root, "merged");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Join(sourceDirectory, "coverage.cobertura.xml"),
            "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />");
        File.WriteAllText(Path.Join(sourceDirectory, "junit-tools-1-Sample.Tests.xml"), "<testsuite />");
        var app = CreateApp(new RecordingCommandRunner(workspace));

        var exitCode = await app.RunAsync(
            ["--merge-only", sourceDirectory, "--output", outputDirectory],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Join(outputDirectory, "coverage.cobertura.xml")));
        Assert.True(File.Exists(Path.Join(outputDirectory, "summary.txt")));
        Assert.True(File.Exists(Path.Join(outputDirectory, "timings.json")));
        Assert.True(File.Exists(Path.Join(outputDirectory, "junit-tools-1-Sample.Tests.xml")));
    }

    [Fact]
    public async Task RunAsync_ShouldReturnMergeExitCodeWhenMergeOnlyReportGenerationFails()
    {
        using var workspace = TestRepo.Create();
        var sourceDirectory = Path.Join(workspace.Root, "coverage-shards");
        var outputDirectory = Path.Join(workspace.Root, "merged");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Join(sourceDirectory, "coverage.cobertura.xml"),
            "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />");
        var runner = new RecordingCommandRunner(workspace) { ReportGeneratorExitCode = 9 };
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--merge-only", sourceDirectory, "--output", outputDirectory],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(9, exitCode);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnOneWhenMergeOnlySummaryCannotBeParsed()
    {
        using var workspace = TestRepo.Create();
        var sourceDirectory = Path.Join(workspace.Root, "coverage-shards");
        var outputDirectory = Path.Join(workspace.Root, "merged");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Join(sourceDirectory, "coverage.cobertura.xml"),
            "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />");
        var runner = new RecordingCommandRunner(workspace) { ReportGeneratorCoverageText = "<coverage />" };
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--merge-only", sourceDirectory, "--output", outputDirectory],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnOneWhenMergeOnlySourceIsMissing()
    {
        using var workspace = TestRepo.Create();
        var error = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), standardError: error);

        var exitCode = await app.RunAsync(
            ["--merge-only", "missing-shards"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
        Assert.Contains("Merge source directory not found:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnToolRestoreExitCodeWhenMergeRestoreFails()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace) { ToolRestoreExitCode = 6 };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(6, exitCode);
        Assert.Contains("Failed to restore local .NET tools", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnOneWhenReportGeneratorDoesNotCreateMergedCoverage()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace) { ReportGeneratorShouldOmitCoverage = true };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
        Assert.Contains("ReportGenerator did not create", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnOneWhenMergedCoverageSummaryCannotBeParsed()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace) { ReportGeneratorCoverageText = "<coverage />" };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
        Assert.Contains("Failed to parse numeric coverage attributes", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnOneWhenMergedCoverageAttributesAreNotNumeric()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace)
        {
            ReportGeneratorCoverageText =
                "<coverage lines-covered=\"abc\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />",
        };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
        Assert.Contains("Failed to parse numeric coverage attributes", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldWriteZeroRatesWhenCoverageDenominatorsAreZero()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace)
        {
            ReportGeneratorCoverageText =
                "<coverage lines-covered=\"0\" lines-valid=\"0\" branches-covered=\"0\" branches-valid=\"0\" />",
        };
        var output = new StringWriter();
        var app = CreateApp(runner, output);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        Assert.Contains("Line coverage: 0.00% (0/0)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Branch coverage: 0.00% (0/0)", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("nested/path")]
    public void RequirePathSegment_ShouldRejectUnsafeSegments(string segment)
    {
        Assert.Throws<InvalidOperationException>(() => CoverageRunnerApplication.RequirePathSegment(segment, "segment"));
    }

    [Fact]
    public void RequirePathSegment_ShouldReturnSafeSegments()
    {
        Assert.Equal("Sample.Tests", CoverageRunnerApplication.RequirePathSegment("Sample.Tests", "segment"));
    }

    [Fact]
    public async Task RunAsync_ShouldLimitNonExclusiveProjectConcurrency()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Fast.One.Tests/Fast.One.Tests.csproj");
        workspace.AddProject("tools/Fast.Two.Tests/Fast.Two.Tests.csproj");
        workspace.AddProject("tools/Fast.Three.Tests/Fast.Three.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace) { TestDelay = TimeSpan.FromMilliseconds(50) };
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = "2" });

        Assert.Equal(0, exitCode);
        Assert.Equal(2, runner.MaxConcurrentTests);
    }

    [Fact]
    public async Task RunAsync_ShouldRunExclusiveProjectAlone()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Fast.One.Tests/Fast.One.Tests.csproj");
        workspace.AddProject("Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj");
        workspace.AddProject("tools/Fast.Two.Tests/Fast.Two.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace) { TestDelay = TimeSpan.FromMilliseconds(50) };
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = "2" });

        Assert.Equal(0, exitCode);
        var exclusive = Assert.Single(
            runner.TestSnapshots,
            snapshot => snapshot.Project.Contains("IntegrationTests", StringComparison.Ordinal));
        Assert.Equal(1, exclusive.ActiveAtStart);
    }

    [Fact]
    public async Task RunAsync_ShouldAggregateFailuresAndContinueRemainingProjects()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Pass.One.Tests/Pass.One.Tests.csproj");
        workspace.AddProject("tools/Fail.Tests/Fail.Tests.csproj");
        workspace.AddProject("tools/Pass.Two.Tests/Pass.Two.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace) { FailingProject = "Fail.Tests.csproj" };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = "2" });

        Assert.Equal(7, exitCode);
        Assert.Equal(3, runner.TestCommands.Count);
        Assert.Contains("One or more test projects failed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldWriteTimingsWhenFailedProjectProducesNoCoverage()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Fail.Tests/Fail.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace)
        {
            FailingProject = "Fail.Tests.csproj",
            MissingCoverageProject = "Fail.Tests.csproj",
        };
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(7, exitCode);
        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Join(workspace.Root, "TestResults", "coverage-merged", "timings.json")));
        var project = Assert.Single(timings.RootElement.GetProperty("projects").EnumerateArray());
        Assert.Equal(7, project.GetProperty("exitCode").GetInt32());
        Assert.Equal(1, timings.RootElement.GetProperty("merge").GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task RunAsync_ShouldWriteTimingsWhenReportGeneratorFails()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Pass.Tests/Pass.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace) { ReportGeneratorExitCode = 9 };
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(9, exitCode);
        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Join(workspace.Root, "TestResults", "coverage-merged", "timings.json")));
        Assert.Equal(9, timings.RootElement.GetProperty("merge").GetProperty("exitCode").GetInt32());
        var project = Assert.Single(timings.RootElement.GetProperty("projects").EnumerateArray());
        Assert.Equal(0, project.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task RunAsync_ShouldRejectMergeOnlyWhenSourceMatchesOutput()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        var coverageFile = Path.Join(outputDirectory, "projects", "Sample.Tests", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(coverageFile)!);
        File.WriteAllText(coverageFile, "<coverage />");

        var runner = new RecordingCommandRunner(workspace);
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--merge-only", outputDirectory, "--output", outputDirectory],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(2, exitCode);
        Assert.True(File.Exists(coverageFile));
    }

    [Fact]
    public async Task RunAsync_ShouldRejectMergeOnlyWhenSourceIsUnderOutput()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        var sourceDirectory = Path.Join(outputDirectory, "projects");
        var coverageFile = Path.Join(sourceDirectory, "Sample.Tests", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(coverageFile)!);
        File.WriteAllText(coverageFile, "<coverage />");

        var runner = new RecordingCommandRunner(workspace);
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--merge-only", sourceDirectory, "--output", outputDirectory],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(2, exitCode);
        Assert.True(File.Exists(coverageFile));
    }

    [Fact]
    public async Task RunAsync_ShouldRejectMergeOnlyWhenOutputIsUnderSource()
    {
        using var workspace = TestRepo.Create();
        var sourceDirectory = Path.Join(workspace.Root, "coverage-shards");
        var outputDirectory = Path.Join(sourceDirectory, "merged");
        var coverageFile = Path.Join(sourceDirectory, "Sample.Tests", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(coverageFile)!);
        File.WriteAllText(coverageFile, "<coverage />");

        var runner = new RecordingCommandRunner(workspace);
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--merge-only", sourceDirectory, "--output", outputDirectory],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(2, exitCode);
        Assert.True(File.Exists(coverageFile));
    }

    [Fact]
    public void DirectoriesOverlap_ShouldRespectPathComparison()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        var caseVariantOutputDirectory = Path.Join(workspace.Root, "testresults", "coverage-merged");

        Assert.True(CoverageRunnerApplication.DirectoriesOverlap(
            caseVariantOutputDirectory,
            outputDirectory,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(CoverageRunnerApplication.DirectoriesOverlap(
            caseVariantOutputDirectory,
            outputDirectory,
            StringComparison.Ordinal));
    }

    [Fact]
    public void IsProjectCoveragePath_ShouldRespectPathComparison()
    {
        var coveragePath = "/tmp/coverage/PROJECTS/Sample.Tests/coverage.cobertura.xml";

        Assert.True(CoverageRunnerApplication.IsProjectCoveragePath(coveragePath, StringComparison.OrdinalIgnoreCase));
        Assert.False(CoverageRunnerApplication.IsProjectCoveragePath(coveragePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ShouldReplayLogsInSolutionOrder()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/First.Tests/First.Tests.csproj");
        workspace.AddProject("tools/Second.Tests/Second.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace) { TestDelay = TimeSpan.FromMilliseconds(25) };
        var output = new StringWriter();
        var app = CreateApp(runner, output);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = "2" });

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.True(
            text.IndexOf("--- BEGIN tools/First.Tests/First.Tests.csproj ---", StringComparison.Ordinal)
            < text.IndexOf("--- BEGIN tools/Second.Tests/Second.Tests.csproj ---", StringComparison.Ordinal));
        Assert.Contains("passed ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRecordProjectCommandExceptionsAndContinue()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Throw.Tests/Throw.Tests.csproj");
        workspace.AddProject("tools/Pass.Tests/Pass.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace) { ThrowingProject = "Throw.Tests.csproj" };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = "2" });

        Assert.Equal(1, exitCode);
        Assert.Equal(2, runner.TestCommands.Count);
        Assert.Contains("Test run failed for tools/Throw.Tests/Throw.Tests.csproj (exit 1)", error.ToString(), StringComparison.Ordinal);

        var logFile = Path.Join(workspace.Root, "TestResults", "coverage-merged", "projects", "Throw.Tests", "dotnet-test.log");
        Assert.Contains("Failed to run dotnet test", File.ReadAllText(logFile), StringComparison.Ordinal);

        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Join(workspace.Root, "TestResults", "coverage-merged", "timings.json")));
        var projects = timings.RootElement.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(2, projects.Length);
        Assert.Equal(1, projects[0].GetProperty("exitCode").GetInt32());
        Assert.Equal(0, projects[1].GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task RunAsync_ShouldPreserveProjectCommandCancellation()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Cancel.Tests/Cancel.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace) { CancelingProject = "Cancel.Tests.csproj" };
        var app = CreateApp(runner);

        await Assert.ThrowsAsync<OperationCanceledException>(() => app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>()));
    }

    private static CoverageRunnerApplication CreateApp(
        ICommandRunner commandRunner,
        TextWriter? standardOut = null,
        TextWriter? standardError = null)
    {
        return new CoverageRunnerApplication(
            commandRunner,
            new FakeClock(),
            standardOut ?? new StringWriter(),
            standardError ?? new StringWriter());
    }

    private sealed class RecordingCommandRunner : ICommandRunner
    {
        private readonly TestRepo _workspace;
        private int _activeTests;

        public RecordingCommandRunner(TestRepo workspace)
        {
            _workspace = workspace;
        }

        public TimeSpan TestDelay { get; init; }

        public int SlnListExitCode { get; init; }

        public string? SlnListOutput { get; init; }

        public int BuildExitCode { get; init; }

        public int ToolRestoreExitCode { get; init; }

        public string? FailingProject { get; init; }

        public string? MissingCoverageProject { get; init; }

        public string? ThrowingProject { get; init; }

        public string? CancelingProject { get; init; }

        public int ReportGeneratorExitCode { get; init; }

        public bool ReportGeneratorShouldOmitCoverage { get; init; }

        public string ReportGeneratorCoverageText { get; init; } =
            "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />";

        private int _maxConcurrentTests;

        public int MaxConcurrentTests => Volatile.Read(ref _maxConcurrentTests);

        public ConcurrentBag<IReadOnlyList<string>> BuildCommands { get; } = [];

        public ConcurrentBag<IReadOnlyList<string>> TestArguments { get; } = [];

        public ConcurrentBag<string> TestCommands { get; } = [];

        public ConcurrentBag<TestSnapshot> TestSnapshots { get; } = [];

        public async Task<CommandResult> RunAsync(
            string command,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            string? outputFile = null)
        {
            if (arguments.Count == 3 && arguments[0] == "sln" && arguments[2] == "list")
            {
                if (SlnListExitCode != 0)
                {
                    return new CommandResult(SlnListExitCode, SlnListOutput ?? "");
                }

                return new CommandResult(0, string.Join(Environment.NewLine, _workspace.Projects));
            }

            if (arguments.Count > 0 && arguments[0] == "build")
            {
                BuildCommands.Add(arguments.ToArray());
                return new CommandResult(BuildExitCode, BuildExitCode == 0 ? "build ok" : "build failed");
            }

            if (arguments.Count == 2 && arguments[0] == "tool" && arguments[1] == "restore")
            {
                return new CommandResult(ToolRestoreExitCode, ToolRestoreExitCode == 0 ? "" : "tool restore failed");
            }

            if (arguments.Count >= 4 && arguments[0] == "tool" && arguments[2] == "reportgenerator")
            {
                if (ReportGeneratorExitCode != 0)
                {
                    return new CommandResult(ReportGeneratorExitCode, "reportgenerator failed");
                }

                var target = arguments.Single(argument => argument.StartsWith("-targetdir:", StringComparison.Ordinal))["-targetdir:".Length..];
                Directory.CreateDirectory(target);
                if (!ReportGeneratorShouldOmitCoverage)
                {
                    File.WriteAllText(Path.Join(target, "Cobertura.xml"), ReportGeneratorCoverageText);
                }

                File.WriteAllText(Path.Join(target, "Summary.txt"), "reportgenerator summary");
                return new CommandResult(0, "");
            }

            if (arguments.Count > 0 && arguments[0] == "test")
            {
                var project = arguments[1];
                TestArguments.Add(arguments.ToArray());
                var active = Interlocked.Increment(ref _activeTests);
                RecordMaxConcurrentTests(active);
                TestCommands.Add(project);
                TestSnapshots.Add(new TestSnapshot(project, active));
                try
                {
                    if (TestDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(TestDelay, cancellationToken);
                    }

                    if (ThrowingProject is not null && project.EndsWith(ThrowingProject, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"simulated command failure for {project}");
                    }

                    if (CancelingProject is not null && project.EndsWith(CancelingProject, StringComparison.Ordinal))
                    {
                        throw new OperationCanceledException();
                    }

                    var coveragePrefix = arguments.Single(argument => argument.StartsWith("/p:CoverletOutput=", StringComparison.Ordinal))["/p:CoverletOutput=".Length..];
                    if (MissingCoverageProject is null || !project.EndsWith(MissingCoverageProject, StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(coveragePrefix)!);
                        File.WriteAllText(
                            coveragePrefix + ".cobertura.xml",
                            "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />");
                    }

                    var junit = arguments.Single(argument => argument.StartsWith("--logger:junit;LogFilePath=", StringComparison.Ordinal))["--logger:junit;LogFilePath=".Length..];
                    File.WriteAllText(junit, "<testsuite />");

                    if (FailingProject is not null && project.EndsWith(FailingProject, StringComparison.Ordinal))
                    {
                        return Complete(7, $"failed {project}", outputFile);
                    }

                    return Complete(0, $"passed {project}", outputFile);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeTests);
                }
            }

            return new CommandResult(0, "");
        }

        private static CommandResult Complete(int exitCode, string output, string? outputFile)
        {
            if (outputFile is null)
            {
                return new CommandResult(exitCode, output);
            }

            var directory = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(outputFile, output);
            return new CommandResult(exitCode, string.Empty);
        }

        private void RecordMaxConcurrentTests(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxConcurrentTests);
                var next = Math.Max(current, active);
                if (next == current || Interlocked.CompareExchange(ref _maxConcurrentTests, next, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed record TestSnapshot(string Project, int ActiveAtStart);

    private sealed class FakeClock : IClock
    {
        public ITimer StartTimer() => new FakeTimer();

        private sealed class FakeTimer : ITimer
        {
            public long ElapsedSeconds => 1;
        }
    }
}
