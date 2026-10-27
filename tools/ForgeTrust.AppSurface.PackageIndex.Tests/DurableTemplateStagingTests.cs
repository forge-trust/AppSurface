namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateStagingTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder(
        OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "template-stage-guards", Guid.NewGuid().ToString("N"));

    public DurableTemplateStagingTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("file")]
    public void NonDirectorySourceNeverCreatesTheDestination(string sourceKind)
    {
        var source = Path.Join(_root, "source");
        var destination = Path.Join(_root, "candidate");
        if (sourceKind == "file") File.WriteAllText(source, "preserve source");

        var error = Assert.Throws<PackageIndexException>(() =>
            DurableTemplateStaging.Stage(source, destination, "0.2.0-preview.13"));

        Assert.Contains("existing regular source", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
        if (sourceKind == "file") Assert.Equal("preserve source", File.ReadAllText(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OccupiedDestinationIsPreservedBeforeAnyContentCopy(bool destinationIsDirectory)
    {
        var source = Path.Join(_root, "source");
        var destination = Path.Join(_root, "candidate");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Join(source, "source-sentinel"), "source contents");
        if (destinationIsDirectory) Directory.CreateDirectory(destination);
        var sentinel = destinationIsDirectory ? Path.Join(destination, "caller-sentinel") : destination;
        File.WriteAllText(sentinel, "preserve caller contents");

        Assert.Throws<PackageIndexException>(() =>
            DurableTemplateStaging.Stage(source, destination, "0.2.0-preview.13"));

        Assert.Equal("preserve caller contents", File.ReadAllText(sentinel));
        Assert.Equal("source contents", File.ReadAllText(Path.Join(source, "source-sentinel")));
        if (destinationIsDirectory) Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
