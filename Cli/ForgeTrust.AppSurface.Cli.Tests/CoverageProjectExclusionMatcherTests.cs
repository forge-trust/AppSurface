using CliFx;
using ForgeTrust.AppSurface.Cli;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageProjectExclusionMatcherTests
{
    [Fact]
    public void NormalizePatterns_ShouldTrimNormalizeAndPreserveLeadingParents()
    {
        var result = CoverageProjectExclusionMatcher.NormalizePatterns(
            ["  ..\\Shared\\**\\Browser*.Tests.csproj  "]);

        Assert.Equal(["../Shared/**/Browser*.Tests.csproj"], result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/tests/Browser.Tests.csproj")]
    [InlineData("\\\\server\\share\\Browser.Tests.csproj")]
    [InlineData("C:\\tests\\Browser.Tests.csproj")]
    [InlineData("C:tests/Browser.Tests.csproj")]
    [InlineData("tests/Browser.Tests.csproj/")]
    [InlineData("tests//Browser.Tests.csproj")]
    [InlineData("./tests/Browser.Tests.csproj")]
    [InlineData("tests/../Browser.Tests.csproj")]
    [InlineData("tests/**Browser.Tests.csproj")]
    [InlineData("tests/Browser**.Tests.csproj")]
    [InlineData("..")]
    [InlineData("../..")]
    public void NormalizePatterns_ShouldRejectInvalidPatterns(string pattern)
    {
        var exception = Assert.Throws<CommandException>(
            () => CoverageProjectExclusionMatcher.NormalizePatterns([pattern]));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--exclude-test-project", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizePatterns_ShouldRejectNormalizedDuplicatesCaseInsensitively()
    {
        var exception = Assert.Throws<CommandException>(
            () => CoverageProjectExclusionMatcher.NormalizePatterns(
                ["tests\\Browser.Tests.csproj", "TESTS/Browser.Tests.csproj"]));

        Assert.Contains("duplicates an earlier", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Browser.Tests.csproj", "tests/e2e/Browser.Tests.csproj")]
    [InlineData("tests/*/Browser.Tests.csproj", "tests/e2e/Browser.Tests.csproj")]
    [InlineData("tests/**/Browser*.Tests.csproj", "tests/BrowserSmoke.Tests.csproj")]
    [InlineData("tests/**/Browser*.Tests.csproj", "tests/e2e/BrowserSmoke.Tests.csproj")]
    [InlineData("tests/**/Browser*.Tests.csproj", "tests/e2e/mobile/BrowserSmoke.Tests.csproj")]
    [InlineData("../Shared/*.Tests.csproj", "../Shared/Shared.Tests.csproj")]
    [InlineData("TESTS/**/browser*.tests.csproj", "tests\\e2e\\BrowserSmoke.Tests.csproj")]
    [InlineData("tests/**/𐐀*.Tests.csproj", "tests/𐐨Browser.Tests.csproj")]
    [InlineData("tests/**/𐐀Browser.Tests.csproj", "tests/𐐨Browser.Tests.csproj")]
    [InlineData("tests/**/Browser*", "tests/e2e/BrowserSmoke.Tests.csproj")]
    [InlineData("tests/**/*Browser*Tests.csproj", "tests/e2e/MobileBrowserSmoke.Tests.csproj")]
    [InlineData("Browser?.Tests.csproj", "tests/Browser?.Tests.csproj")]
    [InlineData("Browser[1].Tests.csproj", "tests/Browser[1].Tests.csproj")]
    [InlineData("Browser{One}.Tests.csproj", "tests/Browser{One}.Tests.csproj")]
    public void IsMatch_ShouldMatchDocumentedSegmentGrammar(string pattern, string projectPath)
    {
        Assert.True(CoverageProjectExclusionMatcher.IsMatch(pattern, projectPath));
    }

    [Theory]
    [InlineData("tests/*/Browser.Tests.csproj", "tests/e2e/mobile/Browser.Tests.csproj")]
    [InlineData("Browser?.Tests.csproj", "tests/Browser1.Tests.csproj")]
    [InlineData("tests/**/Browser*.Tests.csproj", "src/Browser.csproj")]
    [InlineData("../Shared/*.Tests.csproj", "Shared/Shared.Tests.csproj")]
    [InlineData("tests/**/*Browser*Tests.csproj", "tests/e2e/Browser.csproj")]
    public void IsMatch_ShouldRejectNonMatches(string pattern, string projectPath)
    {
        Assert.False(CoverageProjectExclusionMatcher.IsMatch(pattern, projectPath));
    }
}
