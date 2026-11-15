using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInNativeInputTests
{
    [Fact]
    public void DiscoveryRequiresExactProducerSetAndDoesNotEstablishCustody()
    {
        using var temp = TestDirectory.Create();
        foreach (var id in new[] { "linux", "windows", "darwin" }) Manifest(temp, id, id);
        temp.WriteFile("ignored.txt", "ordinary transport payload");

        var found = CoverageFanInNativeInputs.Discover(temp.Path, ["linux", "windows", "darwin"]);

        Assert.Equal(3, found.Count);
        Assert.Equal("C:/source", found["windows"].SourceRoot);
        Assert.Equal(Path.Join(temp.Path, "linux"), found["linux"].Directory);
        Assert.DoesNotContain(Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).StartsWith(".appsurface", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("null-id")]
    [InlineData("empty-source")]
    [InlineData("malformed")]
    [InlineData("missing-property")]
    public void InvalidManifestSetCannotBecomeAnImportSelector(string fault)
    {
        using var temp = TestDirectory.Create();
        Manifest(temp, "one", "linux");
        switch (fault)
        {
            case "missing":
                Assert.Throws<InvalidDataException>(() => CoverageFanInNativeInputs.Discover(temp.Path, ["linux", "windows"]));
                return;
            case "duplicate": Manifest(temp, "two", "linux"); break;
            case "unknown": Manifest(temp, "two", "unknown"); break;
            case "null-id": temp.WriteFile("one/manifest.json", "{\"producerId\":null,\"sourceRoot\":\"/src\"}"); break;
            case "empty-source": temp.WriteFile("one/manifest.json", "{\"producerId\":\"linux\",\"sourceRoot\":\" \"}"); break;
            case "malformed":
                temp.WriteFile("one/manifest.json", "[");
                Assert.ThrowsAny<JsonException>(() => CoverageFanInNativeInputs.Discover(temp.Path, ["linux"]));
                return;
            case "missing-property":
                temp.WriteFile("one/manifest.json", "{}");
                Assert.Throws<KeyNotFoundException>(() => CoverageFanInNativeInputs.Discover(temp.Path, ["linux"]));
                return;
        }
        Assert.Throws<InvalidDataException>(() => CoverageFanInNativeInputs.Discover(temp.Path, ["linux"]));
    }

    [Fact]
    public void ManifestByteLimitIsInclusiveAndRejectsLargerInput()
    {
        using var temp = TestDirectory.Create();
        Manifest(temp, "one", "linux");
        var path = Path.Join(temp.Path, "one", "manifest.json");
        var json = File.ReadAllText(path);
        File.WriteAllText(path, json.PadRight(1024 * 1024));
        Assert.Single(CoverageFanInNativeInputs.Discover(temp.Path, ["linux"]));
        File.AppendAllText(path, " ");
        Assert.Throws<InvalidDataException>(() => CoverageFanInNativeInputs.Discover(temp.Path, ["linux"]));
    }

    [Fact]
    public void EntryBudgetIsGlobalAcrossDirectories()
    {
        using var temp = TestDirectory.Create();
        var first = temp.CreateDirectory("first");
        var second = temp.CreateDirectory("second");
        for (var i = 0; i < 5000; i++)
        {
            File.WriteAllText(Path.Join(first, $"entry-{i:D4}"), string.Empty);
            File.WriteAllText(Path.Join(second, $"entry-{i:D4}"), string.Empty);
        }
        Assert.Throws<InvalidDataException>(() => CoverageFanInNativeInputs.Discover(temp.Path, []));
    }

    [Fact]
    public void DepthBoundIsInclusiveAndCancelledDiscoveryDoesNotStart()
    {
        using var temp = TestDirectory.Create();
        var nested = string.Join('/', Enumerable.Repeat("nested", 64));
        Manifest(temp, nested, "linux");
        Assert.Single(CoverageFanInNativeInputs.Discover(temp.Path, ["linux"]));
        Manifest(temp, nested + "/deeper", "windows");
        Assert.Throws<InvalidDataException>(() => CoverageFanInNativeInputs.Discover(temp.Path, ["linux", "windows"]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CoverageFanInNativeInputs.Discover(temp.Path, ["linux"], cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedTransportObjectsAreRefusedWithoutReadingOutsideData(bool linkDirectory)
    {
        if (OperatingSystem.IsWindows()) return; // Windows native link policy has separate privileged coverage.
        using var temp = TestDirectory.Create();
        var input = temp.CreateDirectory("input");
        Manifest(temp, "outside", "linux");
        var outside = Path.Join(temp.Path, "outside");
        var manifest = Path.Join(outside, "manifest.json");
        var original = File.ReadAllBytes(manifest);
        if (linkDirectory) Directory.CreateSymbolicLink(Path.Join(input, "linked"), outside);
        else File.CreateSymbolicLink(Path.Join(input, "manifest.json"), manifest);

        Assert.Throws<IOException>(() => CoverageFanInNativeInputs.Discover(input, ["linux"]));
        Assert.Equal(original, File.ReadAllBytes(manifest));
    }

    private static void Manifest(TestDirectory temp, string directory, string id) =>
        temp.WriteFile(directory + "/manifest.json", JsonSerializer.Serialize(new { producerId = id, sourceRoot = "C:/source" }));
}
