using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageEvidenceExecutionPublicationTests
{
    [Fact]
    public async Task CallerOwnedSessionReportsUnregisteredPublicationWithoutLosingTheGate()
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
            captureOrdinaryPublications: true);

        Assert.True(result.Run.Success);
        Assert.True(result.Gate?.Passed);
        Assert.Null(result.OrdinaryPublications);
        Assert.Equal("inventory-unavailable", result.OrdinaryPublicationIneligibilityReason);
        Assert.Equal("caller-owned report", await File.ReadAllTextAsync(unrelated));
        session.VerifyBinding();
    }

    private static CoverageEvidenceExecutionWorkflow CreateWorkflow() => new(
        new CoverageRunWorkflow(
            new EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner("tests/Sample.Tests/Sample.Tests.csproj"),
            new EvidenceCoverageInventoryEdgeTests.FixtureReportGenerator(),
            TimeProvider.System));

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
