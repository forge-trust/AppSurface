using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactSessionRecoveryEdgesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidInventoryCannotPublishCallerFilesEvenWhenDiagnosticWritingFails(bool failDiagnostics)
    {
        using var temp = TestDirectory.Create();
        using var configured = ConfigureStore(Path.Join(temp.Path, "store"));
        var repository = temp.CreateDirectory("repo");
        var output = temp.CreateDirectory("repo/output");
        var callerFile = Path.Join(repository, "caller-owned.xml");
        File.WriteAllText(callerFile, "preserve caller bytes");
        using StringWriter errors = failDiagnostics ? new FailingDiagnosticWriter() : new StringWriter();
        using var session = await CoverageArtifactSession.BeginAsync(output, repository, false,
            CoverageTextWriters.Create(TextWriter.Null, errors), TimeProvider.System, CancellationToken.None);
        session.Inventory.RecordFile(callerFile);

        await session.CompleteAsync("failed", new("revision", "unknown", "configuration"));

        Assert.Equal("preserve caller bytes", File.ReadAllText(callerFile));
        Assert.False(File.Exists(Path.Join(output, CoverageArtifactSession.SidecarName)));
        using var store = CoverageArtifactStore.Open();
        Assert.Equal(0, store.GetStatus().RegisteredRuns);
        if (errors is FailingDiagnosticWriter failedWriter) Assert.True(failedWriter.Attempts > 0);
        else Assert.Contains("ASCOV201 inventory", errors.ToString(), StringComparison.Ordinal);
    }

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
    [InlineData("run", false)]
    [InlineData("output", false)]
    [InlineData("inventory", false)]
    [InlineData("run", true)]
    [InlineData("output", true)]
    [InlineData("inventory", true)]
    public async Task GateSidecarMetadataMismatchPreservesRegisteredProvenance(string fault, bool failDiagnostics)
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
        // Admit the actual envelope bytes so this checks semantic disagreement after hash verification.
        var sidecarBytes = File.ReadAllBytes(sidecarPath);
        prior = prior with
        {
            Inventory = prior.Inventory.Select(entry => entry.Path == CoverageArtifactSession.SidecarName
                ? entry with { Length = sidecarBytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(sidecarBytes)).ToLowerInvariant() }
                : entry).ToArray()
        };
        using (var registration = CoverageArtifactStore.Open())
            await registration.RegisterAsync(prior, CancellationToken.None);
        using StringWriter errors = failDiagnostics ? new FailingDiagnosticWriter() : new StringWriter();
        using var outputLease = CoverageArtifactOperationLease.Acquire(output, exclusive: true);
        await CoverageArtifactSession.UpdateRegisteredInventoryAsync(output, [], errors, outputLease);
        if (errors is FailingDiagnosticWriter failedWriter)
            Assert.Equal(1, failedWriter.Attempts);
        else
            Assert.Contains("ASCOV201 gate-inventory", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(corrupt, File.ReadAllText(sidecarPath));
        using var final = CoverageArtifactStore.Open();
        Assert.Equal(prior.ManifestBase.RunId, final.GetCurrentRun(output)!.ManifestBase.RunId);
        Assert.Equal(prior.Inventory.ToArray(), final.GetCurrentRun(output)!.Inventory.ToArray());
    }

    private sealed class FailingDiagnosticWriter : StringWriter
    {
        internal int Attempts { get; private set; }

        public override Task WriteLineAsync(string? value)
        {
            Attempts++;
            return Task.FromException(new IOException("Diagnostic output is unavailable."));
        }
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
