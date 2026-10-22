using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactRecoveryFinalPathsTests
{
    [Theory]
    [InlineData("source-missing")]
    [InlineData("source-busy")]
    [InlineData("archive-busy")]
    [InlineData("repository-rebound")]
    [InlineData("manifest-length")]
    [InlineData("manifest-version")]
    [InlineData("manifest-outcome")]
    [InlineData("manifest-created")]
    [InlineData("manifest-provenance")]
    public async Task InterruptedPublicationPreservesArchiveAndReservationUntilAllFactsMatch(string fault)
    {
        using var temp = TestDirectory.Create();
        var repository = temp.CreateDirectory("repository");
        var output = temp.CreateDirectory("output");
        var domain = Path.Join(temp.Path, "domain");
        File.WriteAllText(Path.Join(output, "coverage.xml"), "original");
        using var store = CoverageArtifactStore.Open(domain);
        var run = new CoverageArtifactCurrentRun(new()
        { RunId = Guid.NewGuid().ToString("N"), Outcome = "success", Version = "fixture", CreatedAtUtc = DateTimeOffset.UtcNow },
            [new("coverage.xml", 8, Convert.ToHexString(SHA256.HashData("original"u8)).ToLowerInvariant())], output, repository);
        await store.RegisterAsync(run, default);
        await store.SnapshotAsync(run, false, false, default);
        var registryPath = Path.Join(domain, "registry.json");
        var registry = JsonNode.Parse(File.ReadAllBytes(registryPath))!;
        var registration = registry["Registrations"]![0]!;
        registration["Archived"] = false;
        registration["ArchiveBytes"] = 0;
        registration["ArchivedAtUtc"] = null;
        var reservationId = Guid.NewGuid().ToString("N");
        registry["Reservations"]!.AsArray().Add(new JsonObject
        {
            ["Id"] = reservationId,
            ["RunId"] = run.ManifestBase.RunId,
            ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
            ["StartedAtUtc"] = DateTimeOffset.UtcNow
        });
        File.WriteAllText(registryPath, registry.ToJsonString());
        var archive = Path.Join(domain, "archives", run.ManifestBase.RunId);
        var manifestPath = Path.Join(archive, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!;
        switch (fault)
        {
            case "source-missing": Directory.Move(output, output + "-retained"); break;
            case "repository-rebound": Directory.Move(repository, repository + "-retained"); Directory.CreateDirectory(repository); break;
            case "manifest-length": manifest["files"]![0]!["originalLength"] = 7; break;
            case "manifest-version": manifest["version"] = "changed"; break;
            case "manifest-outcome": manifest["outcome"] = "failed"; break;
            case "manifest-created": manifest["createdAtUtc"] = DateTimeOffset.UnixEpoch; break;
            case "manifest-provenance": manifest["provenance"] = new JsonObject { ["gitStateFingerprint"] = "dirty" }; break;
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var registryBefore = File.ReadAllBytes(registryPath);
        var manifestBefore = File.ReadAllBytes(manifestPath);
        using var blocker = fault == "source-busy" ? CoverageArtifactOperationLease.Acquire(output, true)
            : fault == "archive-busy" ? store.AcquireCandidateLease(archive, true) : null;
        Assert.Empty(await store.MaintainAsync(true, default));
        Assert.Equal(registryBefore, File.ReadAllBytes(registryPath));
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.Equal(0, store.GetStatus().ArchivedRuns);
        Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, store.GetStatus().StagingReservedBytes);
        Assert.Equal("original", File.ReadAllText(Path.Join(fault == "source-missing" ? output + "-retained" : output, "coverage.xml")));
    }
}
