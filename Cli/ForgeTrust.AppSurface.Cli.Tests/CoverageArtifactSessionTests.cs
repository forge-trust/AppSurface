using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactSessionTests
{
    [Fact]
    public async Task BeginAsync_WithCleanEnabled_SnapshotsRegisteredInventoryBeforeCleanup()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var artifact = Path.Join(output, "coverage.xml");
        var bytes = "previous run bytes"u8.ToArray();
        await RegisterRunAsync(output, repository, artifact, bytes, "failed");
        string priorRunId;
        using (var initialStore = CoverageArtifactStore.Open())
            priorRunId = initialStore.GetCurrentRun(output)!.ManifestBase.RunId;

        using (await BeginAsync(output, repository, clean: true))
        using (var store = CoverageArtifactStore.Open())
        {
            Assert.Equal(1, store.GetStatus().ArchivedRuns);
            Assert.Equal(priorRunId, store.GetCurrentRun(output)!.ManifestBase.RunId);

            var restored = Path.Join(fixture.Path, "restored-prior-run");
            await store.RestoreAsync(priorRunId, restored, CancellationToken.None);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Join(restored, "coverage.xml")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(artifact));
        }
    }

    [Fact]
    public async Task BeginAsync_WithoutClean_PreservesCurrentRegistrationWithoutArchiving()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var artifact = Path.Join(output, "coverage.xml");
        await RegisterRunAsync(output, repository, artifact, "current"u8.ToArray(), "success");
        string priorRunId;
        using (var initialStore = CoverageArtifactStore.Open())
            priorRunId = initialStore.GetCurrentRun(output)!.ManifestBase.RunId;

        using (await BeginAsync(output, repository, clean: false))
        using (var store = CoverageArtifactStore.Open())
        {
            Assert.Equal(0, store.GetStatus().ArchivedRuns);
            Assert.Equal(priorRunId, store.GetCurrentRun(output)!.ManifestBase.RunId);
            Assert.Equal("current", await File.ReadAllTextAsync(artifact));
        }
    }

    [Fact]
    public async Task CompleteAsync_RegistersProvenanceAndOnlyCurrentProducedFiles()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var retained = Path.Join(output, "retained-stale.xml");
        await File.WriteAllTextAsync(retained, "unmanaged prior artifact");
        var produced = Path.Join(output, "coverage.xml");
        await File.WriteAllTextAsync(produced, "current artifact");
        var freshTree = Directory.CreateDirectory(Path.Join(output, "raw", "invocation-1")).FullName;
        var rawProduced = Path.Join(freshTree, "coverage.dat");
        await File.WriteAllTextAsync(rawProduced, "current raw data");
        var provenance = new CoverageArtifactProvenance("revision-fingerprint", "clean-fingerprint", "config-fingerprint");

        using (var session = await BeginAsync(output, repository, clean: false))
        {
            session.Inventory.RecordFile(produced);
            session.Inventory.RecordFreshTree(freshTree);
            await session.CompleteAsync("failed", provenance);
        }

        using var store = CoverageArtifactStore.Open();
        var current = store.GetCurrentRun(output)!;
        Assert.Equal("failed", current.ManifestBase.Outcome);
        Assert.Equal(provenance, current.ManifestBase.Provenance);
        Assert.Equal(new[] { "coverage.xml", "raw/invocation-1/coverage.dat" },
            current.Inventory.Where(entry => entry.Path != CoverageArtifactSession.SidecarName)
                .Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(current.Inventory, entry => entry.Path == "retained-stale.xml");

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(CoverageRunOutputLease.NormalizePlatformPath(output), current.OutputPath);
            Assert.Equal(CoverageRunOutputLease.NormalizePlatformPath("/var/tmp/coverage-output"),
                CoverageRunOutputLease.NormalizePlatformPath("/private/var/tmp/coverage-output"));
        }

        var outsideInventory = new CoverageArtifactInventory(output, TimeProvider.System);
        outsideInventory.RecordFile(Path.Join(repository, "outside.xml"));
        Assert.Throws<InvalidDataException>(() => outsideInventory.Capture(CancellationToken.None));
    }

    [Fact]
    public async Task UpdateRegisteredInventoryAsync_AddsGateReportsAndPreservesRunMetadata()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var artifact = Path.Join(output, "coverage.xml");
        await File.WriteAllTextAsync(artifact, "coverage");
        var provenance = new CoverageArtifactProvenance("revision", "dirty", "configuration");
        using (var session = await BeginAsync(output, repository, clean: false))
        {
            session.Inventory.RecordFile(artifact);
            await session.CompleteAsync("failed", provenance);
        }
        string priorRunId;
        using (var initialStore = CoverageArtifactStore.Open())
            priorRunId = initialStore.GetCurrentRun(output)!.ManifestBase.RunId;
        var gateReport = Path.Join(output, "coverage-gate.json");
        await File.WriteAllTextAsync(gateReport, "gate report");
        using var outputLease = CoverageArtifactOperationLease.Acquire(output, exclusive: true);

        await CoverageArtifactSession.UpdateRegisteredInventoryAsync(output, [gateReport], TextWriter.Null, outputLease);

        using var store = CoverageArtifactStore.Open();
        var registered = store.GetCurrentRun(output)!;
        Assert.Equal(priorRunId, registered.ManifestBase.RunId);
        Assert.Equal("failed", registered.ManifestBase.Outcome);
        Assert.Equal(provenance, registered.ManifestBase.Provenance);
        Assert.Contains(registered.Inventory, entry => entry.Path == "coverage-gate.json");
        Assert.Contains(registered.Inventory, entry => entry.Path == CoverageArtifactSession.SidecarName);

        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(output, createMissing: false, requirePrivate: false)!;
        using var sidecarStream = root.OpenArtifactFile(CoverageArtifactSession.SidecarName, createNew: false, requirePrivate: false);
        var sidecar = CoverageArtifactSession.ReadCurrent(sidecarStream);
        Assert.Equal(provenance, sidecar.ManifestBase.Provenance);
        Assert.Equal("failed", sidecar.ManifestBase.Outcome);
        Assert.Contains(sidecar.Inventory, entry => entry.Path == "coverage-gate.json");
        Assert.DoesNotContain(sidecar.Inventory, entry => entry.Path == CoverageArtifactSession.SidecarName);
    }

    [Fact]
    public async Task BeginAndCompleteAsync_InvalidStoreWarnWithoutReplacingPrimaryResult()
    {
        using var fixture = TestDirectory.Create();
        using var invalidHome = TestDirectory.Create();
        await File.WriteAllTextAsync(Path.Join(invalidHome.Path, "unrecognized.txt"), "preserve");
        using var configuredHome = ConfigureStore(invalidHome.Path, privateDomain: false);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        using var errors = new StringWriter();
        var writers = CoverageTextWriters.Create(TextWriter.Null, errors);

        using var session = await CoverageArtifactSession.BeginAsync(output, repository, clean: true,
            writers, TimeProvider.System, CancellationToken.None);
        await session.CompleteAsync("failed", new CoverageArtifactProvenance(null, "unknown", "config"));

        Assert.Contains("ASCOV201", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("storage", errors.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(output, CoverageArtifactSession.SidecarName)));
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Join(invalidHome.Path, "unrecognized.txt")));
    }

    [Fact]
    public async Task BeginAsync_ReconstructsMissingRegistrationFromMatchingProducerSidecar()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var artifact = Path.Join(output, "coverage.xml");
        await RegisterRunAsync(output, repository, artifact, "recoverable"u8.ToArray(), "success");
        using (var store = CoverageArtifactStore.Open())
        {
            var path = Path.Join(store.RootPath, "registry.json");
            var registry = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            registry["Registrations"] = new System.Text.Json.Nodes.JsonArray();
            await File.WriteAllTextAsync(path, registry.ToJsonString());
        }

        using (await BeginAsync(output, repository, clean: true))
        using (var store = CoverageArtifactStore.Open())
        {
            Assert.Equal(1, store.GetStatus().RegisteredRuns);
            Assert.Equal(1, store.GetStatus().ArchivedRuns);
            Assert.Equal("recoverable", await File.ReadAllTextAsync(artifact));
        }
    }

    [Fact]
    public async Task BeginAsync_CorruptSidecarWarnsWithoutPublishingHistoryOrReplacingOutput()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var artifact = Path.Join(output, "coverage.xml");
        await RegisterRunAsync(output, repository, artifact, "preserve"u8.ToArray(), "success");
        await File.WriteAllTextAsync(Path.Join(output, CoverageArtifactSession.SidecarName), "{broken");
        using var errors = new StringWriter();

        using var session = await CoverageArtifactSession.BeginAsync(output, repository, true,
            CoverageTextWriters.Create(TextWriter.Null, errors), TimeProvider.System, CancellationToken.None);

        Assert.Contains("ASCOV200", errors.ToString(), StringComparison.Ordinal);
        using var store = CoverageArtifactStore.Open();
        Assert.Equal(0, store.GetStatus().ArchivedRuns);
        Assert.Equal("preserve", await File.ReadAllTextAsync(artifact));
    }

    [Fact]
    public async Task BeginAsync_ConflictingWriterFailsBeforeCleanupAndLeavesFirstSessionUsable()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var artifact = Path.Join(output, "coverage.xml");
        await File.WriteAllTextAsync(artifact, "current");
        using var first = await BeginAsync(output, repository, false);

        var failure = await Assert.ThrowsAsync<CoverageExecutionException>(() => BeginAsync(output, repository, true));

        Assert.Equal("ASCOV202", failure.Code);
        first.VerifyBinding();
        Assert.Equal("current", await File.ReadAllTextAsync(artifact));
    }

    [Fact]
    public async Task CompleteAsync_IncompleteWriterInventoryWarnsAndNeverPublishesFalseOwnership()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        using var errors = new StringWriter();
        using var session = await CoverageArtifactSession.BeginAsync(output, repository, false,
            CoverageTextWriters.Create(TextWriter.Null, errors), TimeProvider.System, CancellationToken.None);
        session.Inventory.RecordFile(Path.Join(repository, "outside.xml"));

        await session.CompleteAsync("failed", new(null, "unknown", "configuration"));

        Assert.Contains("ASCOV201 inventory", errors.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(output, CoverageArtifactSession.SidecarName)));
        using var store = CoverageArtifactStore.Open();
        Assert.Equal(0, store.GetStatus().RegisteredRuns);
    }

    private static async Task<CoverageArtifactCurrentRun> RegisterRunAsync(
        string output, string repository, string artifactPath, byte[] contents, string outcome)
    {
        await File.WriteAllBytesAsync(artifactPath, contents);
        using var session = await BeginAsync(output, repository, clean: false);
        session.Inventory.RecordFile(artifactPath);
        await session.CompleteAsync(outcome,
            new CoverageArtifactProvenance("seed-revision", "seed-state", "seed-configuration"));
        using var store = CoverageArtifactStore.Open();
        return store.GetCurrentRun(output)!;
    }

    private static Task<CoverageArtifactSession> BeginAsync(string output, string repository, bool clean) =>
        CoverageArtifactSession.BeginAsync(output, repository, clean,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), TimeProvider.System, CancellationToken.None);

    private static IDisposable ConfigureStore(string path, bool privateDomain = true)
    {
        const string variable = "APPSURFACE_ARTIFACT_HOME";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, privateDomain ? Path.Join(path, "private-store") : path);
        return new RestoreEnvironment(variable, previous);
    }

    private sealed class RestoreEnvironment(string name, string? value) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(name, value);
    }
}
