using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreFinalPathsTests
{
    [Fact]
    public async Task RegisterAsync_HardRegistryByteLimitPreservesPriorMetadata()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var seed = fixture.CreateRun("capacity-seed", "seed"u8.ToArray());
        await store.RegisterAsync(seed, CancellationToken.None);
        var candidate = fixture.CreateRun("capacity-candidate", "candidate"u8.ToArray());

        var registry = fixture.ReadRegistry();
        var registration = registry["Registrations"]!.AsArray().Single()!.AsObject();
        registration["OutputPath"] = "x";
        var oneCharacterSize = JsonSerializer.SerializeToUtf8Bytes(registry).Length;
        var targetSize = checked((int)CoverageArtifactLimits.RegistryMaxBytes - 64);
        registration["OutputPath"] = new string('x', targetSize - oneCharacterSize + 1);
        Assert.Equal(targetSize, JsonSerializer.SerializeToUtf8Bytes(registry).Length);
        fixture.WriteRegistry(registry);
        var before = File.ReadAllBytes(fixture.RegistryPath);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.RegisterAsync(candidate, CancellationToken.None));

        Assert.Equal("The artifact registry reached its bounded metadata capacity.", error.Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        Assert.False(File.Exists(fixture.RegistryStagePath));
        Assert.True(File.Exists(fixture.SourceFilePath(candidate)));
    }

    [Fact]
    public async Task RegisterAsync_NonprivateRootModeRejectsBeforeMetadataMutation()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var existing = fixture.CreateRun("publication-existing", "existing"u8.ToArray());
        await store.RegisterAsync(existing, CancellationToken.None);
        var candidate = fixture.CreateRun("publication-candidate", "candidate"u8.ToArray());
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var rootMode = File.GetUnixFileMode(fixture.StoreRoot);
        File.SetUnixFileMode(fixture.StoreRoot, rootMode & ~UnixFileMode.UserWrite);

        Exception? publicationError;
        try
        {
            publicationError = await Record.ExceptionAsync(() =>
                store.RegisterAsync(candidate, CancellationToken.None));
        }
        finally
        {
            File.SetUnixFileMode(fixture.StoreRoot, rootMode);
        }

        Assert.NotNull(publicationError);
        Assert.True(publicationError is IOException or UnauthorizedAccessException, publicationError.ToString());
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.False(File.Exists(fixture.RegistryStagePath));
        Assert.Equal(1, store.GetStatus().RegisteredRuns);
        Assert.True(File.Exists(fixture.SourceFilePath(candidate)));
    }

    [Fact]
    public async Task SnapshotAsync_ArchivedReuseWithMissingManifestPreservesSourceAndRegistry()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var contents = "snapshot source"u8.ToArray();
        var run = fixture.CreateRun("snapshot-missing-manifest", contents);
        await fixture.RegisterAndSnapshotAsync(store, run);
        var manifestPath = fixture.ManifestPath(run.ManifestBase.RunId);
        File.Delete(manifestPath);
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None));

        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
        Assert.Equal(contents, File.ReadAllBytes(fixture.SourceFilePath(run)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.StagingPath, "run-*"));
    }

    [Fact]
    public async Task SnapshotAsync_ArchivedReuseRejectsCorruptPayloadAndPreservesEvidence()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var contents = "snapshot payload"u8.ToArray();
        var run = fixture.CreateRun("snapshot-corrupt-payload", contents);
        await fixture.RegisterAndSnapshotAsync(store, run);
        fixture.CorruptPayload(run.ManifestBase.RunId);
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None));

        Assert.Equal("Stored archive hash mismatch.", error.Message);
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
        Assert.Equal(contents, File.ReadAllBytes(fixture.SourceFilePath(run)));
    }

    [Fact]
    public async Task PinAsync_MissingArchiveManifestPreservesArchiveAndPinState()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("pin-missing-manifest", "pin source"u8.ToArray());
        await fixture.RegisterAndSnapshotAsync(store, run);
        File.Delete(fixture.ManifestPath(run.ManifestBase.RunId));
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.PinAsync(run.ManifestBase.RunId, pin: true, CancellationToken.None));

        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Equal(0, store.GetStatus().PinnedBytes);
        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
    }

    [Fact]
    public async Task RestoreAsync_MissingArchiveManifestCreatesNoDestinationOrStage()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("restore-missing-manifest", "restore source"u8.ToArray());
        await fixture.RegisterAndSnapshotAsync(store, run);
        File.Delete(fixture.ManifestPath(run.ManifestBase.RunId));
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);
        var repositoryDirectoriesBefore = fixture.CaptureRepositoryDirectoryNames();
        var destination = TestPathUtils.PathUnder(fixture.RepositoryPath, "restore-missing-manifest-output");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.RestoreAsync(run.ManifestBase.RunId, destination, CancellationToken.None));

        Assert.False(Directory.Exists(destination));
        Assert.Equal(repositoryDirectoriesBefore, fixture.CaptureRepositoryDirectoryNames());
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
    }

    [Fact]
    public async Task MaintainAsync_MissingCandidateManifestPreservesArchiveAndRegistration()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore(historyMaxBytes: 0);
        var run = await fixture.CreateEligibleArchiveAsync(store, "retention-missing-manifest");
        File.Delete(fixture.ManifestPath(run.ManifestBase.RunId));
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.MaintainAsync(apply: true, CancellationToken.None));

        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.True(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
    }

    [Fact]
    public async Task MaintainAsync_ExpiredCandidateWithUnknownDirectoryPreservesEvidence()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore(historyMaxBytes: 0);
        var run = await fixture.CreateEligibleArchiveAsync(store, "retention-unknown-directory");
        var archivePath = fixture.ArchivePath(run.ManifestBase.RunId);
        using (var archive = CoverageRunOutputLease.AcquireArtifactDirectory(archivePath, false, true)!)
            archive.CreateArtifactDirectory("operator-evidence").Dispose();
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);
        var unknownDirectory = TestPathUtils.PathUnder(archivePath, "operator-evidence");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.MaintainAsync(apply: true, CancellationToken.None));

        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.True(Directory.Exists(unknownDirectory));
        Assert.True(Directory.Exists(archivePath));
        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("different-run-id")]
    [InlineData("unsupported-schema")]
    [InlineData("malformed-json")]
    public async Task MaintainAsync_CorruptExpiredManifestIsSkippedOrPreserved(string corruption)
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore(historyMaxBytes: 0);
        var run = await fixture.CreateEligibleArchiveAsync(store, "retention-corrupt-" + corruption);
        var manifestPath = fixture.ManifestPath(run.ManifestBase.RunId);
        if (corruption == "malformed-json")
        {
            File.WriteAllText(manifestPath, "{");
        }
        else
        {
            var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
            switch (corruption)
            {
                case "incomplete":
                    manifest["complete"] = false;
                    break;
                case "different-run-id":
                    manifest["runId"] = Guid.NewGuid().ToString("N");
                    break;
                case "unsupported-schema":
                    manifest["schemaVersion"] = 2;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(corruption));
            }
            File.WriteAllText(manifestPath, manifest.ToJsonString());
        }
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);

        if (corruption == "different-run-id")
            Assert.Empty(await store.MaintainAsync(apply: true, CancellationToken.None));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.MaintainAsync(apply: true, CancellationToken.None));

        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.True(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
    }

    [Fact]
    public async Task MaintainAsync_ManifestPayloadNameMismatchPreservesExpiredCandidate()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore(historyMaxBytes: 0);
        var run = await fixture.CreateEligibleArchiveAsync(store, "retention-payload-name-mismatch");
        fixture.ChangeManifestPayloadId(run.ManifestBase.RunId, "99999999");
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.MaintainAsync(apply: true, CancellationToken.None));

        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.True(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
    }

    [Fact]
    public async Task PinAsync_MissingManifestPayloadPreservesArchiveAndRegistry()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("pin-missing-payload", "pin payload"u8.ToArray());
        await fixture.RegisterAndSnapshotAsync(store, run);
        fixture.ChangeManifestPayloadId(run.ManifestBase.RunId, "99999999");
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.PinAsync(run.ManifestBase.RunId, pin: true, CancellationToken.None));

        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Equal(0, store.GetStatus().PinnedBytes);
        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
    }

    [Fact]
    public async Task RestoreAsync_MissingManifestPayloadRemovesPartialStageAndPreservesArchive()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("restore-missing-payload", "restore payload"u8.ToArray());
        await fixture.RegisterAndSnapshotAsync(store, run);
        fixture.ChangeManifestPayloadId(run.ManifestBase.RunId, "99999999");
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);
        var repositoryDirectoriesBefore = fixture.CaptureRepositoryDirectoryNames();
        var destination = TestPathUtils.PathUnder(fixture.RepositoryPath, "restore-missing-payload-output");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.RestoreAsync(run.ManifestBase.RunId, destination, CancellationToken.None));

        Assert.False(Directory.Exists(destination));
        Assert.Equal(repositoryDirectoriesBefore, fixture.CaptureRepositoryDirectoryNames());
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();
        private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-10-22T12:00:00Z"));

        internal Fixture()
        {
            StoreRoot = TestPathUtils.PathUnder(_temp.Path, "private-store");
            RepositoryPath = TestPathUtils.PathUnder(_temp.Path, "repository");
            Directory.CreateDirectory(RepositoryPath);
            using (CoverageArtifactStore.Open(StoreRoot, _clock)) { }
        }

        internal string StoreRoot { get; }
        internal string RepositoryPath { get; }
        internal string RegistryPath => TestPathUtils.PathUnder(StoreRoot, "registry.json");
        internal string RegistryStagePath => TestPathUtils.PathUnder(StoreRoot, "staging", ".registry-write.json");
        internal string StagingPath => TestPathUtils.PathUnder(StoreRoot, "staging");

        internal CoverageArtifactStore OpenStore(long historyMaxBytes = 2_147_483_648, int historyMaxAgeDays = 7)
        {
            WritePolicy(historyMaxBytes, historyMaxAgeDays);
            return CoverageArtifactStore.Open(StoreRoot, _clock);
        }

        internal CoverageArtifactCurrentRun CreateRun(string name, byte[] contents, string? existingOutputPath = null)
        {
            var outputPath = existingOutputPath ?? TestPathUtils.PathUnder(RepositoryPath, name);
            Directory.CreateDirectory(outputPath);
            File.WriteAllBytes(TestPathUtils.PathUnder(outputPath, "coverage.xml"), contents);
            var runId = Guid.NewGuid().ToString("N");
            var manifest = new CoverageArtifactRunManifest
            {
                RunId = runId,
                Outcome = "success",
                CreatedAtUtc = _clock.GetUtcNow(),
                Version = "final-path-tests",
                Provenance = new CoverageArtifactProvenance("revision", "clean", "configuration")
            };
            var inventory = new CoverageArtifactInventoryEntry(
                "coverage.xml", contents.LongLength, Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant());
            return new CoverageArtifactCurrentRun(manifest, [inventory], outputPath, RepositoryPath);
        }

        internal async Task RegisterAndSnapshotAsync(CoverageArtifactStore store, CoverageArtifactCurrentRun run)
        {
            await store.RegisterAsync(run, CancellationToken.None);
            await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        }

        internal async Task<CoverageArtifactCurrentRun> CreateEligibleArchiveAsync(CoverageArtifactStore store, string name)
        {
            var archived = CreateRun(name, Encoding.UTF8.GetBytes("archived " + name));
            await RegisterAndSnapshotAsync(store, archived);
            var current = CreateRun(name + "-current", "current"u8.ToArray(), archived.OutputPath);
            await store.RegisterAsync(current, CancellationToken.None);
            return archived;
        }

        internal string ArchivePath(string runId) => TestPathUtils.PathUnder(StoreRoot, "archives", runId);
        internal string ManifestPath(string runId) => TestPathUtils.PathUnder(ArchivePath(runId), "manifest.json");
        internal string SourceFilePath(CoverageArtifactCurrentRun run) => TestPathUtils.PathUnder(run.OutputPath, "coverage.xml");
        internal string[] CaptureRepositoryDirectoryNames() => Directory
            .EnumerateDirectories(RepositoryPath, "*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        internal JsonObject ReadRegistry() => JsonNode.Parse(File.ReadAllBytes(RegistryPath))!.AsObject();

        internal void WriteRegistry(JsonObject registry)
        {
            File.WriteAllBytes(RegistryPath, JsonSerializer.SerializeToUtf8Bytes(registry));
            SetPrivateFileMode(RegistryPath);
        }

        internal void ChangeManifestPayloadId(string runId, string payloadId)
        {
            var manifest = JsonNode.Parse(File.ReadAllBytes(ManifestPath(runId)))!.AsObject();
            manifest["files"]!.AsArray()[0]!.AsObject()["payloadId"] = payloadId;
            File.WriteAllText(ManifestPath(runId), manifest.ToJsonString());
            SetPrivateFileMode(ManifestPath(runId));
        }

        internal Dictionary<string, byte[]> CaptureArchiveFiles(string runId)
        {
            var archivePath = ArchivePath(runId);
            return Directory.EnumerateFiles(archivePath, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(archivePath, path), File.ReadAllBytes, StringComparer.Ordinal);
        }

        internal void AssertArchiveFilesEqual(string runId, IReadOnlyDictionary<string, byte[]> expected)
        {
            var actual = CaptureArchiveFiles(runId);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
            foreach (var path in expected.Keys)
                Assert.Equal(Convert.ToHexString(expected[path]), Convert.ToHexString(actual[path]));
        }

        internal void CorruptPayload(string runId)
        {
            var payloadPath = Directory.EnumerateFiles(ArchivePath(runId), "*.gz").Single();
            var bytes = File.ReadAllBytes(payloadPath);
            Assert.NotEmpty(bytes);
            bytes[0] ^= 0x80;
            File.WriteAllBytes(payloadPath, bytes);
            SetPrivateFileMode(payloadPath);
        }

        private void WritePolicy(long historyMaxBytes, int historyMaxAgeDays)
        {
            var path = TestPathUtils.PathUnder(StoreRoot, "policy.json");
            var bytes = Encoding.UTF8.GetBytes(
                $"{{\"schemaVersion\":1,\"historyMaxBytes\":{historyMaxBytes},\"historyMaxAgeDays\":{historyMaxAgeDays}}}");
            if (File.Exists(path))
                File.WriteAllBytes(path, bytes);
            else
            {
                using var root = CoverageRunOutputLease.AcquireArtifactDirectory(StoreRoot, false, true)!;
                using var policy = root.OpenArtifactFile("policy.json", createNew: true);
                policy.Write(bytes);
                policy.Flush(true);
            }
            SetPrivateFileMode(path);
        }

        private static void SetPrivateFileMode(string path)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
