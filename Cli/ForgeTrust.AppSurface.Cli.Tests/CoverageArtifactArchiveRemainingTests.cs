using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactArchiveRemainingTests
{
    [Fact]
    public async Task ObservedSourceLengthMismatchPreservesOriginalFileAndEmptyStage()
    {
        using var temp = TestDirectory.Create();
        var source = temp.CreateDirectory("source");
        var path = Path.Join(source, "coverage.xml");
        await File.WriteAllTextAsync(path, "source");
        using var stage = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "stage"), true, true)!;
        await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.StageAsync(source, stage.ArtifactDirectoryPath,
            Manifest(), [new("coverage.xml", 7, Hash("source"u8.ToArray()))], 100, 100, TimeProvider.System, null, default));
        Assert.Empty(stage.EnumerateArtifactEntries(2));
        Assert.Equal("source", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task NonemptyRestoreStageIsPreservedWithoutExtractingPayloads()
    {
        using var temp = TestDirectory.Create();
        var source = temp.CreateDirectory("source");
        var bytes = "original coverage"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Join(source, "coverage.xml"), bytes);
        using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "archive"), true, true)!;
        var manifest = await CoverageArtifactArchive.StageAsync(source, archive.ArtifactDirectoryPath,
            Manifest(), [new("coverage.xml", bytes.Length, Hash(bytes))], 100, 100, TimeProvider.System, null, default);
        using var restore = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "restore"), true, true)!;
        using (var sentinel = restore.OpenArtifactFile("sentinel", true)) sentinel.Write("preserve"u8);
        await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.RestorePayloadsAsync(archive.ArtifactDirectoryPath,
            restore.ArtifactDirectoryPath, manifest, 100, TimeProvider.System, null, default));
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Join(restore.ArtifactDirectoryPath, "sentinel")));
        Assert.Single(restore.EnumerateArtifactEntries(2));
        Assert.False(File.Exists(Path.Join(restore.ArtifactDirectoryPath, "coverage.xml")));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("state")]
    [InlineData("configuration")]
    public void OversizedProvenanceIsRejectedBeforeManifestPublication(string field)
    {
        var oversized = new string('a', 129);
        var manifest = Manifest() with
        {
            Complete = true,
            Provenance = new(field == "revision" ? oversized : null, field == "state" ? oversized : null,
                field == "configuration" ? oversized : null)
        };
        using var destination = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => CoverageArtifactArchive.WriteManifest(destination, manifest));
        Assert.Equal(0, destination.Length);
    }

    [Theory]
    [InlineData("expanded", "expanded while reading")]
    [InlineData("truncated", "truncated")]
    [InlineData("rebound", "binding changed")]
    public async Task SourceMutationDuringArchiveNeverProducesACompleteManifest(string mutation, string reason)
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        var source = temp.CreateDirectory("source");
        var path = Path.Join(source, "coverage.xml");
        var bytes = new byte[CoverageArtifactLimits.OperationBufferMaxBytes + 128];
        new Random(42).NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        using var stage = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "stage"), true, true)!;
        var clock = new MutationClock(() =>
        {
            if (mutation == "rebound")
            {
                File.Move(path, Path.Join(source, "retained-original.xml"));
                File.WriteAllBytes(path, bytes);
            }
            else
            {
                using var changed = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                changed.SetLength(mutation == "expanded" ? bytes.Length + 100 : 1);
            }
        });
        var error = await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.StageAsync(source,
            stage.ArtifactDirectoryPath, Manifest(), [new("coverage.xml", bytes.Length, Hash(bytes))],
            10_000_000, 10_000_000, clock, TimeSpan.FromHours(1), default));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(stage.ArtifactDirectoryPath, "manifest.json")));
        Assert.True(File.Exists(path));
        if (mutation == "rebound") Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Join(source, "retained-original.xml")));
    }

    [Fact]
    public void OversizedEncodedManifestFailsBeforeWritingAnyBytes()
    {
        var pathPrefix = string.Join('/', Enumerable.Repeat(new string('x', 240), 4));
        var manifest = Manifest() with
        {
            Complete = true,
            Files = Enumerable.Range(0, 2000).Select(index => new CoverageArtifactFileEntry(
                pathPrefix + "/coverage-" + index, (index + 1).ToString("D8"), 0, Hash([]), 20, Hash([]), true)).ToArray()
        };
        using var output = new MemoryStream();
        var error = Assert.Throws<InvalidDataException>(() => CoverageArtifactArchive.WriteManifest(output, manifest));
        Assert.Contains("metadata limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    private sealed class MutationClock(Action mutate) : TimeProvider
    {
        private int _observations;
        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _observations) == 4) mutate();
            return 0;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static CoverageArtifactRunManifest Manifest() => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        Version = "fixture",
        Outcome = "success",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        OutputIdentity = "fixture-output",
        RepositoryIdentity = "fixture-repository"
    };
}
