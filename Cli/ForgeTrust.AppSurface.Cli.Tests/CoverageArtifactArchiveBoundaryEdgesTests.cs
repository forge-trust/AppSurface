using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactArchiveBoundaryEdgesTests
{
    [Fact]
    public async Task NonemptyStageIsPreservedBeforeAnyArchiveWrite()
    {
        using var temp = TestDirectory.Create();
        var source = temp.CreateDirectory("source");
        using var stage = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "stage"), true, true)!;
        using (var sentinel = stage.OpenArtifactFile("sentinel", true)) sentinel.Write("preserve"u8);
        await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.StageAsync(source,
            stage.ArtifactDirectoryPath, Manifest(), [], 100, 100, TimeProvider.System, null, default));
        Assert.Equal("preserve", File.ReadAllText(Path.Join(stage.ArtifactDirectoryPath, "sentinel")));
        Assert.Single(stage.EnumerateArtifactEntries(3));
    }

    [Fact]
    public async Task ExcessiveInventoryAndNegativeDeclaredLengthFailBeforeOpeningAnyPaths()
    {
        var entries = Enumerable.Range(0, CoverageArtifactLimits.MaximumFiles + 1)
            .Select(index => new CoverageArtifactInventoryEntry("file-" + index, 0, new string('a', 64))).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.StageAsync("missing-source", "missing-stage",
            Manifest(), entries, 100, 100, TimeProvider.System, null, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.StageAsync("missing-source", "missing-stage",
            Manifest(), [new("file", -1, new string('a', 64))], 100, 100, TimeProvider.System, null, default));
    }

    private static CoverageArtifactRunManifest Manifest() => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        Outcome = "success",
        Version = "fixture",
        CreatedAtUtc = DateTimeOffset.UtcNow
    };
}
