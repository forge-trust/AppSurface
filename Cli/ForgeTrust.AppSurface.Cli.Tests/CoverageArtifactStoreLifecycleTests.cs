using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreLifecycleTests
{
    [Fact]
    public async Task Snapshot_IsStableAcrossRepeats_AndMaintenanceBoundsHistoryForAReusedOutput()
    {
        using var fixture = new StoreFixture();
        using var store = fixture.OpenStore(historyMaxBytes: 0);
        var output = fixture.CreateOutput("coverage");
        var first = fixture.CreateRun(output, "coverage.xml", [1, 2, 3], "success");

        await store.RegisterAsync(first, CancellationToken.None);
        Assert.Equal(first.ManifestBase.RunId, store.GetCurrentRun(output)!.ManifestBase.RunId);
        var firstManifest = await store.SnapshotAsync(first, pin: false, automatic: false, CancellationToken.None);
        var repeatedManifest = await store.SnapshotAsync(first, pin: false, automatic: false, CancellationToken.None);

        Assert.True(firstManifest.Complete);
        Assert.Equal(firstManifest.RunId, repeatedManifest.RunId);
        Assert.Equal(firstManifest.ArchivedAtUtc, repeatedManifest.ArchivedAtUtc);
        Assert.Equal(firstManifest.Files, repeatedManifest.Files);
        Assert.Equal(1, store.GetStatus().ArchivedRuns);

        var second = fixture.CreateRun(output, "coverage.xml", [4, 5, 6, 7], "success");
        await store.RegisterAsync(second, CancellationToken.None);
        Assert.Equal(second.ManifestBase.RunId, store.GetCurrentRun(output)!.ManifestBase.RunId);
        await store.SnapshotAsync(second, pin: false, automatic: false, CancellationToken.None);

        var beforePreview = store.GetStatus();
        var preview = await store.MaintainAsync(apply: false, CancellationToken.None);
        Assert.Equal([first.ManifestBase.RunId], preview);
        Assert.Equal(beforePreview.ArchivedRuns, store.GetStatus().ArchivedRuns);
        Assert.Equal(first.ManifestBase.RunId,
            Assert.Single(await store.MaintainAsync(apply: true, CancellationToken.None)));

        var afterMaintenance = store.GetStatus();
        Assert.Equal(2, afterMaintenance.RegisteredRuns);
        Assert.Equal(1, afterMaintenance.CurrentRuns);
        Assert.Equal(1, afterMaintenance.ArchivedRuns);
        Assert.False(Directory.Exists(fixture.ArchivePath(first.ManifestBase.RunId)));
        Assert.True(Directory.Exists(fixture.ArchivePath(second.ManifestBase.RunId)));
    }

    [Fact]
    public async Task Maintain_EvictsSuccessBeforeFailure_AndPreservesPinnedHistoryUnderQuota()
    {
        using var fixture = new StoreFixture();
        var output = fixture.CreateOutput("coverage");
        string successId;
        string failedId;
        string pinnedId;
        long successBytes;
        long failedBytes;
        long archiveBytes;

        using (var store = fixture.OpenStore())
        {
            var success = fixture.CreateRun(output, "coverage.xml", [1, 1, 1, 1], "success");
            successId = success.ManifestBase.RunId;
            await store.RegisterAsync(success, CancellationToken.None);
            await store.SnapshotAsync(success, pin: false, automatic: false, CancellationToken.None);

            var failed = fixture.CreateRun(output, "coverage.xml", [2, 2, 2, 2, 2], "failed");
            failedId = failed.ManifestBase.RunId;
            await store.RegisterAsync(failed, CancellationToken.None);
            await store.SnapshotAsync(failed, pin: false, automatic: false, CancellationToken.None);

            var pinned = fixture.CreateRun(output, "coverage.xml", [3, 3, 3, 3, 3, 3], "success");
            pinnedId = pinned.ManifestBase.RunId;
            await store.RegisterAsync(pinned, CancellationToken.None);
            await store.PinAsync(pinnedId, pin: true, CancellationToken.None);

            var current = fixture.CreateRun(output, "coverage.xml", [4], "success");
            await store.RegisterAsync(current, CancellationToken.None);
            successBytes = fixture.DirectoryBytes(fixture.ArchivePath(successId));
            failedBytes = fixture.DirectoryBytes(fixture.ArchivePath(failedId));
            archiveBytes = store.GetStatus().ArchivedBytes;
        }

        // Leave room for failed history and metadata, but not successful history. Pins are separate.
        long metadataBytes;
        using (var measured = fixture.OpenStore()) metadataBytes = measured.GetStatus().MetadataBytes;
        Assert.True(archiveBytes > successBytes + failedBytes);
        using (var bounded = fixture.OpenStore(historyMaxBytes: failedBytes + metadataBytes + 64))
        {
            var preview = await bounded.MaintainAsync(apply: false, CancellationToken.None);
            Assert.Equal([successId], preview);
            Assert.Equal(3, bounded.GetStatus().ArchivedRuns);
            Assert.Equal([successId], await bounded.MaintainAsync(apply: true, CancellationToken.None));
            Assert.Equal(2, bounded.GetStatus().ArchivedRuns);
            Assert.True(Directory.Exists(fixture.ArchivePath(failedId)));
            Assert.True(Directory.Exists(fixture.ArchivePath(pinnedId)));
        }

        // A zero-byte limit can now expire the failed archive, while the non-current pinned archive remains protected.
        using var zeroLimit = fixture.OpenStore(historyMaxBytes: 0);
        Assert.Equal([failedId], await zeroLimit.MaintainAsync(apply: false, CancellationToken.None));
        Assert.True(Directory.Exists(fixture.ArchivePath(pinnedId)));
    }

    [Fact]
    public async Task Maintain_UsesArchiveAgeEvenAfterGateRefreshesCurrentRegistration()
    {
        using var fixture = new StoreFixture();
        using var store = fixture.OpenStore(historyMaxAgeDays: 1);
        var output = fixture.CreateOutput("coverage");
        var first = fixture.CreateRun(output, "coverage.xml", [1, 2, 3], "success");
        await store.RegisterAsync(first, CancellationToken.None);
        await store.SnapshotAsync(first, false, false, CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromDays(2));

        // A standalone gate republishes the same current run's registration. This observation
        // must not extend the age of a snapshot already verified two days earlier.
        await store.RegisterAsync(first, CancellationToken.None);
        var second = fixture.CreateRun(output, "coverage.xml", [4], "success");
        await store.RegisterAsync(second, CancellationToken.None);

        Assert.Equal([first.ManifestBase.RunId], await store.MaintainAsync(false, CancellationToken.None));
        Assert.Equal([first.ManifestBase.RunId], await store.MaintainAsync(true, CancellationToken.None));
        Assert.False(Directory.Exists(fixture.ArchivePath(first.ManifestBase.RunId)));
    }

    [Fact]
    public async Task Maintain_PinnedArchiveDoesNotConsumeEligibleHistoryBudget()
    {
        using var fixture = new StoreFixture();
        var output = fixture.CreateOutput("coverage");
        string retainedId;
        string pinnedId;
        using (var store = fixture.OpenStore())
        {
            var retained = fixture.CreateRun(output, "coverage.xml", [1, 2, 3], "success");
            retainedId = retained.ManifestBase.RunId;
            await store.RegisterAsync(retained, CancellationToken.None);
            await store.SnapshotAsync(retained, false, false, CancellationToken.None);
            var pinned = fixture.CreateRun(output, "coverage.xml", [4, 5, 6], "success");
            pinnedId = pinned.ManifestBase.RunId;
            await store.RegisterAsync(pinned, CancellationToken.None);
            await store.PinAsync(pinnedId, true, CancellationToken.None);
            await store.RegisterAsync(fixture.CreateRun(output, "coverage.xml", [7], "success"), CancellationToken.None);
        }
        long budget;
        using (var measured = fixture.OpenStore())
            budget = measured.GetStatus().MetadataBytes + fixture.DirectoryBytes(fixture.ArchivePath(retainedId)) + 64;
        using var bounded = fixture.OpenStore(historyMaxBytes: budget);

        Assert.Empty(await bounded.MaintainAsync(false, CancellationToken.None));
        Assert.Empty(await bounded.MaintainAsync(true, CancellationToken.None));
        Assert.True(Directory.Exists(fixture.ArchivePath(retainedId)));
        Assert.True(Directory.Exists(fixture.ArchivePath(pinnedId)));
    }

    [Fact]
    public async Task PinCurrent_SnapshotsVerifiedRun_AndRestorePreservesBytesProvenanceAndNoClobber()
    {
        using var fixture = new StoreFixture();
        using var store = fixture.OpenStore();
        var output = fixture.CreateOutput("coverage");
        var original = Encoding.UTF8.GetBytes("verified coverage bytes\0\n");
        var run = fixture.CreateRun(output, "nested/coverage.xml", original, "success");

        await store.RegisterAsync(run, CancellationToken.None);
        await store.PinAsync(run.ManifestBase.RunId, pin: true, CancellationToken.None);
        var manifest = await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        Assert.True(manifest.Complete);
        Assert.Equal(run.ManifestBase.Provenance, manifest.Provenance);
        Assert.Equal(1, store.GetStatus().ArchivedRuns);

        using var restoreParent = fixture.CreatePrivateDirectory("restore-parent");
        var restored = Path.Join(restoreParent.ArtifactDirectoryPath, "restored");
        await store.RestoreAsync(run.ManifestBase.RunId, restored, CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(Path.Join(restored, "nested", "coverage.xml")));

        using (var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Join(restored, "coverage-history.json"))))
        {
            Assert.Equal(1, receipt.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(run.ManifestBase.RunId, receipt.RootElement.GetProperty("runId").GetString());
            Assert.True(receipt.RootElement.GetProperty("historical").GetBoolean());
            var provenance = receipt.RootElement.GetProperty(nameof(CoverageArtifactRunManifest.Provenance));
            Assert.Equal(run.ManifestBase.Provenance!.GitRevisionFingerprint,
                provenance.GetProperty(nameof(CoverageArtifactProvenance.GitRevisionFingerprint)).GetString());
            Assert.Equal(run.ManifestBase.Provenance.GitStateFingerprint,
                provenance.GetProperty(nameof(CoverageArtifactProvenance.GitStateFingerprint)).GetString());
            Assert.Equal(run.ManifestBase.Provenance.ConfigurationFingerprint,
                provenance.GetProperty(nameof(CoverageArtifactProvenance.ConfigurationFingerprint)).GetString());
        }

        using (var existing = restoreParent.CreateArtifactDirectory("existing"))
        {
            using (var sentinel = existing.OpenArtifactFile("sentinel.txt", createNew: true))
            {
                var bytes = Encoding.UTF8.GetBytes("leave this directory alone");
                sentinel.Write(bytes);
                sentinel.Flush(true);
            }
            await Assert.ThrowsAsync<IOException>(() => store.RestoreAsync(
                run.ManifestBase.RunId, existing.ArtifactDirectoryPath, CancellationToken.None));
            Assert.Equal("leave this directory alone", File.ReadAllText(Path.Join(existing.ArtifactDirectoryPath, "sentinel.txt")));
        }
    }

    [Fact]
    public async Task FailedSnapshot_ReleasesStagingReservationSoTheSameRunCanBeRetried()
    {
        using var fixture = new StoreFixture();
        using var store = fixture.OpenStore();
        var output = fixture.CreateOutput("coverage");
        var run = fixture.CreateRun(output, "coverage.xml", [8, 9, 10], "success");
        await store.RegisterAsync(run, CancellationToken.None);

        File.WriteAllBytes(Path.Join(output, "coverage.xml"), [0, 0, 0]);
        await Assert.ThrowsAnyAsync<IOException>(() => store.SnapshotAsync(
            run, pin: false, automatic: false, CancellationToken.None));
        Assert.Equal(0, store.GetStatus().ArchivedRuns);

        File.WriteAllBytes(Path.Join(output, "coverage.xml"), [8, 9, 10]);
        var retried = await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);

        Assert.True(retried.Complete);
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
    }

    [Fact]
    public async Task ReadOnlyExpiryPreviewAndCorruptOrUnknownState_PreserveStoreFilesAndCounts()
    {
        using var fixture = new StoreFixture();
        var output = fixture.CreateOutput("coverage");
        string expiringId;
        CoverageArtifactCurrentRun current;
        using (var store = fixture.OpenStore(historyMaxBytes: 2_000_000_000, historyMaxAgeDays: 1))
        {
            var expiring = fixture.CreateRun(output, "coverage.xml", [1, 2, 3], "success");
            expiringId = expiring.ManifestBase.RunId;
            await store.RegisterAsync(expiring, CancellationToken.None);
            await store.SnapshotAsync(expiring, pin: false, automatic: false, CancellationToken.None);
            current = fixture.CreateRun(output, "coverage.xml", [4, 5, 6, 7], "success");
            await store.RegisterAsync(current, CancellationToken.None);
        }

        fixture.Clock.Advance(TimeSpan.FromDays(2));
        var beforePreview = fixture.CaptureStoreFiles();
        using (var readOnly = fixture.OpenReadOnly())
        {
            var beforeStatus = readOnly.GetStatus();
            Assert.Equal(2, beforeStatus.RegisteredRuns);
            Assert.Equal(1, beforeStatus.ArchivedRuns);
            Assert.Equal([expiringId], await readOnly.MaintainAsync(apply: false, CancellationToken.None));
            Assert.Equal(beforeStatus.ArchivedRuns, readOnly.GetStatus().ArchivedRuns);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                readOnly.MaintainAsync(apply: true, CancellationToken.None));
        }
        fixture.AssertStoreFilesEqual(beforePreview);
        Assert.True(Directory.Exists(fixture.ArchivePath(expiringId)));

        using (var store = fixture.OpenStore(historyMaxBytes: 2_000_000_000, historyMaxAgeDays: 1))
        {
            Assert.Equal([expiringId], await store.MaintainAsync(apply: true, CancellationToken.None));
            Assert.Equal(0, store.GetStatus().ArchivedRuns);

            var currentManifest = await store.SnapshotAsync(current, pin: false, automatic: false, CancellationToken.None);
            Assert.True(currentManifest.Complete);
        }

        fixture.CorruptOnePayload(current.ManifestBase.RunId);
        var corruptedArchive = fixture.CaptureDirectoryFiles(fixture.ArchivePath(current.ManifestBase.RunId));
        using (var store = fixture.OpenStore(historyMaxBytes: 2_000_000_000, historyMaxAgeDays: 1))
        using (var restoreParent = fixture.CreatePrivateDirectory("corrupt-restore-parent"))
        {
            var destination = Path.Join(restoreParent.ArtifactDirectoryPath, "must-not-publish");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.RestoreAsync(current.ManifestBase.RunId, destination, CancellationToken.None));
            Assert.False(Directory.Exists(destination));
            Assert.Empty(Directory.EnumerateDirectories(restoreParent.ArtifactDirectoryPath, "restore-*"));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SnapshotAsync(current, false, false, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.PinAsync(current.ManifestBase.RunId, true, CancellationToken.None));
            Assert.Equal(1, store.GetStatus().ArchivedRuns);
            fixture.AssertDirectoryFilesEqual(fixture.ArchivePath(current.ManifestBase.RunId), corruptedArchive);
        }

        fixture.WritePolicyJson("{\"schemaVersion\":2}");
        var unknownSchemaState = fixture.CaptureStoreFiles();
        Assert.Throws<InvalidDataException>(() => fixture.OpenReadOnly());
        fixture.AssertStoreFilesEqual(unknownSchemaState);
        Assert.True(Directory.Exists(fixture.ArchivePath(current.ManifestBase.RunId)));
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();

        internal StoreFixture()
        {
            StoreRoot = Path.Join(_temp.Path, "private-store");
            using (CoverageRunOutputLease.AcquireArtifactDirectory(StoreRoot, createMissing: true, requirePrivate: true)) { }
            Clock = new TestClock(DateTimeOffset.Parse("2026-10-21T12:00:00Z"));
            using (CoverageArtifactStore.Open(StoreRoot, Clock)) { }
            WritePolicyJson("{\"schemaVersion\":1}");
            RepositoryPath = _temp.CreateDirectory("repository");
        }

        internal string StoreRoot { get; }
        internal string RepositoryPath { get; }
        internal TestClock Clock { get; }

        internal CoverageArtifactStore OpenStore(long historyMaxBytes = 2_147_483_648, int historyMaxAgeDays = 7)
        {
            WritePolicy(historyMaxBytes, historyMaxAgeDays);
            return CoverageArtifactStore.Open(StoreRoot, Clock);
        }

        internal CoverageArtifactStore OpenReadOnly() => CoverageArtifactStore.OpenReadOnly(StoreRoot, Clock);

        internal string CreateOutput(string name)
        {
            var path = Path.Join(RepositoryPath, name);
            Directory.CreateDirectory(path);
            return path;
        }

        internal CoverageArtifactCurrentRun CreateRun(string output, string relativePath, byte[] contents, string outcome)
        {
            var filePath = TestPathUtils.PathUnder(output, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllBytes(filePath, contents);
            var runId = Guid.NewGuid().ToString("N");
            var provenance = new CoverageArtifactProvenance("revision-" + runId[..8], "clean", "config-" + runId[..8]);
            var manifest = new CoverageArtifactRunManifest
            {
                RunId = runId,
                Outcome = outcome,
                CreatedAtUtc = Clock.GetUtcNow(),
                Version = "lifecycle-test",
                Provenance = provenance,
            };
            var entry = new CoverageArtifactInventoryEntry(relativePath, contents.LongLength, Hash(contents));
            return new CoverageArtifactCurrentRun(manifest, [entry], output, RepositoryPath);
        }

        internal CoverageRunOutputLease CreatePrivateDirectory(string name) =>
            CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(_temp.Path, name), createMissing: true, requirePrivate: true)!;

        internal string ArchivePath(string runId) => Path.Join(StoreRoot, "archives", runId);

        internal long DirectoryBytes(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

        internal void CorruptOnePayload(string runId)
        {
            var payload = Directory.EnumerateFiles(ArchivePath(runId), "*.gz").Single();
            var bytes = File.ReadAllBytes(payload);
            Assert.NotEmpty(bytes);
            bytes[0] ^= 0xff;
            File.WriteAllBytes(payload, bytes);
        }

        internal Dictionary<string, byte[]> CaptureStoreFiles() => CaptureDirectoryFiles(StoreRoot);

        internal Dictionary<string, byte[]> CaptureDirectoryFiles(string directory) =>
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes, StringComparer.Ordinal);

        internal void AssertStoreFilesEqual(IReadOnlyDictionary<string, byte[]> expected) =>
            AssertDirectoryFilesEqual(StoreRoot, expected);

        internal void AssertDirectoryFilesEqual(string directory, IReadOnlyDictionary<string, byte[]> expected)
        {
            var actual = CaptureDirectoryFiles(directory);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
            foreach (var path in expected.Keys)
                Assert.Equal(Convert.ToHexString(expected[path]), Convert.ToHexString(actual[path]));
        }

        internal void WritePolicy(long historyMaxBytes, int historyMaxAgeDays) => WritePolicyJson(
            $"{{\"schemaVersion\":1,\"historyMaxBytes\":{historyMaxBytes},\"historyMaxAgeDays\":{historyMaxAgeDays}}}");

        internal void WritePolicyJson(string json)
        {
            var path = Path.Join(StoreRoot, "policy.json");
            if (File.Exists(path))
            {
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
            }
            else
            {
                using var root = CoverageRunOutputLease.AcquireArtifactDirectory(StoreRoot, createMissing: false, requirePrivate: true)!;
                using var policy = root.OpenArtifactFile("policy.json", createNew: true);
                var bytes = Encoding.UTF8.GetBytes(json);
                policy.Write(bytes);
                policy.Flush(true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private static string Hash(byte[] contents) =>
            Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();

        public void Dispose() => _temp.Dispose();
    }

    private sealed class TestClock(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
