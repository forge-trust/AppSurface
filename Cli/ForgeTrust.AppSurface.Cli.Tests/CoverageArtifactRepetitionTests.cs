using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactRepetitionTests
{
    [Fact]
    public async Task OneHundredRunsAcrossTenOutputs_ConvergeAndPreserveProtectedArtifacts()
    {
        using var fixture = new RepetitionFixture();
        var outcomes = new[] { "success", "failed", "interrupted" };
        var runs = new List<CoverageArtifactCurrentRun>();
        var currentByOutput = new Dictionary<string, CoverageArtifactCurrentRun>(StringComparer.Ordinal);
        var outputHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        using (var store = fixture.OpenStore())
        {
            for (var i = 0; i < 100; i++)
            {
                var run = fixture.CreateRun(i % 10, outcomes[i % outcomes.Length]);
                runs.Add(run);
                currentByOutput[run.OutputPath] = run;
                outputHashes[run.OutputPath] = fixture.HashOutput(run);
                await store.RegisterAsync(run, CancellationToken.None);
                Assert.Equal(run.ManifestBase.RunId, store.GetCurrentRun(run.OutputPath)!.ManifestBase.RunId);
                var archived = await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
                Assert.True(archived.Complete);
                Assert.Equal(run.ManifestBase.Outcome, archived.Outcome);
            }
            Assert.Equal(100, store.GetStatus().RegisteredRuns);
            Assert.Equal(10, store.GetStatus().CurrentRuns);
        }

        var pinned = runs[1];
        using (var store = fixture.OpenStore()) await store.PinAsync(pinned.ManifestBase.RunId, true, CancellationToken.None);
        fixture.CreateProtectedSentinels();

        var protectedArchiveIds = currentByOutput.Values.Select(run => run.ManifestBase.RunId)
            .Append(pinned.ManifestBase.RunId).ToHashSet(StringComparer.Ordinal);
        using (var store = fixture.OpenStore(historyMaxBytes: 0))
        {
            var expectedEligible = runs.Where(run => !protectedArchiveIds.Contains(run.ManifestBase.RunId))
                .Select(run => run.ManifestBase.RunId).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(expectedEligible.Count, (await store.MaintainAsync(apply: false, CancellationToken.None)).Count);
            var removed = await store.MaintainAsync(apply: true, CancellationToken.None);
            Assert.Equal(expectedEligible, removed.ToHashSet(StringComparer.Ordinal));
            Assert.Empty(await store.MaintainAsync(apply: false, CancellationToken.None));

            Assert.Equal(100, store.GetStatus().RegisteredRuns);
            Assert.Equal(10, store.GetStatus().CurrentRuns);
            Assert.Equal(11, store.GetStatus().ArchivedRuns);
            Assert.True(store.GetStatus().MetadataBytes > 0);
            var archiveIds = Directory.EnumerateDirectories(Path.Join(fixture.StoreRoot, "archives"))
                .Select(path => Path.GetFileName(path)!).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(protectedArchiveIds, archiveIds);
            Assert.Equal(0L, fixture.EligibleArchiveBytes(archiveIds, protectedArchiveIds));
        }

        foreach (var (output, run) in currentByOutput)
            Assert.Equal(outputHashes[output], fixture.HashOutput(run));

        using (var store = fixture.OpenStore())
        using (var restoreParent = fixture.CreatePrivateDirectory("restore-parent"))
        {
            var destination = Path.Join(restoreParent.ArtifactDirectoryPath, "pinned-restore");
            await store.RestoreAsync(pinned.ManifestBase.RunId, destination, CancellationToken.None);
            Assert.Equal(fixture.OriginalBytes(pinned), File.ReadAllBytes(Path.Join(destination, "coverage.xml")));
        }
        fixture.AssertProtectedSentinels();

        await fixture.VerifyParallelArchivesAndBusyRetry();
        fixture.AssertProtectedSentinels();
    }

    private sealed class RepetitionFixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();
        private readonly FixedClock _clock = new(DateTimeOffset.Parse("2026-10-21T12:00:00Z"));
        internal RepetitionFixture()
        {
            StoreRoot = Path.Join(_temp.Path, "store");
            using (CoverageRunOutputLease.AcquireArtifactDirectory(StoreRoot, true, true)) { }
            using (CoverageArtifactStore.Open(StoreRoot, _clock)) { }
            WritePolicy(2_147_483_648);
            RepositoryPath = _temp.CreateDirectory("repo");
        }
        internal string StoreRoot { get; }
        internal string RepositoryPath { get; }
        internal CoverageArtifactStore OpenStore(long historyMaxBytes = 2_147_483_648)
        {
            WritePolicy(historyMaxBytes);
            return CoverageArtifactStore.Open(StoreRoot, _clock);
        }
        internal CoverageArtifactCurrentRun CreateRun(int identity, string outcome)
        {
            var output = Path.Join(RepositoryPath, "output-" + identity);
            Directory.CreateDirectory(output);
            var bytes = Encoding.UTF8.GetBytes($"<coverage identity=\"{identity}\">bounded</coverage>");
            File.WriteAllBytes(Path.Join(output, "coverage.xml"), bytes);
            var id = Guid.NewGuid().ToString("N");
            var entry = new CoverageArtifactInventoryEntry("coverage.xml", bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            var manifest = new CoverageArtifactRunManifest
            {
                RunId = id,
                Outcome = outcome,
                CreatedAtUtc = _clock.GetUtcNow(),
                Version = "repetition-proof"
            };
            return new CoverageArtifactCurrentRun(manifest, [entry], output, RepositoryPath);
        }
        internal string HashOutput(CoverageArtifactCurrentRun run) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Join(run.OutputPath, "coverage.xml"))));
        internal byte[] OriginalBytes(CoverageArtifactCurrentRun run) => File.ReadAllBytes(Path.Join(run.OutputPath, "coverage.xml"));
        internal CoverageRunOutputLease CreatePrivateDirectory(string name) =>
            CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(_temp.Path, name), true, true)!;
        internal long EligibleArchiveBytes(HashSet<string> present, HashSet<string> protectedIds) =>
            present.Where(id => !protectedIds.Contains(id)).Sum(id => Directory.EnumerateFiles(Path.Join(StoreRoot, "archives", id))
                .Sum(path => new FileInfo(path).Length));

        private string ProtectedRoot => Path.Join(RepositoryPath, "protected");
        internal void CreateProtectedSentinels()
        {
            Directory.CreateDirectory(Path.Join(ProtectedRoot, "legacy"));
            File.WriteAllText(Path.Join(ProtectedRoot, "legacy", "sentinel.txt"), "legacy");
            Directory.CreateDirectory(Path.Join(ProtectedRoot, "unmanaged"));
            File.WriteAllText(Path.Join(ProtectedRoot, "unmanaged", "sentinel.txt"), "unmanaged");
            Directory.CreateSymbolicLink(Path.Join(ProtectedRoot, "linked"), Path.Join(ProtectedRoot, "legacy"));
        }
        internal void AssertProtectedSentinels()
        {
            Assert.Equal("legacy", File.ReadAllText(Path.Join(ProtectedRoot, "legacy", "sentinel.txt")));
            Assert.Equal("unmanaged", File.ReadAllText(Path.Join(ProtectedRoot, "unmanaged", "sentinel.txt")));
            Assert.True(new DirectoryInfo(Path.Join(ProtectedRoot, "linked")).LinkTarget is not null);
        }

        internal async Task VerifyParallelArchivesAndBusyRetry()
        {
            var left = CreateRun(20, "success");
            var right = CreateRun(21, "interrupted");
            using (var store = OpenStore())
            {
                await store.RegisterAsync(left, CancellationToken.None);
                await store.RegisterAsync(right, CancellationToken.None);
                var leftBytes = OriginalBytes(left);
                using (CoverageArtifactOperationLease.Acquire(StoreRoot, exclusive: true))
                {
                    await Assert.ThrowsAsync<CoverageExecutionException>(() => store.SnapshotAsync(left, false, false, CancellationToken.None));
                    Assert.Equal(leftBytes, OriginalBytes(left));
                    Assert.False(Directory.Exists(Path.Join(StoreRoot, "archives", left.ManifestBase.RunId)));
                }
                var firstAttempts = await Task.WhenAll(ArchiveOnce(store, left), ArchiveOnce(store, right));
                Assert.All(firstAttempts, result => Assert.True(result.Complete || result.Busy));
                var results = new[] { firstAttempts[0].Complete || await ArchiveAfterContention(store, left),
                    firstAttempts[1].Complete || await ArchiveAfterContention(store, right) };
                Assert.All(results, Assert.True);
                Assert.Equal(leftBytes, OriginalBytes(left));
                Assert.Equal(OriginalBytes(right), File.ReadAllBytes(Path.Join(right.OutputPath, "coverage.xml")));
            }

            static async Task<(bool Complete, bool Busy)> ArchiveOnce(CoverageArtifactStore store, CoverageArtifactCurrentRun run)
            {
                try { return ((await store.SnapshotAsync(run, false, false, CancellationToken.None)).Complete, false); }
                catch (Exception exception) when (exception is CoverageExecutionException or IOException) { return (false, true); }
            }
            static async Task<bool> ArchiveAfterContention(CoverageArtifactStore store, CoverageArtifactCurrentRun run)
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try { return (await store.SnapshotAsync(run, false, false, CancellationToken.None)).Complete; }
                    catch (Exception exception) when (attempt < 4 && exception is (CoverageExecutionException or IOException))
                    {
                        await Task.Delay(10 * (attempt + 1));
                    }
                }
                return false;
            }
        }

        private void WritePolicy(long maxBytes)
        {
            var path = Path.Join(StoreRoot, "policy.json");
            var bytes = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"historyMaxBytes\":{maxBytes},\"historyMaxAgeDays\":7}}");
            if (File.Exists(path)) File.WriteAllBytes(path, bytes);
            else
            {
                using var root = CoverageRunOutputLease.AcquireArtifactDirectory(StoreRoot, false, true)!;
                using var stream = root.OpenArtifactFile("policy.json", true);
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        public void Dispose() => _temp.Dispose();
    }

    private sealed class FixedClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
