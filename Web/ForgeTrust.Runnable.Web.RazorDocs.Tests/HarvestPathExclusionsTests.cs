using ForgeTrust.Runnable.Web.RazorDocs.Services;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public class HarvestPathExclusionsTests
{
    [Theory]
    [InlineData(".github/workflows/file.md")]
    [InlineData(".github/bin/file.md")]
    [InlineData("docs/.agent/nested/file.md")]
    [InlineData("src/.codex/config/file.cs")]
    public void ShouldExcludeFilePath_WhenPathContainsDotPrefixedDirectory(string filePath)
    {
        Assert.True(HarvestPathExclusions.ShouldExcludeFilePath(filePath));
    }

    [Theory]
    [InlineData("src/bin/file.cs")]
    [InlineData("src/obj/file.cs")]
    [InlineData("src/Tests/file.cs")]
    [InlineData("src/Test/file.cs")]
    [InlineData("Web/ForgeTrust.Runnable.Web.Tests/README.md")]
    [InlineData("Web/ForgeTrust.Runnable.Web.UnitTests/Fixture.cs")]
    [InlineData("node_modules/pkg/readme.md")]
    public void ShouldExcludeFilePath_WhenPathContainsExplicitExcludedDirectory(string filePath)
    {
        Assert.True(HarvestPathExclusions.ShouldExcludeFilePath(filePath));
    }

    [Theory]
    [InlineData("docs/readme.md")]
    [InlineData(".hidden.md")]
    [InlineData("docs/@special!$/file.md")]
    [InlineData("")]
    public void ShouldExcludeFilePath_WhenPathHasNoExcludedDirectories_ReturnsFalse(string filePath)
    {
        Assert.False(HarvestPathExclusions.ShouldExcludeFilePath(filePath));
    }

    [Fact]
    public void ShouldExcludeFilePath_WhenPathUsesMixedSeparators_IsHandledCorrectly()
    {
        Assert.True(HarvestPathExclusions.ShouldExcludeFilePath(@"docs\.github/workflows\file.md"));
        Assert.False(HarvestPathExclusions.ShouldExcludeFilePath(@"docs\guides/getting-started.md"));
    }

    [Fact]
    public void ShouldExcludeFilePath_WhenCustomExcludedDirectoriesAreProvided_UsesSharedHiddenDirectoryRules()
    {
        IReadOnlySet<string> excludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TestResults"
        };

        Assert.True(HarvestPathExclusions.ShouldExcludeFilePath("artifacts/TestResults/README.md", excludedDirectories));
        Assert.False(HarvestPathExclusions.ShouldExcludeFilePath("src/Tests/README.md", excludedDirectories));
        Assert.True(HarvestPathExclusions.ShouldExcludeFilePath(".github/README.md", excludedDirectories));
    }

    [Fact]
    public void ShouldExcludeFilePath_WhenCustomExcludedDirectoriesAreNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => HarvestPathExclusions.ShouldExcludeFilePath("README.md", null!));
    }

    [Fact]
    public void ShouldExcludeFilePath_WhenPathIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => HarvestPathExclusions.ShouldExcludeFilePath(null!));
    }
}
