using System.Collections.Concurrent;
using System.Text.Json;

namespace ForgeTrust.AppSurface.CoverageRunner.Tests;

public sealed class CoverageRunnerApplicationTests
{
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
    public async Task RunAsync_ShouldRejectMergeOnlyWhenCaseVariantSourceMatchesOutputOnCaseInsensitiveFileSystems()
    {
        using var workspace = TestRepo.Create();
        var outputDirectory = Path.Join(workspace.Root, "TestResults", "coverage-merged");
        var coverageFile = Path.Join(outputDirectory, "projects", "Sample.Tests", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(coverageFile)!);
        File.WriteAllText(coverageFile, "<coverage />");

        var caseVariantOutputDirectory = Path.Join(workspace.Root, "testresults", "coverage-merged");
        if (!Directory.Exists(caseVariantOutputDirectory))
        {
            return;
        }

        var runner = new RecordingCommandRunner(workspace);
        var app = CreateApp(runner);

        var exitCode = await app.RunAsync(
            ["--merge-only", caseVariantOutputDirectory, "--output", outputDirectory],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Equal(2, exitCode);
        Assert.True(File.Exists(coverageFile));
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

        public string? FailingProject { get; init; }

        public string? MissingCoverageProject { get; init; }

        public int ReportGeneratorExitCode { get; init; }

        public int MaxConcurrentTests { get; private set; }

        public ConcurrentBag<string> TestCommands { get; } = [];

        public ConcurrentBag<TestSnapshot> TestSnapshots { get; } = [];

        public async Task<CommandResult> RunAsync(
            string command,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            if (arguments.Count == 3 && arguments[0] == "sln" && arguments[2] == "list")
            {
                return new CommandResult(0, string.Join(Environment.NewLine, _workspace.Projects));
            }

            if (arguments.Count == 2 && arguments[0] == "tool" && arguments[1] == "restore")
            {
                return new CommandResult(0, "");
            }

            if (arguments.Count >= 4 && arguments[0] == "tool" && arguments[2] == "reportgenerator")
            {
                if (ReportGeneratorExitCode != 0)
                {
                    return new CommandResult(ReportGeneratorExitCode, "reportgenerator failed");
                }

                var target = arguments.Single(argument => argument.StartsWith("-targetdir:", StringComparison.Ordinal))["-targetdir:".Length..];
                Directory.CreateDirectory(target);
                File.WriteAllText(
                    Path.Join(target, "Cobertura.xml"),
                    "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />");
                return new CommandResult(0, "");
            }

            if (arguments.Count > 0 && arguments[0] == "test")
            {
                var project = arguments[1];
                var active = Interlocked.Increment(ref _activeTests);
                MaxConcurrentTests = Math.Max(MaxConcurrentTests, active);
                TestCommands.Add(project);
                TestSnapshots.Add(new TestSnapshot(project, active));
                try
                {
                    if (TestDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(TestDelay, cancellationToken);
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
                        return new CommandResult(7, $"failed {project}");
                    }

                    return new CommandResult(0, $"passed {project}");
                }
                finally
                {
                    Interlocked.Decrement(ref _activeTests);
                }
            }

            return new CommandResult(0, "");
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
