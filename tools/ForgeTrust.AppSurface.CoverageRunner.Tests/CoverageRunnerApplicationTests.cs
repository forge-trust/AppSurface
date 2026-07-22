using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Testing;

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
        File.WriteAllText(Path.Join(outputDirectory, "slow-test-diagnostics.md"), "STALE-SLOW-DIAGNOSTICS");
        File.WriteAllText(Path.Join(outputDirectory, "slow-test-diagnostics.json"), "{\"stale\":true}");
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
        Assert.DoesNotContain("STALE-SLOW-DIAGNOSTICS", File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.md")), StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.json")), StringComparison.Ordinal);
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
        Assert.Contains(
            "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude=[*.Tests]*,[*.IntegrationTests]*",
            testArguments);
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
        Assert.True(File.Exists(Path.Join(outputDirectory, "slow-test-diagnostics.md")));
        using var diagnostics = JsonDocument.Parse(File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.json")));
        Assert.False(diagnostics.RootElement.GetProperty("metadataComplete").GetBoolean());
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
    public async Task WriteSummaryAsync_ShouldReturnFalseWhenMergedCoverageFileIsMissing()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        var error = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), standardError: error);

        var result = await app.WriteSummaryAsync(
            new CoverageRunnerOptions
            {
                RepositoryRoot = workspace.Root,
                SolutionPath = Path.Join(workspace.Root, "ForgeTrust.AppSurface.slnx"),
                OutputDirectory = outputDirectory,
                GroupName = "tools",
                BuildConfiguration = "Release",
                BuildSolution = false,
                BuildNoRestore = false,
                IncludeFilter = "[ForgeTrust.AppSurface.*]*",
                ExcludeFilter = "[*.Tests]*,[*.IntegrationTests]*",
                Parallelism = 1,
                MergeOnly = false,
                ListGroups = false,
            },
            CancellationToken.None);

        Assert.False(result);
        Assert.Contains("Merged Cobertura file was not created", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteSummaryAsync_ShouldPreserveCanonicalSummaryWhenCoverageValidationFails()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Join(outputDirectory, "coverage.cobertura.xml"), "<coverage />");
        var summaryPath = Path.Join(outputDirectory, "summary.txt");
        File.WriteAllText(summaryPath, "previous");
        var app = CreateApp(new RecordingCommandRunner(workspace));

        var result = await app.WriteSummaryAsync(
            CreateCoverageOptions(workspace.Root, outputDirectory),
            CancellationToken.None);

        Assert.False(result);
        Assert.Equal("previous", File.ReadAllText(summaryPath));
        Assert.Empty(Directory.EnumerateFiles(outputDirectory, ".summary.txt.*.tmp"));
    }

    [Fact]
    public async Task WriteSummaryAsync_ShouldIncludeProvidedDiagnosticsMetadata()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Join(outputDirectory, "coverage.cobertura.xml"),
            "<coverage lines-covered=\"9\" lines-valid=\"10\" branches-covered=\"7\" branches-valid=\"8\" />");
        var output = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), output);
        var options = CreateCoverageOptions(workspace.Root, outputDirectory);
        var diagnostics = new SlowTestDiagnosticsRun(
            Path.Join(outputDirectory, "custom-diagnostics.md"),
            Path.Join(outputDirectory, "custom-diagnostics.json"),
            AggregationSeconds: 3,
            AggregationPercent: 12.5m,
            WarningCount: 2,
            MetadataComplete: true);

        var result = await app.WriteSummaryAsync(options, diagnostics, CancellationToken.None);

        Assert.True(result);
        var summary = File.ReadAllText(Path.Join(outputDirectory, "summary.txt"));
        Assert.Contains("Slow test diagnostics: " + diagnostics.MarkdownPath, summary, StringComparison.Ordinal);
        Assert.Contains("Diagnostic aggregation overhead: 3s (12.50% of total runner time)", summary, StringComparison.Ordinal);
        Assert.Contains("Diagnostic warnings: 2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteSummaryAsync_ShouldReturnFalseWhenSummaryCannotReplaceDirectory()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Join(outputDirectory, "coverage.cobertura.xml"),
            RecordingCommandRunner.ValidCoverageText);
        Directory.CreateDirectory(Path.Join(outputDirectory, "summary.txt"));
        var error = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), standardError: error);

        var result = await app.WriteSummaryAsync(
            CreateCoverageOptions(workspace.Root, outputDirectory),
            CancellationToken.None);

        Assert.False(result);
        Assert.Contains("Failed to write coverage summary", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(outputDirectory, ".summary.txt.*.tmp"));
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

    [Fact]
    public async Task RunAsync_ShouldWriteSlowTestDiagnosticsAndOverhead()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace)
        {
            JunitText = """
                <testsuites>
                  <testsuite>
                    <testcase classname="Sample.CoverageTests" name="UsesReportGenerator" time="2.5">
                      <failure />
                    </testcase>
                    <testcase classname="Sample.ProcessTests" name="StartsDotnet" time="1.25" />
                  </testsuite>
                </testsuites>
                """,
        };
        var output = new StringWriter();
        var app = CreateApp(runner, output);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        var markdown = File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.md"));
        Assert.Contains("Diagnostic aggregation overhead: 1s (100.00% of total runner time)", markdown, StringComparison.Ordinal);
        Assert.Contains("coverage-tooling", markdown, StringComparison.Ordinal);
        Assert.Contains("process-startup", markdown, StringComparison.Ordinal);

        using var diagnostics = JsonDocument.Parse(File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.json")));
        Assert.True(diagnostics.RootElement.GetProperty("metadataComplete").GetBoolean());
        Assert.Equal(1, diagnostics.RootElement.GetProperty("overhead").GetProperty("aggregationSeconds").GetInt64());
        Assert.Equal(100, diagnostics.RootElement.GetProperty("overhead").GetProperty("aggregationPercent").GetDecimal());
        Assert.Equal(2, diagnostics.RootElement.GetProperty("totals").GetProperty("testCases").GetInt32());

        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Join(outputDirectory, "timings.json")));
        Assert.Equal(1, timings.RootElement.GetProperty("durations").GetProperty("diagnosticAggregationSeconds").GetInt64());
        Assert.Equal(100, timings.RootElement.GetProperty("durations").GetProperty("diagnosticAggregationPercent").GetDecimal());
        var project = Assert.Single(timings.RootElement.GetProperty("projects").EnumerateArray());
        Assert.Contains("junit-tools-1-Sample.Tests.xml", project.GetProperty("junit").GetString(), StringComparison.Ordinal);
        Assert.Contains("Diagnostic aggregation overhead: 1s (100.00% of total runner time)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldReportZeroDiagnosticsPercentWhenTotalTimerHasNotAdvanced()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var output = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), output, clock: new ZeroClock());

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        Assert.Contains("Diagnostic aggregation overhead: 0s (0.00% of total runner time)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldUsePostWriteOverheadInArtifacts()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        var options = CreateCoverageOptions(workspace.Root, outputDirectory);
        var report = new SlowTestDiagnosticsReport(
            SlowTestDiagnosticsWriter.SchemaVersion,
            DateTimeOffset.UnixEpoch,
            "tools",
            MetadataComplete: true,
            JunitFileCount: 0,
            Projects: [],
            TestCases: [],
            Categories: [],
            Warnings: []);
        var elapsedReads = 0;

        var diagnostics = await SlowTestDiagnosticsWriter.WriteAsync(
            options,
            report,
            getAggregationSeconds: () => ++elapsedReads,
            calculateAggregationPercent: seconds => seconds * 10m,
            CancellationToken.None);

        Assert.Equal(2, diagnostics.AggregationSeconds);
        Assert.Equal(20, diagnostics.AggregationPercent);
        Assert.Contains(
            "Diagnostic aggregation overhead: 2s (20.00% of total runner time)",
            File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.md")),
            StringComparison.Ordinal);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.json")));
        Assert.Equal(2, json.RootElement.GetProperty("overhead").GetProperty("aggregationSeconds").GetInt64());
        Assert.Equal(20, json.RootElement.GetProperty("overhead").GetProperty("aggregationPercent").GetDecimal());
    }

    [Fact]
    public async Task RunAsync_ShouldKeepDiagnosticsBestEffortWhenJunitCannotBeParsed()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace)
        {
            JunitText = """
                <!DOCTYPE testsuite [
                  <!ENTITY xxe SYSTEM "file:///etc/passwd">
                ]>
                <testsuite><testcase classname="Sample.Tests" name="BlockedDtd" time="1" /></testsuite>
                """,
        };
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        using var diagnostics = JsonDocument.Parse(File.ReadAllText(Path.Join(workspace.Root, "TestResults", "coverage-merged", "slow-test-diagnostics.json")));
        Assert.True(diagnostics.RootElement.GetProperty("totals").GetProperty("warnings").GetInt32() > 0);
        Assert.Equal(0, diagnostics.RootElement.GetProperty("totals").GetProperty("testCases").GetInt32());
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldIgnoreCopiedJunitFallbackWhenOutputDirectoryIsMissing()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "missing-output");
        var options = CreateCoverageOptions(workspace.Root, outputDirectory);

        var report = await SlowTestDiagnosticsWriter.CollectAsync(options, [], CancellationToken.None);

        Assert.Equal(0, report.JunitFileCount);
        Assert.Empty(report.TestCases);
        var warning = Assert.Single(report.Warnings);
        Assert.Contains("No JUnit files were available", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldWriteArtifactsWhenJunitCannotBeOpened()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        var junitPath = Path.Join(outputDirectory, "junit-tools-1-Sample.Tests.xml");
        File.WriteAllText(junitPath, "<testsuite />");
        var logPath = Path.Join(outputDirectory, "projects", "Sample.Tests", "dotnet-test.log");
        var options = CreateCoverageOptions(workspace.Root, outputDirectory);
        var project = new TestProject(
            "tools/Sample.Tests/Sample.Tests.csproj",
            Path.Join(workspace.Root, "tools", "Sample.Tests", "Sample.Tests.csproj"),
            "tools",
            "Sample.Tests",
            IsExclusive: false);
        var result = new ProjectRunResult(0, project, 3, 0, junitPath, logPath);

        var report = await SlowTestDiagnosticsWriter.CollectAsync(
            options,
            [result],
            openJunitStream: _ => throw new IOException("simulated read failure"),
            CancellationToken.None);
        var diagnostics = await SlowTestDiagnosticsWriter.WriteAsync(
            options,
            report,
            getAggregationSeconds: () => 1,
            calculateAggregationPercent: seconds => seconds,
            CancellationToken.None);

        Assert.Equal(1, diagnostics.WarningCount);
        Assert.True(File.Exists(Path.Join(outputDirectory, "slow-test-diagnostics.md")));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.json")));
        Assert.Equal(1, json.RootElement.GetProperty("totals").GetProperty("warnings").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("totals").GetProperty("testCases").GetInt32());
        Assert.Contains("Failed to read JUnit XML", File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldClassifyCopiedJunitFallbackWithoutProjectMetadata()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Join(outputDirectory, "junit-all-1-Fallback.Tests.xml"), """
            <testsuite>
              <testcase classname="Playwright.FileCoverageTests" name="StartsDotnetProcessFromTempArtifact" time="2.75" />
            </testsuite>
            """);
        var options = CreateCoverageOptions(workspace.Root, outputDirectory);

        var report = await SlowTestDiagnosticsWriter.CollectAsync(options, [], CancellationToken.None);
        await SlowTestDiagnosticsWriter.WriteAsync(
            options,
            report,
            getAggregationSeconds: () => 1,
            calculateAggregationPercent: seconds => seconds,
            CancellationToken.None);

        var test = Assert.Single(report.TestCases);
        Assert.Null(test.Project);
        Assert.Contains(test.EvidenceCategories, category => category.Category == "browser-or-integration" && category.Confidence == "medium");
        Assert.Contains(test.EvidenceCategories, category => category.Category == "process-startup");
        Assert.Contains(test.EvidenceCategories, category => category.Category == "filesystem-artifacts");
        Assert.Contains(test.EvidenceCategories, category => category.Category == "coverage-tooling");
        Assert.Contains(
            "| Playwright.FileCoverageTests.StartsDotnetProcessFromTempArtifact | 2.75 | passed | unknown |",
            File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.md")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldReportParserEdgesAndCategoryFallbacks()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        var edgeJunit = Path.Join(outputDirectory, "junit-tools-1-Plain.Tests.xml");
        File.WriteAllText(edgeJunit, """
            <testsuite>
              <testcase classname="" name="MissingClassAndTime" />
              <testcase classname="Plain.Tests" name="" time="-1"><error /></testcase>
              <testcase classname="Plain.FileTests" name="WritesTempArtifact" time="0.5"><skipped /></testcase>
            </testsuite>
            """);
        var exclusiveJunit = Path.Join(outputDirectory, "junit-tools-2-Exclusive.Tests.xml");
        File.WriteAllText(exclusiveJunit, """
            <testsuite>
              <testcase classname="Plain.SlowTests" name="RunsAlone" time="4" />
            </testsuite>
            """);
        var options = CreateCoverageOptions(workspace.Root, outputDirectory);
        var plainProject = new TestProject(
            "tools/Plain.Tests/Plain.Tests.csproj",
            Path.Join(workspace.Root, "tools", "Plain.Tests", "Plain.Tests.csproj"),
            "tools",
            "Plain.Tests",
            IsExclusive: false);
        var exclusiveProject = new TestProject(
            "tools/Exclusive.Tests/Exclusive.Tests.csproj",
            Path.Join(workspace.Root, "tools", "Exclusive.Tests", "Exclusive.Tests.csproj"),
            "tools",
            "Exclusive.Tests",
            IsExclusive: true);

        var report = await SlowTestDiagnosticsWriter.CollectAsync(
            options,
            [
                new ProjectRunResult(0, plainProject, 2, 0, edgeJunit, Path.Join(outputDirectory, "plain.log")),
                new ProjectRunResult(1, exclusiveProject, 4, 0, exclusiveJunit, Path.Join(outputDirectory, "exclusive.log")),
            ],
            CancellationToken.None);
        var diagnostics = await SlowTestDiagnosticsWriter.WriteAsync(
            options,
            report,
            getAggregationSeconds: () => 1,
            calculateAggregationPercent: seconds => seconds,
            CancellationToken.None);

        Assert.Equal(4, report.TestCases.Count);
        Assert.Equal(4, diagnostics.WarningCount);
        Assert.Contains(report.TestCases, test => test.Status == "error");
        Assert.Contains(report.TestCases, test => test.Status == "skipped");
        Assert.Contains(report.Categories, category => category.Category == "unit-test-execution" && category.Confidence == "low");
        Assert.Contains(report.Categories, category => category.Category == "filesystem-artifacts" && category.Confidence == "medium");
        Assert.Contains(report.Categories, category => category.Category == "browser-or-integration" && category.Confidence == "high");
        Assert.Contains(
            "missing classname",
            File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.md")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowTestDiagnosticsWriter_ShouldKeepArtifactsWhenJunitAccessIsDenied()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        var junitPath = Path.Join(outputDirectory, "junit-tools-1-Sample.Tests.xml");
        File.WriteAllText(junitPath, "<testsuite />");
        var options = CreateCoverageOptions(workspace.Root, outputDirectory);
        var project = new TestProject(
            "tools/Sample.Tests/Sample.Tests.csproj",
            Path.Join(workspace.Root, "tools", "Sample.Tests", "Sample.Tests.csproj"),
            "tools",
            "Sample.Tests",
            IsExclusive: false);

        var report = await SlowTestDiagnosticsWriter.CollectAsync(
            options,
            [new ProjectRunResult(0, project, 3, 0, junitPath, Path.Join(outputDirectory, "sample.log"))],
            openJunitStream: _ => throw new UnauthorizedAccessException("simulated access failure"),
            CancellationToken.None);
        await SlowTestDiagnosticsWriter.WriteAsync(
            options,
            report,
            getAggregationSeconds: () => 1,
            calculateAggregationPercent: seconds => seconds,
            CancellationToken.None);

        Assert.Contains(
            "Failed to access JUnit XML",
            File.ReadAllText(Path.Join(outputDirectory, "slow-test-diagnostics.md")),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    public async Task SlowTestDiagnosticsWriter_ShouldWarnWhenJunitPathIsMissing(string missingKind)
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(outputDirectory);
        var options = CreateCoverageOptions(workspace.Root, outputDirectory);
        var project = new TestProject(
            "tools/Missing.Tests/Missing.Tests.csproj",
            Path.Join(workspace.Root, "tools", "Missing.Tests", "Missing.Tests.csproj"),
            "tools",
            "Missing.Tests",
            IsExclusive: false);
        Exception exception = missingKind == "file"
            ? new FileNotFoundException("simulated missing file")
            : new DirectoryNotFoundException("simulated missing directory");

        var report = await SlowTestDiagnosticsWriter.CollectAsync(
            options,
            [new ProjectRunResult(0, project, 1, 0, Path.Join(outputDirectory, "missing.xml"), Path.Join(outputDirectory, "missing.log"))],
            openJunitStream: _ => throw exception,
            CancellationToken.None);

        var warning = Assert.Single(report.Warnings);
        Assert.Contains("JUnit file was not created", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldKeepDiagnosticsMetadataWhenStatusWriteFails()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var error = new StringWriter();
        var app = CreateApp(
            new RecordingCommandRunner(workspace),
            new ThrowingTextWriter("Slow-test diagnostics:", new IOException("simulated terminal failure")),
            error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        Assert.Contains("Slow-test diagnostics status output failed", error.ToString(), StringComparison.Ordinal);
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Assert.True(File.Exists(Path.Join(outputDirectory, "slow-test-diagnostics.md")));
        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Join(outputDirectory, "timings.json")));
        Assert.True(timings.RootElement.GetProperty("diagnostics").GetProperty("metadataComplete").GetBoolean());
        Assert.Equal(0, timings.RootElement.GetProperty("diagnostics").GetProperty("warningCount").GetInt32());
    }

    [Fact]
    public async Task RunAsync_ShouldKeepDiagnosticsBestEffortWhenDiagnosticsArtifactCannotBeWritten()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        Directory.CreateDirectory(Path.Join(outputDirectory, "slow-test-diagnostics.json"));
        var error = new StringWriter();
        var app = CreateApp(new RecordingCommandRunner(workspace), standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        Assert.Contains("Slow-test diagnostics failed after", error.ToString(), StringComparison.Ordinal);
        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Join(outputDirectory, "timings.json")));
        Assert.False(timings.RootElement.GetProperty("diagnostics").GetProperty("metadataComplete").GetBoolean());
        Assert.Equal(1, timings.RootElement.GetProperty("diagnostics").GetProperty("warningCount").GetInt32());
    }

    [Fact]
    public async Task RunAsync_ShouldPreserveDiagnosticCancellation()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var app = CreateApp(
            new RecordingCommandRunner(workspace),
            new ThrowingTextWriter("Slow-test diagnostics:", new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>()));
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
    public async Task RunAsync_ShouldStopSchedulingAfterExclusiveProjectThrowsUnexpectedException()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj");
        workspace.AddProject("tools/Never.Tests/Never.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace)
        {
            UnexpectedThrowingProject = "ForgeTrust.RazorWire.IntegrationTests.csproj",
        };
        var app = CreateApp(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.RunAsync(
            ["--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = "2" }));

        Assert.Single(runner.TestCommands);
        Assert.DoesNotContain(runner.TestCommands, project => project.EndsWith("Never.Tests.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ShouldNotStartExclusiveProjectWhenActiveProjectThrowsUnexpectedException()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Throw.Tests/Throw.Tests.csproj");
        workspace.AddProject("Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj");
        var runner = new RecordingCommandRunner(workspace)
        {
            UnexpectedThrowingProject = "Throw.Tests.csproj",
        };
        var app = CreateApp(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.RunAsync(
            ["--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = "2" }));

        Assert.Single(runner.TestCommands);
        Assert.DoesNotContain(
            runner.TestCommands,
            project => project.EndsWith("ForgeTrust.RazorWire.IntegrationTests.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ShouldStopSchedulingWhenBoundedQueueObservesUnexpectedException()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Throw.Tests/Throw.Tests.csproj");
        workspace.AddProject("tools/Never.Tests/Never.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace)
        {
            UnexpectedThrowingProject = "Throw.Tests.csproj",
        };
        var app = CreateApp(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = "1" }));

        Assert.Single(runner.TestCommands);
        Assert.DoesNotContain(runner.TestCommands, project => project.EndsWith("Never.Tests.csproj", StringComparison.Ordinal));
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

    [Theory]
    [InlineData(0, null, "zero Cobertura files")]
    [InlineData(2, null, "multiple Cobertura files")]
    [InlineData(1, "<not-coverage />", "document root is not 'coverage'")]
    [InlineData(1, "<coverage>", "unreadable or malformed")]
    public async Task RunAsync_ShouldFailSuccessfulTestWhenCollectorArtifactIsInvalid(
        int coverageFileCount,
        string? coverageText,
        string expectedError)
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Pass.Tests/Pass.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace)
        {
            CoverageFileCount = coverageFileCount,
            CoverageText = coverageText ?? RecordingCommandRunner.ValidCoverageText,
        };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
        Assert.Contains("Coverage artifact invalid for tools/Pass.Tests/Pass.Tests.csproj", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(expectedError, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Test run failed", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(
            workspace.Root,
            "TestResults",
            "coverage-merged",
            "projects",
            "Pass.Tests",
            "coverage.cobertura.xml")));
    }

    [Fact]
    public async Task RunAsync_ShouldPreserveTestFailureBeforeCollectorArtifactFailure()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Fail.Tests/Fail.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace)
        {
            FailingProject = "Fail.Tests.csproj",
            CoverageFileCount = 0,
        };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(7, exitCode);
        Assert.DoesNotContain("Coverage artifact invalid", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Fail.Tests.csproj (exit 7)", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldNormalizeOnlyTheCurrentCollectorInvocation()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Pass.Tests/Pass.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace)
        {
            CoverageText = "<coverage marker=\"current\" />",
            StaleSiblingCoverageText = "<coverage marker=\"stale\" />",
        };
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(0, exitCode);
        var canonical = Path.Join(
            workspace.Root,
            "TestResults",
            "coverage-merged",
            "projects",
            "Pass.Tests",
            "coverage.cobertura.xml");
        Assert.Contains("current", File.ReadAllText(canonical), StringComparison.Ordinal);
        Assert.DoesNotContain("stale", File.ReadAllText(canonical), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizeCollectorCoverageAsync_ShouldRejectSymbolicLinkArtifact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TestRepo.Create();
        var projectOutput = Path.Join(workspace.Root, "project");
        var rawResults = Path.Join(projectOutput, "collector-results", "current");
        Directory.CreateDirectory(rawResults);
        var external = Path.Join(workspace.Root, "external.xml");
        File.WriteAllText(external, "<coverage marker=\"external\" />");
        File.CreateSymbolicLink(Path.Join(rawResults, "coverage.cobertura.xml"), external);

        var failure = await CoverageRunnerApplication.NormalizeCollectorCoverageAsync(
            projectOutput,
            rawResults,
            CancellationToken.None);

        Assert.Contains("unreadable", failure, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(projectOutput, "coverage.cobertura.xml")));
    }

    [Fact]
    public async Task NormalizeCollectorCoverageAsync_ShouldPreserveCanonicalArtifactWhenValidationFails()
    {
        using var workspace = TestRepo.Create();
        var projectOutput = Path.Join(workspace.Root, "project");
        var rawResults = Path.Join(projectOutput, "collector-results", "current");
        Directory.CreateDirectory(rawResults);
        var canonical = Path.Join(projectOutput, "coverage.cobertura.xml");
        File.WriteAllText(canonical, "<coverage marker=\"previous\" />");
        File.WriteAllText(Path.Join(rawResults, "coverage.cobertura.xml"), "<coverage>");

        var failure = await CoverageRunnerApplication.NormalizeCollectorCoverageAsync(
            projectOutput,
            rawResults,
            CancellationToken.None);

        Assert.Contains("malformed", failure, StringComparison.Ordinal);
        Assert.Contains("previous", File.ReadAllText(canonical), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(projectOutput, ".coverage.*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task NormalizeCollectorCoverageAsync_ShouldRejectMissingInvocationDirectory()
    {
        using var workspace = TestRepo.Create();
        var projectOutput = Path.Join(workspace.Root, "project");
        var rawResults = Path.Join(projectOutput, "collector-results", "missing");

        var failure = await CoverageRunnerApplication.NormalizeCollectorCoverageAsync(
            projectOutput,
            rawResults,
            CancellationToken.None);

        Assert.Contains("zero Cobertura files", failure, StringComparison.Ordinal);
        Assert.False(Directory.Exists(rawResults));
    }

    [Fact]
    public async Task RunAsync_ShouldReportInvalidMergedCoberturaRoot()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Sample.Tests/Sample.Tests.csproj");
        var runner = new RecordingCommandRunner(workspace)
        {
            ReportGeneratorCoverageText = "<report />",
        };
        var error = new StringWriter();
        var app = CreateApp(runner, standardError: error);

        var exitCode = await app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(1, exitCode);
        Assert.Contains(
            "Merged Cobertura artifact is invalid or could not be committed: the document root is not 'coverage'",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitCoberturaAsync_ShouldPreserveCanonicalAndCleanStagingFileWhenValidationFails()
    {
        using var workspace = TestRepo.Create();
        var destination = Path.Join(workspace.Root, "coverage.cobertura.xml");
        var source = Path.Join(workspace.Root, "Cobertura.xml");
        File.WriteAllText(destination, "<coverage marker=\"previous\" />");
        File.WriteAllText(source, "<coverage lines-covered=\"not-a-number\" />");

        var failure = await CoverageRunnerApplication.CommitCoberturaAsync(
            source,
            destination,
            CancellationToken.None);

        Assert.Contains("numeric coverage attributes", failure, StringComparison.Ordinal);
        Assert.Contains("previous", File.ReadAllText(destination), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, ".coverage.cobertura.xml.*.tmp"));
    }

    [Fact]
    public async Task CommitCoberturaAsync_ShouldPreserveCanonicalAndCleanStagingFileWhenCanceledBeforeCommit()
    {
        using var workspace = TestRepo.Create();
        var destination = Path.Join(workspace.Root, "coverage.cobertura.xml");
        var source = Path.Join(workspace.Root, "Cobertura.xml");
        File.WriteAllText(destination, "<coverage marker=\"previous\" />");
        File.WriteAllText(source, RecordingCommandRunner.ValidCoverageText);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() => CoverageRunnerApplication.CommitCoberturaAsync(
            source,
            destination,
            cancellation.Token,
            cancellation.Cancel));

        Assert.Contains("previous", File.ReadAllText(destination), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, ".coverage.cobertura.xml.*.tmp"));
    }

    [Fact]
    public async Task CommitCoberturaAsync_ShouldPreserveCanonicalAndCleanStagingFileWhenCommitFails()
    {
        using var workspace = TestRepo.Create();
        var destination = Path.Join(workspace.Root, "coverage.cobertura.xml");
        var source = Path.Join(workspace.Root, "Cobertura.xml");
        File.WriteAllText(destination, "<coverage marker=\"previous\" />");
        File.WriteAllText(source, RecordingCommandRunner.ValidCoverageText);

        var failure = await CoverageRunnerApplication.CommitCoberturaAsync(
            source,
            destination,
            CancellationToken.None,
            () => throw new IOException("simulated commit failure"));

        Assert.Contains("IOException", failure, StringComparison.Ordinal);
        Assert.Contains("previous", File.ReadAllText(destination), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, ".coverage.cobertura.xml.*.tmp"));
    }

    [Theory]
    [InlineData("summary.txt")]
    [InlineData("timings.json")]
    [InlineData("reportgenerator-summary.txt")]
    public async Task WriteCanonicalTextAsync_ShouldPreserveCanonicalAndCleanStagingFileWhenCommitFails(string fileName)
    {
        using var workspace = TestRepo.Create();
        var destination = TestPathUtils.PathUnder(workspace.Root, fileName);
        File.WriteAllText(destination, "previous");

        await Assert.ThrowsAsync<IOException>(() => CoverageRunnerApplication.WriteCanonicalTextAsync(
            destination,
            "replacement",
            CancellationToken.None,
            () => throw new IOException("simulated commit failure")));

        Assert.Equal("previous", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, $".{fileName}.*.tmp"));
    }

    [Theory]
    [InlineData("summary.txt")]
    [InlineData("timings.json")]
    [InlineData("reportgenerator-summary.txt")]
    public async Task WriteCanonicalTextAsync_ShouldPreserveCanonicalAndCleanStagingFileWhenCanceledBeforeCommit(string fileName)
    {
        using var workspace = TestRepo.Create();
        var destination = TestPathUtils.PathUnder(workspace.Root, fileName);
        File.WriteAllText(destination, "previous");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() => CoverageRunnerApplication.WriteCanonicalTextAsync(
            destination,
            "replacement",
            cancellation.Token,
            cancellation.Cancel));

        Assert.Equal("previous", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, $".{fileName}.*.tmp"));
    }

    [Fact]
    public async Task CopyCanonicalTextAsync_ShouldPreserveReportSummaryAndAvoidStagingWhenSourceReadFails()
    {
        using var workspace = TestRepo.Create();
        var destination = Path.Join(workspace.Root, "reportgenerator-summary.txt");
        File.WriteAllText(destination, "previous");

        await Assert.ThrowsAsync<FileNotFoundException>(() => CoverageRunnerApplication.CopyCanonicalTextAsync(
            Path.Join(workspace.Root, "missing-Summary.txt"),
            destination,
            CancellationToken.None));

        Assert.Equal("previous", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, ".reportgenerator-summary.txt.*.tmp"));
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

    [Fact]
    public async Task RunAsync_ShouldPreserveFirstCancellationWhileDrainingAndObservingAllActiveProjects()
    {
        using var workspace = TestRepo.Create();
        workspace.AddProject("tools/Cancel.Tests/Cancel.Tests.csproj");
        workspace.AddProject("tools/Throw.Tests/Throw.Tests.csproj");

        var runner = new RecordingCommandRunner(workspace)
        {
            CancelingProject = "Cancel.Tests.csproj",
            UnexpectedThrowingProject = "Throw.Tests.csproj",
            WaitForReleaseProject = "Throw.Tests.csproj",
        };
        var app = CreateApp(runner);

        var run = app.RunAsync(
            ["--group", "tools", "--skip-solution-build"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = "2" });
        await runner.WaitingProjectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(run.IsCompleted);
        runner.ReleaseWaitingProject.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, runner.CompletedTestCommands.Count);
        Assert.Contains(runner.CompletedTestCommands, project => project.EndsWith("Cancel.Tests.csproj", StringComparison.Ordinal));
        Assert.Contains(runner.CompletedTestCommands, project => project.EndsWith("Throw.Tests.csproj", StringComparison.Ordinal));
        Assert.Equal(0, runner.ActiveTests);
    }

    private static CoverageRunnerApplication CreateApp(
        ICommandRunner commandRunner,
        TextWriter? standardOut = null,
        TextWriter? standardError = null,
        IClock? clock = null)
    {
        return new CoverageRunnerApplication(
            commandRunner,
            clock ?? new FakeClock(),
            standardOut ?? TextWriter.Synchronized(new StringWriter()),
            standardError ?? TextWriter.Synchronized(new StringWriter()));
    }

    private static CoverageRunnerOptions CreateCoverageOptions(string repositoryRoot, string outputDirectory)
    {
        return new CoverageRunnerOptions
        {
            RepositoryRoot = repositoryRoot,
            SolutionPath = Path.Join(repositoryRoot, "ForgeTrust.AppSurface.slnx"),
            OutputDirectory = outputDirectory,
            GroupName = "tools",
            BuildConfiguration = "Release",
            BuildSolution = false,
            BuildNoRestore = false,
            IncludeFilter = "[ForgeTrust.AppSurface.*]*",
            ExcludeFilter = "[*.Tests]*,[*.IntegrationTests]*",
            Parallelism = 1,
            MergeOnly = false,
            ListGroups = false,
        };
    }

    private sealed class ThrowingTextWriter : StringWriter
    {
        private readonly string _throwWhenValueContains;
        private readonly Exception _exception;

        public ThrowingTextWriter(string throwWhenValueContains, Exception exception)
        {
            _throwWhenValueContains = throwWhenValueContains;
            _exception = exception;
        }

        public override Task WriteLineAsync(string? value)
        {
            return value?.Contains(_throwWhenValueContains, StringComparison.Ordinal) == true
                ? Task.FromException(_exception)
                : base.WriteLineAsync(value);
        }
    }

    private sealed class RecordingCommandRunner : ICommandRunner
    {
        public const string ValidCoverageText =
            "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />";

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

        public int CoverageFileCount { get; init; } = 1;

        public string CoverageText { get; init; } = ValidCoverageText;

        public string? StaleSiblingCoverageText { get; init; }

        public string? ThrowingProject { get; init; }

        public string? CancelingProject { get; init; }

        public string? UnexpectedThrowingProject { get; init; }

        public string? WaitForReleaseProject { get; init; }

        public int ReportGeneratorExitCode { get; init; }

        public bool ReportGeneratorShouldOmitCoverage { get; init; }

        public string ReportGeneratorCoverageText { get; init; } =
            "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />";

        public string JunitText { get; init; } = "<testsuite />";

        private int _maxConcurrentTests;

        public int MaxConcurrentTests => Volatile.Read(ref _maxConcurrentTests);

        public ConcurrentBag<IReadOnlyList<string>> BuildCommands { get; } = [];

        public ConcurrentBag<IReadOnlyList<string>> TestArguments { get; } = [];

        public ConcurrentBag<string> TestCommands { get; } = [];

        public ConcurrentBag<string> CompletedTestCommands { get; } = [];

        public TaskCompletionSource WaitingProjectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseWaitingProject { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActiveTests => Volatile.Read(ref _activeTests);

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

                    if (WaitForReleaseProject is not null && project.EndsWith(WaitForReleaseProject, StringComparison.Ordinal))
                    {
                        WaitingProjectStarted.TrySetResult();
                        await ReleaseWaitingProject.Task.WaitAsync(cancellationToken);
                    }

                    if (ThrowingProject is not null && project.EndsWith(ThrowingProject, StringComparison.Ordinal))
                    {
                        throw new IOException($"simulated command failure for {project}");
                    }

                    if (CancelingProject is not null && project.EndsWith(CancelingProject, StringComparison.Ordinal))
                    {
                        throw new OperationCanceledException();
                    }

                    if (UnexpectedThrowingProject is not null && project.EndsWith(UnexpectedThrowingProject, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"simulated unexpected failure for {project}");
                    }

                    var resultsIndex = arguments.ToList().IndexOf("--results-directory");
                    var invocationDirectory = arguments[resultsIndex + 1];
                    if (StaleSiblingCoverageText is not null)
                    {
                        var staleDirectory = Path.Join(Path.GetDirectoryName(invocationDirectory)!, "stale", "attachment");
                        Directory.CreateDirectory(staleDirectory);
                        File.WriteAllText(Path.Join(staleDirectory, "coverage.cobertura.xml"), StaleSiblingCoverageText);
                    }

                    var coverageDirectory = Path.Join(invocationDirectory, Guid.NewGuid().ToString("D"));
                    if (MissingCoverageProject is null || !project.EndsWith(MissingCoverageProject, StringComparison.Ordinal))
                    {
                        for (var coverageIndex = 0; coverageIndex < CoverageFileCount; coverageIndex++)
                        {
                            var artifactDirectory = Path.Join(coverageDirectory, coverageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            Directory.CreateDirectory(artifactDirectory);
                            File.WriteAllText(Path.Join(artifactDirectory, "coverage.cobertura.xml"), CoverageText);
                        }
                    }

                    var junit = arguments.Single(argument => argument.StartsWith("--logger:junit;LogFilePath=", StringComparison.Ordinal))["--logger:junit;LogFilePath=".Length..];
                    File.WriteAllText(junit, JunitText);

                    if (FailingProject is not null && project.EndsWith(FailingProject, StringComparison.Ordinal))
                    {
                        return Complete(7, $"failed {project}", outputFile);
                    }

                    return Complete(0, $"passed {project}", outputFile);
                }
                finally
                {
                    CompletedTestCommands.Add(project);
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

    private sealed class ZeroClock : IClock
    {
        public ITimer StartTimer() => new ZeroTimer();

        private sealed class ZeroTimer : ITimer
        {
            public long ElapsedSeconds => 0;
        }
    }
}
