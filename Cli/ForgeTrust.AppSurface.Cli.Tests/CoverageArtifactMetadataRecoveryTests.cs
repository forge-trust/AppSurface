using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactMetadataRecoveryTests
{
    [Fact]
    public async Task Maintain_ReclaimsPreStageReservationOnlyAfterSourceLeaseIsReleased()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun(Encoding.UTF8.GetBytes("registered output awaiting its first archive stage"));
        using var store = fixture.OpenStore();
        await store.RegisterAsync(run, CancellationToken.None);
        var reservationId = fixture.AddReservation(run.ManifestBase.RunId);
        var stagePath = Path.Join(fixture.StagingPath, "run-" + reservationId);

        using (CoverageArtifactOperationLease.Acquire(run.OutputPath, exclusive: true))
        {
            await store.MaintainAsync(apply: true, CancellationToken.None);

            Assert.True(fixture.HasReservation(reservationId));
            Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, store.GetStatus().StagingReservedBytes);
            Assert.False(Directory.Exists(stagePath));
        }

        await store.MaintainAsync(apply: true, CancellationToken.None);

        Assert.False(fixture.HasReservation(reservationId));
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        Assert.False(Directory.Exists(stagePath));
    }

    [Fact]
    public async Task Maintain_CountsAndReclaimsPartialFixedRegistryWriteFileButPreservesUnknownStageFile()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenStore();
        var baselineMetadataBytes = store.GetStatus().MetadataBytes;
        var partialRegistryWrite = Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"Registrations\":");
        var pendingPath = fixture.WriteStagingFile(".registry-write.json", partialRegistryWrite);
        var unknownContents = Encoding.UTF8.GetBytes("retain operator-owned staging evidence");
        var unknownPath = fixture.WriteStagingFile("operator-notes.txt", unknownContents);

        Assert.True(store.GetStatus().MetadataBytes >= baselineMetadataBytes + partialRegistryWrite.Length);

        await store.MaintainAsync(apply: true, CancellationToken.None);

        Assert.False(File.Exists(pendingPath));
        Assert.Equal(unknownContents, File.ReadAllBytes(unknownPath));
    }

    [Fact]
    public async Task OversizedFixedRegistryWriteFile_FailsClosedAndRemainsForInspection()
    {
        using var fixture = new Fixture();
        var oversized = new byte[checked((int)CoverageArtifactLimits.RegistryMaxBytes + 1)];
        Array.Fill(oversized, (byte)'x');
        var pendingPath = fixture.WriteStagingFile(".registry-write.json", oversized);
        var expectedHash = SHA256.HashData(oversized);

        using var store = fixture.OpenStore();
        Assert.Throws<InvalidDataException>(() => store.GetStatus());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.MaintainAsync(apply: true, CancellationToken.None));

        var retained = File.ReadAllBytes(pendingPath);
        Assert.Equal(oversized.Length, retained.Length);
        Assert.Equal(expectedHash, SHA256.HashData(retained));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();

        internal Fixture()
        {
            StoreRoot = Path.Join(_temp.Path, "private-store");
            using (CoverageRunOutputLease.AcquireArtifactDirectory(StoreRoot, createMissing: true, requirePrivate: true)) { }
            RepositoryPath = _temp.CreateDirectory("repository");
            OutputPath = _temp.CreateDirectory("repository/coverage");
            using (CoverageArtifactStore.Open(StoreRoot)) { }
        }

        internal string StoreRoot { get; }
        internal string RepositoryPath { get; }
        internal string OutputPath { get; }
        internal string StagingPath => Path.Join(StoreRoot, "staging");

        internal CoverageArtifactStore OpenStore() => CoverageArtifactStore.Open(StoreRoot);

        internal CoverageArtifactCurrentRun CreateRun(byte[] contents)
        {
            File.WriteAllBytes(Path.Join(OutputPath, "coverage.xml"), contents);
            var runId = Guid.NewGuid().ToString("N");
            var inventory = new CoverageArtifactInventoryEntry("coverage.xml", contents.LongLength,
                Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant());
            return new(new CoverageArtifactRunManifest
            {
                RunId = runId,
                Outcome = "success",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Version = "metadata-recovery-test"
            }, [inventory], OutputPath, RepositoryPath);
        }

        internal string AddReservation(string runId)
        {
            using var operation = CoverageArtifactOperationLease.Acquire(StoreRoot, exclusive: true);
            operation.VerifyBinding();
            var registryPath = Path.Join(StoreRoot, "registry.json");
            var registry = JsonNode.Parse(File.ReadAllText(registryPath))!.AsObject();
            var reservationId = Guid.NewGuid().ToString("N");
            registry["Reservations"]!.AsArray().Add(new JsonObject
            {
                ["Id"] = reservationId,
                ["RunId"] = runId,
                ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
                ["StartedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
            });
            File.WriteAllText(registryPath, registry.ToJsonString());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(registryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return reservationId;
        }

        internal bool HasReservation(string reservationId)
        {
            var registry = JsonNode.Parse(File.ReadAllText(Path.Join(StoreRoot, "registry.json")))!.AsObject();
            return registry["Reservations"]!.AsArray()
                .Any(item => item!["Id"]!.GetValue<string>() == reservationId);
        }

        internal string WriteStagingFile(string name, byte[] contents)
        {
            var path = Path.Join(StagingPath, name);
            using (var staging = CoverageRunOutputLease.AcquireArtifactDirectory(StagingPath, false, true)!)
            using (var stream = staging.OpenArtifactFile(name, createNew: true))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return path;
        }

        public void Dispose() => _temp.Dispose();
    }
}
