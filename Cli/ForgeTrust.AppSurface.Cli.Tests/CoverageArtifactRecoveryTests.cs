using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(null, true)]
    public async Task Maintain_ReconcilesPublishedArchiveAfterInterruptedMetadataAndAllowsRestore(bool? pinIntent, bool expectedPinned)
    {
        using var fixture = new Fixture();
        var contents = Encoding.UTF8.GetBytes("complete archive published before metadata registration\0\n");
        var run = fixture.CreateRun(contents);
        using (var store = fixture.OpenStore())
        {
            await store.RegisterAsync(run, CancellationToken.None);
            Assert.True((await store.SnapshotAsync(run, pinIntent ?? false, automatic: false, CancellationToken.None)).Complete);
        }
        var reservationId = fixture.SimulatePublishedButUnregistered(run.ManifestBase.RunId, pinIntent);

        using (var store = fixture.OpenStore())
        {
            Assert.Empty(await store.MaintainAsync(apply: true, CancellationToken.None));
            var status = store.GetStatus();
            Assert.Equal(1, status.ArchivedRuns);
            Assert.Equal(0, status.StagingReservedBytes);
            Assert.Equal(expectedPinned, status.PinnedBytes > 0);

            using var restoreParent = fixture.CreatePrivateDirectory("restore-parent");
            var restored = Path.Join(restoreParent.ArtifactDirectoryPath, "restored");
            await store.RestoreAsync(run.ManifestBase.RunId, restored, CancellationToken.None);
            Assert.Equal(contents, File.ReadAllBytes(Path.Join(restored, "coverage.xml")));
        }

        Assert.False(fixture.HasReservation(reservationId));
    }

    [Fact]
    public async Task Maintain_PreservesCorruptPublishedArchiveAndReservationWithoutRegisteringIt()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun(Encoding.UTF8.GetBytes("archive whose payload will be corrupted"));
        using (var store = fixture.OpenStore())
        {
            await store.RegisterAsync(run, CancellationToken.None);
            await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        }
        fixture.SimulatePublishedButUnregistered(run.ManifestBase.RunId, pinIntent: false);
        fixture.CorruptOnePayload(run.ManifestBase.RunId);
        var archiveBefore = fixture.CaptureArchiveFiles(run.ManifestBase.RunId);

        using (var store = fixture.OpenStore())
        {
            await store.MaintainAsync(apply: true, CancellationToken.None);
            Assert.Equal(0, store.GetStatus().ArchivedRuns);
            Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, store.GetStatus().StagingReservedBytes);
        }

        fixture.AssertArchiveFilesEqual(run.ManifestBase.RunId, archiveBefore);
    }

    [Fact]
    public async Task Maintain_ActiveCompatibleSourceWriterPreventsArchiveRecovery()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun(Encoding.UTF8.GetBytes("wait until the compatible writer releases its lease"));
        using (var store = fixture.OpenStore())
        {
            await store.RegisterAsync(run, CancellationToken.None);
            await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        }
        fixture.SimulatePublishedButUnregistered(run.ManifestBase.RunId, pinIntent: false);

        using (CoverageArtifactOperationLease.Acquire(run.OutputPath, exclusive: false))
        using (var store = fixture.OpenStore())
        {
            await store.MaintainAsync(apply: true, CancellationToken.None);
            Assert.Equal(0, store.GetStatus().ArchivedRuns);
            Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, store.GetStatus().StagingReservedBytes);
            Assert.True(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
        }

        using (var store = fixture.OpenStore())
        {
            await store.MaintainAsync(apply: true, CancellationToken.None);
            Assert.Equal(1, store.GetStatus().ArchivedRuns);
            Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        }
    }

    [Fact]
    public async Task Maintain_StillReclaimsAbandonedStageAndOnlyItsMatchingReservation()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun(Encoding.UTF8.GetBytes("stage-only crash window"));
        using (var store = fixture.OpenStore()) await store.RegisterAsync(run, CancellationToken.None);
        var reservationId = fixture.AddReservation(run.ManifestBase.RunId, pinIntent: false);
        var stageName = "run-" + reservationId;
        var stagingPath = Path.Join(fixture.StoreRoot, "staging");
        using (var staging = CoverageRunOutputLease.AcquireArtifactDirectory(stagingPath, false, true)!)
            staging.CreateArtifactDirectory(stageName).Dispose();

        using (var store = fixture.OpenStore())
        {
            await store.MaintainAsync(apply: true, CancellationToken.None);
            Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        }

        Assert.False(Directory.Exists(Path.Join(stagingPath, stageName)));
    }

    private sealed class Fixture : IDisposable
    {
        private const string ReservationId = "11111111111141118111111111111111";
        private readonly TestDirectory _temp = TestDirectory.Create();
        private readonly string _repositoryPath;
        private readonly string _outputPath;

        internal Fixture()
        {
            StoreRoot = Path.Join(_temp.Path, "private-store");
            using (CoverageRunOutputLease.AcquireArtifactDirectory(StoreRoot, createMissing: true, requirePrivate: true)) { }
            _repositoryPath = _temp.CreateDirectory("repository");
            _outputPath = _temp.CreateDirectory("repository/coverage");
            using (CoverageArtifactStore.Open(StoreRoot)) { }
        }

        internal string StoreRoot { get; }
        internal string ArchivePath(string runId) => Path.Join(StoreRoot, "archives", runId);

        internal CoverageArtifactStore OpenStore() => CoverageArtifactStore.Open(StoreRoot);

        internal CoverageArtifactCurrentRun CreateRun(byte[] contents)
        {
            File.WriteAllBytes(Path.Join(_outputPath, "coverage.xml"), contents);
            var runId = Guid.NewGuid().ToString("N");
            var inventory = new CoverageArtifactInventoryEntry("coverage.xml", contents.LongLength,
                Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant());
            return new(new CoverageArtifactRunManifest
            {
                RunId = runId,
                Outcome = "success",
                CreatedAtUtc = DateTimeOffset.Parse("2026-10-21T12:00:00Z"),
                Version = "recovery-test",
                Provenance = new CoverageArtifactProvenance("revision", "clean", "configuration")
            }, [inventory], _outputPath, _repositoryPath);
        }

        internal string SimulatePublishedButUnregistered(string runId, bool? pinIntent)
        {
            var reservationId = AddReservation(runId, pinIntent);
            var registry = ReadRegistry();
            var registration = registry["Registrations"]!.AsArray()
                .Single(item => item!["RunId"]!.GetValue<string>() == runId)!;
            registration["Archived"] = false;
            registration["Pinned"] = false;
            registration["ArchiveBytes"] = 0;
            registration["ArchivedAtUtc"] = (System.Text.Json.Nodes.JsonNode?)null;
            WriteRegistry(registry);
            return reservationId;
        }

        internal string AddReservation(string runId, bool? pinIntent)
        {
            var registry = ReadRegistry();
            var reservation = new JsonObject
            {
                ["Id"] = ReservationId,
                ["RunId"] = runId,
                ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
                ["StartedAtUtc"] = "2026-10-21T12:00:00Z"
            };
            if (pinIntent is { } pin) reservation["Pin"] = pin;
            registry["Reservations"] = new JsonArray(reservation);
            WriteRegistry(registry);
            return ReservationId;
        }

        internal CoverageRunOutputLease CreatePrivateDirectory(string name) =>
            CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(_temp.Path, name), createMissing: true, requirePrivate: true)!;

        internal Dictionary<string, byte[]> CaptureArchiveFiles(string runId) =>
            Directory.EnumerateFiles(ArchivePath(runId), "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(ArchivePath(runId), path), File.ReadAllBytes, StringComparer.Ordinal);

        internal bool HasReservation(string reservationId) => ReadRegistry()["Reservations"]!.AsArray()
            .Any(item => item!["Id"]!.GetValue<string>() == reservationId);

        internal void AssertArchiveFilesEqual(string runId, IReadOnlyDictionary<string, byte[]> expected)
        {
            var actual = CaptureArchiveFiles(runId);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
            foreach (var path in expected.Keys)
                Assert.Equal(Convert.ToHexString(expected[path]), Convert.ToHexString(actual[path]));
        }

        internal void CorruptOnePayload(string runId)
        {
            var payloadPath = Directory.EnumerateFiles(ArchivePath(runId), "*.gz").Single();
            var bytes = File.ReadAllBytes(payloadPath);
            Assert.NotEmpty(bytes);
            bytes[0] ^= 0xff;
            File.WriteAllBytes(payloadPath, bytes);
        }

        private JsonObject ReadRegistry() => JsonNode.Parse(File.ReadAllText(Path.Join(StoreRoot, "registry.json")))!.AsObject();

        private void WriteRegistry(JsonObject registry)
        {
            File.WriteAllText(Path.Join(StoreRoot, "registry.json"), registry.ToJsonString());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Join(StoreRoot, "registry.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose() => _temp.Dispose();
    }
}
