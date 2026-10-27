using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreCoverageGapTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Snapshot_NormalizesBlankVersionInPublishedManifest(string version)
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var run = fixture.CreateRun("blank-version", "coverage bytes"u8.ToArray(), version);
        await store.RegisterAsync(run, CancellationToken.None);

        var manifest = await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);

        Assert.True(manifest.Complete);
        Assert.Equal(CoverageArtifactInventory.ProducingVersion, manifest.Version);
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
        Assert.Equal("coverage bytes", File.ReadAllText(Path.Join(run.OutputPath, "coverage.xml")));
    }

    [Fact]
    public async Task Maintain_RetainsRecentArchiveWhenHistoryAllowanceIsNotExceeded()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore(historyMaxBytes: 2_147_483_648);
        var archived = fixture.CreateRun("recent-archive", "history"u8.ToArray());
        await fixture.RegisterAndSnapshotAsync(store, archived);
        var current = fixture.CreateRun("current-output", "current"u8.ToArray(), existingOutputPath: archived.OutputPath);
        await store.RegisterAsync(current, CancellationToken.None);

        var removed = await store.MaintainAsync(apply: true, CancellationToken.None);

        Assert.Empty(removed);
        Assert.True(Directory.Exists(fixture.ArchivePath(archived.ManifestBase.RunId)));
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
        Assert.Equal(1, store.GetStatus().CurrentRuns);
    }

    [Fact]
    public async Task Maintain_ExpiryReceiptByteLimitDropsOldestReceiptDuringPolicyEviction()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore(historyMaxBytes: 0);
        var archived = fixture.CreateRun("receipt-eviction", "history"u8.ToArray());
        await fixture.RegisterAndSnapshotAsync(store, archived);
        await store.RegisterAsync(fixture.CreateRun("current-output", "current"u8.ToArray(), existingOutputPath: archived.OutputPath), CancellationToken.None);
        var seeded = fixture.SeedExpiryReceiptsNearByteLimit();
        var budget = new CoverageArtifactWorkBudget(fixture.Clock, TimeSpan.FromSeconds(5));

        var removed = await store.MaintainAsync(apply: true, CancellationToken.None, routineBudget: budget);

        Assert.Equal([archived.ManifestBase.RunId], removed);
        Assert.False(Directory.Exists(fixture.ArchivePath(archived.ManifestBase.RunId)));
        fixture.AssertReceiptPruning(seeded, archived.ManifestBase.RunId, "age-or-byte-policy", store.GetStatus());
    }

    [Fact]
    public async Task Maintain_ExpiryReceiptByteLimitDropsOldestReceiptDuringMissingArchiveRecovery()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var archived = fixture.CreateRun("missing-archive", "history"u8.ToArray());
        await fixture.RegisterAndSnapshotAsync(store, archived);
        await store.RegisterAsync(fixture.CreateRun("current-output", "current"u8.ToArray(), existingOutputPath: archived.OutputPath), CancellationToken.None);
        Directory.Delete(fixture.ArchivePath(archived.ManifestBase.RunId), recursive: true);
        var seeded = fixture.SeedExpiryReceiptsNearByteLimit();

        var removed = await store.MaintainAsync(apply: true, CancellationToken.None);

        Assert.Empty(removed);
        var updated = fixture.ReadRegistry();
        var registration = updated["Registrations"]!.AsArray()
            .Single(item => item!["RunId"]!.GetValue<string>() == archived.ManifestBase.RunId)!;
        Assert.False(registration["Archived"]!.GetValue<bool>());
        Assert.Equal(0, registration["ArchiveBytes"]!.GetValue<long>());
        Assert.False(Directory.Exists(fixture.ArchivePath(archived.ManifestBase.RunId)));
        fixture.AssertReceiptPruning(seeded, archived.ManifestBase.RunId, "archive-missing-reconciled", store.GetStatus());
    }

    [Fact]
    public async Task Maintain_MissingArchiveParentFailsClosedWithoutChangingRegistration()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var archived = fixture.CreateRun("missing-archive-parent", "history"u8.ToArray());
        await fixture.RegisterAndSnapshotAsync(store, archived);
        await store.RegisterAsync(fixture.CreateRun("current-output", "current"u8.ToArray(), existingOutputPath: archived.OutputPath), CancellationToken.None);
        Directory.Delete(Path.Join(fixture.StoreRoot, "archives"), recursive: true);
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            store.MaintainAsync(apply: true, CancellationToken.None));

        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
        Assert.False(Directory.Exists(Path.Join(fixture.StoreRoot, "archives")));
    }

    private sealed class Fixture : IDisposable
    {
        private const int ReceiptReasonLength = 512;
        private readonly TestDirectory _temp = TestDirectory.Create();

        internal Fixture()
        {
            StoreRoot = Path.Join(_temp.Path, "private-store");
            RepositoryPath = _temp.CreateDirectory("repository");
            Clock = new TestClock(DateTimeOffset.Parse("2026-10-22T12:00:00Z"));
            using (CoverageArtifactStore.Open(StoreRoot, Clock)) { }
        }

        internal string StoreRoot { get; }
        internal string RepositoryPath { get; }
        internal string RegistryPath => Path.Join(StoreRoot, "registry.json");
        internal TestClock Clock { get; }

        internal CoverageArtifactStore OpenStore(long historyMaxBytes = 2_147_483_648, int historyMaxAgeDays = 7)
        {
            var policy = $"{{\"schemaVersion\":1,\"historyMaxBytes\":{historyMaxBytes},\"historyMaxAgeDays\":{historyMaxAgeDays}}}";
            TestFileSystem.WritePrivateFile(Path.Join(StoreRoot, "policy.json"), policy);
            return CoverageArtifactStore.Open(StoreRoot, Clock);
        }

        internal CoverageArtifactCurrentRun CreateRun(string name, byte[] contents,
            string version = "coverage-store-coverage-gap", string? existingOutputPath = null)
        {
            var outputPath = existingOutputPath ?? Path.Join(RepositoryPath, name);
            Directory.CreateDirectory(outputPath);
            File.WriteAllBytes(Path.Join(outputPath, "coverage.xml"), contents);
            return new(new CoverageArtifactRunManifest
            {
                RunId = Guid.NewGuid().ToString("N"),
                Outcome = "success",
                CreatedAtUtc = Clock.GetUtcNow(),
                Version = version,
                Provenance = new CoverageArtifactProvenance("revision", "clean", "configuration")
            }, [new CoverageArtifactInventoryEntry("coverage.xml", contents.LongLength,
                Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant())], outputPath, RepositoryPath);
        }

        internal async Task RegisterAndSnapshotAsync(CoverageArtifactStore store, CoverageArtifactCurrentRun run)
        {
            await store.RegisterAsync(run, CancellationToken.None);
            await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        }

        internal string ArchivePath(string runId) => Path.Join(StoreRoot, "archives", runId);

        internal JsonObject ReadRegistry() => JsonNode.Parse(File.ReadAllBytes(RegistryPath))!.AsObject();

        internal void WriteRegistry(JsonObject registry) =>
            TestFileSystem.WritePrivateFile(RegistryPath, registry.ToJsonString());

        internal (int Count, string FirstRunId) SeedExpiryReceiptsNearByteLimit()
        {
            var registry = ReadRegistry();
            var receipts = registry["ExpiryReceipts"]!.AsArray();
            for (var index = 0; index < 5_000; index++)
                receipts.Add(CreateReceipt(index, new string('x', ReceiptReasonLength)));

            var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipts).LongLength;
            while (receiptBytes < CoverageArtifactLimits.ExpiryReceiptsMaxBytes - 32_768)
            {
                var batchEnd = receipts.Count + 32;
                for (var index = receipts.Count; index < batchEnd; index++)
                    receipts.Add(CreateReceipt(index, new string('x', ReceiptReasonLength)));
                receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipts).LongLength;
            }

            var last = receipts[receipts.Count - 1]!.AsObject();
            var reason = last["Reason"]!.GetValue<string>();
            var targetBytes = CoverageArtifactLimits.ExpiryReceiptsMaxBytes - 100;
            last["Reason"] = reason + new string('x', checked((int)(targetBytes - receiptBytes)));
            WriteRegistry(registry);
            return (receipts.Count, receipts[0]!["RunId"]!.GetValue<string>());
        }

        internal void AssertReceiptPruning((int Count, string FirstRunId) seeded, string runId,
            string reason, CoverageArtifactStoreStatus status)
        {
            var receipts = ReadRegistry()["ExpiryReceipts"]!.AsArray();
            Assert.Equal(seeded.Count, receipts.Count);
            Assert.DoesNotContain(receipts, receipt => receipt!["RunId"]!.GetValue<string>() == seeded.FirstRunId);
            Assert.Contains(receipts, receipt => receipt!["RunId"]!.GetValue<string>() == runId
                && receipt["Reason"]!.GetValue<string>() == reason);
            Assert.Equal(seeded.Count, status.ExpiryReceiptCount);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(receipts).LongLength <= CoverageArtifactLimits.ExpiryReceiptsMaxBytes);
        }

        private JsonObject CreateReceipt(int index, string reason) => new()
        {
            ["RunId"] = index.ToString("x32"),
            ["Outcome"] = "failed",
            ["StoredBytes"] = index,
            // A negative offset keeps the string seed and typed DateTimeOffset encoding the same width.
            ["ExpiredAtUtc"] = Clock.GetUtcNow().AddHours(-1).AddTicks(1).ToOffset(TimeSpan.FromHours(-1)).ToString("O"),
            ["Reason"] = reason
        };

        public void Dispose() => _temp.Dispose();
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
    }
}
