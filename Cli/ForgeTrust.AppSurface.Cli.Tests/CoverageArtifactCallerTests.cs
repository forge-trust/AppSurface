using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactCallerTests
{
    private const string Cobertura = "<coverage lines-covered=\"10\" lines-valid=\"10\" branches-covered=\"10\" branches-valid=\"10\" line-rate=\"1\" branch-rate=\"1\"><packages /></coverage>";
    private const string EnabledSettings = "<RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName=\"XPlat Code Coverage\" enabled=\"true\" /></DataCollectors></DataCollectionRunSettings></RunSettings>";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task EvidenceProducer_ShouldPropagateDefaultOrFullRetentionThroughSharedRunner(bool retainFull, bool expectRawArtifact)
    {
        using var directory = TestDirectory.Create();
        var solutionPath = Path.Join(directory.Path, "sample.slnx");
        var projectPath = TestPathUtils.PathUnder(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        await File.WriteAllTextAsync(solutionPath, "{}");
        await File.WriteAllTextAsync(projectPath, "<Project />");
        var runner = new RecordingCoverageRunProcessRunner();
        var producer = CreateProducer(runner);
        var outputDirectory = Path.Join(directory.Path, "output");
        var declaration = CreateDeclaration();
        if (retainFull)
        {
            declaration = declaration with { CoverageArtifactRetention = "full" };
        }

        using var standardOutputWriter = new StringWriter();
        using var standardErrorWriter = new StringWriter();
        var result = await producer.RunAsync(
            declaration,
            solutionPath,
            outputDirectory,
            null,
            CoverageTextWriters.Create(standardOutputWriter, standardErrorWriter),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Contains("coverage/assertion@1", result.SatisfiedAssertionIds, StringComparer.Ordinal);
        var testCommand = Assert.Single(runner.Commands, command => command[0] == "test");
        Assert.Contains("--collect:XPlat Code Coverage", testCommand);
        var resultsDirectoryIndex = Array.IndexOf(testCommand, "--results-directory");
        Assert.True(resultsDirectoryIndex >= 0);
        var rawArtifact = Directory.GetFiles(testCommand[resultsDirectoryIndex + 1], "coverage.cobertura.xml", SearchOption.AllDirectories);
        Assert.Equal(expectRawArtifact, rawArtifact.Length > 0);
        Assert.NotEmpty(Directory.GetFiles(Path.Join(outputDirectory, "coverage", "projects"), "coverage.cobertura.xml", SearchOption.AllDirectories));
        Assert.DoesNotContain("ASCOV208", standardErrorWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvidenceProducer_ShouldRejectInvalidRetentionBeforeProcessOrOutputMutation()
    {
        using var directory = TestDirectory.Create();
        var solutionPath = Path.Join(directory.Path, "sample.slnx");
        await File.WriteAllTextAsync(solutionPath, "{}");
        var outputDirectory = Path.Join(directory.Path, "output");
        var runner = new RecordingCoverageRunProcessRunner();
        using var standardOutputWriter = new StringWriter();
        using var standardErrorWriter = new StringWriter();

        var result = await CreateProducer(runner).RunAsync(
            CreateDeclaration() with { CoverageArtifactRetention = "archive" },
            solutionPath,
            outputDirectory,
            null,
            CoverageTextWriters.Create(standardOutputWriter, standardErrorWriter),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Contains("ASCOV208", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
        Assert.False(Directory.Exists(outputDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path, "coverage*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("<RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName=\"XPlat Code Coverage\" enabled=\"false\" /></DataCollectors></DataCollectionRunSettings></RunSettings>", null)]
    [InlineData("<RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName=\"XPlat Code Coverage\" enabled=\"true\" /></DataCollectors></DataCollectionRunSettings></RunSettings>", "ASCOV209")]
    [InlineData("<RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName=\"XPlat Code Coverage\" isEnabled=\"false\" /></DataCollectors></DataCollectionRunSettings></RunSettings>", "ASCOV211")]
    public async Task RunAsync_Preflight_ShouldReportEffectiveSelectedSettingsWithoutChangingInvocation(string settingsXml, string? expectedDiagnostic)
    {
        using var repo = TestDirectory.Create();
        var project = TestPathUtils.PathUnder(repo.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project />");
        await File.WriteAllTextAsync(Path.Join(repo.Path, "inherited.runsettings"), EnabledSettings);
        await File.WriteAllTextAsync(Path.Join(repo.Path, "effective.runsettings"), settingsXml);
        using var current = PushCurrentDirectory(repo.Path);
        var runner = new RecordingCoverageRunProcessRunner
        {
            OuterCapabilityOutput = Capability("", targetFrameworks: "net10.0", runSettingsFilePath: "inherited.runsettings"),
        };
        runner.CapabilityOutputsByFramework["net10.0"] = Capability(
            "net10.0", targetFrameworks: "net10.0", runSettingsFilePath: "inherited.runsettings");
        var workflow = new CoverageRunWorkflow(runner, new PassingCoverageReportGenerator(), TimeProvider.System);
        using var console = new FakeInMemoryConsole();
        var effectiveSettingsPath = Path.Join(repo.Path, "effective.runsettings");
        var callerArguments = new[] { $"-property:RunSettingsFilePath={effectiveSettingsPath}", "--filter", "FullyQualifiedName~Smoke" };

        var result = await workflow.RunAsync(
            CreateRunRequest(project, callerArguments),
            console,
            CancellationToken.None);

        Assert.True(result.Success);
        var testCommand = Assert.Single(runner.Commands, command => command[0] == "test");
        Assert.Equal("Release", testCommand[Array.IndexOf(testCommand, "--configuration") + 1]);
        var capabilityCommands = runner.Commands.Where(command => command[0] == "msbuild").ToArray();
        Assert.Equal(2, capabilityCommands.Length);
        Assert.Contains(capabilityCommands, command => command.Contains("-property:TargetFramework=net10.0"));
        Assert.Contains("-property:Configuration=Release", runner.Commands.SelectMany(command => command));
        AssertSubsequence(testCommand, callerArguments);
        var errors = console.ReadErrorString();
        Assert.True((expectedDiagnostic == "ASCOV209") == errors.Contains("ASCOV209", StringComparison.Ordinal), errors);
        Assert.True((expectedDiagnostic == "ASCOV211") == errors.Contains("ASCOV211", StringComparison.Ordinal), errors);
        if (expectedDiagnostic is not null)
        {
            Assert.Contains(expectedDiagnostic, console.ReadErrorString(), StringComparison.Ordinal);
        }
    }

    private static CoverageEvidenceProducer CreateProducer(ICoverageRunProcessRunner runner) => new(
        new CoverageEvidenceExecutionWorkflow(
            new CoverageRunWorkflow(runner, new PassingCoverageReportGenerator(), TimeProvider.System)));

    private static EvidenceProducerDeclaration CreateDeclaration() => new(
        "coverage",
        "coverage",
        "1.0.0",
        [],
        ["coverage/assertion@1"],
        [],
        60,
        new EvidenceCoverageGateRequirements(95, 85, TolerancePercent: 0));

    private static CoverageRunRequest CreateRunRequest(string project, IReadOnlyList<string> testArguments) => new(
        SolutionPath: null,
        TestProjects: [project],
        ExcludeTestProjects: [],
        OutputDirectory: "TestResults/coverage-merged",
        Configuration: "Release",
        Parallelism: 1,
        ScheduleMode: CoverageRunScheduleMode.InputOrder,
        ScheduleTimingsPath: null,
        PriorityTestProjects: [],
        NoRestore: false,
        Build: false,
        NoBuild: false,
        IncludeFilter: null,
        ExcludeFilter: "[*.Tests]*,[*.IntegrationTests]*",
        DryRun: false,
        NoDiscoverExclusive: false,
        ExclusiveTestProjects: [],
        Loggers: [],
        TestArguments: testArguments,
        TestResults: CoverageRunTestResultFormat.None,
        SlowTestDiagnostics: false,
        Clean: true,
        Verbosity: "minimal",
        HeartbeatInterval: TimeSpan.Zero,
        NoProgressTimeout: TimeSpan.FromMinutes(10),
        WatchdogMode: CoverageRunWatchdogMode.Off,
        CoverageDriver: CoverageRunDriver.Collector,
        RequireNonSandbox: false);

    private static string Capability(string framework, string targetFrameworks, string? runSettingsFilePath = null)
    {
        var properties = new Dictionary<string, string?>
        {
            ["Configuration"] = "Release",
            ["TargetFrameworks"] = targetFrameworks,
        };
        if (!string.IsNullOrEmpty(framework)) properties["TargetFramework"] = framework;
        if (runSettingsFilePath is not null) properties["RunSettingsFilePath"] = runSettingsFilePath;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Properties = properties,
            Items = new { PackageReference = new[] { new { Identity = "coverlet.collector" } } },
        });
    }

    private static void AssertSubsequence(IReadOnlyList<string> actual, IReadOnlyList<string> expected)
    {
        var next = 0;
        foreach (var argument in actual)
        {
            if (next < expected.Count && argument == expected[next]) next++;
        }

        Assert.Equal(expected.Count, next);
    }

    private static IDisposable PushCurrentDirectory(string path)
    {
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(path);
        return new DelegateDisposable(() => Directory.SetCurrentDirectory(previous));
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class RecordingCoverageRunProcessRunner : ICoverageRunProcessRunner
    {
        public string OuterCapabilityOutput { get; init; } = Capability("net10.0", "net10.0");
        public Dictionary<string, string> CapabilityOutputsByFramework { get; } = [];
        public List<string[]> Commands { get; } = [];

        public async Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            request.Lease.Complete();
            var arguments = request.Arguments.ToArray();
            Commands.Add(arguments);
            switch (arguments[0])
            {
                case "sln":
                    return new CoverageRunProcessResult(0, "Project(s)\n----------\ntests/Sample.Tests/Sample.Tests.csproj\n");
                case "msbuild":
                    {
                        var frameworkArgument = arguments.FirstOrDefault(argument => argument.StartsWith("-property:TargetFramework=", StringComparison.Ordinal));
                        var capability = frameworkArgument is null
                            ? OuterCapabilityOutput
                            : CapabilityOutputsByFramework.GetValueOrDefault(frameworkArgument["-property:TargetFramework=".Length..], OuterCapabilityOutput);
                        return new CoverageRunProcessResult(0, capability, StandardOutput: capability);
                    }
                case "test":
                    {
                        var resultsIndex = Array.IndexOf(arguments, "--results-directory");
                        Assert.True(resultsIndex >= 0);
                        var rawDirectory = Path.Join(arguments[resultsIndex + 1], Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(rawDirectory);
                        await File.WriteAllTextAsync(Path.Join(rawDirectory, "coverage.cobertura.xml"), Cobertura, cancellationToken);
                        if (request.OutputFile is not null)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputFile)!);
                            await File.WriteAllTextAsync(request.OutputFile, "test output", cancellationToken);
                        }

                        return new CoverageRunProcessResult(0, "test output");
                    }
                default:
                    return new CoverageRunProcessResult(0, "build output");
            }
        }
    }

    private sealed class PassingCoverageReportGenerator : ICoverageRunReportGenerator
    {
        public async Task<CoverageRunMergeResult> MergeAsync(
            IReadOnlyList<string> coverageFiles,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(outputDirectory);
            var coberturaPath = Path.Join(outputDirectory, "Cobertura.xml");
            var summaryPath = Path.Join(outputDirectory, "Summary.txt");
            await File.WriteAllTextAsync(coberturaPath, Cobertura, cancellationToken);
            await File.WriteAllTextAsync(summaryPath, "coverage summary", cancellationToken);
            return new CoverageRunMergeResult(0, coberturaPath, summaryPath);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path) => Path = path;

        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "coverage-artifact-caller-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
