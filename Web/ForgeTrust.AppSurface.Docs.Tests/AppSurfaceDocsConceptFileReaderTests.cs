using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsConceptFileReaderTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "docs-concept-read-" + Guid.NewGuid().ToString("N"));

    public AppSurfaceDocsConceptFileReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Read_RequiresCompletePinnedBytes_AndCountsUtf8Bytes()
    {
        var bytes = Encoding.UTF8.GetBytes("é文");
        File.WriteAllBytes(Path.Join(_root, "map.json"), bytes);
        var pin = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.Equal(bytes, AppSurfaceDocsConceptFileReader.Read(_root, "map.json", pin, bytes.Length, bytes.Length));
        Assert.Throws<InvalidDataException>(() => AppSurfaceDocsConceptFileReader.Read(_root, "map.json", pin, bytes.Length - 1));
        Assert.Throws<InvalidDataException>(() => AppSurfaceDocsConceptFileReader.Read(_root, "map.json", pin, bytes.Length, 1));
        Assert.Throws<InvalidDataException>(() => AppSurfaceDocsConceptFileReader.Read(_root, "map.json", new string('0', 64), bytes.Length));
    }

    [Theory]
    [InlineData("../map.json")]
    [InlineData("/map.json")]
    [InlineData("sub\\map.json")]
    [InlineData("map.json?x")]
    [InlineData("map.json#x")]
    [InlineData("%2e%2e/map.json")]
    [InlineData("sub//map.json")]
    [InlineData("./map.json")]
    [InlineData("")]
    [InlineData("missing.json")]
    public void Read_RejectsUnconfinedOrMissingArtifacts(string path) =>
        Assert.Throws<InvalidDataException>(() => AppSurfaceDocsConceptFileReader.Read(_root, path, new string('0', 64), 100));

    [Theory]
    [InlineData("")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Read_RejectsInvalidTrustAnchors(string pin) =>
        Assert.Throws<InvalidDataException>(() => AppSurfaceDocsConceptFileReader.Read(_root, "map.json", pin, 100));

    [Fact]
    public void Read_RejectsSymlinksEvenWhenBytesAndPinMatch()
    {
        var target = Path.Combine(_root, "target.json");
        File.WriteAllText(target, "data");
        var link = Path.Combine(_root, "link.json");
        File.CreateSymbolicLink(link, target);
        var pin = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target)));
        Assert.Throws<InvalidDataException>(() => AppSurfaceDocsConceptFileReader.Read(_root, "link.json", pin, 100));
    }

    [Fact]
    public void ReadVerifiedStream_RejectsGrowthAndFinalInventoryMismatchWithoutAcceptingPrefix()
    {
        using var stream = new GrowingStream([1, 2], 1);
        var pin = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2 }));
        Assert.Throws<InvalidDataException>(() => AppSurfaceDocsConceptFileReader.ReadVerifiedStream(stream, pin, 1));
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => AppSurfaceDocsConceptFileReader.ReadVerifiedStream(stream, pin, 3, 1));
    }

    private sealed class GrowingStream(byte[] bytes, long initialLength) : MemoryStream(bytes)
    {
        public override long Length => initialLength;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
