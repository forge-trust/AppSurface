using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreTargetedValidationTests
{
    [Theory]
    [InlineData("schema")]
    [InlineData("outcome")]
    [InlineData("null-version")]
    [InlineData("long-version")]
    [InlineData("null-entry")]
    [InlineData("null-path")]
    [InlineData("null-hash")]
    [InlineData("negative-length")]
    [InlineData("short-hash")]
    [InlineData("nonhex-hash")]
    [InlineData("backslash")]
    [InlineData("rooted")]
    [InlineData("empty-component")]
    [InlineData("dot-component")]
    [InlineData("parent-component")]
    [InlineData("duplicate")]
    public async Task InvalidRunFactFailsBeforeRegistryMutation(string fault)
    {
        using var temp = TestDirectory.Create();
        var output = temp.CreateDirectory("repo/output");
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var run = Run(output, Path.Join(temp.Path, "repo"));
        var manifest = run.ManifestBase;
        var entry = run.Inventory[0];
        run = fault switch
        {
            "schema" => run with { ManifestBase = manifest with { SchemaVersion = 2 } },
            "outcome" => run with { ManifestBase = manifest with { Outcome = "unknown" } },
            "null-version" => run with { ManifestBase = manifest with { Version = null! } },
            "long-version" => run with { ManifestBase = manifest with { Version = new string('v', 257) } },
            "null-entry" => run with { Inventory = [null!] },
            "null-path" => run with { Inventory = [entry with { Path = null! }] },
            "null-hash" => run with { Inventory = [entry with { Sha256 = null! }] },
            "negative-length" => run with { Inventory = [entry with { Length = -1 }] },
            "short-hash" => run with { Inventory = [entry with { Sha256 = "a" }] },
            "nonhex-hash" => run with { Inventory = [entry with { Sha256 = new string('z', 64) }] },
            "backslash" => run with { Inventory = [entry with { Path = "a\\b" }] },
            "rooted" => run with { Inventory = [entry with { Path = "/outside" }] },
            "empty-component" => run with { Inventory = [entry with { Path = "a//b" }] },
            "dot-component" => run with { Inventory = [entry with { Path = "a/./b" }] },
            "parent-component" => run with { Inventory = [entry with { Path = "a/../b" }] },
            "duplicate" => run with { Inventory = [entry, entry] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        var registry = Path.Join(store.RootPath, "registry.json");
        var before = File.ReadAllBytes(registry);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterAsync(run, default));
        Assert.Equal(before, File.ReadAllBytes(registry));
        Assert.Equal(0, store.GetStatus().RegisteredRuns);
    }

    [Theory]
    [InlineData("OutputPath")]
    [InlineData("RepositoryPath")]
    [InlineData("RepositoryIdentity")]
    [InlineData("Inventory")]
    [InlineData("ArchiveBytes")]
    [InlineData("OutputIdentity")]
    public async Task InvalidPersistedRegistrationFactPreservesRegistry(string field)
    {
        using var temp = TestDirectory.Create();
        var output = temp.CreateDirectory("repo/output");
        var root = Path.Join(temp.Path, "store");
        using (var store = CoverageArtifactStore.Open(root))
            await store.RegisterAsync(Run(output, Path.Join(temp.Path, "repo")), default);
        var path = Path.Join(root, "registry.json");
        var registry = JsonNode.Parse(File.ReadAllText(path))!;
        var entry = registry["Registrations"]![0]!;
        if (field == "ArchiveBytes") entry[field] = -1;
        else entry[field] = null;
        File.WriteAllText(path, registry.ToJsonString());
        var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.OpenReadOnly(root));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("store.json")]
    [InlineData("registry.json")]
    [InlineData("archives")]
    [InlineData("staging")]
    public void MissingOwnedComponentDoesNotRepairReadOnlyStatus(string missing)
    {
        using var temp = TestDirectory.Create();
        var root = Path.Join(temp.Path, "store");
        using (CoverageArtifactStore.Open(root)) { }
        var path = Path.Join(root, missing);
        if (Directory.Exists(path)) Directory.Delete(path);
        else File.Delete(path);
        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.OpenReadOnly(root));
        Assert.False(File.Exists(path) || Directory.Exists(path));
    }

    [Fact]
    public void UnmarkedPrivateStoreWithKnownRegistryIsNotAdopted()
    {
        using var temp = TestDirectory.Create();
        var root = Path.Join(temp.Path, "store");
        using (CoverageArtifactStore.Open(root)) { }
        File.Delete(Path.Join(root, "store.json"));
        var before = File.ReadAllBytes(Path.Join(root, "registry.json"));
        var error = Assert.Throws<IOException>(() => CoverageArtifactStore.Open(root));
        Assert.Contains("unmarked non-empty", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(root, "store.json")));
        Assert.Equal(before, File.ReadAllBytes(Path.Join(root, "registry.json")));
    }

    [Fact]
    public async Task ReusingArchivedRunRejectsMismatchedManifestAndPreservesCurrentSource()
    {
        using var temp = TestDirectory.Create();
        var output = temp.CreateDirectory("repo/output");
        File.WriteAllText(Path.Join(output, "coverage.xml"), "source");
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var run = Run(output, Path.Join(temp.Path, "repo"));
        await store.RegisterAsync(run, default);
        await store.SnapshotAsync(run, false, false, default);
        var path = Path.Join(store.RootPath, "archives", run.ManifestBase.RunId, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["runId"] = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, manifest.ToJsonString());
        var before = File.ReadAllBytes(path);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.SnapshotAsync(run, false, false, default));
        Assert.Contains("existing archive is incomplete or mismatched", error.Message, StringComparison.Ordinal);
        Assert.False(await store.HasVerifiedArchiveAsync(output, default));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("source", File.ReadAllText(Path.Join(output, "coverage.xml")));
    }

    private static CoverageArtifactCurrentRun Run(string output, string repository) => new(new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        Outcome = "success",
        Version = "fixture",
        CreatedAtUtc = DateTimeOffset.UtcNow
    }, [new("coverage.xml", 6, Convert.ToHexString(SHA256.HashData("source"u8)).ToLowerInvariant())], output, repository);
}
