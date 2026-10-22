using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactRecoveryRacePathsTests
{
    [Theory]
    [InlineData(false, "remove")]
    [InlineData(false, "pin")]
    [InlineData(false, "id")]
    [InlineData(true, "remove")]
    [InlineData(true, "pin")]
    [InlineData(true, "id")]
    public async Task RecoveryDoesNotReleaseReservationChangedSinceSelection(bool published, string change)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        await fixture.PrepareAsync(store, published);
        byte[]? changed = null;
        // The third observation is the selected reservation's first recovery check,
        // after its registry snapshot and before verification/revalidation.
        fixture.Clock.OnObservation(3, () =>
        {
            var state = fixture.Registry();
            var reservations = state["Reservations"]!.AsArray();
            if (change == "remove") reservations.Clear();
            else if (change == "pin") reservations[0]!["Pin"] = true;
            else reservations[0]!["Id"] = Guid.NewGuid().ToString("N");
            fixture.Write(state);
            changed = File.ReadAllBytes(fixture.RegistryPath);
        });
        var budget = new CoverageArtifactWorkBudget(fixture.Clock, TimeSpan.FromSeconds(1));
        Assert.Empty(await store.MaintainAsync(true, default, budget));
        Assert.NotNull(changed);
        Assert.Equal(changed, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertOriginals();
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("archived")]
    [InlineData("inventory")]
    public async Task PublishedRecoveryDoesNotAdoptRegistrationChangedDuringVerification(string change)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        await fixture.PrepareAsync(store, true);
        byte[]? changed = null;
        // The fourth observation is the first payload verification chunk, after
        // recovery selected the registration but before it rereads metadata.
        fixture.Clock.OnObservation(4, () =>
        {
            var state = fixture.Registry();
            var registrations = state["Registrations"]!.AsArray();
            if (change == "remove") registrations.Clear();
            else if (change == "archived") registrations[0]!["Archived"] = true;
            else registrations[0]!["Inventory"]![0]!["Length"] = 7;
            fixture.Write(state);
            changed = File.ReadAllBytes(fixture.RegistryPath);
        });
        var budget = new CoverageArtifactWorkBudget(fixture.Clock, TimeSpan.FromSeconds(1));
        Assert.Empty(await store.MaintainAsync(true, default, budget));
        Assert.NotNull(changed);
        Assert.Equal(changed, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertOriginals();
        Assert.Single(fixture.Registry()["Reservations"]!.AsArray());
    }

    [Fact]
    public async Task PublishedVerificationDeadlinePropagatesAndPreservesItsReservation()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        await fixture.PrepareAsync(store, true);
        var before = File.ReadAllBytes(fixture.RegistryPath);
        // The fourth observation is the first payload verification chunk, after
        // recovery selected the registration but before it rereads metadata.
        fixture.Clock.OnObservation(4, () => fixture.Clock.Expired = true);
        var budget = new CoverageArtifactWorkBudget(fixture.Clock, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => store.MaintainAsync(true, default, budget));
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task BusyOwnedStageIsPreservedUntilItsLeaseIsReleased()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        await fixture.PrepareAsync(store, false);
        var stage = Path.Join(fixture.Domain, "staging", "run-" + fixture.ReservationId);
        using (CoverageRunOutputLease.AcquireArtifactDirectory(stage, true, true)) { }
        var payload = Path.Join(stage, "00000000.gz");
        File.WriteAllText(payload, "stage");
        var before = File.ReadAllBytes(fixture.RegistryPath);
        using (store.AcquireCandidateLease(stage, true))
        {
            Assert.Empty(await store.MaintainAsync(true, default));
            Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
            Assert.Equal("stage", File.ReadAllText(payload));
        }
        Assert.Empty(await store.MaintainAsync(true, default));
        Assert.False(Directory.Exists(stage));
        Assert.Empty(fixture.Registry()["Reservations"]!.AsArray());
        fixture.AssertOriginals();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();
        internal readonly CallbackClock Clock = new();
        internal string Domain => Path.Join(_temp.Path, "domain");
        internal string RegistryPath => Path.Join(Domain, "registry.json");
        internal string ReservationId { get; } = Guid.NewGuid().ToString("N");
        private string Output => Path.Join(_temp.Path, "output");
        private string? _archive;
        private Dictionary<string, byte[]> _payloads = [];
        internal CoverageArtifactStore Open() => CoverageArtifactStore.Open(Domain, Clock);
        internal async Task PrepareAsync(CoverageArtifactStore store, bool published)
        {
            var repository = _temp.CreateDirectory("repository");
            _temp.CreateDirectory("output");
            File.WriteAllText(Path.Join(Output, "coverage.xml"), "original");
            var run = new CoverageArtifactCurrentRun(new()
            { RunId = Guid.NewGuid().ToString("N"), Outcome = "success", Version = "fixture", CreatedAtUtc = Clock.GetUtcNow() },
                [new("coverage.xml", 8, Convert.ToHexString(SHA256.HashData("original"u8)).ToLowerInvariant())], Output, repository);
            await store.RegisterAsync(run, default);
            if (published)
            {
                await store.SnapshotAsync(run, false, false, default);
                _archive = Path.Join(Domain, "archives", run.ManifestBase.RunId);
                _payloads = Directory.EnumerateFiles(_archive).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
            }
            var state = Registry();
            var registration = state["Registrations"]![0]!;
            registration["Archived"] = false;
            registration["ArchiveBytes"] = 0;
            registration["ArchivedAtUtc"] = null;
            state["Reservations"]!.AsArray().Add(new JsonObject
            {
                ["Id"] = ReservationId,
                ["RunId"] = run.ManifestBase.RunId,
                ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
                ["StartedAtUtc"] = Clock.GetUtcNow(),
                ["Pin"] = false
            });
            Write(state);
        }
        internal JsonNode Registry() => JsonNode.Parse(File.ReadAllBytes(RegistryPath))!;
        internal void Write(JsonNode value) => File.WriteAllText(RegistryPath, value.ToJsonString());
        internal void AssertOriginals()
        {
            Assert.Equal("original", File.ReadAllText(Path.Join(Output, "coverage.xml")));
            if (_archive is null) return;
            foreach (var (name, bytes) in _payloads) Assert.Equal(bytes, File.ReadAllBytes(TestPathUtils.PathUnder(_archive, name)));
        }
        public void Dispose() => _temp.Dispose();
    }

    private sealed class CallbackClock : TimeProvider
    {
        private int _count;
        private int _at;
        private Action? _callback;
        internal bool Expired;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-22T00:00:00Z");
        internal void OnObservation(int at, Action action) { _count = 0; _at = at; _callback = action; }
        public override long GetTimestamp()
        {
            if (++_count == _at) { var action = _callback; _callback = null; action?.Invoke(); }
            return Expired ? TimestampFrequency * 2 : 0;
        }
    }
}
