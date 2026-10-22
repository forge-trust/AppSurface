using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactSessionRecoveryEdgesTests
{
    [Fact]
    public async Task MissingRepositoryReleasesAcquiredOutputLeaseBeforePropagatingFailure()
    {
        using var temp = TestDirectory.Create();
        var output = temp.CreateDirectory("output");
        await Assert.ThrowsAsync<IOException>(() => CoverageArtifactSession.BeginAsync(output,
            Path.Join(temp.Path, "missing"), false, CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null),
            TimeProvider.System, CancellationToken.None));
        using var retry = CoverageArtifactOperationLease.Acquire(output, true);
        retry.VerifyBinding();
    }

    [Fact]
    public async Task ReplacedOutputCannotPublishARegistrationForDifferentBytes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        using var configured = ConfigureStore(Path.Join(temp.Path, "store"));
        var output = temp.CreateDirectory("repo/output");
        var repository = Path.Join(temp.Path, "repo");
        File.WriteAllText(Path.Join(output, "coverage.xml"), "original");
        using var errors = new StringWriter();
        using var session = await CoverageArtifactSession.BeginAsync(output, repository, false,
            CoverageTextWriters.Create(TextWriter.Null, errors), TimeProvider.System, CancellationToken.None);
        session.Inventory.RecordFile(Path.Join(output, "coverage.xml"));
        Directory.Move(output, output + "-original");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Join(output, "coverage.xml"), "replacement");
        await session.CompleteAsync("failed", new("revision", "unknown", "policy"));
        Assert.Contains("ASCOV201 register", errors.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(output, CoverageArtifactSession.SidecarName)));
        using var store = CoverageArtifactStore.Open();
        Assert.Equal(0, store.GetStatus().RegisteredRuns);
        Assert.Equal("replacement", File.ReadAllText(Path.Join(output, "coverage.xml")));
        Assert.Equal("original", File.ReadAllText(Path.Join(output + "-original", "coverage.xml")));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("output")]
    [InlineData("inventory")]
    public async Task GateSidecarMetadataMismatchPreservesRegisteredProvenance(string fault)
    {
        using var temp = TestDirectory.Create();
        using var configured = ConfigureStore(Path.Join(temp.Path, "store"));
        var output = temp.CreateDirectory("repo/output");
        var repository = Path.Join(temp.Path, "repo");
        File.WriteAllText(Path.Join(output, "coverage.xml"), "producer");
        using (var session = await CoverageArtifactSession.BeginAsync(output, repository, false,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), TimeProvider.System, CancellationToken.None))
        {
            session.Inventory.RecordFile(Path.Join(output, "coverage.xml"));
            await session.CompleteAsync("failed", new("revision", "unknown", "policy"));
        }
        CoverageArtifactCurrentRun prior;
        using (var store = CoverageArtifactStore.Open()) prior = store.GetCurrentRun(output)!;
        var sidecarPath = Path.Join(output, CoverageArtifactSession.SidecarName);
        var sidecar = JsonNode.Parse(File.ReadAllText(sidecarPath))!;
        if (fault == "run") sidecar["manifestBase"]!["runId"] = Guid.NewGuid().ToString("N");
        if (fault == "output") sidecar["manifestBase"]!["outputIdentity"] = "changed";
        if (fault == "inventory") sidecar["inventory"]![0]!["length"] = 100;
        var corrupt = sidecar.ToJsonString();
        File.WriteAllText(sidecarPath, corrupt);
        using var errors = new StringWriter();
        await CoverageArtifactSession.UpdateRegisteredInventoryAsync(output, [], errors);
        Assert.Contains("ASCOV201 gate-inventory", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(corrupt, File.ReadAllText(sidecarPath));
        using var final = CoverageArtifactStore.Open();
        Assert.Equal(prior.ManifestBase.RunId, final.GetCurrentRun(output)!.ManifestBase.RunId);
        Assert.Equal(prior.Inventory.ToArray(), final.GetCurrentRun(output)!.Inventory.ToArray());
    }

    private static IDisposable ConfigureStore(string root)
    {
        var before = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", root);
        return new RestoreEnvironment(before);
    }

    private sealed class RestoreEnvironment(string? before) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", before);
    }
}
