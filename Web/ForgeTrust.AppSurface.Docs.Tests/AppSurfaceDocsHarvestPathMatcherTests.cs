using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestPathMatcherTests
{
    [Fact]
    public void MatchDirectoryOrDescendant_FindsLiteralFilePatternUnderDirectory()
    {
        var matcher = new AppSurfaceDocsHarvestPathMatcher([".github/workflows/README.md"]);

        Assert.Equal(".github/workflows/README.md", matcher.MatchDirectoryOrDescendant(".github"));
        Assert.Equal(".github/workflows/README.md", matcher.MatchDirectoryOrDescendant(".github/workflows"));
        Assert.Null(matcher.MatchDirectoryOrDescendant(".git"));
    }

    [Fact]
    public void MatchDirectoryOrDescendant_FindsSubtreePatternUnderDirectory()
    {
        var matcher = new AppSurfaceDocsHarvestPathMatcher([".github/workflows/**"]);

        Assert.Equal(".github/workflows/**", matcher.MatchDirectoryOrDescendant(".github"));
        Assert.Equal(".github/workflows/**", matcher.MatchDirectoryOrDescendant(".github/workflows"));
        Assert.Equal(".github/workflows/**", matcher.MatchDirectoryOrDescendant(".github/workflows/nested"));
    }

    [Fact]
    public void MatchDirectoryOrDescendant_IgnoresRootLiteralFilePatternForDirectories()
    {
        var matcher = new AppSurfaceDocsHarvestPathMatcher(["README.md"]);

        Assert.Null(matcher.MatchDirectoryOrDescendant(".github"));
    }

    [Fact]
    public void MatchDirectoryOrDescendant_TreatsRootGlobPatternAsPotentialDirectoryMatch()
    {
        var matcher = new AppSurfaceDocsHarvestPathMatcher(["*/README.md"]);

        Assert.Equal("*/README.md", matcher.MatchDirectoryOrDescendant(".github"));
    }

    [Fact]
    public void MatchDirectoryOrDescendantSubtree_IgnoresFileLevelPatterns()
    {
        var matcher = new AppSurfaceDocsHarvestPathMatcher([".github/workflows/README.md"]);

        Assert.Null(matcher.MatchDirectoryOrDescendantSubtree(".github"));
    }
}
