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
}
