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

    [Fact]
    public void MatchDirectoryOrDescendantSubtree_FindsLiteralSubtreePatterns()
    {
        var matcher = new AppSurfaceDocsHarvestPathMatcher([".github/workflows/**"]);

        Assert.Equal(".github/workflows/**", matcher.MatchDirectoryOrDescendantSubtree(".github"));
        Assert.Equal(".github/workflows/**", matcher.MatchDirectoryOrDescendantSubtree(".github/workflows"));
        Assert.Equal(".github/workflows/**", matcher.MatchDirectoryOrDescendantSubtree(".github/workflows/nested"));
        Assert.Null(matcher.MatchDirectoryOrDescendantSubtree(".git"));
    }

    [Fact]
    public void MatchDirectoryOrDescendantSubtree_IgnoresGlobSubtreeRootsWithoutDirectoryMatch()
    {
        var matcher = new AppSurfaceDocsHarvestPathMatcher(["docs/*/**"]);

        Assert.Null(matcher.MatchDirectoryOrDescendantSubtree("docs"));
    }

    [Theory]
    [InlineData("src/public*.js", "src", true)]
    [InlineData("src/public*.js", "src/nested", false)]
    [InlineData("src/**/*.js", "src/nested/deeper", true)]
    [InlineData("*.js", "src", false)]
    [InlineData("src/*/public.js", "src/widget", true)]
    [InlineData("src/*/public.js", "src/widget/deeper", false)]
    public void MatchFileInDirectoryOrDescendant_IdentifiesPotentialFileSubtrees(
        string pattern,
        string relativeDirectory,
        bool expectedMatch)
    {
        var matcher = new AppSurfaceDocsHarvestPathMatcher([pattern]);

        Assert.Equal(expectedMatch, matcher.MatchFileInDirectoryOrDescendant(relativeDirectory) is not null);
    }
}
