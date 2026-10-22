using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactCallerRemainingTests
{
    private const string Coverage = "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />";

    [Theory]
    [InlineData("different-length")]
    [InlineData("different-hash")]
    [InlineData("missing-raw")]
    [InlineData("missing-canonical")]
    [InlineData("missing-raw-parent")]
    public async Task FailedDuplicateVerificationPreservesPrimaryPublicationAndReportsRetainedRaw(string change)
    {
        using var temp = TestDirectory.Create();
        var output = temp.CreateDirectory("project");
        var raw = temp.CreateDirectory("project/raw");
        var input = Path.Join(raw, "coverage.cobertura.xml");
        var canonical = Path.Join(output, "coverage.cobertura.xml");
        File.WriteAllText(input, Coverage);
        var invocation = new CoverageRunDriverInvocation(CoverageRunDriver.Collector, output, raw, []);
        var normalized = await CoverageRunDriverStrategy.NormalizeDetailedAsync(invocation, default,
            commitGate: publish =>
            {
                publish();
                switch (change)
                {
                    case "different-length": File.WriteAllText(canonical, Coverage + " "); break;
                    case "different-hash": File.WriteAllText(canonical, Coverage.Replace("covered=\"1\"", "covered=\"0\"", StringComparison.Ordinal)); break;
                    case "missing-raw": File.Delete(input); break;
                    case "missing-canonical": File.Delete(canonical); break;
                    case "missing-raw-parent": Directory.Delete(raw, true); break;
                }
            }, removeRedundantRaw: true);
        Assert.Equal("produced", normalized.Status);
        Assert.Null(normalized.Cause);
        Assert.Contains("ASCOV123", normalized.CleanupDiagnostic, StringComparison.Ordinal);
        if (change is "different-length" or "different-hash" or "missing-canonical")
            Assert.Equal(Coverage, File.ReadAllText(input));
        if (change is "missing-raw" or "missing-raw-parent")
            Assert.Equal(Coverage, File.ReadAllText(canonical));
    }

    [Fact]
    public async Task InvalidRunRetentionFailsBeforeAnyChildOrOutputMutation()
    {
        using var temp = TestDirectory.Create();
        var runner = new RejectingRunner();
        var workflow = new CoverageRunWorkflow(runner, new FaultReportGenerator("unexpected"), TimeProvider.System);
        var request = new CoverageRunCommand(workflow).CreateRequest() with
        { ArtifactRetention = "invalid", OutputDirectory = Path.Join(temp.Path, "output") };
        using var console = new FakeInMemoryConsole();
        var error = await Assert.ThrowsAsync<CoverageExecutionException>(() => workflow.RunAsync(request, console, default));
        Assert.Contains("ASCOV208", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task InvalidMergeRetentionFailsAtCommandBeforeDiscovery()
    {
        using var temp = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        var command = new CoverageMergeCommand(new CoverageMergeWorkflow(new FaultReportGenerator("unexpected"), TimeProvider.System))
        { ArtifactRetention = "invalid", SourceDirectory = Path.Join(temp.Path, "missing"), OutputDirectory = Path.Join(temp.Path, "output") };
        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () => await command.ExecuteAsync(console, default));
        Assert.Contains("ASCOV208", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData("cancelled", "interrupted")]
    [InlineData("io", "failed")]
    public async Task MergeFailureKeepsItsPrimaryOutcomeAndRecordsActualRunState(string fault, string outcome)
    {
        using var temp = TestDirectory.Create();
        var storeRoot = Path.Join(temp.Path, "store");
        using var configured = new StoreEnvironment(storeRoot);
        var shards = temp.CreateDirectory("shards");
        File.WriteAllText(Path.Join(shards, "coverage.cobertura.xml"), Coverage);
        var output = Path.Join(temp.Path, "output");
        var workflow = new CoverageMergeWorkflow(new FaultReportGenerator(fault), TimeProvider.System);
        using var console = new FakeInMemoryConsole();
        if (fault == "cancelled")
            await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.MergeAsync(new(shards, output, true), console, default));
        else
        {
            var error = await Assert.ThrowsAsync<CoverageExecutionException>(() => workflow.MergeAsync(new(shards, output, true), console, default));
            Assert.Contains("ASCOV137", error.Message, StringComparison.Ordinal);
        }
        using var store = CoverageArtifactStore.Open();
        Assert.Equal(outcome, store.GetCurrentRun(output)!.ManifestBase.Outcome);
        Assert.Equal(Coverage, File.ReadAllText(Path.Join(shards, "coverage.cobertura.xml")));
        Assert.Equal(fault == "io", File.Exists(Path.Join(output, "coverage.cobertura.xml")));
    }

    [Fact]
    public async Task CleanupPreviewReportsUnknownHistoryWhenOptionalStoreIsInvalid()
    {
        using var temp = TestDirectory.Create();
        var root = Path.Join(temp.Path, "store");
        using (CoverageArtifactStore.Open(root)) { }
        File.WriteAllText(Path.Join(root, "registry.json"), "invalid metadata");
        using var configured = new StoreEnvironment(root);
        var output = temp.CreateDirectory("output");
        CoverageRunOutputGuard.Prepare(output, temp.Path, [], false);
        var report = Path.Join(output, "coverage.cobertura.xml");
        File.WriteAllText(report, Coverage);
        using var console = new FakeInMemoryConsole();
        await new CoverageCleanCommand(new TestResultsCleanupWorkflow()) { OutputDirectory = output }.ExecuteAsync(console, default);
        Assert.Contains("Historical archive state is unknown", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Equal(Coverage, File.ReadAllText(report));
        Assert.Equal("invalid metadata", File.ReadAllText(Path.Join(root, "registry.json")));
    }

    private sealed class FaultReportGenerator(string fault) : ICoverageRunReportGenerator
    {
        public async Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> files, string outputDirectory, CancellationToken token)
        {
            if (fault == "cancelled") throw new OperationCanceledException(token);
            if (fault != "io") throw new InvalidOperationException("No report generation should occur");
            var cobertura = Path.Join(outputDirectory, "Cobertura.xml");
            var summary = Path.Join(outputDirectory, "Summary.txt");
            await File.WriteAllTextAsync(cobertura, Coverage, token);
            await File.WriteAllTextAsync(summary, "summary", token);
            Directory.CreateDirectory(Path.Join(Path.GetDirectoryName(outputDirectory)!, "summary.txt"));
            return new CoverageRunMergeResult(0, cobertura, summary);
        }
    }

    private sealed class RejectingRunner : ICoverageRunProcessRunner
    {
        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken token)
            => throw new InvalidOperationException("No child should launch for invalid retention");
    }

    private sealed class StoreEnvironment : IDisposable
    {
        private readonly string? _before = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        internal StoreEnvironment(string root) => Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", root);
        public void Dispose() => Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _before);
    }
}
