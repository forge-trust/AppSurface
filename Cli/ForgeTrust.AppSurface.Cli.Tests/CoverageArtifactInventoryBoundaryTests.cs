using ForgeTrust.AppSurface.Testing;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactInventoryBoundaryTests
{
    [Theory]
    [InlineData("file")]
    [InlineData("tree")]
    [InlineData("before")]
    [InlineData("after")]
    public void InvalidWriterDestinationsPreventCompleteInventory(string operation)
    {
        using var temp = TestDirectory.Create();
        var inventory = new CoverageArtifactInventory(temp.Path, TimeProvider.System);
        switch (operation)
        {
            case "file": inventory.RecordFile(Path.Join(temp.Path, "..", "outside")); break;
            case "tree": inventory.RecordFreshTree(Path.Join(temp.Path, "..", "outside")); break;
            case "before": Assert.Null(inventory.ObserveBeforeWrite("\0")); break;
            case "after": inventory.RecordAfterWrite("\0", null); break;
        }
        Assert.Throws<InvalidDataException>(() => inventory.Capture(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FileAndFreshTreeCapacityFailClosedWithoutTouchingSource(bool trees)
    {
        using var temp = TestDirectory.Create();
        var inventory = new CoverageArtifactInventory(temp.Path, TimeProvider.System);
        for (var index = 0; index <= CoverageArtifactLimits.MaximumFiles; index++)
        {
            var path = Path.Join(temp.Path, "missing-" + index);
            if (trees) inventory.RecordFreshTree(path);
            else inventory.RecordFile(path);
        }
        Assert.Throws<InvalidDataException>(() => inventory.Capture(CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("length")]
    public void UnrepresentableRelativePathIsNotAdopted(string defect)
    {
        using var temp = TestDirectory.Create();
        var inventory = new CoverageArtifactInventory(temp.Path, TimeProvider.System);
        var path = defect == "depth" ? string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("x", 33))
            : string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('x', 239), 20));
        inventory.RecordFile(TestPathUtils.PathUnder(temp.Path, path));
        Assert.Throws<InvalidDataException>(() => inventory.Capture(CancellationToken.None));
    }

    [Fact]
    public void ExistingDirectoryIsNotMisreportedAsAnAbsentFailedFileWrite()
    {
        using var temp = TestDirectory.Create();
        var directory = temp.CreateDirectory("coverage.xml");
        var inventory = new CoverageArtifactInventory(temp.Path, TimeProvider.System);
        inventory.RecordFile(directory);
        Assert.Throws<IOException>(() => inventory.Capture(CancellationToken.None));
        Assert.True(Directory.Exists(directory));
    }
}
