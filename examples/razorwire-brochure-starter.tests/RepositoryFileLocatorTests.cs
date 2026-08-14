namespace NorthstarBrochureStarter.Tests;

public sealed class RepositoryFileLocatorTests
{
    [Fact]
    public void Find_RejectsANullSegmentArray()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => RepositoryFileLocator.Find(null!));

        Assert.Equal("segments", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Find_RejectsEmptyRepositoryPathSegments(string segment)
    {
        var exception = Assert.Throws<ArgumentException>(() => RepositoryFileLocator.Find(segment));

        Assert.Equal("segments", exception.ParamName);
    }

    [Theory]
    [InlineData("/tmp/northstar")]
    [InlineData("\\tmp\\northstar")]
    [InlineData("C:\\temp\\northstar")]
    [InlineData("..")]
    [InlineData("nested/../northstar")]
    public void Find_RejectsUnsafeRepositoryPathSegments(string segment)
    {
        var exception = Assert.Throws<ArgumentException>(() => RepositoryFileLocator.Find(segment));

        Assert.Equal("segments", exception.ParamName);
    }

    [Fact]
    public void Find_ReportsEveryRelativeSegmentWhenTheFileIsMissing()
    {
        var exception = Assert.Throws<FileNotFoundException>(() => RepositoryFileLocator.Find(
            "not-a-repository-directory",
            "not-a-repository-file"));

        Assert.Contains(
            $"not-a-repository-directory{Path.DirectorySeparatorChar}not-a-repository-file",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Find_ReturnsARepositoryRelativeFile()
    {
        var solutionPath = RepositoryFileLocator.Find("ForgeTrust.AppSurface.slnx");

        Assert.EndsWith($"{Path.DirectorySeparatorChar}ForgeTrust.AppSurface.slnx", solutionPath, StringComparison.Ordinal);
        Assert.True(File.Exists(solutionPath));
    }
}
