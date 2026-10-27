using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactNestedGateTests
{
    [Fact]
    public async Task NestedGateReusesTheRunLeaseAndExtendsTheSameRunWithOptionalPatchReports()
    {
        using var temp = TestDirectory.Create();
        var previousHome = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Path.Join(temp.Path, "store"));
        try
        {
            var repository = temp.CreateDirectory("repo");
            var output = temp.CreateDirectory("repo/TestResults");
            var coverage = Path.Join(output, "coverage.xml");
            File.WriteAllText(coverage, "current coverage");
            using var session = await CoverageArtifactSession.BeginAsync(output, repository, false,
                CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), TimeProvider.System, CancellationToken.None);
            session.Inventory.RecordFile(coverage);
            await session.CompleteAsync("failed", new("revision", "dirty", "configuration"));
            using var store = CoverageArtifactStore.Open();
            var runId = store.GetCurrentRun(output)!.ManifestBase.RunId;
            var request = new CoverageGateRequest(coverage, output, 95, 85) { ArtifactSession = session };
            using (var nested = await CoverageArtifactGateContext.AcquireAsync(request, TextWriter.Null, CancellationToken.None))
            {
                var json = Path.Join(output, "coverage-gate.json");
                var markdown = Path.Join(output, "coverage-gate.md");
                var patch = Path.Join(output, "coverage-patch-targets.md");
                File.WriteAllText(json, "json");
                File.WriteAllText(markdown, "markdown");
                File.WriteAllText(patch, "patch markdown");
                var result = new CoverageGateResult(coverage, new(1, 1, 100), new(1, 1, 100), 95, 85,
                    true, json, markdown, PatchTargetsMarkdownPath: patch);
                await nested.RecordReportsAsync(request, result, CancellationToken.None);
            }
            var current = store.GetCurrentRun(output)!;
            Assert.Equal(runId, current.ManifestBase.RunId);
            Assert.Equal("failed", current.ManifestBase.Outcome);
            Assert.Contains(current.Inventory, file => file.Path == "coverage-patch-targets.md");
            Assert.Contains(current.Inventory, file => file.Path == "coverage-gate.json");
            Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(output, true));
            session.Dispose();
            using var replacement = CoverageArtifactOperationLease.Acquire(output, true);
        }
        finally { Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", previousHome); }
    }

    [Fact]
    public async Task PreCancelledGateDoesNotCreateItsOutputOrRecordReports()
    {
        using var temp = TestDirectory.Create();
        var output = Path.Join(temp.Path, "reports");
        var request = new CoverageGateRequest(Path.Join(temp.Path, "coverage.xml"), output, 95, 85);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CoverageArtifactGateContext.AcquireAsync(request, TextWriter.Null, cancelled.Token));
        Assert.False(Directory.Exists(output));
        using var context = await CoverageArtifactGateContext.AcquireAsync(request, TextWriter.Null, CancellationToken.None);
        var result = new CoverageGateResult(request.CoveragePath, new(0, 0, 0), new(0, 0, 0), 95, 85,
            false, Path.Join(output, "coverage-gate.json"), Path.Join(output, "coverage-gate.md"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.RecordReportsAsync(request, result, cancelled.Token));
        Assert.DoesNotContain(Directory.GetFiles(output), file => !file.EndsWith(".lock", StringComparison.Ordinal));
    }
}
