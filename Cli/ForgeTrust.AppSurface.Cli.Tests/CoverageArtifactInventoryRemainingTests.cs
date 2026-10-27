using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactInventoryRemainingTests
{
    [Fact]
    public void FreshTreeCannotExpandAnAlreadyFullExplicitFileInventory()
    {
        using var temp = TestDirectory.Create();
        var fresh = temp.CreateDirectory("fresh");
        var sentinel = Path.Join(fresh, "sentinel");
        File.WriteAllText(sentinel, "preserve");
        var inventory = new CoverageArtifactInventory(temp.Path, new FrozenClock());
        for (var index = 0; index < CoverageArtifactLimits.MaximumFiles; index++)
            inventory.RecordFile(TestPathUtils.PathUnder(temp.Path, "missing-" + index));
        inventory.RecordFreshTree(fresh);
        var error = Assert.Throws<InvalidDataException>(() => inventory.Capture(default));
        Assert.Contains("file limit", error.Message, StringComparison.Ordinal);
        Assert.Equal("preserve", File.ReadAllText(sentinel));
    }

    [Fact]
    public void LargePortableFileInventoryCannotPublishOversizedEncodedMetadata()
    {
        using var temp = TestDirectory.Create();
        var relativeParent = string.Join('/', Enumerable.Repeat(new string('x', 240), 3));
        var parent = TestPathUtils.PathUnder(temp.Path, relativeParent);
        Directory.CreateDirectory(parent);
        var inventory = new CoverageArtifactInventory(temp.Path, new FrozenClock());
        const int count = 2800;
        for (var index = 0; index < count; index++)
        {
            var path = TestPathUtils.PathUnder(parent, "coverage-" + index.ToString("D4") + ".xml");
            File.WriteAllBytes(path, []);
            inventory.RecordFile(path);
        }
        var error = Assert.Throws<InvalidDataException>(() => inventory.Capture(default));
        Assert.Contains("encoded metadata limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(count, Directory.EnumerateFiles(parent).Count());
        Assert.All(Directory.EnumerateFiles(parent), path => Assert.Equal(0, new FileInfo(path).Length));
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }
}
