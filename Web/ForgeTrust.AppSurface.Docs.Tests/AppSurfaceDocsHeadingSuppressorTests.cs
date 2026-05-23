using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public class AppSurfaceDocsHeadingSuppressorTests
{
    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldRemoveLeadingH1_WhenShellOwnsH1()
    {
        var content = "\n<h1 id=\"quickstart\"><a href=\"#quickstart\">Quickstart</a></h1>\n<p>Body</p>";

        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal("<p>Body</p>", suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldRemoveLeadingCommentsAndH1_WhenShellOwnsH1()
    {
        var content = "<!-- docs:snippet start -->\n<h1 id=\"quickstart\">Quickstart</h1>\n<p>Body</p>";

        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal("<p>Body</p>", suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldKeepLeadingH1_WhenShellDoesNotOwnH1()
    {
        var content = "<h1 id=\"api\">API Reference</h1>\n<p>Body</p>";

        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: false);

        Assert.Equal(content, suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldKeepLaterH1_WhenBodyDoesNotStartWithH1()
    {
        var content = "<p>Intro</p>\n<h1 id=\"deep-cut\">Deep Cut</h1>";

        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal(content, suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldKeepH10_WhenBodyStartsWithDifferentElement()
    {
        var content = "<h10>Not a page heading</h10>\n<p>Body</p>";

        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal(content, suppressed);
    }

    [Theory]
    [InlineData("<h1")]
    [InlineData("<h1 id=\"quickstart\"")]
    [InlineData("<h1>Quickstart")]
    [InlineData("<h1>Quickstart</h1")]
    [InlineData("<h1>Quickstart</h1 ")]
    [InlineData("<h1>Quickstart</h1 x>\n<p>Body</p>")]
    [InlineData("<h1>Quickstart</h10>\n<p>Body</p>")]
    [InlineData("<h1>Quickstart</h1x>\n<p>Body</p>")]
    [InlineData("<h1/>")]
    public void SuppressLeadingMarkdownH1_ShouldKeepMalformedLeadingH1(string content)
    {
        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal(content, suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldRemoveLeadingH1_WhenCloseTagHasWhitespace()
    {
        var content = "<h1>Quickstart</h1 \t >\n<p>Body</p>";

        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal("<p>Body</p>", suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldReturnEmptyContent_WhenOnlyLeadingH1Exists()
    {
        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(
            "<h1>Quickstart</h1>",
            shellOwnsH1: true);

        Assert.Equal(string.Empty, suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldRemoveLargeLeadingH1_WithoutRegexTimeout()
    {
        var headingText = new string('A', 200_000);
        var content = $"<!-- docs:snippet start -->\n<h1>{headingText}</h1>\n<p>Body</p>";

        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal("<p>Body</p>", suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldKeepContent_WhenLeadingCommentIsNotFollowedByH1()
    {
        var content = "<!-- docs:snippet start -->\n<p>Intro</p>\n<h1 id=\"deep-cut\">Deep Cut</h1>";

        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal(content, suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldKeepContent_WhenLeadingCommentIsUnterminated()
    {
        var content = "<!-- docs:snippet start\n<h1 id=\"quickstart\">Quickstart</h1>";

        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal(content, suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldKeepEmptyContent()
    {
        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(string.Empty, shellOwnsH1: true);

        Assert.Equal(string.Empty, suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldKeepWhitespaceOnlyContent()
    {
        var content = " \n\t";

        var suppressed = AppSurfaceDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal(content, suppressed);
    }
}
