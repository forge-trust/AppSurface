using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactArchiveTests
{
    [Fact]
    public async Task StageAndRestore_ShouldPreserveNestedOriginalBytesAndEmptyFiles_AndManifestRoundTrips()
    {
        using var tree = PrivateTree.Create();
        var first = Encoding.UTF8.GetBytes("arbitrary original bytes\0\n");
        tree.WriteSource("nested/deeper/run.dat", first);
        tree.WriteSource("empty.bin", []);
        var manifest = await tree.StageAsync(["nested/deeper/run.dat", "empty.bin"]);

        Assert.True(manifest.Complete);
        Assert.Equal("success", manifest.Outcome);
        Assert.All(manifest.Files, entry => Assert.True(entry.Complete));
        using var json = new MemoryStream();
        CoverageArtifactArchive.WriteManifest(json, manifest);
        json.Position = 0;
        var roundTrip = CoverageArtifactArchive.ReadManifest(json);
        Assert.Equal(manifest.SchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(manifest.RunId, roundTrip.RunId);
        Assert.Equal(manifest.OutputIdentity, roundTrip.OutputIdentity);
        Assert.Equal(manifest.RepositoryIdentity, roundTrip.RepositoryIdentity);
        Assert.Equal(manifest.Version, roundTrip.Version);
        Assert.Equal(manifest.Outcome, roundTrip.Outcome);
        Assert.Equal(manifest.CreatedAtUtc, roundTrip.CreatedAtUtc);
        Assert.Equal(manifest.ArchivedAtUtc, roundTrip.ArchivedAtUtc);
        Assert.Equal(manifest.Complete, roundTrip.Complete);
        Assert.Equal(manifest.Files, roundTrip.Files);

        await CoverageArtifactArchive.RestorePayloadsAsync(tree.Stage, tree.Restore, roundTrip,
            CoverageArtifactLimits.ExplicitExpandedMaxBytes, TimeProvider.System, null, default);
        Assert.Equal(first, File.ReadAllBytes(Path.Join(tree.Restore, "nested", "deeper", "run.dat")));
        Assert.Empty(File.ReadAllBytes(Path.Join(tree.Restore, "empty.bin")));
    }

    [Fact]
    public async Task StageAsync_ShouldAcceptExactOriginalAndStoredByteBounds()
    {
        using var tree = PrivateTree.Create();
        tree.WriteSource("data", Encoding.UTF8.GetBytes("bytes"));
        var first = await tree.StageAsync(["data"]);
        var storedLength = Assert.Single(first.Files).StoredLength;
        tree.NewEmptyStage("exact");
        var exact = await CoverageArtifactArchive.StageAsync(tree.Source, tree.ExistingStage("exact"), ManifestBase(),
            tree.Inventory("data"), 5, storedLength, TimeProvider.System, null, default);
        Assert.Equal(5, exact.Files[0].OriginalLength);

        tree.NewEmptyStage("over");
        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.StageAsync(tree.Source,
            tree.ExistingStage("over"), ManifestBase(), tree.Inventory("data"), 5, storedLength - 1,
            TimeProvider.System, null, default));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("A|a")]
    [InlineData("é.txt|é.txt")]
    [InlineData("CON.txt")]
    [InlineData("name.")]
    public async Task StageAsync_ShouldRejectTraversalCollisionsUnicodeAliasesAndReservedNames(string specification)
    {
        using var tree = PrivateTree.Create();
        string[] paths = specification.Contains('|') ? specification.Split('|') : [specification];
        foreach (var path in paths.Where(path => path is "A" or "a" or "é.txt" or "é.txt"))
            tree.WriteSource(path, [1]);
        tree.NewEmptyStage("invalid");
        var inventory = paths.Select(path => new CoverageArtifactInventoryEntry(path, 1, Hash([1]))).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.StageAsync(tree.Source,
            tree.ExistingStage("invalid"), ManifestBase(), inventory, 10, 10_000, TimeProvider.System, null, default));
    }

    [Fact]
    public async Task StageAsync_ShouldRejectSourceExpectedHashMismatch()
    {
        using var tree = PrivateTree.Create();
        tree.WriteSource("data", [1, 2]);
        tree.NewEmptyStage("bad-hash");
        var wrong = new CoverageArtifactInventoryEntry("data", 2, Hash([2, 1]));
        await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.StageAsync(tree.Source,
            tree.ExistingStage("bad-hash"), ManifestBase(), [wrong], 2, 1000, TimeProvider.System, null, default));
    }

    [Fact]
    public async Task RestorePayloadsAsync_ShouldRejectStoredLengthAndStoredHashCorruption()
    {
        foreach (var corruption in new[] { "length", "hash" })
        {
            using var tree = PrivateTree.Create();
            tree.WriteSource("data", [8, 9, 10]);
            var manifest = await tree.StageAsync(["data"]);
            var entry = Assert.Single(manifest.Files);
            if (corruption == "length") manifest = manifest with { Files = [entry with { StoredLength = entry.StoredLength + 1 }] };
            else manifest = manifest with { Files = [entry with { StoredSha256 = Hash([0]) }] };
            tree.NewEmptyStage("restore-corrupt");
            await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.RestorePayloadsAsync(
                tree.Stage, tree.ExistingStage("restore-corrupt"), manifest, 100, TimeProvider.System, null, default));
        }
    }

    [Theory]
    [InlineData("original-length")]
    [InlineData("original-hash")]
    public async Task RestorePayloadsAsync_ShouldRejectExpandedLengthOrHashMismatch(string corruption)
    {
        using var tree = PrivateTree.Create();
        tree.WriteSource("data", [3, 4, 5]);
        var manifest = await tree.StageAsync(["data"]);
        var entry = Assert.Single(manifest.Files);
        var altered = corruption == "original-length" ? entry with { OriginalLength = 4 } : entry with { OriginalSha256 = Hash([0]) };
        manifest = manifest with { Files = [altered] };
        tree.NewEmptyStage("restore-corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.RestorePayloadsAsync(
            tree.Stage, tree.ExistingStage("restore-corrupt"), manifest, 100, TimeProvider.System, null, default));
    }

    [Fact]
    public async Task StageAndRestore_ShouldEnforceExpandedLimitAndCancellationAndDeadline()
    {
        using var tree = PrivateTree.Create();
        tree.WriteSource("data", [1, 2]);
        tree.NewEmptyStage("limit");
        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.StageAsync(tree.Source,
            tree.ExistingStage("limit"), ManifestBase(), tree.Inventory("data"), 1, 1000, TimeProvider.System, null, default));
        tree.NewEmptyStage("cancel");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CoverageArtifactArchive.StageAsync(tree.Source,
            tree.ExistingStage("cancel"), ManifestBase(), tree.Inventory("data"), 10, 1000, TimeProvider.System, null, canceled.Token));
        tree.NewEmptyStage("deadline");
        await Assert.ThrowsAsync<TimeoutException>(() => CoverageArtifactArchive.StageAsync(tree.Source,
            tree.ExistingStage("deadline"), ManifestBase(), tree.Inventory("data"), 10, 1000, TimeProvider.System,
            TimeSpan.Zero, default));

        tree.NewEmptyStage("restore-over");
        var manifest = await tree.StageAsync(["data"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.RestorePayloadsAsync(
            tree.Stage, tree.ExistingStage("restore-over"), manifest, 1, TimeProvider.System, null, default));
    }

    private static CoverageArtifactRunManifest ManifestBase() => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        OutputIdentity = "output-id",
        RepositoryIdentity = "repository-id",
        Version = "test",
        Outcome = "success",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class PrivateTree : IDisposable
    {
        private readonly string root;
        private readonly CoverageRunOutputLease rootLease;
        private PrivateTree(string root, CoverageRunOutputLease lease) { this.root = root; rootLease = lease; }
        internal string Source => Path.Join(root, "source");
        internal string Stage => Path.Join(root, "stage");
        internal string Restore => Path.Join(root, "restore");
        internal static PrivateTree Create()
        {
            var path = Path.Join(Path.GetTempPath(), "coverage-archive-tests-" + Guid.NewGuid().ToString("N"));
            var lease = CoverageRunOutputLease.AcquireArtifactDirectory(path, true, true)!;
            foreach (var name in new[] { "source", "stage", "restore" }) lease.CreateArtifactDirectory(name).Dispose();
            return new PrivateTree(path, lease);
        }
        internal void WriteSource(string relative, byte[] bytes)
        {
            var path = TestPathUtils.PathUnder(Source, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        internal IReadOnlyList<CoverageArtifactInventoryEntry> Inventory(params string[] paths) => paths.Select(path =>
        {
            var bytes = File.ReadAllBytes(TestPathUtils.PathUnder(Source, path.Replace('/', Path.DirectorySeparatorChar)));
            return new CoverageArtifactInventoryEntry(path, bytes.Length, Hash(bytes));
        }).ToArray();
        internal Task<CoverageArtifactRunManifest> StageAsync(string[] paths) => CoverageArtifactArchive.StageAsync(
            Source, Stage, ManifestBase(), Inventory(paths), 1_000_000, 1_000_000, TimeProvider.System, null, default);
        internal void NewEmptyStage(string name) => rootLease.CreateArtifactDirectory(name).Dispose();
        internal string ExistingStage(string name) => Path.Join(root, name);
        public void Dispose()
        {
            rootLease.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
