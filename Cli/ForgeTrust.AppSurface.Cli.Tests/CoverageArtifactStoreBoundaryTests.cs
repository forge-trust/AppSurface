using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreBoundaryTests
{
    [Fact]
    public void OpenAndOpenReadOnly_MissingRegistryFailClosedWithoutRepairingMetadata()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.StoreFilePath("registry.json"));
        var before = fixture.CaptureFiles(fixture.StoreRoot);

        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.Open(fixture.StoreRoot, fixture.Clock));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.OpenReadOnly(fixture.StoreRoot, fixture.Clock));

        Assert.False(File.Exists(fixture.StoreFilePath("registry.json")));
        fixture.AssertFilesEqual(fixture.StoreRoot, before);
    }

    [Fact]
    public async Task Snapshot_AutomaticHistoryDisabledByPolicyReleasesNoReservationButExplicitSnapshotWorks()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore(historyMaxBytes: 0);
        var run = fixture.CreateRun([1, 3, 5, 7]);
        await store.RegisterAsync(run, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SnapshotAsync(
            run, pin: false, automatic: true, CancellationToken.None));

        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        Assert.False(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
        Assert.True((await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None)).Complete);
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_RejectsReplacedRegisteredSourceDirectoryBeforeReservation(bool replaceRepository)
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun([2, 4, 6]);
        await store.RegisterAsync(run, CancellationToken.None);
        var registryBefore = fixture.ReadStoreFile("registry.json");
        var replacedPath = replaceRepository ? fixture.RepositoryPath : fixture.OutputPath;
        fixture.ReplaceDirectory(replacedPath, replaceRepository ? "old-repository" : "old-output");
        if (!replaceRepository)
            File.WriteAllBytes(Path.Join(fixture.OutputPath, "coverage.xml"), [9, 9, 9]);

        await Assert.ThrowsAsync<IOException>(() => store.SnapshotAsync(
            run, pin: false, automatic: false, CancellationToken.None));

        Assert.Equal(registryBefore, fixture.ReadStoreFile("registry.json"));
        Assert.Equal(1, store.GetStatus().CurrentRuns);
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        Assert.False(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
        if (!replaceRepository)
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Join(fixture.OutputPath, "coverage.xml")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maintain_RecoveryPreservesPublishedArchiveWhenRegisteredSourceIdentityChanges(bool replaceRepository)
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun(Encoding.UTF8.GetBytes("archive source identity remains part of recovery authority"));
        using (var store = fixture.OpenStore())
        {
            await store.RegisterAsync(run, CancellationToken.None);
            await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        }

        var reservationId = fixture.SimulatePublishedButUnregistered(run.ManifestBase.RunId);
        var archiveBefore = fixture.CaptureFiles(fixture.ArchivePath(run.ManifestBase.RunId));
        fixture.ReplaceDirectory(replaceRepository ? fixture.RepositoryPath : fixture.OutputPath,
            replaceRepository ? "replaced-repository" : "replaced-output");

        using (var store = fixture.OpenStore())
        {
            Assert.Empty(await store.MaintainAsync(apply: true, CancellationToken.None));
            var status = store.GetStatus();
            Assert.Equal(0, status.ArchivedRuns);
            Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, status.StagingReservedBytes);
        }

        Assert.True(fixture.HasReservation(reservationId));
        fixture.AssertFilesEqual(fixture.ArchivePath(run.ManifestBase.RunId), archiveBefore);
    }

    [Fact]
    public async Task Maintain_OrphanReservationWithoutRegistrationRemainsAccountedFor()
    {
        using var fixture = new Fixture();
        var orphanRunId = Guid.NewGuid().ToString("N");
        fixture.AddReservation(orphanRunId, pin: null);
        var registryBefore = fixture.ReadStoreFile("registry.json");

        using (var store = fixture.OpenStore())
        {
            Assert.Empty(await store.MaintainAsync(apply: true, CancellationToken.None));
            Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, store.GetStatus().StagingReservedBytes);
            Assert.Equal(0, store.GetStatus().RegisteredRuns);
        }

        Assert.Equal(registryBefore, fixture.ReadStoreFile("registry.json"));
        Assert.False(Directory.Exists(Path.Join(fixture.StoreRoot, "staging", "run-" + fixture.LastReservationId)));
    }

    [Fact]
    public async Task Maintain_AbandonedStageWithUnknownEntryPreservesStageAndReservation()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun([7, 8, 9]);
        using (var store = fixture.OpenStore()) await store.RegisterAsync(run, CancellationToken.None);
        var reservationId = fixture.AddReservation(run.ManifestBase.RunId, pin: false);
        var stageName = "run-" + reservationId;
        fixture.CreateStageFile(stageName, "operator-notes.txt", "preserve unknown stage contents");
        var registryBefore = fixture.ReadStoreFile("registry.json");
        var stageBefore = fixture.CaptureFiles(Path.Join(fixture.StoreRoot, "staging", stageName));

        using (var store = fixture.OpenStore())
        {
            Assert.Empty(await store.MaintainAsync(apply: true, CancellationToken.None));
            Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, store.GetStatus().StagingReservedBytes);
        }

        Assert.Equal(registryBefore, fixture.ReadStoreFile("registry.json"));
        fixture.AssertFilesEqual(Path.Join(fixture.StoreRoot, "staging", stageName), stageBefore);
    }

    [Fact]
    public async Task HasVerifiedArchiveAsync_ReportsCurrentArchiveAndPreservesCorruptPayloadEvidence()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun(Encoding.UTF8.GetBytes("verified coverage payload"));
        await store.RegisterAsync(run, CancellationToken.None);

        Assert.False(await store.HasVerifiedArchiveAsync(fixture.OutputPath, CancellationToken.None));
        await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        Assert.True(await store.HasVerifiedArchiveAsync(fixture.OutputPath, CancellationToken.None));

        fixture.CorruptOnePayload(run.ManifestBase.RunId);
        var corruptArchive = fixture.CaptureFiles(fixture.ArchivePath(run.ManifestBase.RunId));
        var registryBefore = fixture.ReadStoreFile("registry.json");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.HasVerifiedArchiveAsync(fixture.OutputPath, CancellationToken.None));

        Assert.Equal(registryBefore, fixture.ReadStoreFile("registry.json"));
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
        fixture.AssertFilesEqual(fixture.ArchivePath(run.ManifestBase.RunId), corruptArchive);
    }

    [Fact]
    public async Task Restore_RequiresExistingSafeParentAndCreatesNoPartialOutput()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun([11, 12, 13]);
        await store.RegisterAsync(run, CancellationToken.None);
        await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        var missingParent = Path.Join(fixture.TempPath, "missing-restore-parent");
        var destination = Path.Join(missingParent, "restored");

        await Assert.ThrowsAsync<IOException>(() =>
            store.RestoreAsync(run.ManifestBase.RunId, destination, CancellationToken.None));

        Assert.False(Directory.Exists(missingParent));
        Assert.False(File.Exists(destination));
        Assert.True(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Join(fixture.StoreRoot, "staging"), "restore-*"));
    }

    [Fact]
    public async Task Pin_UnpinMakesNonCurrentArchiveEligibleAndMaintenanceRecordsExpiry()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore(historyMaxBytes: 0);
        var archived = fixture.CreateRun([1, 2, 3, 4]);
        await store.RegisterAsync(archived, CancellationToken.None);
        await store.SnapshotAsync(archived, pin: false, automatic: false, CancellationToken.None);
        await store.RegisterAsync(fixture.CreateRun([5, 6, 7], "success"), CancellationToken.None);

        await store.PinAsync(archived.ManifestBase.RunId, pin: true, CancellationToken.None);
        Assert.True(store.GetStatus().PinnedBytes > 0);
        Assert.Empty(await store.MaintainAsync(apply: false, CancellationToken.None));

        await store.PinAsync(archived.ManifestBase.RunId, pin: false, CancellationToken.None);
        Assert.Equal(0, store.GetStatus().PinnedBytes);
        Assert.Equal([archived.ManifestBase.RunId], await store.MaintainAsync(apply: false, CancellationToken.None));
        Assert.Equal([archived.ManifestBase.RunId], await store.MaintainAsync(apply: true, CancellationToken.None));

        var status = store.GetStatus();
        Assert.Equal(2, status.RegisteredRuns);
        Assert.Equal(0, status.ArchivedRuns);
        Assert.Equal(1, status.ExpiryReceiptCount);
        Assert.False(Directory.Exists(fixture.ArchivePath(archived.ManifestBase.RunId)));
    }

    [Fact]
    public async Task Maintain_ExpiredArchiveWithUnknownEntryFailsClosedAndPreservesMetadataAndBytes()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore(historyMaxAgeDays: 1);
        var archived = fixture.CreateRun([21, 22, 23]);
        await store.RegisterAsync(archived, CancellationToken.None);
        await store.SnapshotAsync(archived, pin: false, automatic: false, CancellationToken.None);
        await store.RegisterAsync(fixture.CreateRun([24], "success"), CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        fixture.CreateArchiveFile(archived.ManifestBase.RunId, "unregistered-note.txt", "preserve unknown archive contents");
        var archiveBefore = fixture.CaptureFiles(fixture.ArchivePath(archived.ManifestBase.RunId));
        var registryBefore = fixture.ReadStoreFile("registry.json");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.MaintainAsync(
            apply: true, CancellationToken.None));

        Assert.Equal(registryBefore, fixture.ReadStoreFile("registry.json"));
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
        Assert.Equal(0, store.GetStatus().ExpiryReceiptCount);
        fixture.AssertFilesEqual(fixture.ArchivePath(archived.ManifestBase.RunId), archiveBefore);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();

        internal Fixture()
        {
            StoreRoot = Path.Join(_temp.Path, "private-store");
            RepositoryPath = _temp.CreateDirectory("repository");
            OutputPath = _temp.CreateDirectory("coverage-output");
            Clock = new TestClock(DateTimeOffset.Parse("2026-10-21T12:00:00Z"));
            using (CoverageArtifactStore.Open(StoreRoot, Clock)) { }
            WritePolicy(historyMaxBytes: 2_147_483_648, historyMaxAgeDays: 7);
        }

        internal string StoreRoot { get; }
        internal string TempPath => _temp.Path;
        internal string RepositoryPath { get; }
        internal string OutputPath { get; }
        internal TestClock Clock { get; }
        internal string? LastReservationId { get; private set; }

        internal CoverageArtifactStore OpenStore(long historyMaxBytes = 2_147_483_648, int historyMaxAgeDays = 7)
        {
            WritePolicy(historyMaxBytes, historyMaxAgeDays);
            return CoverageArtifactStore.Open(StoreRoot, Clock);
        }

        internal CoverageArtifactCurrentRun CreateRun(byte[] contents, string outcome = "success")
        {
            File.WriteAllBytes(Path.Join(OutputPath, "coverage.xml"), contents);
            var runId = Guid.NewGuid().ToString("N");
            var inventory = new CoverageArtifactInventoryEntry("coverage.xml", contents.LongLength,
                Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant());
            var manifest = new CoverageArtifactRunManifest
            {
                RunId = runId,
                Outcome = outcome,
                CreatedAtUtc = Clock.GetUtcNow(),
                Version = "store-boundary-test",
                Provenance = new CoverageArtifactProvenance("revision", "clean", "configuration"),
            };
            return new CoverageArtifactCurrentRun(manifest, [inventory], OutputPath, RepositoryPath);
        }

        internal string ArchivePath(string runId) => Path.Join(StoreRoot, "archives", runId);
        internal string StoreFilePath(string name) => Path.Join(StoreRoot, name);
        internal byte[] ReadStoreFile(string name) => File.ReadAllBytes(StoreFilePath(name));

        internal string AddReservation(string runId, bool? pin)
        {
            var registry = ReadRegistry();
            var id = Guid.NewGuid().ToString("N");
            var reservation = new JsonObject
            {
                ["Id"] = id,
                ["RunId"] = runId,
                ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
                ["StartedAtUtc"] = Clock.GetUtcNow().ToString("O"),
            };
            if (pin is { } pinIntent) reservation["Pin"] = pinIntent;
            registry["Reservations"]!.AsArray().Add(reservation);
            WriteRegistry(registry);
            LastReservationId = id;
            return id;
        }

        internal string SimulatePublishedButUnregistered(string runId)
        {
            var registry = ReadRegistry();
            var registration = registry["Registrations"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(item => item["RunId"]!.GetValue<string>() == runId);
            registration["Archived"] = false;
            registration["Pinned"] = false;
            registration["ArchiveBytes"] = 0;
            registration["ArchivedAtUtc"] = (JsonNode?)null;

            var id = Guid.NewGuid().ToString("N");
            registry["Reservations"]!.AsArray().Add(new JsonObject
            {
                ["Id"] = id,
                ["RunId"] = runId,
                ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
                ["StartedAtUtc"] = Clock.GetUtcNow().ToString("O"),
                ["Pin"] = false,
            });
            WriteRegistry(registry);
            LastReservationId = id;
            return id;
        }

        internal bool HasReservation(string id) => ReadRegistry()["Reservations"]!.AsArray()
            .Any(item => item!["Id"]!.GetValue<string>() == id);

        internal void ReplaceDirectory(string path, string backupName)
        {
            Directory.Move(path, Path.Join(_temp.Path, backupName));
            Directory.CreateDirectory(path);
        }

        internal void CreateStageFile(string stageName, string fileName, string contents)
        {
            var stagingPath = Path.Join(StoreRoot, "staging");
            using var staging = CoverageRunOutputLease.AcquireArtifactDirectory(stagingPath, false, true)!;
            using var stage = staging.CreateArtifactDirectory(stageName);
            using var file = stage.OpenArtifactFile(fileName, createNew: true);
            var bytes = Encoding.UTF8.GetBytes(contents);
            file.Write(bytes);
            file.Flush(true);
        }

        internal void CreateArchiveFile(string runId, string fileName, string contents)
        {
            using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(ArchivePath(runId), false, true)!;
            using var file = archive.OpenArtifactFile(fileName, createNew: true);
            var bytes = Encoding.UTF8.GetBytes(contents);
            file.Write(bytes);
            file.Flush(true);
        }

        internal void CorruptOnePayload(string runId)
        {
            var payloadPath = Directory.EnumerateFiles(ArchivePath(runId), "*.gz").Single();
            var bytes = File.ReadAllBytes(payloadPath);
            Assert.NotEmpty(bytes);
            bytes[0] ^= 0xff;
            File.WriteAllBytes(payloadPath, bytes);
        }

        internal Dictionary<string, byte[]> CaptureFiles(string directory) =>
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(directory, path), path => File.ReadAllBytes(path), StringComparer.Ordinal);

        internal void AssertFilesEqual(string directory, IReadOnlyDictionary<string, byte[]> expected)
        {
            var actual = CaptureFiles(directory);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
            foreach (var path in expected.Keys)
                Assert.Equal(Convert.ToHexString(expected[path]), Convert.ToHexString(actual[path]));
        }

        private JsonObject ReadRegistry() => JsonNode.Parse(ReadStoreFile("registry.json"))!.AsObject();

        private void WriteRegistry(JsonObject registry)
        {
            var path = StoreFilePath("registry.json");
            File.WriteAllText(path, registry.ToJsonString(), new UTF8Encoding(false));
            SetPrivateFileMode(path);
        }

        private void WritePolicy(long historyMaxBytes, int historyMaxAgeDays)
        {
            var path = StoreFilePath("policy.json");
            var json = JsonSerializer.Serialize(new { schemaVersion = 1, historyMaxBytes, historyMaxAgeDays });
            File.WriteAllText(path, json, new UTF8Encoding(false));
            SetPrivateFileMode(path);
        }

        private static void SetPrivateFileMode(string path)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed class TestClock(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
