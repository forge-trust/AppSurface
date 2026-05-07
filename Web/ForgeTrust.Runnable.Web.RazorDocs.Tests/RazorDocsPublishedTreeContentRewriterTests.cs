using ForgeTrust.Runnable.Web.RazorDocs.Services;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public sealed class RazorDocsPublishedTreeContentRewriterTests
{
    [Fact]
    public void RewriteHtml_ShouldRebaseStableDocsLinks_AndPreserveArchiveLink()
    {
        const string html =
            """
            <!DOCTYPE html>
            <html>
            <head>
              <link rel="stylesheet" href="/docs/search.css" />
              <link rel="preload" href="/docs/search-index.json" as="fetch" crossorigin="use-credentials" />
              <script src="/docs/outline-client.js"></script>
              <script>window.__razorDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","docsVersionsUrl":"/docs/versions"};</script>
            </head>
            <body>
              <a href="/docs/guide.html">Guide</a>
              <a href="/docs/search">Search</a>
              <a href="/docs/versions">Archive</a>
            </body>
            </html>
            """;

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs/v/1.2.3");

        Assert.Contains("/docs/v/1.2.3/search.css", rewritten);
        Assert.Contains("/docs/v/1.2.3/search-index.json", rewritten);
        Assert.Contains("/docs/v/1.2.3/outline-client.js", rewritten);
        Assert.Contains("/docs/v/1.2.3/guide.html", rewritten);
        Assert.Contains("/docs/v/1.2.3/search", rewritten);
        Assert.Contains("\"docsRootPath\":\"/docs/v/1.2.3\"", rewritten);
        Assert.Contains("\"docsSearchUrl\":\"/docs/v/1.2.3/search\"", rewritten);
        Assert.Contains("\"docsSearchIndexUrl\":\"/docs/v/1.2.3/search-index.json\"", rewritten);
        Assert.DoesNotContain("docsVersionsUrl", rewritten);
        Assert.Contains("href=\"/docs/versions\"", rewritten);
    }

    [Fact]
    public void RewriteSearchIndexJson_ShouldRebaseDocumentPaths()
    {
        const string json = "{\"documents\":[{\"path\":\"/docs/guide.html\",\"title\":\"Guide\"}]}";

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteSearchIndexJson(json, "/docs/v/1.2.3");
        var unchanged = RazorDocsPublishedTreeContentRewriter.RewriteSearchIndexJson(json, "/docs");

        Assert.Contains("\"path\":\"/docs/v/1.2.3/guide.html\"", rewritten);
        Assert.Equal(json, unchanged);
    }

    [Fact]
    public void RewriteHtml_ShouldBeNoOp_ForDocsBase()
    {
        const string html =
            """
            <!DOCTYPE html>
            <html>
            <head>
              <script>window.__razorDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","docsVersionsUrl":"/docs/versions"};</script>
            </head>
            <body>
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """;

        var unchanged = RazorDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs");

        Assert.Equal(html, unchanged);
    }

    [Fact]
    public void RewriteHtml_ShouldPrefixPathBaseForMountedDocsLinksAndClientConfig()
    {
        const string html =
            """
            <!DOCTYPE html>
            <html>
            <head>
              <link rel="stylesheet" href="/docs/search.css" />
              <script src="/docs/outline-client.js"></script>
              <script>window.__razorDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","docsVersionsUrl":"/docs/versions"};</script>
            </head>
            <body>
              <a href="/docs/guide.html">Guide</a>
              <a href="/docs/versions">Archive</a>
              <a href="/docs/preview/search">Preview</a>
            </body>
            </html>
            """;

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteHtml(
            html,
            "/docs/v/1.2.3",
            previewRootPath: "/docs/preview",
            requestPathBase: "/some-base");

        Assert.Contains("href=\"/some-base/docs/v/1.2.3/guide.html\"", rewritten);
        Assert.Contains("href=\"/some-base/docs/versions\"", rewritten);
        Assert.Contains("href=\"/some-base/docs/preview/search\"", rewritten);
        Assert.Contains("href=\"/some-base/docs/v/1.2.3/search.css\"", rewritten);
        Assert.Contains("src=\"/some-base/docs/v/1.2.3/outline-client.js\"", rewritten);
        Assert.Contains("\"docsRootPath\":\"/some-base/docs/v/1.2.3\"", rewritten);
        Assert.Contains("\"docsSearchUrl\":\"/some-base/docs/v/1.2.3/search\"", rewritten);
        Assert.Contains("\"docsSearchIndexUrl\":\"/some-base/docs/v/1.2.3/search-index.json\"", rewritten);
        Assert.DoesNotContain("docsVersionsUrl", rewritten);
    }

    [Fact]
    public void RewriteSearchIndexJson_ShouldPrefixPathBaseForRewrittenDocumentPaths()
    {
        const string json = "{\"documents\":[{\"path\":\"/docs/guide.html\",\"title\":\"Guide\"}]}";

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteSearchIndexJson(
            json,
            "/docs/v/1.2.3",
            requestPathBase: "/some-base");

        Assert.Contains("\"path\":\"/some-base/docs/v/1.2.3/guide.html\"", rewritten);
    }

    [Fact]
    public void RewriteHtml_ShouldRewriteSrcSetAbsoluteStableUrls_AndPreserveUnrelatedUrls()
    {
        const string html =
            """
            <!DOCTYPE html>
            <html>
            <body>
              <img srcset="  , /docs/img/hero.png 1x, https://example.com/docs/img/hero@2x.png 2x, https://example.com/static/logo.png 3x" />
              <img srcset="   " />
              <a href="https://example.com/docs/guide.html?view=full#top">Guide</a>
              <a href="https://example.com/blog">Blog</a>
              <a href="guide.html">Relative</a>
              <a href="   ">Blank</a>
            </body>
            </html>
            """;

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs/v/1.2.3");

        Assert.Contains("/docs/v/1.2.3/img/hero.png 1x", rewritten);
        Assert.Contains("https://example.com/docs/v/1.2.3/img/hero@2x.png 2x", rewritten);
        Assert.Contains("https://example.com/static/logo.png 3x", rewritten);
        Assert.Contains("https://example.com/docs/v/1.2.3/guide.html?view=full#top", rewritten);
        Assert.Contains("href=\"https://example.com/blog\"", rewritten);
        Assert.Contains("href=\"guide.html\"", rewritten);
        Assert.Contains("href=\"   \"", rewritten);
    }

    [Fact]
    public void RewriteHtml_ShouldPreservePathBase_WhenRewritingAbsoluteMountedDocsUrls()
    {
        const string html =
            """
            <!DOCTYPE html>
            <html>
            <body>
              <a href="https://example.com/docs/guide.html?view=full#top">Guide</a>
            </body>
            </html>
            """;

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteHtml(
            html,
            "/docs/v/1.2.3",
            requestPathBase: "/some-base");

        Assert.Contains("https://example.com/some-base/docs/v/1.2.3/guide.html?view=full#top", rewritten);
    }

    [Fact]
    public void RewriteHtml_ShouldLeaveInvalidDocsConfigScriptUnchanged()
    {
        const string html =
            """
            <!DOCTYPE html>
            <html>
            <body>
              <script>window.__razorDocsConfig = "oops";</script>
              <script>window.__razorDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":};</script>
              <script>window.otherConfig = true;</script>
            </body>
            </html>
            """;

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs/v/1.2.3");

        Assert.Contains("window.__razorDocsConfig = \"oops\";", rewritten);
        Assert.Contains("window.__razorDocsConfig = {\"docsRootPath\":\"/docs\",\"docsSearchUrl\":};", rewritten);
        Assert.Contains("window.otherConfig = true;", rewritten);
    }

    [Fact]
    public void RewriteHtml_ShouldLeaveNullDocsConfigScriptUnchanged_AndRewriteDescriptorlessSrcSetEntries()
    {
        const string html =
            """
            <!DOCTYPE html>
            <html>
            <body>
              <script>window.__razorDocsConfig = null;</script>
              <img srcset="/docs/img/hero.png, https://example.com/docs/img/hero@2x.png" />
            </body>
            </html>
            """;

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs/v/1.2.3");

        Assert.Contains("window.__razorDocsConfig = null;", rewritten);
        Assert.Contains("srcset=\"/docs/v/1.2.3/img/hero.png, https://example.com/docs/v/1.2.3/img/hero@2x.png\"", rewritten);
    }

    [Fact]
    public void RewriteHtml_ShouldPreservePreviewArchiveAndExactVersionUrls()
    {
        const string html =
            """
            <!DOCTYPE html>
            <html>
            <body>
              <a href="/docs/versions?view=full#archive">Archive</a>
              <a href="/docs/next/search?tab=preview#input">Preview</a>
              <a href="/docs/v/9.9.9/guide.html?view=full#top">Exact</a>
            </body>
            </html>
            """;

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs/v/1.2.3");

        Assert.Contains("href=\"/docs/versions?view=full#archive\"", rewritten);
        Assert.Contains("href=\"/docs/next/search?tab=preview#input\"", rewritten);
        Assert.Contains("href=\"/docs/v/9.9.9/guide.html?view=full#top\"", rewritten);
    }

    [Fact]
    public void RewriteHtml_ShouldPreserveConfiguredPreviewRootInsteadOfHardcodedDocsNext()
    {
        const string html =
            """
            <!DOCTYPE html>
            <html>
            <body>
              <a href="/docs/preview/search?tab=preview#input">Preview</a>
              <script>window.__razorDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","docsVersionsUrl":"/docs/versions"};</script>
            </body>
            </html>
            """;

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteHtml(
            html,
            "/docs/v/1.2.3",
            previewRootPath: "/docs/preview");

        Assert.Contains("href=\"/docs/preview/search?tab=preview#input\"", rewritten);
        Assert.Contains("\"docsRootPath\":\"/docs/v/1.2.3\"", rewritten);
    }

    [Fact]
    public void RewriteHtml_ShouldRewriteFragmentHtmlWithoutInjectingDoctype()
    {
        const string html =
            """
            <div>
              <script>window.__razorDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","docsVersionsUrl":"/docs/versions"};</script>
            </div>
            """;

        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs/v/1.2.3");

        Assert.DoesNotContain("<!DOCTYPE html>", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"docsRootPath\":\"/docs/v/1.2.3\"", rewritten);
    }

    [Fact]
    public void RewriteSearchIndexJson_ShouldIgnoreUnexpectedPayloads_AndBlankDocumentPaths()
    {
        const string unexpectedPayload = "{\"items\":[]}";
        const string nullPayload = "null";
        const string documentPayload =
            """
            {
              "documents": [
                {},
                { "path": "" },
                { "path": "/docs/guide.html", "title": "Guide" },
                { "path": "/docs/v/9.9.9/guide.html", "title": "Already versioned" },
                { "path": "https://example.com/blog", "title": "Blog" }
              ]
            }
            """;

        var unchanged = RazorDocsPublishedTreeContentRewriter.RewriteSearchIndexJson(unexpectedPayload, "/docs/v/1.2.3");
        var unchangedNull = RazorDocsPublishedTreeContentRewriter.RewriteSearchIndexJson(nullPayload, "/docs/v/1.2.3");
        var rewritten = RazorDocsPublishedTreeContentRewriter.RewriteSearchIndexJson(documentPayload, "/docs/v/1.2.3");

        Assert.Equal(unexpectedPayload, unchanged);
        Assert.Equal(nullPayload, unchangedNull);
        Assert.Contains("\"path\":\"/docs/v/1.2.3/guide.html\"", rewritten);
        Assert.Contains("\"path\":\"/docs/v/9.9.9/guide.html\"", rewritten);
        Assert.Contains("\"path\":\"https://example.com/blog\"", rewritten);
    }
}
