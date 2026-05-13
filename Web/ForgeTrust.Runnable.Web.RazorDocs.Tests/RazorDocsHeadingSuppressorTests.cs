using ForgeTrust.Runnable.Web.RazorDocs.Services;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public class RazorDocsHeadingSuppressorTests
{
    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldRemoveLeadingH1_WhenShellOwnsH1()
    {
        var content = "\n<h1 id=\"quickstart\"><a href=\"#quickstart\">Quickstart</a></h1>\n<p>Body</p>";

        var suppressed = RazorDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal("<p>Body</p>", suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldRemoveLeadingCommentsAndH1_WhenShellOwnsH1()
    {
        var content = "<!-- docs:snippet start -->\n<h1 id=\"quickstart\">Quickstart</h1>\n<p>Body</p>";

        var suppressed = RazorDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal("<p>Body</p>", suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldKeepLeadingH1_WhenShellDoesNotOwnH1()
    {
        var content = "<h1 id=\"api\">API Reference</h1>\n<p>Body</p>";

        var suppressed = RazorDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: false);

        Assert.Equal(content, suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldKeepLaterH1_WhenBodyDoesNotStartWithH1()
    {
        var content = "<p>Intro</p>\n<h1 id=\"deep-cut\">Deep Cut</h1>";

        var suppressed = RazorDocsHeadingSuppressor.SuppressLeadingMarkdownH1(content, shellOwnsH1: true);

        Assert.Equal(content, suppressed);
    }

    [Fact]
    public void SuppressLeadingMarkdownH1_ShouldKeepEmptyContent()
    {
        var suppressed = RazorDocsHeadingSuppressor.SuppressLeadingMarkdownH1(string.Empty, shellOwnsH1: true);

        Assert.Equal(string.Empty, suppressed);
    }
}
