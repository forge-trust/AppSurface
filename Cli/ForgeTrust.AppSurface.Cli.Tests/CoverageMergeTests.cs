using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageMergeTests
{
    [Fact]
    public async Task MergeAsync_ShouldDiscoverNestedArtifactsStageAndWriteArtifacts()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        repo.WriteFile(
            "TestResults/coverage-shards/unit/coverage.cobertura.xml",
            "<coverage lines-covered=\"7\" lines-valid=\"10\" branches-covered=\"2\" branches-valid=\"4\" />");
        repo.WriteFile(
            "TestResults/coverage-shards/integration/nested/coverage.cobertura.xml",
            "<coverage lines-covered=\"3\" lines-valid=\"4\" branches-covered=\"1\" branches-valid=\"2\" />");
        repo.WriteFile(
            "TestResults/coverage-shards/downloaded/reportgenerator-input/000001-stale/coverage.cobertura.xml",
            "<coverage lines-covered=\"99\" lines-valid=\"99\" branches-covered=\"99\" branches-valid=\"99\" />");
        repo.WriteFile("TestResults/coverage-shards/unit/Cobertura.xml", "<coverage />");
        using var current = PushCurrentDirectory(repo.Path);
        var reportGenerator = new RecordingReportGenerator();
        var workflow = CreateWorkflow(reportGenerator);
        using var console = new FakeInMemoryConsole();
        var request = new CoverageMergeRequest("TestResults/coverage-shards", "TestResults/coverage-merged", Clean: true);

        var result = await workflow.MergeAsync(request, console, CancellationToken.None);

        Assert.Equal(
            [
                Path.Join("integration", "nested", "coverage.cobertura.xml"),
                Path.Join("unit", "coverage.cobertura.xml"),
            ],
            result.SelectedReports.Select(path => Path.GetRelativePath(Path.Join(Directory.GetCurrentDirectory(), "TestResults", "coverage-shards"), path)).ToArray());
        Assert.True(File.Exists(Path.Join(result.OutputDirectory, "coverage.cobertura.xml")));
        Assert.True(File.Exists(Path.Join(result.OutputDirectory, "summary.txt")));
        Assert.True(File.Exists(Path.Join(result.OutputDirectory, "timings.json")));
        Assert.True(File.Exists(Path.Join(result.OutputDirectory, "reportgenerator-summary.txt")));
        Assert.EndsWith(Path.Join("TestResults", "coverage-merged", "coverage.cobertura.xml"), result.CoveragePath, StringComparison.Ordinal);
        var mergeInput = Assert.Single(reportGenerator.CoverageFiles);
        Assert.EndsWith(Path.Join("reportgenerator-input", "**", "coverage.cobertura.xml"), mergeInput, StringComparison.Ordinal);
        Assert.Equal(2, Directory.EnumerateFiles(Path.Join(result.OutputDirectory, "reportgenerator-input"), "coverage.cobertura.xml", SearchOption.AllDirectories).Count());
        var output = console.ReadOutputString();
        Assert.Contains($"Source: {Path.Join("TestResults", "coverage-shards")}", output, StringComparison.Ordinal);
        Assert.Contains($"Output: {Path.Join("TestResults", "coverage-merged")}", output, StringComparison.Ordinal);
        Assert.Contains("Discovered 2 Cobertura shard(s).", output, StringComparison.Ordinal);
        Assert.Contains("integration", output, StringComparison.Ordinal);
        Assert.Contains("Next: appsurface coverage gate", output, StringComparison.Ordinal);
        Assert.DoesNotContain(repo.Path, output, StringComparison.Ordinal);
        var timings = File.ReadAllText(Path.Join(result.OutputDirectory, "timings.json"));
        Assert.Contains("\"command\": \"coverage merge\"", timings, StringComparison.Ordinal);
        Assert.Contains("\"sourceDirectory\": \"TestResults/coverage-shards\"", timings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeAsync_ShouldPrintTruncatedDiscoverySample()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        for (var index = 0; index < 6; index++)
        {
            repo.WriteFile($"shards/project-{index}/coverage.cobertura.xml", "<coverage />");
        }

        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();

        await workflow.MergeAsync(new CoverageMergeRequest("shards", "merged", Clean: true), console, CancellationToken.None);

        var output = console.ReadOutputString();
        Assert.Contains("Discovered 6 Cobertura shard(s).", output, StringComparison.Ordinal);
        Assert.Contains("... 1 more shard(s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeAsync_ShouldAllowSingleShard()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        repo.WriteFile("shards/coverage.cobertura.xml", "<coverage line-rate=\"0.75\" branch-rate=\"0.5\" />");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingReportGenerator { MergedCoverage = "<coverage line-rate=\"0.75\" branch-rate=\"0.5\" />" });
        using var console = new FakeInMemoryConsole();

        var result = await workflow.MergeAsync(new CoverageMergeRequest("shards", "merged", Clean: true), console, CancellationToken.None);

        Assert.Single(result.SelectedReports);
        var summary = File.ReadAllText(Path.Join(result.OutputDirectory, "summary.txt"));
        Assert.Contains("Line coverage: 75.00%", summary, StringComparison.Ordinal);
        Assert.Contains("Branch coverage: 50.00%", summary, StringComparison.Ordinal);
        Assert.Contains($"Cobertura: {Path.Join("merged", "coverage.cobertura.xml")}", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(repo.Path, summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeAsync_ShouldUseAbsoluteDisplayPathForOutputOutsideCurrentDirectory()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        using var outputRoot = TempDirectory.Create("appsurface-coverage-output-");
        repo.WriteFile("shards/coverage.cobertura.xml", "<coverage />");
        using var current = PushCurrentDirectory(repo.Path);
        var outputDirectory = Path.Join(outputRoot.Path, "merged");
        var workflow = CreateWorkflow(new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();

        var result = await workflow.MergeAsync(new CoverageMergeRequest("shards", outputDirectory, Clean: true), console, CancellationToken.None);

        Assert.Equal(outputDirectory, result.OutputDirectory);
        var output = console.ReadOutputString();
        Assert.Contains($"Output: {outputDirectory}", output, StringComparison.Ordinal);
        Assert.Contains($"Coverage artifacts: {outputDirectory}", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeAsync_ShouldCleanOwnedMergeArtifacts()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        repo.WriteFile("shards/coverage.cobertura.xml", "<coverage />");
        repo.WriteFile("merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        var oldCoverage = repo.WriteFile("merged/coverage.cobertura.xml", "old");
        var oldSummary = repo.WriteFile("merged/summary.txt", "old summary");
        var oldTimings = repo.WriteFile("merged/timings.json", "{}");
        var oldReportGeneratorSummary = repo.WriteFile("merged/reportgenerator-summary.txt", "old rg");
        var oldInput = repo.WriteFile("merged/reportgenerator-input/old/coverage.cobertura.xml", "stale");
        repo.WriteFile("merged/reportgenerator/old.txt", "old rg output");
        var retainedProjectArtifact = repo.WriteFile("merged/projects/old/coverage.cobertura.xml", "legacy run artifact");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();

        var result = await workflow.MergeAsync(new CoverageMergeRequest("shards", "merged", Clean: true), console, CancellationToken.None);

        Assert.EndsWith(Path.Join("merged", "coverage.cobertura.xml"), result.CoveragePath, StringComparison.Ordinal);
        Assert.NotEqual("old", File.ReadAllText(oldCoverage));
        Assert.NotEqual("old summary", File.ReadAllText(oldSummary));
        Assert.NotEqual("{}", File.ReadAllText(oldTimings));
        Assert.NotEqual("old rg", File.ReadAllText(oldReportGeneratorSummary));
        Assert.False(File.Exists(oldInput));
        Assert.False(File.Exists(Path.Join(repo.Path, "merged", "reportgenerator", "old.txt")));
        Assert.True(File.Exists(retainedProjectArtifact));
    }

    [Fact]
    public async Task MergeAsync_ShouldRejectMissingSourceAndEmptySource()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        Directory.CreateDirectory(Path.Join(repo.Path, "empty"));
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();

        var missingOption = await Assert.ThrowsAsync<CommandException>(
            () => workflow.MergeAsync(new CoverageMergeRequest(null, "merged", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV130", missingOption.Message, StringComparison.Ordinal);
        Assert.Contains("--source is required", missingOption.Message, StringComparison.Ordinal);

        var emptySource = await Assert.ThrowsAsync<CommandException>(
            () => workflow.MergeAsync(new CoverageMergeRequest("empty", "merged", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV131", emptySource.Message, StringComparison.Ordinal);
        Assert.Contains("No Cobertura shard files", emptySource.Message, StringComparison.Ordinal);

        var missingSource = await Assert.ThrowsAsync<CommandException>(
            () => workflow.MergeAsync(new CoverageMergeRequest("missing", "merged", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV130", missingSource.Message, StringComparison.Ordinal);
        Assert.Contains("source directory was not found", missingSource.Message, StringComparison.Ordinal);

        var invalidSource = await Assert.ThrowsAsync<CommandException>(
            () => workflow.MergeAsync(new CoverageMergeRequest("bad\0source", "merged", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV130", invalidSource.Message, StringComparison.Ordinal);
        Assert.Contains("source path is invalid", invalidSource.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeAsync_ShouldRejectMalformedInputAndMalformedMergedCoverage()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        repo.WriteFile("bad/coverage.cobertura.xml", "<not-coverage />");
        repo.WriteFile("good/coverage.cobertura.xml", "<coverage />");
        using var current = PushCurrentDirectory(repo.Path);
        using var console = new FakeInMemoryConsole();

        var badInput = await Assert.ThrowsAsync<CommandException>(
            () => CreateWorkflow(new RecordingReportGenerator()).MergeAsync(new CoverageMergeRequest("bad", "merged", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV132", badInput.Message, StringComparison.Ordinal);

        var badMerge = await Assert.ThrowsAsync<CommandException>(
            () => CreateWorkflow(new RecordingReportGenerator { MergedCoverage = "<not-coverage />" }).MergeAsync(new CoverageMergeRequest("good", "merged", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV135", badMerge.Message, StringComparison.Ordinal);
        Assert.Contains("Merged Cobertura file is malformed", badMerge.Message, StringComparison.Ordinal);

        var malformedMerge = await Assert.ThrowsAsync<CommandException>(
            () => CreateWorkflow(new RecordingReportGenerator { MergedCoverage = "<coverage" }).MergeAsync(new CoverageMergeRequest("good", "merged", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV135", malformedMerge.Message, StringComparison.Ordinal);
        Assert.Contains("malformed or unreadable", malformedMerge.Message, StringComparison.Ordinal);

        repo.WriteFile("malformed/coverage.cobertura.xml", "<coverage");
        var malformedInput = await Assert.ThrowsAsync<CommandException>(
            () => CreateWorkflow(new RecordingReportGenerator()).MergeAsync(new CoverageMergeRequest("malformed", "merged", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV132", malformedInput.Message, StringComparison.Ordinal);
        Assert.Contains("malformed or unreadable", malformedInput.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeAsync_ShouldReportReportGeneratorFailures()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        repo.WriteFile("shards/coverage.cobertura.xml", "<coverage />");
        using var current = PushCurrentDirectory(repo.Path);
        using var console = new FakeInMemoryConsole();

        var missingPackage = await Assert.ThrowsAsync<CommandException>(
            () => CreateWorkflow(new RecordingReportGenerator { ThrowMissingPackage = true }).MergeAsync(new CoverageMergeRequest("shards", "merged", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV134", missingPackage.Message, StringComparison.Ordinal);

        var mergeFailure = await Assert.ThrowsAsync<CommandException>(
            () => CreateWorkflow(new RecordingReportGenerator { ExitCode = 42, WriteMergedCoverage = false }).MergeAsync(new CoverageMergeRequest("shards", "merged", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV135", mergeFailure.Message, StringComparison.Ordinal);
        Assert.Contains("ReportGenerator exit code: 42", mergeFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeAsync_ShouldRejectUnsafeOutputPaths()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        var source = Directory.CreateDirectory(Path.Join(repo.Path, "shards")).FullName;
        repo.WriteFile("shards/coverage.cobertura.xml", "<coverage />");
        var outputFile = repo.WriteFile("coverage-output.txt", "not a directory");
        repo.WriteFile("populated/user-file.txt", "mine");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();

        await AssertUnsafeOutputAsync(workflow, console, source, outputFile, "points to a file");
        var blankOutput = Assert.Throws<CommandException>(
            () => CoverageMergeOutputGuard.Prepare(" ", source, clean: true));
        Assert.Contains("ASCOV136", blankOutput.Message, StringComparison.Ordinal);
        Assert.Contains("output path was blank", blankOutput.Message, StringComparison.Ordinal);
        await AssertUnsafeOutputAsync(workflow, console, source, ".", "current working directory");
        await AssertUnsafeOutputAsync(workflow, console, source, "populated", "not marked as AppSurface-owned");
        await AssertUnsafeOutputAsync(workflow, console, source, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "user home directory");
        await AssertOverlapAsync(workflow, console, source, source);
        await AssertOverlapAsync(workflow, console, source, Path.Join(source, "merged"));
        await AssertOverlapAsync(workflow, console, Path.Join(source, "nested"), source);
        await AssertSymlinkOverlapWhenSupportedAsync(workflow, console, source, Path.Join(repo.Path, "linked-output"));

        var invalidOutput = await Assert.ThrowsAsync<CommandException>(
            () => workflow.MergeAsync(new CoverageMergeRequest(source, "bad\0output", Clean: true), console, CancellationToken.None));
        Assert.Contains("ASCOV138", invalidOutput.Message, StringComparison.Ordinal);

        var invalidGuardPath = Assert.Throws<CommandException>(
            () => CoverageMergeOutputGuard.Prepare("merged", "bad\0source", clean: true));
        Assert.Contains("ASCOV138", invalidGuardPath.Message, StringComparison.Ordinal);

        var root = Path.GetPathRoot(repo.Path);
        if (!string.IsNullOrWhiteSpace(root))
        {
            await AssertUnsafeOutputAsync(workflow, console, source, root, "filesystem root");
        }
    }

    [Fact]
    public void Resolve_ShouldReportSourceReadFailures()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        var sourceFile = repo.WriteFile("not-a-directory", "not a directory");

        var exception = Assert.Throws<CommandException>(
            () => CoverageMergeSourceResolver.Resolve(sourceFile, Path.Join(repo.Path, "merged")));

        Assert.Contains("ASCOV130", exception.Message, StringComparison.Ordinal);
        Assert.Contains("could not be read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeAsync_ShouldReportStagingFailures()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        repo.WriteFile("shards/coverage.cobertura.xml", "<coverage />");
        repo.WriteFile("merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        repo.WriteFile("merged/reportgenerator-input", "file blocks staging directory");
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.MergeAsync(new CoverageMergeRequest("shards", "merged", Clean: true), console, CancellationToken.None));

        Assert.Contains("ASCOV137", exception.Message, StringComparison.Ordinal);
        Assert.Contains("staging failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsurePreservedSelectedShardCount_ShouldReportStagingCountMismatch()
    {
        CoverageMergeStaging.EnsurePreservedSelectedShardCount(selectedReportCount: 2, stagedReportCount: 2);

        var exception = Assert.Throws<CommandException>(
            () => CoverageMergeStaging.EnsurePreservedSelectedShardCount(selectedReportCount: 2, stagedReportCount: 1));

        Assert.Contains("ASCOV139", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Selected 2 shard(s) but staged 1.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeAsync_ShouldReportArtifactWriteFailures()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        repo.WriteFile("shards/coverage.cobertura.xml", "<coverage />");
        repo.WriteFile("merged/.appsurface-coverage-output", "AppSurface coverage output directory");
        Directory.CreateDirectory(Path.Join(repo.Path, "merged", "summary.txt"));
        using var current = PushCurrentDirectory(repo.Path);
        var workflow = CreateWorkflow(new RecordingReportGenerator());
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.MergeAsync(new CoverageMergeRequest("shards", "merged", Clean: true), console, CancellationToken.None));

        Assert.Contains("ASCOV137", exception.Message, StringComparison.Ordinal);
        Assert.Contains("artifacts could not be written", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldForwardPublicOptions()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-merge-");
        repo.WriteFile("shards/coverage.cobertura.xml", "<coverage />");
        using var current = PushCurrentDirectory(repo.Path);
        var reportGenerator = new RecordingReportGenerator();
        var command = new CoverageMergeCommand(CreateWorkflow(reportGenerator))
        {
            SourceDirectory = "shards",
            OutputDirectory = "custom-merged",
        };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console, CancellationToken.None);

        Assert.True(File.Exists(Path.Join(repo.Path, "custom-merged", "coverage.cobertura.xml")));
        Assert.Single(reportGenerator.CoverageFiles);
    }

    [Fact]
    public void Constructors_ShouldRejectMissingDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new CoverageMergeCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new CoverageMergeWorkflow(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoverageMergeWorkflow(new RecordingReportGenerator(), null!));
    }

    private static async Task AssertUnsafeOutputAsync(
        CoverageMergeWorkflow workflow,
        IConsole console,
        string sourceDirectory,
        string outputDirectory,
        string expectedCause)
    {
        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.MergeAsync(new CoverageMergeRequest(sourceDirectory, outputDirectory, Clean: true), console, CancellationToken.None));

        Assert.Contains("ASCOV136", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedCause, exception.Message, StringComparison.Ordinal);
    }

    private static async Task AssertOverlapAsync(
        CoverageMergeWorkflow workflow,
        IConsole console,
        string sourceDirectory,
        string outputDirectory)
    {
        Directory.CreateDirectory(sourceDirectory);
        var exception = await Assert.ThrowsAsync<CommandException>(
            () => workflow.MergeAsync(new CoverageMergeRequest(sourceDirectory, outputDirectory, Clean: true), console, CancellationToken.None));

        Assert.Contains("ASCOV133", exception.Message, StringComparison.Ordinal);
    }

    private static async Task AssertSymlinkOverlapWhenSupportedAsync(
        CoverageMergeWorkflow workflow,
        IConsole console,
        string sourceDirectory,
        string outputDirectory)
    {
        try
        {
            Directory.CreateSymbolicLink(outputDirectory, sourceDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        await AssertOverlapAsync(workflow, console, sourceDirectory, outputDirectory);
    }

    private static CoverageMergeWorkflow CreateWorkflow(ICoverageRunReportGenerator reportGenerator)
        => new(reportGenerator, TimeProvider.System);

    private static IDisposable PushCurrentDirectory(string path)
    {
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(path);
        return new DelegateDisposable(() => Directory.SetCurrentDirectory(previous));
    }

    private sealed class RecordingReportGenerator : ICoverageRunReportGenerator
    {
        public List<string> CoverageFiles { get; } = [];
        public int ExitCode { get; init; }
        public bool WriteMergedCoverage { get; init; } = true;
        public bool ThrowMissingPackage { get; init; }
        public string MergedCoverage { get; init; } = "<coverage lines-covered=\"8\" lines-valid=\"10\" branches-covered=\"2\" branches-valid=\"4\" />";

        public async Task<CoverageRunMergeResult> MergeAsync(
            IReadOnlyList<string> coverageFiles,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            if (ThrowMissingPackage)
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV114",
                    "ReportGenerator package dependency was not found.",
                    "Expected fake package.",
                    "Restore the package.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-diagnostics");
            }

            CoverageFiles.AddRange(coverageFiles);
            Directory.CreateDirectory(outputDirectory);
            var cobertura = Path.Join(outputDirectory, "Cobertura.xml");
            var summary = Path.Join(outputDirectory, "Summary.txt");
            if (WriteMergedCoverage)
            {
                await File.WriteAllTextAsync(cobertura, MergedCoverage, cancellationToken);
            }

            await File.WriteAllTextAsync(summary, "ReportGenerator summary", cancellationToken);
            return new CoverageRunMergeResult(ExitCode, cobertura, summary);
        }
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
