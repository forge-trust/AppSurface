using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreTests
{
    [Fact]
    public async Task RegisterAsync_TracksOnlyExplicitCurrentRunAndPreservesRunMetadata()
    {
        using var temp = TestDirectory.Create();
        var repository = temp.CreateDirectory("repo");
        var output = temp.CreateDirectory("repo/coverage");
        var storeRoot = Path.Join(temp.Path, "private-store");
        using var store = CoverageArtifactStore.Open(storeRoot);
        var provenance = new CoverageArtifactProvenance("revision", "clean", "configuration");
        var first = CreateRun("11111111111141118111111111111111", output, repository, "v-test-1", provenance);
        var second = CreateRun("22222222222242228222222222222222", output, repository, "v-test-2", provenance);

        await store.RegisterAsync(first, CancellationToken.None);
        Assert.Equal(first.ManifestBase.RunId, store.GetCurrentRun(output)!.ManifestBase.RunId);

        await store.RegisterAsync(second, CancellationToken.None);
        var current = store.GetCurrentRun(output);
        Assert.NotNull(current);
        Assert.Equal(second.ManifestBase.RunId, current.ManifestBase.RunId);
        Assert.Equal(second.ManifestBase.Version, current.ManifestBase.Version);
        Assert.Equal(provenance, current.ManifestBase.Provenance);
        Assert.Equal(second.Inventory, current.Inventory);
        Assert.Equal(1, store.GetStatus().CurrentRuns);
    }

    [Fact]
    public void Open_RejectsFilesystemRootAsArtifactDomain()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Throws<IOException>(() => CoverageArtifactStore.Open(root));
    }

    [Fact]
    public void OpenReadOnly_WhenDomainIsAbsent_DoesNotCreateAnything()
    {
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "missing", "store");
        using var store = CoverageArtifactStore.OpenReadOnly(path);

        var status = store.GetStatus();

        Assert.Equal(0, status.RegisteredRuns);
        Assert.Equal(0, status.CurrentBytes);
        Assert.False(Directory.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task RegisterAsync_RejectsMalformedOrDuplicateInventoryPaths()
    {
        using var temp = TestDirectory.Create();
        var repository = temp.CreateDirectory("repo");
        var output = temp.CreateDirectory("repo/coverage");
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var malformed = CreateRun("33333333333343338333333333333333", output, repository, "version", null) with
        {
            Inventory =
            [
                new CoverageArtifactInventoryEntry("../outside.xml", 1, new string('a', 64))
            ]
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterAsync(malformed, CancellationToken.None));
        Assert.Equal(0, store.GetStatus().RegisteredRuns);
    }

    private static CoverageArtifactCurrentRun CreateRun(
        string runId,
        string output,
        string repository,
        string version,
        CoverageArtifactProvenance? provenance)
    {
        var manifest = new CoverageArtifactRunManifest
        {
            RunId = runId,
            Outcome = "success",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            Version = version,
            Provenance = provenance,
        };
        IReadOnlyList<CoverageArtifactInventoryEntry> inventory =
        [new("coverage.cobertura.xml", 1, new string('a', 64))];
        return new(manifest, inventory, output, repository);
    }
}
