using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactStoreReuseEdgesTests
{
    [Fact]
    public async Task RepeatedSnapshotWithPinReusesVerifiedArchiveAndProtectsIt()
    {
        using var fixture = new Fixture();
        using var store = CoverageArtifactStore.Open(fixture.Root);
        await store.RegisterAsync(fixture.Run, default);
        var first = await store.SnapshotAsync(fixture.Run, false, false, default);
        var before = File.ReadAllBytes(fixture.RegistryPath);
        var reused = await store.SnapshotAsync(fixture.Run, true, false, default);
        Assert.Equal(first.RunId, reused.RunId);
        Assert.Equal(first.Files, reused.Files);
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
        Assert.True(store.GetStatus().PinnedBytes > 0);
        Assert.NotEqual(before, File.ReadAllBytes(fixture.RegistryPath));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("pin")]
    [InlineData("unpin")]
    [InlineData("maintain")]
    [InlineData("restore")]
    public async Task ReadOnlyStoreCannotMutateRegistryOrOutput(string operation)
    {
        using var fixture = new Fixture();
        using (var writer = CoverageArtifactStore.Open(fixture.Root))
        {
            await writer.RegisterAsync(fixture.Run, default);
            await writer.SnapshotAsync(fixture.Run, false, false, default);
        }
        var before = File.ReadAllBytes(fixture.RegistryPath);
        using var reader = CoverageArtifactStore.OpenReadOnly(fixture.Root);
        var destination = Path.Join(fixture.Temp.Path, "restore");
        Func<Task> attempt = operation switch
        {
            "snapshot" => async () => { await reader.SnapshotAsync(fixture.Run, false, false, default); }
            ,
            "pin" => async () => { await reader.PinAsync(fixture.Run.ManifestBase.RunId, true, default); }
            ,
            "unpin" => async () => { await reader.PinAsync(fixture.Run.ManifestBase.RunId, false, default); }
            ,
            "maintain" => async () => { await reader.MaintainAsync(true, default); }
            ,
            _ => async () => { await reader.RestoreAsync(fixture.Run.ManifestBase.RunId, destination, default); }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(attempt);
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        Assert.False(Directory.Exists(destination));
        Assert.Equal("original", File.ReadAllText(fixture.Payload));
    }

    [Fact]
    public async Task AutomaticSnapshotRejectsDeclaredOverCapBeforeAllocatingStaging()
    {
        using var fixture = new Fixture();
        using var store = CoverageArtifactStore.Open(fixture.Root);
        var run = fixture.Run with { Inventory = [fixture.Run.Inventory[0] with { Length = 256L * 1024 * 1024 + 1 }] };
        await store.RegisterAsync(run, default);
        var before = File.ReadAllBytes(fixture.RegistryPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SnapshotAsync(run, false, true, default));
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        Assert.Equal(0, store.GetStatus().ArchivedRuns);
        Assert.Equal("original", File.ReadAllText(fixture.Payload));
    }

    [Fact]
    public async Task InvalidPersistedRunIdIsRejectedWithoutRepairingRegistry()
    {
        using var fixture = new Fixture();
        using (var store = CoverageArtifactStore.Open(fixture.Root)) await store.RegisterAsync(fixture.Run, default);
        var registry = JsonNode.Parse(File.ReadAllText(fixture.RegistryPath))!;
        registry["Registrations"]![0]!["RunId"] = "invalid";
        File.WriteAllText(fixture.RegistryPath, registry.ToJsonString());
        var before = File.ReadAllBytes(fixture.RegistryPath);
        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.Open(fixture.Root));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.OpenReadOnly(fixture.Root));
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
    }

    private sealed class Fixture : IDisposable
    {
        internal TestDirectory Temp { get; } = TestDirectory.Create();
        internal string Root => Path.Join(Temp.Path, "store");
        internal string Payload { get; }
        internal string RegistryPath => Path.Join(Root, "registry.json");
        internal CoverageArtifactCurrentRun Run { get; }

        internal Fixture()
        {
            var repository = Temp.CreateDirectory("repo");
            var output = Temp.CreateDirectory("repo/output");
            Payload = Path.Join(output, "coverage.xml");
            File.WriteAllText(Payload, "original");
            var bytes = File.ReadAllBytes(Payload);
            Run = new(new CoverageArtifactRunManifest
            { RunId = Guid.NewGuid().ToString("N"), Outcome = "success", Version = "fixture", CreatedAtUtc = DateTimeOffset.UtcNow },
                [new("coverage.xml", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())], output, repository);
        }
        public void Dispose() => Temp.Dispose();
    }
}
