using ForgeTrust.Runnable.Web.RazorDocs.Services;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public sealed class DocContentLinkRewriterTests
{
    private const string DefaultDocsRootPath = "/docs";

    [Fact]
    public void RewriteInternalDocLinks_ShouldRewriteRelativeMarkdownLinks_ToCanonicalDocsRoutes()
    {
        var html = """
            <p>
              <a class="proof-link" href="./unreleased.md">Unreleased</a>
              <a href="../CHANGELOG.md">Changelog</a>
            </p>
            """;

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/README.md", html, "releases/unreleased.md", "CHANGELOG.md");

        Assert.Contains("class=\"proof-link\"", rewritten);
        Assert.Contains("href=\"/docs/releases/unreleased.md.html\"", rewritten);
        Assert.Contains("href=\"/docs/CHANGELOG.md.html\"", rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldRewriteFragmentOnlyLinks_AndMarkAnchorNavigation()
    {
        var html = "<p><a href=\"#migration\">Migration</a></p>";

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/unreleased.md", html);

        Assert.Contains("href=\"/docs/releases/unreleased.md.html#migration\"", rewritten);
        Assert.Contains("data-doc-anchor-link=\"true\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldRewriteRootMountedFragmentOnlyLinks_AndMarkAnchorNavigation()
    {
        var html = "<p><a href=\"#migration\">Migration</a></p>";
        var manifest = DocLinkTargetManifest.FromPaths(["guides/quickstart.md"]);

        var rewritten = DocContentLinkRewriter.RewriteInternalDocLinks(
            "guides/quickstart.md",
            html,
            "/",
            manifest);

        Assert.Contains("href=\"/guides/quickstart.md.html#migration\"", rewritten);
        Assert.DoesNotContain("href=\"//guides/quickstart.md.html#migration\"", rewritten);
        Assert.Contains("data-doc-anchor-link=\"true\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldLeaveFragmentOnlyLinksUnchanged_WhenSourceIsNotPublished()
    {
        var html = "<p><a href=\"#migration\">Migration</a></p>";
        var manifest = DocLinkTargetManifest.FromPaths(["releases/unreleased.md"]);

        var rewritten = DocContentLinkRewriter.RewriteInternalDocLinks(
            "releases/draft.md",
            html,
            DefaultDocsRootPath,
            manifest);

        Assert.Contains("href=\"#migration\"", rewritten);
        Assert.DoesNotContain("data-doc-anchor-link=\"true\"", rewritten);
        Assert.DoesNotContain("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.DoesNotContain("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldDecorateCanonicalDocsLinks_WithoutChangingTheirRoutes()
    {
        var html = "<p><a href=\"/docs/releases/upgrade-policy.md.html\">Policy</a></p>";

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/unreleased.md", html, "releases/upgrade-policy.md");

        Assert.Contains("href=\"/docs/releases/upgrade-policy.md.html\"", rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldCanonicalizeSourceDocsLinks_UnderDocsRoot()
    {
        var html = "<p><a href=\"/docs/releases/upgrade-policy.md\">Policy</a></p>";

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/unreleased.md", html, "releases/upgrade-policy.md");

        Assert.Contains("href=\"/docs/releases/upgrade-policy.md.html\"", rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldLeaveNonPageDocsAssets_Unchanged()
    {
        var html = "<p><a href=\"/docs/search-index.json\">Search index</a></p>";

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/unreleased.md", html);

        Assert.Contains("href=\"/docs/search-index.json\"", rewritten);
        Assert.DoesNotContain("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.DoesNotContain("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldDecorateDocsLandingLinks_WithoutChangingTheirRoutes()
    {
        var html = "<p><a href=\"/docs\">Docs home</a></p>";

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/unreleased.md", html);

        Assert.Contains("href=\"/docs\"", rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldRewriteRootedMarkdownLinks_ToCanonicalDocsRoutes()
    {
        var html = "<p><a href=\"/releases/unreleased.md\">Unreleased</a></p>";

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/README.md", html, "releases/unreleased.md");

        Assert.Contains("href=\"/docs/releases/unreleased.md.html\"", rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldRewriteRootLevelRelativeLinks_AndUseSourcePathWithoutFragments()
    {
        var html = """
            <p>
              <a href="./README.md">Start here</a>
              <a href="#summary">Summary</a>
            </p>
            """;

        var rewritten = Rewrite(DefaultDocsRootPath, "CHANGELOG.md#history", html, "README.md");

        Assert.Contains("href=\"/docs/README.md.html\"", rewritten);
        Assert.Contains("href=\"/docs/CHANGELOG.md.html#summary\"", rewritten);
        Assert.Contains("data-doc-anchor-link=\"true\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldRewriteRelativeLinks_WhenSourcePathIsEmpty()
    {
        var html = "<p><a href=\"./README.md\">Start here</a></p>";

        var rewritten = Rewrite(DefaultDocsRootPath, string.Empty, html, "README.md");

        Assert.Contains("href=\"/docs/README.md.html\"", rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldLeaveRootedNonDocHtmlLinksUnchanged()
    {
        var html = "<p><a href=\"/privacy.html\">Privacy</a></p>";

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/README.md", html);

        Assert.Contains("href=\"/privacy.html\"", rewritten);
        Assert.DoesNotContain("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.DoesNotContain("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldRewriteRelativeCanonicalMarkdownLinks_ToDocsRoutes()
    {
        var html = "<p><a href=\"../CHANGELOG.md.html\">Changelog</a></p>";

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/README.md", html, "CHANGELOG.md");

        Assert.Contains("href=\"/docs/CHANGELOG.md.html\"", rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldRewritePackageReadmeLinks_InsideMarkdownTables()
    {
        var html = """
            <table>
              <tbody>
                <tr>
                  <td><code>ForgeTrust.Runnable.Web.OpenApi</code></td>
                  <td><a href="../Web/ForgeTrust.Runnable.Web.OpenApi/README.md">Package README</a></td>
                </tr>
              </tbody>
            </table>
            """;

        var rewritten = Rewrite(
            DefaultDocsRootPath,
            "packages/README.md",
            html,
            "Web/ForgeTrust.Runnable.Web.OpenApi/README.md");

        Assert.Contains(
            "href=\"/docs/Web/ForgeTrust.Runnable.Web.OpenApi/README.md.html\"",
            rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldLeaveRelativeNonDocHtmlLinksUnchanged()
    {
        var html = "<p><a href=\"../privacy.html\">Privacy</a> <a href=\"../../status.html\">Status</a></p>";

        var rewritten = Rewrite(
            DefaultDocsRootPath,
            "releases/templates/tagged-release-template.md",
            html);

        Assert.Contains("href=\"../privacy.html\"", rewritten);
        Assert.Contains("href=\"../../status.html\"", rewritten);
        Assert.DoesNotContain("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.DoesNotContain("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldRewriteSelfClosingAnchors_AsValidHtmlAnchors()
    {
        var html = "<a href=\"./unreleased.md\" />";

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/README.md", html, "releases/unreleased.md");

        Assert.Contains("href=\"/docs/releases/unreleased.md.html\"", rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("data-turbo-action=\"advance\"", rewritten);
        Assert.Contains("</a>", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldLeaveAnchorsWithoutNavigableDocTargetsUnchanged()
    {
        var html = """
            <p>
              <a href="   ">Empty</a>
              <a href="//example.com/releases">Scheme relative</a>
              <a href="?view=compact">Query only</a>
              <a href="/">Site root</a>
            </p>
            """;

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/README.md", html);

        Assert.Contains("href=\"   \"", rewritten);
        Assert.Contains("href=\"//example.com/releases\"", rewritten);
        Assert.Contains("href=\"?view=compact\"", rewritten);
        Assert.Contains("href=\"/\"", rewritten);
        Assert.DoesNotContain("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.DoesNotContain("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldLeaveExternalAndNewTabLinksUnchanged()
    {
        var html = """
            <p>
              <a href="https://example.com/releases">External</a>
              <a href="./unreleased.md" target="_blank">Open in new tab</a>
            </p>
            """;

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/README.md", html, "releases/unreleased.md");

        Assert.Contains("href=\"https://example.com/releases\"", rewritten);
        Assert.DoesNotContain("href=\"https://example.com/releases\" data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("href=\"./unreleased.md\" target=\"_blank\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldLeaveSourceLikeLinksUnchanged_WhenTargetIsNotHarvested()
    {
        var html = "<p><a href=\"./missing.md\">Missing</a> <a href=\"/docs/missing.md.html\">Missing route</a></p>";

        var rewritten = Rewrite(DefaultDocsRootPath, "releases/README.md", html, "releases/unreleased.md");

        Assert.Contains("href=\"./missing.md\"", rewritten);
        Assert.Contains("href=\"/docs/missing.md.html\"", rewritten);
        Assert.DoesNotContain("href=\"/docs/releases/missing.md.html\"", rewritten);
        Assert.DoesNotContain("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.DoesNotContain("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void PrefixPathBaseForDocsUrls_ShouldPrefixDocsLocalRootedLinks_AndLeaveOtherLinksUnchanged()
    {
        var html = """
            <p>
              <a href="/docs/next/guides/quickstart">Preview guide</a>
              <a href="/docs">Docs home</a>
              <a href="/docs/versions">Archive</a>
              <a href="/docs/v/1.2.3/guides/quickstart">Exact release</a>
              <a href="/privacy.html">Privacy</a>
              <a href="https://example.com/docs/guides/quickstart">External</a>
            </p>
            """;

        var rewritten = DocContentLinkRewriter.PrefixPathBaseForDocsUrls(html, "/docs/next", "/some-base");

        Assert.Contains("href=\"/some-base/docs/next/guides/quickstart\"", rewritten);
        Assert.Contains("href=\"/some-base/docs\"", rewritten);
        Assert.Contains("href=\"/some-base/docs/versions\"", rewritten);
        Assert.Contains("href=\"/some-base/docs/v/1.2.3/guides/quickstart\"", rewritten);
        Assert.Contains("href=\"/privacy.html\"", rewritten);
        Assert.Contains("href=\"https://example.com/docs/guides/quickstart\"", rewritten);
    }

    [Fact]
    public void PrefixPathBaseForDocsUrls_ShouldPrefixCustomRouteRootArchiveAndExactLinks()
    {
        var html = """
            <p>
              <a href="/foo/bar/next/guides/quickstart">Preview guide</a>
              <a href="/foo/bar">Stable docs</a>
              <a href="/foo/bar/versions">Archive</a>
              <a href="/foo/bar/v/1.2.3/guides/quickstart">Exact release</a>
              <a href="/privacy.html">Privacy</a>
            </p>
            """;

        var rewritten = DocContentLinkRewriter.PrefixPathBaseForDocsUrls(
            html,
            "/foo/bar/next",
            "/some-base",
            "/foo/bar");

        Assert.Contains("href=\"/some-base/foo/bar/next/guides/quickstart\"", rewritten);
        Assert.Contains("href=\"/some-base/foo/bar\"", rewritten);
        Assert.Contains("href=\"/some-base/foo/bar/versions\"", rewritten);
        Assert.Contains("href=\"/some-base/foo/bar/v/1.2.3/guides/quickstart\"", rewritten);
        Assert.Contains("href=\"/privacy.html\"", rewritten);
    }

    [Fact]
    public void PrefixPathBaseForDocsUrls_ShouldPrefixRootMountedStableRoutes_WhenLiveDocsRootIsChild()
    {
        var html = """
            <p>
              <a href="/next/guides/quickstart">Preview guide</a>
              <a href="/">Stable docs</a>
              <a href="/versions">Archive</a>
              <a href="/v/1.2.3/guides/quickstart">Exact release</a>
              <a href="/privacy.html">Privacy</a>
            </p>
            """;

        var rewritten = DocContentLinkRewriter.PrefixPathBaseForDocsUrls(
            html,
            "/next",
            "/some-base",
            "/");

        Assert.Contains("href=\"/some-base/next/guides/quickstart\"", rewritten);
        Assert.Contains("href=\"/some-base/\"", rewritten);
        Assert.Contains("href=\"/some-base/versions\"", rewritten);
        Assert.Contains("href=\"/some-base/v/1.2.3/guides/quickstart\"", rewritten);
        Assert.Contains("href=\"/privacy.html\"", rewritten);
    }

    [Fact]
    public void PrefixPathBaseForDocsUrls_ShouldLeaveHtmlUnchanged_WhenPathBaseIsMissing()
    {
        var html = "<p><a href=\"/docs/guides/quickstart\">Quickstart</a></p>";

        var rewritten = DocContentLinkRewriter.PrefixPathBaseForDocsUrls(html, "/docs", requestPathBase: null);

        Assert.Equal(html, rewritten);
    }

    [Fact]
    public void PrefixPathBaseForDocsUrls_ShouldHandleRootMountedDocsSurface()
    {
        var html = """
            <p>
              <a href="/guides/quickstart.md.html">Quickstart</a>
              <a href="/guides.html" data-turbo-frame="doc-content" data-turbo-action="advance">Guides</a>
              <a href="/search">Search</a>
              <a href="/privacy.html">Privacy</a>
              <a href="https://example.com/guides/quickstart">External</a>
            </p>
            """;

        var rewritten = DocContentLinkRewriter.PrefixPathBaseForDocsUrls(html, "/", "/some-base");

        Assert.Contains("href=\"/some-base/guides/quickstart.md.html\"", rewritten);
        Assert.Contains("href=\"/some-base/guides.html\"", rewritten);
        Assert.Contains("href=\"/some-base/search\"", rewritten);
        Assert.Contains("href=\"/privacy.html\"", rewritten);
        Assert.Contains("href=\"https://example.com/guides/quickstart\"", rewritten);
    }

    [Fact]
    public void PrefixPathBaseForDocsUrls_ShouldNormalizePathBase_AndIgnoreBlankFragmentAndSchemeRelativeLinks()
    {
        var html = """
            <p>
              <a href="   ">Blank</a>
              <a href="#migration">Fragment</a>
              <a href="//example.com/docs">Scheme relative</a>
              <a href="/docs/guide.html">Guide</a>
            </p>
            """;

        var rewritten = DocContentLinkRewriter.PrefixPathBaseForDocsUrls(html, "/docs", "some-base/");

        Assert.Contains("href=\"   \"", rewritten);
        Assert.Contains("href=\"#migration\"", rewritten);
        Assert.Contains("href=\"//example.com/docs\"", rewritten);
        Assert.Contains("href=\"/some-base/docs/guide.html\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldHandleRootMountedDocsSurface()
    {
        var html = "<p><a href=\"/guides/quickstart.md\">Quickstart</a></p>";
        var manifest = DocLinkTargetManifest.FromPaths(["guides/quickstart.md"]);

        var rewritten = DocContentLinkRewriter.RewriteInternalDocLinks(
            "README.md",
            html,
            "/",
            manifest);

        Assert.Contains("href=\"/guides/quickstart.md.html\"", rewritten);
        Assert.DoesNotContain("href=\"//guides/quickstart.md.html\"", rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldDecorateRootMountedCanonicalPlainHtmlDocsLinks()
    {
        var html = "<p><a href=\"/guides.html\">Guides</a></p>";
        var manifest = DocLinkTargetManifest.FromPaths(["guides"]);

        var rewritten = DocContentLinkRewriter.RewriteInternalDocLinks(
            "README.md",
            html,
            "/",
            manifest);

        Assert.Contains("href=\"/guides.html\"", rewritten);
        Assert.Contains("data-turbo-frame=\"doc-content\"", rewritten);
        Assert.Contains("data-turbo-action=\"advance\"", rewritten);
    }

    [Fact]
    public void RewriteInternalDocLinks_ShouldNotTreatSourceDocsFolder_AsDocsRouteRoot()
    {
        var html = """
            <p>
              <a href="./guide.md">Wrong directory</a>
              <a href="./docs/guide.md">Docs directory</a>
            </p>
            """;

        var rewritten = Rewrite(DefaultDocsRootPath, "README.md", html, "docs/guide.md");

        Assert.Contains("href=\"./guide.md\"", rewritten);
        Assert.Contains("href=\"/docs/docs/guide.md.html\"", rewritten);
    }

    [Fact]
    public void DocLinkTargetManifest_ShouldMatchRootedDocsRoutesWithQueryAndFragment()
    {
        var manifest = DocLinkTargetManifest.FromPaths(["releases/unreleased.md"]);

        Assert.True(manifest.Contains("/docs/releases/unreleased.md.html?view=compact#summary"));
    }

    [Fact]
    public void DocLinkTargetManifest_ShouldNotTreatDocsRootWithQueryAsDocumentTarget()
    {
        var manifest = DocLinkTargetManifest.FromPaths(["releases/unreleased.md"]);

        Assert.False(manifest.Contains("/docs/?view=compact"));
    }

    [Fact]
    public void DocLinkTargetManifest_ShouldMatchPreviewRootedDocsRoutes()
    {
        var manifest = DocLinkTargetManifest.FromPaths(["releases/unreleased.md"]);

        Assert.True(manifest.Contains("/docs/next/releases/unreleased.md.html?view=compact#summary"));
    }

    [Fact]
    public void DocLinkTargetManifest_ShouldMatchExactVersionDocsRoutes()
    {
        var manifest = DocLinkTargetManifest.FromPaths(["releases/unreleased.md"]);

        Assert.True(manifest.Contains("/docs/v/1.2.3/releases/unreleased.md.html#summary"));
    }

    [Fact]
    public void DocLinkTargetManifest_ShouldNotTreatExactVersionRootAsDocumentTarget()
    {
        var manifest = DocLinkTargetManifest.FromPaths(["releases/unreleased.md"]);

        Assert.False(manifest.Contains("/docs/v/1.2.3?view=compact"));
    }

    private static string Rewrite(string docsRootPath, string sourcePath, string html, params string[] knownPaths)
    {
        var manifest = DocLinkTargetManifest.FromPaths(knownPaths.Prepend(sourcePath));
        return DocContentLinkRewriter.RewriteInternalDocLinks(sourcePath, html, docsRootPath, manifest);
    }
}
