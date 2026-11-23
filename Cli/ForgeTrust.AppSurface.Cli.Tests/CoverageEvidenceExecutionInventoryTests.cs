using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageEvidenceExecutionInventoryTests
{
    [Fact]
    public async Task RunAndGateAsync_CapturesCompleteInventoryWhenRequested()
    {
        using var directory = TestDirectory.Create();
        using var history = new EnvironmentVariableScope("APPSURFACE_ARTIFACT_HOME",
            TestPathUtils.PathUnder(directory.Path, "history"));
        var (solution, output) = await CreateInputsAsync(directory.Path);
        var workflow = CreateWorkflow();

        var result = await workflow.RunAndGateAsync(
            CreateRunRequest(solution, output),
            CreateGateRequest(TestPathUtils.PathUnder(output, "coverage")),
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null),
            CancellationToken.None,
            captureInventory: true);

        Assert.True(result.Run.Success);
        Assert.True(result.Gate?.Passed);
        Assert.Null(result.InventoryIneligibilityReason);
        var inventory = Assert.IsType<CoverageEvidenceInventory>(result.Inventory);
        Assert.Contains(inventory.Files, file => file.RelativePath == "coverage/coverage.cobertura.xml");
        Assert.Contains(inventory.Files, file => file.RelativePath == "coverage/coverage-gate.json");
        Assert.Contains(inventory.Files, file => file.RelativePath == "coverage/coverage-gate.md");
        Assert.Contains(inventory.Files, file => file.RelativePath == "coverage/timings.json");
        Assert.Contains(inventory.Files, file => file.RelativePath == "coverage/summary.txt");
        Assert.Contains(inventory.Files, file => file.RelativePath.EndsWith("/coverage-project.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAndGateAsync_IncompleteInventoryReportsReasonAndPreservesPassingGate()
    {
        using var directory = TestDirectory.Create();
        using var history = new EnvironmentVariableScope("APPSURFACE_ARTIFACT_HOME",
            TestPathUtils.PathUnder(directory.Path, "history"));
        var (solution, output) = await CreateInputsAsync(directory.Path);
        var coverageOutput = TestPathUtils.PathUnder(output, "coverage");
        void OversizeStagedTimings()
        {
            var staged = Directory.GetFiles(coverageOutput, ".timings.*.tmp", SearchOption.AllDirectories).Single();
            File.AppendAllText(staged, new string(' ', CoverageEvidenceInventory.MaximumMetadataBytes + 1));
        }
        var workflow = CreateWorkflow(OversizeStagedTimings);

        var result = await workflow.RunAndGateAsync(
            CreateRunRequest(solution, output),
            CreateGateRequest(coverageOutput),
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null),
            CancellationToken.None,
            captureInventory: true);

        Assert.True(result.Run.Success);
        Assert.True(result.Gate?.Passed);
        Assert.Null(result.Inventory);
        Assert.Equal("inventory-metadata-limit", result.InventoryIneligibilityReason);
        Assert.True(File.Exists(TestPathUtils.PathUnder(coverageOutput, "timings.json")));
        Assert.True(new FileInfo(TestPathUtils.PathUnder(coverageOutput, "timings.json")).Length
            > CoverageEvidenceInventory.MaximumMetadataBytes);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CallerOwnedSessionReportsUnregisteredPublicationWithoutLosingTheGate(
        bool captureInventory, bool captureOrdinaryPublications)
    {
        using var directory = TestDirectory.Create();
        using var history = new EnvironmentVariableScope("APPSURFACE_ARTIFACT_HOME",
            TestPathUtils.PathUnder(directory.Path, "history"));
        var (solution, output) = await CreateInputsAsync(directory.Path);
        var request = CreateRunRequest(solution, output);
        var writers = CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null);
        using var session = await CoverageArtifactSession.BeginAsync(request.OutputDirectory, directory.Path,
            request.Clean, writers, TimeProvider.System, CancellationToken.None);
        var unrelated = TestPathUtils.PathUnder(directory.Path, "caller-report.txt");
        await File.WriteAllTextAsync(unrelated, "caller-owned report");
        session.Inventory.RecordFile(unrelated);

        var result = await CreateWorkflow().RunAndGateAsync(request with { ArtifactSession = session },
            CreateGateRequest(request.OutputDirectory), writers, CancellationToken.None,
            captureInventory, captureOrdinaryPublications);

        Assert.True(result.Run.Success);
        Assert.True(result.Gate?.Passed);
        Assert.Null(result.Inventory);
        Assert.Null(result.OrdinaryPublications);
        Assert.Equal(captureInventory ? "inventory-unavailable" : null, result.InventoryIneligibilityReason);
        Assert.Equal(captureOrdinaryPublications ? "inventory-unavailable" : null, result.OrdinaryPublicationIneligibilityReason);
        Assert.Equal("caller-owned report", await File.ReadAllTextAsync(unrelated));
        session.VerifyBinding();
    }

    private static CoverageEvidenceExecutionWorkflow CreateWorkflow(Action? timingsStaged = null) => new(
        new CoverageRunWorkflow(
            new EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner("tests/Sample.Tests/Sample.Tests.csproj"),
            new EvidenceCoverageInventoryEdgeTests.FixtureReportGenerator(),
            TimeProvider.System,
            timingsStaged: timingsStaged));

    private static async Task<(string Solution, string Output)> CreateInputsAsync(string root)
    {
        var solution = TestPathUtils.PathUnder(root, "sample.slnx");
        var project = TestPathUtils.PathUnder(root, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(solution, "solution fixture");
        await File.WriteAllTextAsync(project, "<Project />");
        return (solution, TestPathUtils.PathUnder(root, "run"));
    }

    private static CoverageRunRequest CreateRunRequest(string solution, string output) => new(
        SolutionPath: solution,
        TestProjects: [],
        ExcludeTestProjects: [],
        OutputDirectory: TestPathUtils.PathUnder(output, "coverage"),
        Configuration: "Release",
        Parallelism: 1,
        ScheduleMode: CoverageRunScheduleMode.InputOrder,
        ScheduleTimingsPath: null,
        PriorityTestProjects: [],
        NoRestore: false,
        Build: true,
        NoBuild: false,
        IncludeFilter: null,
        ExcludeFilter: "[*.Tests]*,[*.IntegrationTests]*",
        DryRun: false,
        NoDiscoverExclusive: false,
        ExclusiveTestProjects: [],
        Loggers: [],
        TestArguments: [],
        TestResults: CoverageRunTestResultFormat.Junit,
        SlowTestDiagnostics: false,
        Clean: true,
        Verbosity: "minimal",
        HeartbeatInterval: TimeSpan.Zero,
        NoProgressTimeout: TimeSpan.FromMinutes(10),
        WatchdogMode: CoverageRunWatchdogMode.Off,
        CoverageDriver: CoverageRunDriver.Collector,
        RequireNonSandbox: false);

    private static CoverageGateRequest CreateGateRequest(string coverageOutput) => new(
        TestPathUtils.PathUnder(coverageOutput, "coverage.cobertura.xml"),
        coverageOutput,
        MinLinePercent: 95,
        MinBranchPercent: 85,
        TolerancePercent: 0);
}
