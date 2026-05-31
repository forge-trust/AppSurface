namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class TestPathUtilsTests
{
    [Fact]
    public void PathUnder_ShouldAppendRelativeSegmentsWithoutRootedReset()
    {
        var basePath = Path.Join(Path.GetTempPath(), "appsurface");
        var path = TestPathUtils.PathUnder(basePath, "Web", "Docs", "Docs.csproj");

        Assert.Equal(
            Path.Join(basePath, $"Web{Path.DirectorySeparatorChar}Docs{Path.DirectorySeparatorChar}Docs.csproj"),
            path);
    }

    [Fact]
    public void PathUnder_ShouldRejectTraversalOutsideBasePath()
    {
        var basePath = Path.Join(Path.GetTempPath(), "appsurface");

        Assert.Throws<ArgumentException>(() => TestPathUtils.PathUnder(basePath, "..", "outside.txt"));
    }

    [Fact]
    public void PathUnder_ShouldAllowSamePathWhenSegmentIsDot()
    {
        var basePath = Path.Join(Path.GetTempPath(), "appsurface");
        var path = TestPathUtils.PathUnder(basePath, ".");

        Assert.Equal(Path.GetFullPath(basePath), path);
    }

    [Fact]
    public void RelativePath_ShouldTrimSeparatorNoise()
    {
        var relativePath = TestPathUtils.RelativePath(
            $"Web{Path.DirectorySeparatorChar}",
            $"Docs{Path.DirectorySeparatorChar}",
            "Docs.csproj");

        Assert.Equal($"Web{Path.DirectorySeparatorChar}Docs{Path.DirectorySeparatorChar}Docs.csproj", relativePath);
    }

    [Fact]
    public void RelativePath_ShouldRejectRootedSegments()
    {
        var rootedSegment = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath("Web", rootedSegment, "Docs.csproj"));
    }

    [Fact]
    public void RelativePath_ShouldRejectSeparatorOnlySegment()
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath(Path.DirectorySeparatorChar.ToString()));
    }

    [Fact]
    public void RelativePath_ShouldRejectEmptySegments()
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath());
    }

    [Fact]
    public void RelativePath_ShouldRejectNullSegment()
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath("Web", null!, "Docs.csproj"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RelativePath_ShouldRejectBlankSegment(string segment)
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath("Web", segment, "Docs.csproj"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PathUnder_ShouldRejectBlankBasePath(string basePath)
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.PathUnder(basePath, "Web", "Docs.csproj"));
    }
}
