using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreRemainingEdgesTests
{
    [Fact]
    public void GetCurrentRun_ReadOnlyStoreForAbsentDomainReturnsNullWithoutCreatingPaths()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun("absent-domain-query");
        var root = Path.Join(fixture.TempPath, "absent-domain", "store");
        using var store = CoverageArtifactStore.OpenReadOnly(root);

        Assert.Null(store.GetCurrentRun(run.OutputPath));
        Assert.False(Directory.Exists(root));
        Assert.False(Directory.Exists(Path.GetDirectoryName(root)!));
    }

    [Fact]
    public async Task GetCurrentRun_ReadOnlyStoreMatchesPhysicalOutputAndReturnsNullForUnregisteredOutput()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun("registered");
        var unregistered = fixture.CreateRun("unregistered");
        using (var writer = fixture.OpenStore())
        {
            await writer.RegisterAsync(run, CancellationToken.None);
        }

        var before = File.ReadAllBytes(fixture.RegistryPath);
        using var reader = CoverageArtifactStore.OpenReadOnly(fixture.Root);

        var current = reader.GetCurrentRun(run.OutputPath);

        Assert.NotNull(current);
        Assert.Equal(run.ManifestBase.RunId, current.ManifestBase.RunId);
        Assert.Null(reader.GetCurrentRun(unregistered.OutputPath));
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
    }

    [Fact]
    public async Task Snapshot_UnregisteredArchiveFileCollisionPreservesFileAndReleasesReservation()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("archive-collision");
        await store.RegisterAsync(run, CancellationToken.None);
        var archivePath = fixture.ArchivePath(run.ManifestBase.RunId);
        fixture.WritePrivateFile(archivePath, "operator evidence");
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);

        var error = await Assert.ThrowsAsync<IOException>(() => store.SnapshotAsync(
            run, pin: false, automatic: false, CancellationToken.None));

        Assert.Equal("An unregistered archive occupies the requested run identifier; preserve it for inspection.", error.Message);
        Assert.Equal("operator evidence", File.ReadAllText(archivePath));
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        Assert.Equal(1, store.GetStatus().CurrentRuns);
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Empty(Directory.EnumerateDirectories(Path.Join(fixture.Root, "staging"), "run-*"));
    }

    [Fact]
    public async Task Pin_IncompleteArchiveManifestPreservesArchiveAndRegistry()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("incomplete-pin");
        await fixture.RegisterAndSnapshotAsync(store, run);
        var manifestPath = Path.Join(fixture.ArchivePath(run.ManifestBase.RunId), "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["complete"] = false;
        fixture.WritePrivateFile(manifestPath, manifest.ToJsonString());
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.PinAsync(run.ManifestBase.RunId, pin: true, CancellationToken.None));

        Assert.Equal("Archive manifest is unsupported or incomplete.", error.Message);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Equal(0, store.GetStatus().PinnedBytes);
    }

    [Fact]
    public async Task Pin_MismatchedManifestRunIdPreservesArchiveAndRegistry()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("mismatched-pin");
        await fixture.RegisterAndSnapshotAsync(store, run);
        var manifestPath = Path.Join(fixture.ArchivePath(run.ManifestBase.RunId), "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["runId"] = Guid.NewGuid().ToString("N");
        fixture.WritePrivateFile(manifestPath, manifest.ToJsonString());
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.PinAsync(run.ManifestBase.RunId, pin: true, CancellationToken.None));

        Assert.Equal("The archive cannot be pinned because its manifest is incomplete.", error.Message);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Equal(0, store.GetStatus().PinnedBytes);
    }

    [Fact]
    public async Task Restore_UnregisteredArchiveDoesNotCreateDestinationOrStage()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("unregistered-restore");
        await fixture.RegisterAndSnapshotAsync(store, run);
        var registry = fixture.ReadRegistry();
        registry["Registrations"]!.AsArray().Single()!["Archived"] = false;
        fixture.WriteRegistry(registry);
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var destination = Path.Join(fixture.RepositoryPath, "restored-output");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.RestoreAsync(run.ManifestBase.RunId, destination, CancellationToken.None));

        Assert.Equal("The archive is not registered as complete.", error.Message);
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(fixture.RepositoryPath, "restore-*"));
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.True(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
    }

    [Fact]
    public async Task Restore_MismatchedManifestRunIdPreservesArchiveAndDoesNotPublishOutput()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("incomplete-restore");
        await fixture.RegisterAndSnapshotAsync(store, run);
        var manifestPath = Path.Join(fixture.ArchivePath(run.ManifestBase.RunId), "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["runId"] = Guid.NewGuid().ToString("N");
        fixture.WritePrivateFile(manifestPath, manifest.ToJsonString());
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var destination = Path.Join(fixture.RepositoryPath, "incomplete-restore-output");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.RestoreAsync(run.ManifestBase.RunId, destination, CancellationToken.None));

        Assert.Equal("The archive is incomplete or mismatched.", error.Message);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(fixture.RepositoryPath, "restore-*"));
    }

    [Theory]
    [InlineData("negative-current-bytes")]
    [InlineData("unknown-outcome")]
    [InlineData("null-version")]
    [InlineData("null-output-identity")]
    public async Task Open_InvalidPersistedRegistrationFactFailsClosedWithoutRepair(string fact)
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun("invalid-registration-" + fact);
        using (var store = fixture.OpenStore()) await store.RegisterAsync(run, CancellationToken.None);
        var registry = fixture.ReadRegistry();
        var registration = registry["Registrations"]!.AsArray().Single()!.AsObject();
        switch (fact)
        {
            case "negative-current-bytes": registration["CurrentBytes"] = -1; break;
            case "unknown-outcome": registration["Outcome"] = "unknown"; break;
            case "null-version": registration["Version"] = null; break;
            case "null-output-identity": registration["OutputIdentity"] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(fact));
        }
        fixture.WriteRegistry(registry);
        var before = File.ReadAllBytes(fixture.RegistryPath);

        var error = Assert.Throws<InvalidDataException>(() => fixture.OpenStore().Dispose());

        Assert.Equal("The artifact registry contains invalid registration facts.", error.Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
    }

    [Fact]
    public async Task RegisterAsync_InventoryLengthSumOverflowDoesNotPersistRegistration()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("inventory-overflow") with
        {
            Inventory =
            [
                new("coverage.xml", long.MaxValue, new string('a', 64)),
                new("other.xml", 1, new string('b', 64))
            ]
        };
        var before = File.ReadAllBytes(fixture.RegistryPath);

        await Assert.ThrowsAsync<OverflowException>(() => store.RegisterAsync(run, CancellationToken.None));

        Assert.Equal(0, store.GetStatus().RegisteredRuns);
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
    }

    [Fact]
    public async Task GetStatus_CurrentByteAggregateOverflowFailsWithoutChangingRegistry()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        await store.RegisterAsync(fixture.CreateRun("sum-overflow-one"), CancellationToken.None);
        await store.RegisterAsync(fixture.CreateRun("sum-overflow-two"), CancellationToken.None);
        var registry = fixture.ReadRegistry();
        var registrations = registry["Registrations"]!.AsArray();
        registrations[0]!["CurrentBytes"] = long.MaxValue;
        registrations[1]!["CurrentBytes"] = 1;
        fixture.WriteRegistry(registry);
        var before = File.ReadAllBytes(fixture.RegistryPath);

        Assert.Throws<OverflowException>(() => store.GetStatus());

        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
    }

    [Theory]
    [InlineData("invalid-run-id")]
    [InlineData("blank-output-identity")]
    [InlineData("negative-current-bytes")]
    public async Task Open_InvalidOverflowObservationFactFailsClosedWithoutRepair(string fact)
    {
        using var fixture = new Fixture();
        using (var store = fixture.OpenStore()) await store.RegisterAsync(
            fixture.CreateRun("invalid-overflow-" + fact), CancellationToken.None);
        var registry = fixture.ReadRegistry();
        var observation = new JsonObject
        {
            ["RunId"] = Guid.NewGuid().ToString("N"),
            ["OutputIdentity"] = "directory-identity",
            ["CurrentBytes"] = 12,
            ["ObservedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        switch (fact)
        {
            case "invalid-run-id": observation["RunId"] = "not-an-id"; break;
            case "blank-output-identity": observation["OutputIdentity"] = " "; break;
            case "negative-current-bytes": observation["CurrentBytes"] = -1; break;
            default: throw new ArgumentOutOfRangeException(nameof(fact));
        }
        registry["OverflowObservations"]!.AsArray().Add(observation);
        fixture.WriteRegistry(registry);
        var before = File.ReadAllBytes(fixture.RegistryPath);

        var error = Assert.Throws<InvalidDataException>(() => fixture.OpenStore().Dispose());

        Assert.Equal("The artifact registry contains invalid observation or expiry receipt facts.", error.Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
    }

    [Fact]
    public async Task Maintain_UnknownInspectionMetadataFailsClosedAndPreservesEvidence()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var statePath = fixture.WriteObservationFile("inspection", "operator-note.json", "preserve this evidence");
        var stateBefore = File.ReadAllBytes(statePath);
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.MaintainAsync(
            apply: false, CancellationToken.None));

        Assert.Equal("Unknown observation metadata requires reconciliation.", error.Message);
        Assert.Equal(stateBefore, File.ReadAllBytes(statePath));
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
    }

    [Fact]
    public async Task Maintain_OversizedInspectionStateFailsClosedAndPreservesBytes()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var contents = new byte[64 * 1024 + 1];
        var statePath = fixture.WriteObservationFile("inspection", "state.json", contents);
        var stateBefore = File.ReadAllBytes(statePath);
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.MaintainAsync(
            apply: false, CancellationToken.None));

        Assert.Equal("Observation metadata exceeds its category byte limit.", error.Message);
        Assert.Equal(stateBefore, File.ReadAllBytes(statePath));
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
    }

    [Fact]
    public async Task Maintain_ChangedRegisteredOutputIdentityKeepsAbandonedReservation()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("changed-output-identity");
        await store.RegisterAsync(run, CancellationToken.None);
        var reservationId = Guid.NewGuid().ToString("N");
        var registry = fixture.ReadRegistry();
        registry["Reservations"]!.AsArray().Add(new JsonObject
        {
            ["Id"] = reservationId,
            ["RunId"] = run.ManifestBase.RunId,
            ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
            ["StartedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["Pin"] = false
        });
        fixture.WriteRegistry(registry);
        fixture.ReplaceOutputDirectory(run.OutputPath);

        Assert.Empty(await store.MaintainAsync(apply: true, CancellationToken.None));

        var recovered = fixture.ReadRegistry();
        Assert.Contains(recovered["Reservations"]!.AsArray(), item => item!["Id"]!.GetValue<string>() == reservationId);
        Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, store.GetStatus().StagingReservedBytes);
        Assert.True(Directory.Exists(run.OutputPath));
        Assert.False(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();

        internal Fixture()
        {
            RepositoryPath = _temp.CreateDirectory("repository");
            using (CoverageArtifactStore.Open(Root)) { }
        }

        internal string Root => Path.Join(_temp.Path, "private-store");
        internal string TempPath => _temp.Path;
        internal string RegistryPath => Path.Join(Root, "registry.json");
        internal string RepositoryPath { get; }

        internal CoverageArtifactStore OpenStore() => CoverageArtifactStore.Open(Root);

        internal CoverageArtifactCurrentRun CreateRun(string name)
        {
            var output = _temp.CreateDirectory(Path.Join("repository", name));
            var contents = Encoding.UTF8.GetBytes("coverage fixture: " + name);
            File.WriteAllBytes(Path.Join(output, "coverage.xml"), contents);
            return new(new CoverageArtifactRunManifest
            {
                RunId = Guid.NewGuid().ToString("N"),
                Outcome = "success",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
                Version = "coverage-edge-fixture",
            },
            [new CoverageArtifactInventoryEntry("coverage.xml", contents.Length, Hash(contents))], output, RepositoryPath);
        }

        internal string ArchivePath(string runId) => Path.Join(Root, "archives", runId);

        internal JsonObject ReadRegistry() => JsonNode.Parse(File.ReadAllText(RegistryPath))!.AsObject();

        internal void WriteRegistry(JsonObject registry) => WritePrivateFile(RegistryPath, registry.ToJsonString());

        internal async Task RegisterAndSnapshotAsync(CoverageArtifactStore store, CoverageArtifactCurrentRun run)
        {
            await store.RegisterAsync(run, CancellationToken.None);
            await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        }

        internal string WriteObservationFile(string category, string name, string contents)
            => WriteObservationFile(category, name, Encoding.UTF8.GetBytes(contents));

        internal string WriteObservationFile(string category, string name, byte[] contents)
        {
            var path = Path.Join(Root, "staging", category);
            using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(path, createMissing: true, requirePrivate: true)!;
            using (var stream = directory.OpenArtifactFile(name, createNew: true))
            {
                stream.Write(contents);
                stream.Flush(true);
            }
            return Path.Join(path, name);
        }

        internal void ReplaceOutputDirectory(string outputPath)
        {
            var displaced = outputPath + "-displaced";
            Directory.Move(outputPath, displaced);
            Directory.CreateDirectory(outputPath);
            File.WriteAllText(Path.Join(outputPath, "coverage.xml"), "replacement output");
        }

        internal void WritePrivateFile(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private static string Hash(byte[] contents)
            => Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();

        public void Dispose() => _temp.Dispose();
    }
}
