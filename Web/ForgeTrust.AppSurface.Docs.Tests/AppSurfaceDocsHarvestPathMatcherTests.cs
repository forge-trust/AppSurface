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
    public void MatchDirectoryOrDescendant_TreatsBracesAsLiteralPathCharacters()
    {
        var matcher = new AppSurfaceDocsHarvestPathMatcher(["docs/{literal}/README.md"]);

        Assert.Equal("docs/{literal}/README.md", matcher.MatchDirectoryOrDescendant("docs"));
        Assert.Equal("docs/{literal}/README.md", matcher.MatchDirectoryOrDescendant("docs/{literal}"));
        Assert.Null(matcher.MatchDirectoryOrDescendant("docs/{other}"));
    }

    [Fact]
    public void MatchDirectoryOrDescendantSubtree_IgnoresFileLevelPatterns()
    {
        var matcher = new AppSurfaceDocsHarvestPathMatcher([".github/workflows/README.md"]);

        Assert.Null(matcher.MatchDirectoryOrDescendantSubtree(".github"));
    }
}
