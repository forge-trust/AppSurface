using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;

namespace ForgeTrust.AppSurface.Cli.Tests;

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
        Assert.DoesNotContain("[ForgeTrust.AppSurface.", string.Join(" ", testCommand.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain("build", runner.Commands.Select(command => command.Arguments.FirstOrDefault()));
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
        Assert.Contains(runner.Commands, command => command.Arguments.FirstOrDefault() == "test");
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.FirstOrDefault() == "build");
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

    private static CoverageRunWorkflow CreateWorkflow(
        ICoverageRunProcessRunner runner,
        ICoverageRunReportGenerator reportGenerator)
        => new(runner, reportGenerator, TimeProvider.System);

    private static CoverageRunRequest CreateRequest(
        string? SolutionPath = null,
        IReadOnlyList<string>? TestProjects = null,
        string OutputDirectory = "TestResults/coverage-merged",
        string Configuration = "Debug",
        int Parallelism = 1,
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
        bool Clean = true,
        string Verbosity = "minimal")
        => new(
            SolutionPath,
            TestProjects ?? [],
            OutputDirectory,
            Configuration,
            Parallelism,
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
            Clean,
            Verbosity);

    private static IDisposable PushCurrentDirectory(string path)
    {
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(path);
        return new DelegateDisposable(() => Directory.SetCurrentDirectory(previous));
    }

    private sealed class RecordingCoverageRunProcessRunner : ICoverageRunProcessRunner
    {
        public string SlnListOutput { get; init; } = string.Empty;
        public bool WriteCoverageFiles { get; init; } = true;
        public int TestExitCode { get; init; }
        public List<RecordedCommand> Commands { get; } = [];

        public async Task<CoverageRunProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            string? outputFile = null)
        {
            Commands.Add(new RecordedCommand(fileName, arguments.ToArray(), workingDirectory, outputFile));
            if (arguments is ["sln", ..])
            {
                return new CoverageRunProcessResult(0, SlnListOutput);
            }

            if (arguments.FirstOrDefault() == "test")
            {
                if (outputFile is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
                    await File.WriteAllTextAsync(outputFile, "test output", cancellationToken);
                }

                if (WriteCoverageFiles)
                {
                    var coverletOutput = arguments.Single(argument => argument.StartsWith("/p:CoverletOutput=", StringComparison.Ordinal))["/p:CoverletOutput=".Length..];
                    var projectDirectory = Path.GetDirectoryName(coverletOutput)!;
                    Directory.CreateDirectory(projectDirectory);
                    await File.WriteAllTextAsync(
                        Path.Join(projectDirectory, "coverage.cobertura.xml"),
                        "<coverage lines-covered=\"8\" lines-valid=\"10\" branches-covered=\"2\" branches-valid=\"4\" />",
                        cancellationToken);
                }

                return new CoverageRunProcessResult(TestExitCode, "test output");
            }

            return new CoverageRunProcessResult(0, string.Empty);
        }
    }

    private sealed class RecordingReportGenerator : ICoverageRunReportGenerator
    {
        public List<string> CoverageFiles { get; } = [];
        public int ExitCode { get; init; }
        public bool WriteMergedCoverage { get; init; } = true;

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
                    "<coverage lines-covered=\"8\" lines-valid=\"10\" branches-covered=\"2\" branches-valid=\"4\" />",
                    cancellationToken);
            }

            await File.WriteAllTextAsync(summary, "reportgenerator summary", cancellationToken);
            return new CoverageRunMergeResult(ExitCode, cobertura, summary);
        }
    }

    private sealed record RecordedCommand(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        string? OutputFile);

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
            var path = System.IO.Path.Join(Path, relativePath);
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
