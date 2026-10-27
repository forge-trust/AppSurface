using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreAdmissionTests
{
    private static readonly string ValidHash = new('a', 64);

    [Fact]
    public async Task RegisterAsync_AtRegistrationLimitPreservesCurrentAndPinnedEntriesAndKeepsOverflowAsLowerBounds()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var archived = fixture.CreateRun("coverage", [1, 2, 3]);
        await store.RegisterAsync(archived, CancellationToken.None);
        await store.SnapshotAsync(archived, pin: true, automatic: false, CancellationToken.None);

        var registry = fixture.ReadRegistry();
        var registrations = registry["Registrations"]!.AsArray();
        var template = registrations.Single()!.AsObject();
        var protectedCurrentId = "00000000000000000000000000000001";
        var staleRunId = "00000000000000000000000000000002";
        for (var index = 1; index < CoverageArtifactLimits.MaximumRegistrations; index++)
        {
            var registration = (JsonObject)template.DeepClone();
            registration["RunId"] = index.ToString("x32");
            registration["OutputIdentity"] = "seed-output-" + index.ToString("D5");
            registration["Current"] = index == 1;
            registration["Archived"] = false;
            registration["Pinned"] = false;
            registration["ArchiveBytes"] = 0;
            registration["ArchivedAtUtc"] = null;
            registration["ObservedAtUtc"] = fixture.Clock.GetUtcNow().ToString("O");
            if (index == 1)
                registration["RunId"] = protectedCurrentId;
            if (index == 2)
            {
                registration["RunId"] = staleRunId;
                registration["ObservedAtUtc"] = DateTimeOffset.UnixEpoch.ToString("O");
            }
            registrations.Add(registration);
        }
        fixture.WriteRegistry(registry);
        Assert.Equal(CoverageArtifactLimits.MaximumRegistrations, store.GetStatus().RegisteredRuns);

        var lastSlot = fixture.CreateRun("coverage", [7, 8]);
        await store.RegisterAsync(lastSlot, CancellationToken.None);
        var atCapacity = store.GetStatus();
        Assert.Equal(CoverageArtifactLimits.MaximumRegistrations, atCapacity.RegisteredRuns);
        Assert.Equal(2, atCapacity.CurrentRuns);
        Assert.Equal(5, atCapacity.CurrentBytes);
        Assert.True(atCapacity.PinnedBytes > 0);

        var overflow = fixture.CreateRun("overflow", [7, 8, 9, 10]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterAsync(overflow, CancellationToken.None));
        var afterFirstOverflow = store.GetStatus();
        Assert.Equal(CoverageArtifactLimits.MaximumRegistrations, afterFirstOverflow.RegisteredRuns);
        Assert.False(afterFirstOverflow.IsComplete);
        Assert.Contains("registration limit", afterFirstOverflow.IncompleteReason, StringComparison.Ordinal);
        Assert.Equal(atCapacity.CurrentBytes, afterFirstOverflow.CurrentBytes);
        Assert.Single(afterFirstOverflow.OverflowObservations);
        Assert.Equal(overflow.ManifestBase.RunId, afterFirstOverflow.OverflowObservations[0].RunId);
        Assert.Equal(overflow.Inventory.Sum(file => file.Length), afterFirstOverflow.OverflowObservations[0].Bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterAsync(overflow, CancellationToken.None));

        var status = store.GetStatus();
        Assert.Equal(CoverageArtifactLimits.MaximumRegistrations, status.RegisteredRuns);
        Assert.Equal(atCapacity.CurrentRuns, status.CurrentRuns);
        Assert.Equal(atCapacity.CurrentBytes, status.CurrentBytes);
        Assert.True(status.PinnedBytes > 0);
        Assert.Equal(2, status.OverflowObservations.Count);
        Assert.All(status.OverflowObservations, observation =>
        {
            Assert.Equal(overflow.ManifestBase.RunId, observation.RunId);
            Assert.Equal(overflow.Inventory.Sum(file => file.Length), observation.Bytes);
        });
        Assert.Equal(atCapacity.CurrentBytes, status.CurrentBytes);
        Assert.NotEqual(atCapacity.CurrentBytes + status.OverflowObservations.Sum(observation => observation.Bytes), status.CurrentBytes);

        var saved = fixture.ReadRegistry();
        var savedRegistrations = saved["Registrations"]!.AsArray().ToDictionary(
            registration => registration!["RunId"]!.GetValue<string>(),
            registration => registration!.AsObject(), StringComparer.Ordinal);
        Assert.True(savedRegistrations[protectedCurrentId]["Current"]!.GetValue<bool>());
        Assert.True(savedRegistrations[archived.ManifestBase.RunId]["Archived"]!.GetValue<bool>());
        Assert.True(savedRegistrations[archived.ManifestBase.RunId]["Pinned"]!.GetValue<bool>());
        Assert.False(savedRegistrations[archived.ManifestBase.RunId]["Current"]!.GetValue<bool>());
        Assert.DoesNotContain(staleRunId, savedRegistrations.Keys);
        Assert.Contains(lastSlot.ManifestBase.RunId, savedRegistrations.Keys);
        Assert.DoesNotContain(overflow.ManifestBase.RunId, savedRegistrations.Keys);
        fixture.AssertNoStagingLeaks();
    }

    [Fact]
    public async Task RegisterAsync_EncodedRegistryReserveRejectsAnOtherwiseUnderCountRegistration()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var seed = fixture.CreateRun("seed", [1]);
        await store.RegisterAsync(seed, CancellationToken.None);
        var candidate = fixture.CreateRun("candidate", [4, 5, 6, 7, 8]);
        await store.RegisterAsync(candidate, CancellationToken.None);

        var registry = fixture.ReadRegistry();
        var registrations = registry["Registrations"]!.AsArray();
        var candidateRegistration = (JsonObject)registrations.Single(node =>
            node!["RunId"]!.GetValue<string>() == candidate.ManifestBase.RunId)!.DeepClone();
        registrations.RemoveAt(registrations.IndexOf(registrations.Single(node =>
            node!["RunId"]!.GetValue<string>() == candidate.ManifestBase.RunId)));
        var seedRegistration = registrations.Single(node =>
            node!["RunId"]!.GetValue<string>() == seed.ManifestBase.RunId)!.AsObject();
        seedRegistration["OutputIdentity"] = "seed-output-with-no-filesystem-match";
        seedRegistration["Current"] = false;

        var threshold = CoverageArtifactLimits.RegistryMaxBytes - CoverageArtifactLimits.OverflowObservationReserveBytes;
        fixture.SetStringToEncodedSize(registry, seedRegistration, "OutputPath", checked((int)(threshold - 128)));
        var beforeBytes = fixture.EncodedSize(registry);
        Assert.True(beforeBytes >= 0);
        Assert.True(beforeBytes <= threshold);

        var proposed = (JsonObject)registry.DeepClone();
        proposed["Registrations"]!.AsArray().Add(candidateRegistration);
        Assert.True(fixture.EncodedSize(proposed) > threshold);
        fixture.WriteRegistry(registry);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterAsync(candidate, CancellationToken.None));

        var status = store.GetStatus();
        Assert.Equal(1, status.RegisteredRuns);
        Assert.Equal(0, status.CurrentRuns);
        Assert.Equal(0, status.CurrentBytes);
        Assert.False(status.IsComplete);
        Assert.Single(status.OverflowObservations);
        Assert.Equal(candidate.ManifestBase.RunId, status.OverflowObservations[0].RunId);
        Assert.Equal(candidate.Inventory.Sum(file => file.Length), status.OverflowObservations[0].Bytes);
        Assert.True(new FileInfo(fixture.RegistryPath).Length <= CoverageArtifactLimits.RegistryMaxBytes);
        fixture.AssertNoStagingLeaks();
    }

    [Fact]
    public async Task Open_DuplicateRegistrationRunIdsFailClosedAndPreserveRegistry()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun("coverage", [1, 2]);
        using (var store = fixture.Open()) await store.RegisterAsync(run, CancellationToken.None);

        var registry = fixture.ReadRegistry();
        var registrations = registry["Registrations"]!.AsArray();
        registrations.Add((JsonObject)registrations.Single()!.DeepClone());
        fixture.WriteRegistry(registry);
        var before = File.ReadAllBytes(fixture.RegistryPath);

        Assert.Throws<InvalidDataException>(() => fixture.Open());

        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertNoStagingLeaks();
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":1,\"Registrations\":[],\"Reservations\":[],\"OverflowObservations\":[],\"IsComplete\":true,\"ExpiryReceipts\":[],\"FutureField\":true}")]
    [InlineData("{\"SchemaVersion\":1,\"SchemaVersion\":1,\"Registrations\":[],\"Reservations\":[],\"OverflowObservations\":[],\"IsComplete\":true,\"ExpiryReceipts\":[]}")]
    public void Open_UnknownOrDuplicateRegistrySchemaFieldsFailClosed(string json)
    {
        using var fixture = new Fixture();
        fixture.WriteRegistry(json);
        var before = File.ReadAllBytes(fixture.RegistryPath);

        Assert.Throws<InvalidDataException>(() => fixture.Open());

        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertNoStagingLeaks();
    }

    [Fact]
    public async Task Open_InvalidPersistedInventoryFactsFailClosedAndPreserveRegistry()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun("coverage", [1]);
        using (var store = fixture.Open()) await store.RegisterAsync(run, CancellationToken.None);

        var registry = fixture.ReadRegistry();
        var registration = registry["Registrations"]!.AsArray().Single()!.AsObject();
        registration["Inventory"]![0]!["Sha256"] = "not-a-sha256";
        fixture.WriteRegistry(registry);
        var before = File.ReadAllBytes(fixture.RegistryPath);

        Assert.Throws<InvalidDataException>(() => fixture.Open());

        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertNoStagingLeaks();
    }

    [Theory]
    [InlineData("coverage.xml", -1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("coverage.xml", 0, "short")]
    [InlineData("coverage.xml", 0, "gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("nested//coverage.xml", 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("nested\\coverage.xml", 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("/absolute.xml", 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task RegisterAsync_InvalidInventoryFactsLeaveRegistryAndStagingUntouched(string path, long length, string hash)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var run = fixture.CreateRun("coverage", [1]) with
        {
            Inventory = [new CoverageArtifactInventoryEntry(path, length, hash)]
        };
        var before = File.ReadAllBytes(fixture.RegistryPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterAsync(run, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Equal(0, store.GetStatus().RegisteredRuns);
        fixture.AssertNoStagingLeaks();
    }

    [Fact]
    public async Task RegisterAsync_DuplicateInventoryPathsAreRejectedBeforePersistence()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var run = fixture.CreateRun("coverage", [1]) with
        {
            Inventory =
            [
                new CoverageArtifactInventoryEntry("coverage.xml", 1, ValidHash),
                new CoverageArtifactInventoryEntry("coverage.xml", 1, ValidHash)
            ]
        };
        var before = File.ReadAllBytes(fixture.RegistryPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterAsync(run, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertNoStagingLeaks();
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("path")]
    [InlineData("hash")]
    public async Task RegisterAsync_NullInventoryFactsAreRejectedBeforePersistence(string nullFact)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var run = fixture.CreateRun("coverage", [1]);
        IReadOnlyList<CoverageArtifactInventoryEntry> inventory = nullFact switch
        {
            "entry" => new CoverageArtifactInventoryEntry[] { null! },
            "path" => [new CoverageArtifactInventoryEntry(null!, 1, ValidHash)],
            "hash" => [new CoverageArtifactInventoryEntry("coverage.xml", 1, null!)],
            _ => throw new ArgumentOutOfRangeException(nameof(nullFact))
        };
        run = run with { Inventory = inventory };
        var before = File.ReadAllBytes(fixture.RegistryPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterAsync(run, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertNoStagingLeaks();
    }

    [Fact]
    public async Task RegisterAsync_InventoryCountAndEncodedByteLimitsRejectWithoutLeaks()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var template = fixture.CreateRun("coverage", [1]);
        var before = File.ReadAllBytes(fixture.RegistryPath);
        var tooManyFiles = template with
        {
            Inventory = Enumerable.Range(0, CoverageArtifactLimits.MaximumFiles + 1)
                .Select(index => new CoverageArtifactInventoryEntry($"file-{index:D5}.xml", 0, ValidHash)).ToArray()
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterAsync(tooManyFiles, CancellationToken.None));

        var oversizedInventory = template with
        {
            Inventory = Enumerable.Range(0, 8_000)
                .Select(index => new CoverageArtifactInventoryEntry(
                    "file-" + new string('a', 180) + index.ToString("D5") + ".xml", 0, ValidHash)).ToArray()
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterAsync(oversizedInventory, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Equal(0, store.GetStatus().RegisteredRuns);
        fixture.AssertNoStagingLeaks();
    }

    [Fact]
    public async Task Snapshot_RegistryProvenanceBeyondArchiveBoundReleasesReservationAndStage()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var validRun = fixture.CreateRun("coverage", [3, 1, 4, 1, 5]);
        var run = validRun with
        {
            ManifestBase = validRun.ManifestBase with
            {
                Provenance = new CoverageArtifactProvenance(new string('r', 129), "clean", "configuration")
            }
        };
        await store.RegisterAsync(run, CancellationToken.None);
        var invalidRegistry = File.ReadAllBytes(fixture.RegistryPath);
        var registeredRun = store.GetCurrentRun(run.OutputPath)!;
        Assert.Equal(129, registeredRun.ManifestBase.Provenance!.GitRevisionFingerprint!.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SnapshotAsync(registeredRun, pin: false, automatic: false, CancellationToken.None));

        Assert.Equal(invalidRegistry, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        Assert.False(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
        fixture.AssertNoStagingLeaks();
    }

    [Fact]
    public async Task Snapshot_MissingOutputFailsWithoutCreatingOutputOrLeakingReservation()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var run = fixture.CreateRun("coverage", [2, 7, 1, 8]);
        await store.RegisterAsync(run, CancellationToken.None);
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);
        Directory.Delete(run.OutputPath, recursive: true);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None));

        Assert.False(Directory.Exists(run.OutputPath));
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        Assert.False(Directory.Exists(fixture.ArchivePath(run.ManifestBase.RunId)));
        fixture.AssertNoStagingLeaks();
    }

    [Theory]
    [InlineData("run-id")]
    [InlineData("incomplete")]
    public async Task Snapshot_ExistingArchiveWithMismatchedManifestFailsWithoutChangingEvidence(string corruption)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var run = fixture.CreateRun("coverage", [9, 2, 6, 5]);
        await store.RegisterAsync(run, CancellationToken.None);
        await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);

        var manifestPath = TestPathUtils.PathUnder(fixture.ArchivePath(run.ManifestBase.RunId), "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        if (corruption == "run-id")
            manifest["RunId"] = Guid.NewGuid().ToString("N");
        else
            manifest["Complete"] = false;
        fixture.WritePrivateFile(manifestPath, manifest.ToJsonString());
        var archiveBefore = fixture.CaptureDirectoryFiles(fixture.ArchivePath(run.ManifestBase.RunId));
        var registryBefore = File.ReadAllBytes(fixture.RegistryPath);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None));

        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.RegistryPath));
        fixture.AssertDirectoryFilesEqual(fixture.ArchivePath(run.ManifestBase.RunId), archiveBefore);
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        fixture.AssertNoStagingLeaks();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();

        internal Fixture()
        {
            StoreRoot = TestPathUtils.PathUnder(_temp.Path, "private-store");
            RepositoryPath = _temp.CreateDirectory("repository");
            Clock = new TestClock(DateTimeOffset.Parse("2026-10-21T12:00:00Z"));
            using (CoverageArtifactStore.Open(StoreRoot, Clock)) { }
        }

        internal string StoreRoot { get; }
        internal string RepositoryPath { get; }
        internal string RegistryPath => TestPathUtils.PathUnder(StoreRoot, "registry.json");
        internal string StagingPath => TestPathUtils.PathUnder(StoreRoot, "staging");
        internal TestClock Clock { get; }

        internal CoverageArtifactStore Open() => CoverageArtifactStore.Open(StoreRoot, Clock);

        internal CoverageArtifactCurrentRun CreateRun(string outputName, byte[] contents)
        {
            var outputPath = _temp.CreateDirectory(Path.Join("repository", outputName));
            var outputFile = TestPathUtils.PathUnder(outputPath, "coverage.xml");
            File.WriteAllBytes(outputFile, contents);
            var runId = Guid.NewGuid().ToString("N");
            var manifest = new CoverageArtifactRunManifest
            {
                RunId = runId,
                Outcome = "success",
                CreatedAtUtc = Clock.GetUtcNow(),
                Version = "admission-tests",
                Provenance = new CoverageArtifactProvenance("revision", "clean", "configuration")
            };
            var inventory = new CoverageArtifactInventoryEntry(
                "coverage.xml", contents.LongLength, Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant());
            return new CoverageArtifactCurrentRun(manifest, [inventory], outputPath, RepositoryPath);
        }

        internal string ArchivePath(string runId) => TestPathUtils.PathUnder(StoreRoot, "archives", runId);

        internal JsonObject ReadRegistry() => JsonNode.Parse(File.ReadAllText(RegistryPath))!.AsObject();

        internal void WriteRegistry(JsonObject registry) => WriteRegistry(registry.ToJsonString());

        internal void WriteRegistry(string json) => WritePrivateFile(RegistryPath, json);

        internal void WritePrivateFile(string path, string contents)
        {
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        internal int EncodedSize(JsonObject registry) => Encoding.UTF8.GetByteCount(registry.ToJsonString());

        internal void SetStringToEncodedSize(JsonObject registry, JsonObject target, string property, int targetBytes)
        {
            var low = 0;
            var high = targetBytes;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                target[property] = new string('x', middle);
                var size = EncodedSize(registry);
                if (size == targetBytes) return;
                if (size < targetBytes)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            throw new InvalidOperationException("The requested encoded registry size could not be represented.");
        }

        internal Dictionary<string, byte[]> CaptureDirectoryFiles(string directory) =>
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != ".appsurface-coverage-operation.lock")
                .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes, StringComparer.Ordinal);

        internal void AssertDirectoryFilesEqual(string directory, IReadOnlyDictionary<string, byte[]> expected)
        {
            var actual = CaptureDirectoryFiles(directory);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
            foreach (var path in expected.Keys)
                Assert.Equal(Convert.ToHexString(expected[path]), Convert.ToHexString(actual[path]));
        }

        internal void AssertNoStagingLeaks()
        {
            var unexpected = Directory.EnumerateFileSystemEntries(StagingPath)
                .Where(path => Path.GetFileName(path) != ".appsurface-coverage-operation.lock");
            Assert.Empty(unexpected);
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed class TestClock(DateTimeOffset initialUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => initialUtc;
    }
}
