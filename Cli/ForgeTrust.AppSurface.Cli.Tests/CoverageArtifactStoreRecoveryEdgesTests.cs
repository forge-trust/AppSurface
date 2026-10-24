using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreRecoveryEdgesTests
{
    [Theory]
    [InlineData("unknown-entry")]
    [InlineData("foreign-marker")]
    public void OpenAndOpenReadOnly_RejectUnownedPrivateRootsWithoutRepairingThem(string state)
    {
        using var temp = TestDirectory.Create();
        var root = Path.Join(temp.Path, "private-store");
        using (var directory = CoverageRunOutputLease.AcquireArtifactDirectory(root, true, true)!)
        {
            var name = state == "unknown-entry" ? "operator-notes.txt" : "store.json";
            var contents = state == "unknown-entry"
                ? "preserve unowned root contents"
                : "{\"SchemaVersion\":1,\"PhysicalIdentity\":\"foreign-directory\"}";
            using var file = directory.OpenArtifactFile(name, createNew: true);
            var bytes = Encoding.UTF8.GetBytes(contents);
            file.Write(bytes);
            file.Flush(true);
        }

        var path = TestPathUtils.PathUnder(root, state == "unknown-entry" ? "operator-notes.txt" : "store.json");
        var before = File.ReadAllBytes(path);
        if (state == "unknown-entry")
        {
            Assert.Throws<IOException>(() => CoverageArtifactStore.Open(root));
            Assert.Throws<IOException>(() => CoverageArtifactStore.OpenReadOnly(root));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.Open(root));
            Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.OpenReadOnly(root));
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Join(root, "registry.json")));
    }

    [Fact]
    public async Task Maintain_PreservesPublishedArchiveAndReservationWhenRepositoryIsMissing()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("published archive with missing repository");
        await store.RegisterAsync(run, CancellationToken.None);
        await store.SnapshotAsync(run, false, false, CancellationToken.None);
        var reservationId = fixture.MarkPublishedButUnregistered(run.ManifestBase.RunId);
        Directory.Delete(fixture.RepositoryPath);

        Assert.Empty(await store.MaintainAsync(true, CancellationToken.None));

        Assert.Equal(0, store.GetStatus().ArchivedRuns);
        Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, store.GetStatus().StagingReservedBytes);
        Assert.True(fixture.HasReservation(reservationId));
        Assert.True(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("path")]
    [InlineData("hash")]
    public async Task Maintain_PreservesPublishedArchiveWhenManifestInventoryDiffers(string difference)
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("manifest inventory recovery evidence");
        await store.RegisterAsync(run, CancellationToken.None);
        await store.SnapshotAsync(run, false, false, CancellationToken.None);
        var reservationId = fixture.MarkPublishedButUnregistered(run.ManifestBase.RunId);
        fixture.ChangeArchiveManifest(run.ManifestBase.RunId, difference);
        var manifestBefore = File.ReadAllBytes(Path.Join(fixture.ArchivePath(run.ManifestBase.RunId), "manifest.json"));

        Assert.Empty(await store.MaintainAsync(true, CancellationToken.None));

        Assert.Equal(0, store.GetStatus().ArchivedRuns);
        Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, store.GetStatus().StagingReservedBytes);
        Assert.True(fixture.HasReservation(reservationId));
        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Join(fixture.ArchivePath(run.ManifestBase.RunId), "manifest.json")));
    }

    [Fact]
    public async Task Maintain_CancellationAfterFirstArchiveDeletionPersistsOnlyThatRemoval()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore(historyMaxBytes: 0);
        var first = fixture.CreateRun("first archived candidate");
        await store.RegisterAsync(first, CancellationToken.None);
        await store.SnapshotAsync(first, false, false, CancellationToken.None);
        var second = fixture.CreateRun("second archived candidate");
        await store.RegisterAsync(second, CancellationToken.None);
        await store.SnapshotAsync(second, false, false, CancellationToken.None);
        await store.RegisterAsync(fixture.CreateRun("current run"), CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        string? deletedRunId = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MaintainAsync(
            true,
            cancellation.Token,
            onArchiveDeleted: runId =>
            {
                deletedRunId = runId;
                cancellation.Cancel();
            }));

        Assert.Contains(deletedRunId, new[] { first.ManifestBase.RunId, second.ManifestBase.RunId });
        var retainedRunId = deletedRunId == first.ManifestBase.RunId
            ? second.ManifestBase.RunId
            : first.ManifestBase.RunId;
        Assert.False(Directory.Exists(fixture.ArchivePath(deletedRunId!)));
        Assert.True(Directory.Exists(fixture.ArchivePath(retainedRunId)));
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
    }

    [Fact]
    public async Task Open_InvalidReservationGuidFailsClosedAndPreservesRegistry()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun("invalid reservation metadata");
        using (var store = fixture.OpenStore()) await store.RegisterAsync(run, CancellationToken.None);
        var registry = fixture.ReadRegistry();
        registry["Reservations"]!.AsArray().Add(new JsonObject
        {
            ["Id"] = "not-a-guid",
            ["RunId"] = run.ManifestBase.RunId,
            ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
            ["StartedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        });
        fixture.WriteRegistry(registry);
        var before = File.ReadAllBytes(fixture.RegistryPath);

        Assert.Throws<InvalidDataException>(() => fixture.OpenStore());
        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.OpenReadOnly(fixture.StoreRoot));

        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();

        internal Fixture()
        {
            StoreRoot = Path.Join(_temp.Path, "private-store");
            RepositoryPath = _temp.CreateDirectory("repository");
            OutputPath = _temp.CreateDirectory("output");
            using (CoverageArtifactStore.Open(StoreRoot)) { }
        }

        internal string StoreRoot { get; }
        internal string RepositoryPath { get; }
        internal string OutputPath { get; }
        internal string RegistryPath => Path.Join(StoreRoot, "registry.json");

        internal CoverageArtifactStore OpenStore(long historyMaxBytes = 2_147_483_648)
        {
            WritePrivateFile(Path.Join(StoreRoot, "policy.json"),
                $"{{\"schemaVersion\":1,\"historyMaxBytes\":{historyMaxBytes},\"historyMaxAgeDays\":7}}");
            return CoverageArtifactStore.Open(StoreRoot);
        }

        internal CoverageArtifactCurrentRun CreateRun(string contents)
        {
            var bytes = Encoding.UTF8.GetBytes(contents);
            File.WriteAllBytes(Path.Join(OutputPath, "coverage.xml"), bytes);
            return new(new CoverageArtifactRunManifest
            {
                RunId = Guid.NewGuid().ToString("N"),
                Outcome = "success",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Version = "recovery-edges-test",
                Provenance = new CoverageArtifactProvenance("revision", "clean", "configuration")
            }, [new CoverageArtifactInventoryEntry("coverage.xml", bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())], OutputPath, RepositoryPath);
        }

        internal string ArchivePath(string runId) => TestPathUtils.PathUnder(StoreRoot, "archives", runId);

        internal string MarkPublishedButUnregistered(string runId)
        {
            var registry = ReadRegistry();
            var registration = registry["Registrations"]!.AsArray().Single(node =>
                node!["RunId"]!.GetValue<string>() == runId)!.AsObject();
            registration["Archived"] = false;
            registration["Pinned"] = false;
            registration["ArchiveBytes"] = 0;
            registration["ArchivedAtUtc"] = null;
            var reservationId = Guid.NewGuid().ToString("N");
            registry["Reservations"]!.AsArray().Add(new JsonObject
            {
                ["Id"] = reservationId,
                ["RunId"] = runId,
                ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
                ["StartedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["Pin"] = false
            });
            WriteRegistry(registry);
            return reservationId;
        }

        internal void ChangeArchiveManifest(string runId, string difference)
        {
            var manifestPath = Path.Join(ArchivePath(runId), "manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var files = manifest["files"]!.AsArray();
            switch (difference)
            {
                case "count": manifest["files"] = new JsonArray(); break;
                case "path": files[0]!["path"] = "other.xml"; break;
                case "hash":
                    var hash = files[0]!["originalSha256"]!.GetValue<string>();
                    files[0]!["originalSha256"] = (hash[0] == '0' ? '1' : '0') + hash[1..];
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(difference));
            }
            WritePrivateFile(manifestPath, manifest.ToJsonString());
        }

        internal bool HasReservation(string reservationId) => ReadRegistry()["Reservations"]!.AsArray()
            .Any(node => node!["Id"]!.GetValue<string>() == reservationId);

        internal JsonObject ReadRegistry() => JsonNode.Parse(File.ReadAllText(RegistryPath))!.AsObject();

        internal void WriteRegistry(JsonObject registry) => WritePrivateFile(RegistryPath, registry.ToJsonString());

        private static void WritePrivateFile(string path, string contents)
        {
            TestFileSystem.WritePrivateFile(path, contents);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose() => _temp.Dispose();
    }
}
