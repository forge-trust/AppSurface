using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreValidationTests
{
    [Theory]
    [InlineData("{\"SchemaVersion\":2,\"Registrations\":[],\"Reservations\":[],\"OverflowObservations\":[],\"IsComplete\":true,\"ExpiryReceipts\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Registrations\":null,\"Reservations\":[],\"OverflowObservations\":[],\"IsComplete\":true,\"ExpiryReceipts\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Registrations\":[],\"Reservations\":[{\"Id\":\"11111111111141118111111111111111\",\"RunId\":\"22222222222242228222222222222222\",\"Bytes\":536870913,\"StartedAtUtc\":\"2026-10-21T12:00:00Z\"}],\"OverflowObservations\":[],\"IsComplete\":true,\"ExpiryReceipts\":[]}")]
    public void Open_InvalidRegistryFailsClosedAndPreservesPrivateJson(string registryJson)
    {
        using var fixture = new Fixture();
        fixture.Write("registry.json", registryJson);
        var before = fixture.Read("registry.json");

        Assert.Throws<InvalidDataException>(() => fixture.Open());

        Assert.Equal(before, fixture.Read("registry.json"));
    }

    [Fact]
    public void OpenReadOnly_UnknownMarkerFailsWithoutRepairingMarker()
    {
        using var fixture = new Fixture();
        fixture.Write("store.json", "{\"SchemaVersion\":77,\"PhysicalIdentity\":\"unknown\"}");
        var before = fixture.Read("store.json");

        Assert.Throws<InvalidDataException>(() => fixture.OpenReadOnly());

        Assert.Equal(before, fixture.Read("store.json"));
    }

    [Fact]
    public void OpenReadOnly_RejectsPolicyFileWithBroadPermissionsWithoutChangingIt()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Write("policy.json", "{\"schemaVersion\":1}");
        File.SetUnixFileMode(fixture.StoreFilePath("policy.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var before = fixture.Read("policy.json");

        Assert.ThrowsAny<IOException>(() => fixture.OpenReadOnly());

        Assert.Equal(before, fixture.Read("policy.json"));
        Assert.NotEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.StoreFilePath("policy.json")));
    }

    [Fact]
    public async Task Snapshot_QuotaReservationOverflowPreservesRunAndCanRetryAfterReconciliation()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun([10, 20, 30]);
        using (var store = fixture.Open()) await store.RegisterAsync(run, CancellationToken.None);
        var registered = fixture.Read("registry.json");
        var registry = JsonNode.Parse(registered)!;
        registry["Reservations"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 32).Select(index => new
        {
            Id = (index + 1).ToString("x32"),
            RunId = (index + 100).ToString("x32"),
            Bytes = 16 * 1024 * 1024,
            StartedAtUtc = "2026-10-21T12:00:00Z"
        }));
        fixture.Write("registry.json", registry.ToJsonString());
        var registryBefore = fixture.Read("registry.json");

        using (var store = fixture.Open())
            await Assert.ThrowsAsync<IOException>(() => store.SnapshotAsync(run, false, false, CancellationToken.None));

        Assert.Equal(registryBefore, fixture.Read("registry.json"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StoreFilePath("archives")));
        fixture.Write("registry.json", Encoding.UTF8.GetString(registered));
        using var reconciled = fixture.Open();
        Assert.True((await reconciled.SnapshotAsync(run, false, false, CancellationToken.None)).Complete);
    }

    [Fact]
    public async Task Snapshot_CancellationReleasesReservationAndAllowsRetry()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var run = fixture.CreateRun([1, 3, 5, 7]);
        await store.RegisterAsync(run, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SnapshotAsync(run, false, false, cancelled.Token));
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);

        Assert.True((await store.SnapshotAsync(run, false, false, CancellationToken.None)).Complete);
    }

    [Fact]
    public async Task Snapshot_ChangedArchiveInputFailsIntegrityCheckAndLeavesNoArchive()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var run = fixture.CreateRun([2, 4, 6]);
        await store.RegisterAsync(run, CancellationToken.None);
        File.WriteAllBytes(fixture.OutputFile, [9, 9, 9]);

        await Assert.ThrowsAnyAsync<IOException>(() => store.SnapshotAsync(run, false, false, CancellationToken.None));

        Assert.False(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
    }

    [Fact]
    public async Task ReadOnlyStore_RejectsRegistrationWithoutChangingRegistry()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun([1]);
        using (var writer = fixture.Open()) await writer.RegisterAsync(run, CancellationToken.None);
        var before = fixture.Read("registry.json");
        using var readOnly = fixture.OpenReadOnly();

        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.RegisterAsync(run, CancellationToken.None));

        Assert.Equal(before, fixture.Read("registry.json"));
    }

    [Fact]
    public async Task Restore_UnknownRegistrationFailsClosedAndLeavesArchiveAndDestinationUntouched()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun([4, 8, 15, 16, 23, 42]);
        using var store = fixture.Open();
        await store.RegisterAsync(run, CancellationToken.None);
        await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);

        var archiveBefore = fixture.CaptureFiles(fixture.ArchivePath(run.ManifestBase.RunId));
        var registry = JsonNode.Parse(Encoding.UTF8.GetString(fixture.Read("registry.json")))!;
        registry["Registrations"] = new JsonArray();
        fixture.Write("registry.json", registry.ToJsonString());
        var registryBefore = fixture.Read("registry.json");
        var destinationParent = fixture.CreateDirectory("restore-parent");
        var destination = Path.Join(destinationParent, "restored");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.RestoreAsync(run.ManifestBase.RunId, destination, CancellationToken.None));

        Assert.Equal(registryBefore, fixture.Read("registry.json"));
        fixture.AssertFilesEqual(fixture.ArchivePath(run.ManifestBase.RunId), archiveBefore);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Snapshot_ArchivedArchiveWithMismatchedManifestFailsClosedAndPreservesRegistrationAndPayload()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun([3, 1, 4, 1, 5]);
        using var store = fixture.Open();
        await store.RegisterAsync(run, CancellationToken.None);
        await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        var registryBefore = fixture.Read("registry.json");
        var archivePath = fixture.ArchivePath(run.ManifestBase.RunId);
        var manifestPath = Path.Join(archivePath, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["runId"] = Guid.NewGuid().ToString("N");
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var corruptArchive = fixture.CaptureFiles(archivePath);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SnapshotAsync(run, pin: true, automatic: false, CancellationToken.None));

        Assert.Equal(registryBefore, fixture.Read("registry.json"));
        fixture.AssertFilesEqual(archivePath, corruptArchive);
        Assert.Contains("manifest.json", corruptArchive.Keys);
    }

    [Fact]
    public async Task PinAndUnpin_ExistingArchiveRepeatsSuccessfullyAndPreservesPayload()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var run = fixture.CreateRun([1, 4, 1, 4, 2]);
        await store.RegisterAsync(run, CancellationToken.None);
        await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        var archivePath = fixture.ArchivePath(run.ManifestBase.RunId);
        var archiveBefore = fixture.CaptureFiles(archivePath);

        await store.PinAsync(run.ManifestBase.RunId, pin: true, CancellationToken.None);
        Assert.True(store.GetStatus().PinnedBytes > 0);
        await store.PinAsync(run.ManifestBase.RunId, pin: true, CancellationToken.None);
        Assert.True(store.GetStatus().PinnedBytes > 0);

        await store.PinAsync(run.ManifestBase.RunId, pin: false, CancellationToken.None);
        Assert.Equal(0, store.GetStatus().PinnedBytes);
        fixture.AssertFilesEqual(archivePath, archiveBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_ExistingVerifiedArchiveCanBeReusedWithoutRewritingPayload(bool pin)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var run = fixture.CreateRun([5, 9, 2, 6]);
        await store.RegisterAsync(run, CancellationToken.None);
        var original = await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        var archivePath = fixture.ArchivePath(run.ManifestBase.RunId);
        var before = fixture.CaptureFiles(archivePath);

        var reused = await store.SnapshotAsync(run, pin, automatic: false, CancellationToken.None);

        Assert.Equal(original.RunId, reused.RunId);
        Assert.True(reused.Complete);
        Assert.Equal(pin, store.GetStatus().PinnedBytes > 0);
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        fixture.AssertFilesEqual(archivePath, before);
    }

    [Fact]
    public async Task Snapshot_ExistingArchiveWithCorruptPayloadPreservesEvidenceAndRegistration()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var run = fixture.CreateRun([8, 6, 7, 5, 3]);
        await store.RegisterAsync(run, CancellationToken.None);
        await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);

        var archivePath = fixture.ArchivePath(run.ManifestBase.RunId);
        var payloadPath = Directory.EnumerateFiles(archivePath, "*.gz").Single();
        File.WriteAllBytes(payloadPath, [0xC0, 0xFF, 0xEE]);
        var archiveBefore = fixture.CaptureFiles(archivePath);
        var registryBefore = fixture.Read("registry.json");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SnapshotAsync(
            run, pin: true, automatic: false, CancellationToken.None));

        Assert.Equal(registryBefore, fixture.Read("registry.json"));
        fixture.AssertFilesEqual(archivePath, archiveBefore);
        Assert.Equal(new byte[] { 0xC0, 0xFF, 0xEE }, File.ReadAllBytes(payloadPath));
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
    }

    [Fact]
    public async Task DisposedStore_RejectsReadsAndWritesWithoutChangingRegistry()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun([7, 7, 7]);
        var store = fixture.Open();
        await store.RegisterAsync(run, CancellationToken.None);
        var registryBefore = fixture.Read("registry.json");
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => store.GetStatus());
        Assert.Throws<ObjectDisposedException>(() => store.GetCurrentRun(run.OutputPath));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            store.RegisterAsync(run, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            store.PinAsync(run.ManifestBase.RunId, pin: true, CancellationToken.None));

        Assert.Equal(registryBefore, fixture.Read("registry.json"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();
        private readonly string _output;
        private readonly string _repository;

        internal Fixture()
        {
            Root = Path.Join(_temp.Path, "private-store");
            _repository = _temp.CreateDirectory("repository");
            _output = _temp.CreateDirectory("repository/coverage");
            using (CoverageArtifactStore.Open(Root)) { }
        }

        internal string Root { get; }
        internal string OutputFile => Path.Join(_output, "coverage.xml");
        internal string ArchivePath(string runId) => Path.Join(Root, "archives", runId);
        internal string StoreFilePath(string name) => System.IO.Path.Join(Root, name);
        internal CoverageArtifactStore Open() => CoverageArtifactStore.Open(Root);
        internal CoverageArtifactStore OpenReadOnly() => CoverageArtifactStore.OpenReadOnly(Root);
        internal byte[] Read(string name) => File.ReadAllBytes(StoreFilePath(name));
        internal string CreateDirectory(string name) => _temp.CreateDirectory(name);

        internal Dictionary<string, byte[]> CaptureFiles(string directory)
            => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);

        internal void AssertFilesEqual(string directory, IReadOnlyDictionary<string, byte[]> expected)
        {
            var actual = CaptureFiles(directory);
            Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
            foreach (var (relativePath, bytes) in expected)
                Assert.Equal(bytes, actual[relativePath]);
        }

        internal void Write(string name, string json)
        {
            var path = StoreFilePath(name);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        internal CoverageArtifactCurrentRun CreateRun(byte[] contents)
        {
            File.WriteAllBytes(OutputFile, contents);
            var runId = Guid.NewGuid().ToString("N");
            var manifest = new CoverageArtifactRunManifest
            {
                RunId = runId,
                Outcome = "success",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Version = "store-validation-test",
            };
            var inventory = new CoverageArtifactInventoryEntry("coverage.xml", contents.LongLength,
                Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant());
            return new(manifest, [inventory], _output, _repository);
        }

        public void Dispose() => _temp.Dispose();
    }
}
