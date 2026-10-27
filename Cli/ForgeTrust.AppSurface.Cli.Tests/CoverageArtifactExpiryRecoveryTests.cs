using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactExpiryRecoveryTests
{
    [Fact]
    public async Task RetryAfterDeletionAndMetadataContentionReconcilesMissingArchiveAndContinuesMaintenance()
    {
        using var temp = TestDirectory.Create();
        var home = Path.Join(temp.Path, "domain");
        using (CoverageArtifactStore.Open(home)) { }
        TestFileSystem.WritePrivateFile(TestPathUtils.PathUnder(home, "policy.json"), "{\"schemaVersion\":1,\"historyMaxBytes\":0}");
        using var store = CoverageArtifactStore.Open(home);
        var output = temp.CreateDirectory("output");
        var first = CreateRun(output, temp.Path, "first");
        await store.RegisterAsync(first, default);
        await store.SnapshotAsync(first, false, false, default);
        var second = CreateRun(output, temp.Path, "second");
        await store.RegisterAsync(second, default);
        await store.SnapshotAsync(second, false, false, default);
        await store.RegisterAsync(CreateRun(output, temp.Path, "current"), default);
        var deleted = new List<string>();
        CoverageArtifactOperationLease? conflictingMetadata = null;
        try
        {
            await Assert.ThrowsAsync<CoverageExecutionException>(() => store.MaintainAsync(true, default,
                onArchiveDeleted: id =>
                {
                    deleted.Add(id);
                    conflictingMetadata = CoverageArtifactOperationLease.Acquire(home, true);
                }));
        }
        finally { conflictingMetadata?.Dispose(); }
        var removedId = Assert.Single(deleted);
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(home, "archives", removedId)));
        Assert.Equal(2, store.GetStatus().ArchivedRuns);
        var preview = await store.MaintainAsync(false, default);
        Assert.Equal(2, preview.Count);
        Assert.Equal(2, store.GetStatus().ArchivedRuns);
        deleted.Clear();
        var result = await store.MaintainAsync(true, default, onArchiveDeleted: deleted.Add);
        Assert.Single(result);
        Assert.Equal(result, deleted);
        Assert.DoesNotContain(removedId, result);
        var status = store.GetStatus();
        Assert.Equal(0, status.ArchivedRuns);
        Assert.Equal(0, status.ArchivedBytes);
        Assert.Equal(2, status.ExpiryReceiptCount);
        Assert.Equal(1, status.CurrentRuns);
        Assert.Equal("current", File.ReadAllText(Path.Join(output, "coverage.xml")));
        Assert.Empty(await store.MaintainAsync(true, default));
        Assert.Equal(2, store.GetStatus().ExpiryReceiptCount);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("pinned")]
    [InlineData("reservation")]
    [InlineData("occupied")]
    public async Task MissingArchiveReconciliationPreservesProtectedOrAmbiguousRegistrations(string protection)
    {
        using var temp = TestDirectory.Create();
        var home = Path.Join(temp.Path, "domain");
        using (CoverageArtifactStore.Open(home)) { }
        TestFileSystem.WritePrivateFile(TestPathUtils.PathUnder(home, "policy.json"), "{\"schemaVersion\":1,\"historyMaxBytes\":0}");
        using var store = CoverageArtifactStore.Open(home);
        var output = temp.CreateDirectory("output");
        var run = CreateRun(output, temp.Path, "protected");
        await store.RegisterAsync(run, default);
        await store.SnapshotAsync(run, protection == "pinned", false, default);
        if (protection != "current") await store.RegisterAsync(CreateRun(output, temp.Path, "current"), default);
        var archive = TestPathUtils.PathUnder(home, "archives", run.ManifestBase.RunId);
        Directory.Delete(archive, true);
        var registryPath = TestPathUtils.PathUnder(home, "registry.json");
        if (protection == "reservation")
        {
            var registry = JsonNode.Parse(File.ReadAllText(registryPath))!.AsObject();
            registry["Reservations"]!.AsArray().Add(new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString("N"),
                ["RunId"] = run.ManifestBase.RunId,
                ["Bytes"] = 123,
                ["StartedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["Pin"] = false
            });
            TestFileSystem.WritePrivateFile(registryPath, registry.ToJsonString());
        }
        if (protection == "occupied") File.WriteAllText(archive, "unknown occupant");
        var registryBefore = File.ReadAllBytes(registryPath);
        using var heldOutput = CoverageArtifactOperationLease.Acquire(output, true);
        if (protection == "occupied") await Assert.ThrowsAnyAsync<IOException>(() => store.MaintainAsync(true, default));
        else Assert.Empty(await store.MaintainAsync(true, default));
        Assert.Equal(registryBefore, File.ReadAllBytes(registryPath));
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
        Assert.Equal(0, store.GetStatus().ExpiryReceiptCount);
        if (protection == "occupied") Assert.Equal("unknown occupant", File.ReadAllText(archive));
    }

    [Fact]
    public async Task ApplyReconcilesOnlyAbsentMetadataWhileExistingAndUnmanagedBytesStayUntouched()
    {
        using var temp = TestDirectory.Create();
        var home = Path.Join(temp.Path, "domain");
        using var store = CoverageArtifactStore.Open(home);
        var output = temp.CreateDirectory("output");
        var missing = CreateRun(output, temp.Path, "missing archive");
        await store.RegisterAsync(missing, default);
        await store.SnapshotAsync(missing, false, false, default);
        var retained = CreateRun(output, temp.Path, "retained archive");
        await store.RegisterAsync(retained, default);
        await store.SnapshotAsync(retained, false, false, default);
        await store.RegisterAsync(CreateRun(output, temp.Path, "current"), default);
        Directory.Delete(TestPathUtils.PathUnder(home, "archives", missing.ManifestBase.RunId), true);
        var retainedPath = TestPathUtils.PathUnder(home, "archives", retained.ManifestBase.RunId);
        var retainedManifest = File.ReadAllBytes(Path.Join(retainedPath, "manifest.json"));
        var unknown = TestPathUtils.PathUnder(home, "archives", "unmanaged-sentinel");
        File.WriteAllText(unknown, "preserve unknown bytes");
        var deleted = new List<string>();
        Assert.Empty(await store.MaintainAsync(true, default, onArchiveDeleted: deleted.Add));
        Assert.Empty(deleted);
        Assert.Equal(1, store.GetStatus().ArchivedRuns);
        Assert.Equal(1, store.GetStatus().ExpiryReceiptCount);
        Assert.Equal(retainedManifest, File.ReadAllBytes(Path.Join(retainedPath, "manifest.json")));
        Assert.Equal("preserve unknown bytes", File.ReadAllText(unknown));
        Assert.Equal("current", File.ReadAllText(Path.Join(output, "coverage.xml")));
    }

    private static CoverageArtifactCurrentRun CreateRun(string output, string repository, string contents)
    {
        var bytes = Encoding.UTF8.GetBytes(contents);
        File.WriteAllBytes(Path.Join(output, "coverage.xml"), bytes);
        return new(new CoverageArtifactRunManifest
        {
            RunId = Guid.NewGuid().ToString("N"),
            Outcome = "success",
            Version = "expiry-regression",
            CreatedAtUtc = DateTimeOffset.UtcNow
        }, [new("coverage.xml", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())], output, repository);
    }
}
