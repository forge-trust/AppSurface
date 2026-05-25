using System.Text;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsPublishedTreeHandlerTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly string _tempDirectory;

    public AppSurfaceDocsPublishedTreeHandlerTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), "appsurfacedocs-published-tree-handler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldIgnoreNonGetAndHeadRequests()
    {
        var tree = CreatePublishedTree("release");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var httpContext = CreateContext(HttpMethods.Post, "/docs/v/1.2.3");

        var handled = await handler.TryHandleAsync(httpContext);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldTreatMissingRequestPathAsUnmatched()
    {
        var tree = CreatePublishedTree("release");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(httpContext);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldReturnFalse_WhenRequestDoesNotMatchMountOrResolvedFileIsMissing()
    {
        var tree = CreatePublishedTree("release");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");

        var unrelatedRequest = CreateContext(HttpMethods.Get, "/elsewhere");
        var missingRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/missing");

        Assert.False(await handler.TryHandleAsync(unrelatedRequest));
        Assert.False(await handler.TryHandleAsync(missingRequest));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldExhaustRootTrailingSlashAndExtensionCandidates_WhenFilesAreMissing()
    {
        var tree = CreatePublishedTree("missing-candidates");
        File.Delete(Path.Combine(tree, "index.html"));
        var handler = CreateHandler(tree, "/docs/v/1.2.3");

        var missingRootRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3");
        var missingTrailingSlashRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/missing-dir/");
        var missingExtensionRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/missing.css");

        Assert.False(await handler.TryHandleAsync(missingRootRequest));
        Assert.False(await handler.TryHandleAsync(missingTrailingSlashRequest));
        Assert.False(await handler.TryHandleAsync(missingExtensionRequest));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldResolveRootDirectoryAndFallbackDirectoryCandidates()
    {
        var tree = CreatePublishedTree("release");
        File.WriteAllText(Path.Combine(tree, "System.Text.html"), "<!DOCTYPE html><html><body>dotted-slug</body></html>");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");

        var rootRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3");
        var trailingSlashRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/guide/");
        var folderFallbackRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/folder-only");
        var dottedSlugRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/System.Text");

        Assert.True(await handler.TryHandleAsync(rootRequest));
        Assert.Contains("/docs/v/1.2.3/guide.html", ReadBody(rootRequest));

        Assert.True(await handler.TryHandleAsync(trailingSlashRequest));
        Assert.Contains("guide-index", ReadBody(trailingSlashRequest));

        Assert.True(await handler.TryHandleAsync(folderFallbackRequest));
        Assert.Contains("folder-only", ReadBody(folderFallbackRequest));

        Assert.True(await handler.TryHandleAsync(dottedSlugRequest));
        Assert.Contains("dotted-slug", ReadBody(dottedSlugRequest));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldServeStaticFilesAndHonorHeadRequests()
    {
        var tree = CreatePublishedTree("release");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");

        var getRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search.css");
        var outlineRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/outline-client.js");
        var headRequest = CreateContext(HttpMethods.Head, "/docs/v/1.2.3/search.css");

        Assert.True(await handler.TryHandleAsync(getRequest));
        Assert.Equal("text/css", getRequest.Response.ContentType);
        Assert.Contains("body { color: #fff; }", ReadBody(getRequest));
        Assert.NotNull(getRequest.Response.ContentLength);

        Assert.True(await handler.TryHandleAsync(outlineRequest));
        Assert.Equal("text/javascript", outlineRequest.Response.ContentType);
        Assert.Contains("window.__outlineClientLoaded = true;", ReadBody(outlineRequest));
        Assert.NotNull(outlineRequest.Response.ContentLength);

        Assert.True(await handler.TryHandleAsync(headRequest));
        Assert.Equal("text/css", headRequest.Response.ContentType);
        Assert.Equal(string.Empty, ReadBody(headRequest));
        Assert.NotNull(headRequest.Response.ContentLength);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldServeExactHtmlFiles()
    {
        var tree = CreatePublishedTree("release");
        File.WriteAllText(Path.Combine(tree, "guide.html"), "<!DOCTYPE html><html><body>guide page</body></html>");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/guide.html");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal("text/html", request.Response.ContentType);
        Assert.Contains("guide page", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRedirectFrozenManifestAliases_ToMountedCanonicalRoutes()
    {
        var tree = CreatePublishedTree("release-with-frozen-manifest");
        WriteFrozenManifest(
            tree,
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "packages/README.md",
                  "canonicalRoutePath": "packages",
                  "recoveryAliases": ["packages/README.md", "packages/README.md.html"],
                  "declaredAliases": ["legacy/package"]
                }
              ]
            }
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/packages/README.md", pathBase: "/base");
        request.Request.QueryString = new QueryString("?view=compact");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status301MovedPermanently, request.Response.StatusCode);
        Assert.Equal("/base/docs/v/1.2.3/packages?view=compact", request.Response.Headers.Location);
        Assert.Equal(string.Empty, ReadBody(request));

        var declaredAliasRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/legacy/package");

        Assert.True(await handler.TryHandleAsync(declaredAliasRequest));
        Assert.Equal("/docs/v/1.2.3/packages", declaredAliasRequest.Response.Headers.Location);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRedirectFrozenManifestAliases_ForRootMountedArchives()
    {
        var tree = CreatePublishedTree("root-mounted-frozen-manifest");
        WriteFrozenManifest(
            tree,
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "packages/README.md",
                  "canonicalRoutePath": "packages",
                  "recoveryAliases": ["packages/README.md"],
                  "declaredAliases": []
                }
              ]
            }
            """);
        var handler = CreateHandler(tree, "/", previewRootPath: "/next", routeRootPath: "/");
        var request = CreateContext(HttpMethods.Get, "/packages/README.md");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status301MovedPermanently, request.Response.StatusCode);
        Assert.Equal("/packages", request.Response.Headers.Location);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPlaceQueryBeforeFrozenCanonicalFragments()
    {
        var tree = CreatePublishedTree("fragment-frozen-manifest");
        WriteFrozenManifest(
            tree,
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "guide.md#advanced",
                  "canonicalRoutePath": "guide#advanced",
                  "recoveryAliases": ["guide.md"],
                  "declaredAliases": []
                }
              ]
            }
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/guide.md");
        request.Request.QueryString = new QueryString("?view=compact");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status301MovedPermanently, request.Response.StatusCode);
        Assert.Equal("/docs/v/1.2.3/guide?view=compact#advanced", request.Response.Headers.Location);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldNotRedirectHiddenFrozenManifestAliases()
    {
        var tree = CreatePublishedTree("hidden-frozen-manifest-alias");
        WriteFrozenManifest(
            tree,
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": ".drafts/guide.md",
                  "canonicalRoutePath": "guide",
                  "recoveryAliases": [".drafts/guide.md"],
                  "declaredAliases": []
                }
              ]
            }
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/.drafts/guide.md");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldNotRedirectUnsafeFrozenManifestAliases()
    {
        var tree = CreatePublishedTree("unsafe-frozen-manifest-alias");
        WriteFrozenManifest(
            tree,
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "guide.md",
                  "canonicalRoutePath": "guide",
                  "recoveryAliases": ["legacy//guide"],
                  "declaredAliases": []
                }
              ]
            }
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/legacy//guide");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldNotRedirectUnsafeFrozenCanonicalRoutes()
    {
        var tree = CreatePublishedTree("unsafe-frozen-canonical-route");
        File.WriteAllText(Path.Join(tree, "legacy.html"), "<!DOCTYPE html><html><body>legacy page</body></html>");
        WriteFrozenManifest(
            tree,
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "legacy.md",
                  "canonicalRoutePath": "../admin",
                  "recoveryAliases": ["legacy"],
                  "declaredAliases": []
                }
              ]
            }
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/legacy");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status200OK, request.Response.StatusCode);
        Assert.Contains("legacy page", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldIgnoreAmbiguousFrozenManifestAliases_AndContinueServingFiles()
    {
        var tree = CreatePublishedTree("ambiguous-frozen-manifest");
        File.WriteAllText(Path.Join(tree, "legacy.html"), "<!DOCTYPE html><html><body>legacy page</body></html>");
        WriteFrozenManifest(
            tree,
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "first.md",
                  "canonicalRoutePath": "first",
                  "recoveryAliases": ["legacy"],
                  "declaredAliases": []
                },
                {
                  "sourcePath": "second.md",
                  "canonicalRoutePath": "second",
                  "recoveryAliases": ["legacy"],
                  "declaredAliases": []
                }
              ]
            }
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/legacy");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status200OK, request.Response.StatusCode);
        Assert.Contains("legacy page", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDisableFrozenAliasRecovery_WhenManifestIsMalformed()
    {
        var tree = CreatePublishedTree("malformed-frozen-manifest");
        Directory.CreateDirectory(Path.Join(tree, "packages"));
        File.WriteAllText(Path.Join(tree, "packages", "README.md.html"), "<!DOCTYPE html><html><body>legacy artifact</body></html>");
        WriteFrozenManifest(tree, "{");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var aliasRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/packages/README.md");
        var manifestRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/.appsurface-docs-route-manifest.json");

        Assert.True(await handler.TryHandleAsync(aliasRequest));
        Assert.Equal(StatusCodes.Status200OK, aliasRequest.Response.StatusCode);
        Assert.Contains("legacy artifact", ReadBody(aliasRequest));
        Assert.False(await handler.TryHandleAsync(manifestRequest));

        var fileFallbackRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/packages/README.md.html");
        Assert.True(await handler.TryHandleAsync(fileFallbackRequest));
        Assert.Contains("legacy artifact", ReadBody(fileFallbackRequest));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldReturnFalse_WhenExactHtmlFileIsMissing()
    {
        var tree = CreatePublishedTree("release");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/missing.html");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectUnexpectedExactFiles()
    {
        var tree = CreatePublishedTree("custom-asset");
        File.WriteAllText(Path.Combine(tree, "asset.weird"), "custom-asset");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/asset.weird");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectExactFilesUnderDotSegments()
    {
        var tree = CreatePublishedTree("dot-segment-asset");
        Directory.CreateDirectory(Path.Combine(tree, ".private"));
        File.WriteAllText(Path.Combine(tree, ".private", "search.css"), "body { color: red; }");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/.private/search.css");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectHiddenDirectoriesThroughIndexFallback()
    {
        var tree = CreatePublishedTree("dot-segment-index");
        Directory.CreateDirectory(Path.Combine(tree, ".private"));
        File.WriteAllText(Path.Combine(tree, ".private", "index.html"), "<html>secret</html>");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/.private/");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectNonContractTextArtifacts()
    {
        var tree = CreatePublishedTree("text-artifact");
        File.WriteAllText(Path.Combine(tree, "notes.txt"), "notes");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/notes.txt");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldServeEmbeddedImageAssets()
    {
        var tree = CreatePublishedTree("embedded-image");
        Directory.CreateDirectory(Path.Combine(tree, "img"));
        File.WriteAllBytes(Path.Combine(tree, "img", "hero.png"), [0x89, 0x50, 0x4E, 0x47]);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/img/hero.png");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal("image/png", request.Response.ContentType);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRewriteSearchIndexAndHonorHeadRequests()
    {
        var tree = CreatePublishedTree("release");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");

        var getRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search-index.json");
        var headRequest = CreateContext(HttpMethods.Head, "/docs/v/1.2.3/search-index.json");

        Assert.True(await handler.TryHandleAsync(getRequest));
        Assert.Equal("application/json; charset=utf-8", getRequest.Response.ContentType);
        Assert.Contains("\"path\":\"/docs/v/1.2.3/guide.html\"", ReadBody(getRequest));

        Assert.True(await handler.TryHandleAsync(headRequest));
        Assert.Equal("application/json; charset=utf-8", headRequest.Response.ContentType);
        Assert.Equal(string.Empty, ReadBody(headRequest));
        Assert.NotNull(headRequest.Response.ContentLength);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldBypassStableAliasForPreviewArchiveAndReservedVersionPaths()
    {
        var tree = CreatePublishedTree("release");
        var handler = CreateHandler(tree, "/docs", previewRootPath: "/docs/preview");

        var previewRequest = CreateContext(HttpMethods.Get, "/docs/preview/search.css");
        var archiveRequest = CreateContext(HttpMethods.Get, "/docs/versions");
        var reservedVersionPrefixRequest = CreateContext(HttpMethods.Get, "/docs/v");
        var reservedVersionRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3");

        Assert.False(await handler.TryHandleAsync(previewRequest));
        Assert.False(await handler.TryHandleAsync(archiveRequest));
        Assert.False(await handler.TryHandleAsync(reservedVersionPrefixRequest));
        Assert.False(await handler.TryHandleAsync(reservedVersionRequest));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldBypassStableAliasForCustomRouteRootReservedPaths()
    {
        var tree = CreatePublishedTree("custom-route-release");
        var handler = CreateHandler(
            tree,
            "/foo/bar",
            previewRootPath: "/foo/bar/next",
            routeRootPath: "/foo/bar");

        var previewRequest = CreateContext(HttpMethods.Get, "/foo/bar/next/search.css");
        var archiveRequest = CreateContext(HttpMethods.Get, "/foo/bar/versions");
        var reservedVersionPrefixRequest = CreateContext(HttpMethods.Get, "/foo/bar/v");
        var reservedVersionRequest = CreateContext(HttpMethods.Get, "/foo/bar/v/1.2.3");

        Assert.False(await handler.TryHandleAsync(previewRequest));
        Assert.False(await handler.TryHandleAsync(archiveRequest));
        Assert.False(await handler.TryHandleAsync(reservedVersionPrefixRequest));
        Assert.False(await handler.TryHandleAsync(reservedVersionRequest));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldServeRootMountedPublishedTreeChildren()
    {
        var tree = CreatePublishedTree("root-mounted-release");
        var handler = CreateHandler(tree, "/", previewRootPath: "/next", routeRootPath: "/");
        var request = CreateContext(HttpMethods.Get, "/guide");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal("text/html", request.Response.ContentType);
        Assert.Contains("guide-index", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPreserveConfiguredPreviewRoot_WhenRewritingMountedHtml()
    {
        var tree = CreatePublishedTree("custom-preview-root");
        File.WriteAllText(
            Path.Combine(tree, "index.html"),
            """
            <!DOCTYPE html>
            <html>
            <head>
              <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","docsVersionsUrl":"/docs/versions"};</script>
            </head>
            <body>
              <a href="/docs/preview/search?tab=preview#input">Preview</a>
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3", previewRootPath: "/docs/preview");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Contains("href=\"/docs/preview/search?tab=preview#input\"", ReadBody(request));
        Assert.Contains("\"docsRootPath\":\"/docs/v/1.2.3\"", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldLeaveNullDocsConfigScriptUnchanged_WhenMountedHtmlContainsNullConfig()
    {
        var tree = CreatePublishedTree("null-config");
        File.WriteAllText(
            Path.Combine(tree, "index.html"),
            """
            <!DOCTYPE html>
            <html>
            <head>
              <script>window.__appSurfaceDocsConfig = null;</script>
            </head>
            <body>
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3");

        Assert.True(await handler.TryHandleAsync(request));
        var html = ReadBody(request);
        Assert.Contains("window.__appSurfaceDocsConfig = null;", html);
        Assert.Contains("href=\"/docs/v/1.2.3/guide.html\"", html);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldHonorHeadRequests_WhenRewritingMountedHtml()
    {
        var tree = CreatePublishedTree("head-rewritten-html");
        File.WriteAllText(
            Path.Combine(tree, "index.html"),
            """
            <!DOCTYPE html>
            <html>
            <head>
              <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","docsVersionsUrl":"/docs/versions"};</script>
            </head>
            <body>
              <a href="/docs/preview/search?tab=preview#input">Preview</a>
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3", previewRootPath: "/docs/preview");
        var request = CreateContext(HttpMethods.Head, "/docs/v/1.2.3");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal("text/html", request.Response.ContentType);
        Assert.NotNull(request.Response.ContentLength);
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPrefixRequestPathBase_WhenRewritingMountedHtmlAndSearchIndex()
    {
        var tree = CreatePublishedTree("path-base");
        File.WriteAllText(
            Path.Combine(tree, "index.html"),
            """
            <!DOCTYPE html>
            <html>
            <head>
              <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","docsVersionsUrl":"/docs/versions"};</script>
            </head>
            <body>
              <a href="/docs/preview/search?tab=preview#input">Preview</a>
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3", previewRootPath: "/docs/preview");
        var htmlRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3", pathBase: "/some-base");
        var searchIndexRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search-index.json", pathBase: "/some-base");

        Assert.True(await handler.TryHandleAsync(htmlRequest));
        var html = ReadBody(htmlRequest);
        Assert.Contains("href=\"/some-base/docs/preview/search?tab=preview#input\"", html);
        Assert.Contains("href=\"/some-base/docs/v/1.2.3/guide.html\"", html);
        Assert.Contains("\"docsRootPath\":\"/some-base/docs/v/1.2.3\"", html);
        Assert.Contains("\"docsSearchUrl\":\"/some-base/docs/v/1.2.3/search\"", html);
        Assert.Contains("\"docsSearchIndexUrl\":\"/some-base/docs/v/1.2.3/search-index.json\"", html);
        Assert.DoesNotContain("docsVersionsUrl", html);

        Assert.True(await handler.TryHandleAsync(searchIndexRequest));
        Assert.Contains("\"path\":\"/some-base/docs/v/1.2.3/guide.html\"", ReadBody(searchIndexRequest));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPreferLongestMatchingMount_WhenMountRootsOverlap()
    {
        var stableTree = CreatePublishedTree("stable");
        var versionedTree = CreatePublishedTree("versioned");
        File.WriteAllText(Path.Combine(stableTree, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Combine(versionedTree, "search.css"), "body { color: #0ea5e9; }");
        using var stableProvider = new PhysicalFileProvider(stableTree);
        using var versionedProvider = new PhysicalFileProvider(versionedTree);

        var handler = new AppSurfaceDocsPublishedTreeHandler(
            [
                new AppSurfaceDocsPublishedTreeMount("/docs", stableProvider),
                new AppSurfaceDocsPublishedTreeMount("/docs/v/1.2.3", versionedProvider)
            ],
            "/docs/next");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search.css");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal("text/css", request.Response.ContentType);
        Assert.Contains("#0ea5e9", ReadBody(request));
        Assert.DoesNotContain("#fff", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPrefixRequestPathBase_WhenStableDocsMountRewritesHtmlAndSearchIndex()
    {
        var tree = CreatePublishedTree("stable-path-base");
        File.WriteAllText(
            Path.Combine(tree, "index.html"),
            """
            <!DOCTYPE html>
            <html>
            <head>
              <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","docsVersionsUrl":"/docs/versions"};</script>
            </head>
            <body>
              <a href="/docs/preview/search?tab=preview#input">Preview</a>
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """);
        var handler = CreateHandler(tree, "/docs", previewRootPath: "/docs/preview");
        var htmlRequest = CreateContext(HttpMethods.Get, "/docs", pathBase: "/some-base");
        var searchIndexRequest = CreateContext(HttpMethods.Get, "/docs/search-index.json", pathBase: "/some-base");

        Assert.True(await handler.TryHandleAsync(htmlRequest));
        var html = ReadBody(htmlRequest);
        Assert.Contains("href=\"/some-base/docs/preview/search?tab=preview#input\"", html);
        Assert.Contains("href=\"/some-base/docs/guide.html\"", html);
        Assert.Contains("\"docsRootPath\":\"/some-base/docs\"", html);
        Assert.Contains("\"docsSearchUrl\":\"/some-base/docs/search\"", html);
        Assert.Contains("\"docsSearchIndexUrl\":\"/some-base/docs/search-index.json\"", html);
        Assert.DoesNotContain("docsVersionsUrl", html);

        Assert.True(await handler.TryHandleAsync(searchIndexRequest));
        Assert.Contains("\"path\":\"/some-base/docs/guide.html\"", ReadBody(searchIndexRequest));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRewriteOnlyCanonicalHrefToExactRoot_ForDefaultRecommendedAlias()
    {
        var tree = CreatePublishedTree("default-recommended-canonical");
        WriteCanonicalPage(tree, "/docs/guide.html", rel: "alternate\tCANONICAL");
        var handler = CreateHandler(tree, "/docs", canonicalRootPath: "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs");

        Assert.True(await handler.TryHandleAsync(request));
        var html = ReadBody(request);
        Assert.Contains("<link rel=\"alternate\tCANONICAL\" href=\"/docs/v/1.2.3/guide.html\">", html);
        Assert.Contains("href=\"/docs/guide.html\"", html);
        Assert.Contains("src=\"/docs/search-client.js\"", html);
        Assert.Contains("srcset=\"/docs/img/small.png 1x, /docs/img/large.png 2x\"", html);
        Assert.DoesNotContain("href=\"/docs/v/1.2.3/search.css\"", html);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldSelfCanonicalizeExactMount()
    {
        var tree = CreatePublishedTree("exact-canonical");
        WriteCanonicalPage(tree, "/docs/guide.html");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3");

        Assert.True(await handler.TryHandleAsync(request));

        Assert.Contains("<link rel=\"canonical\" href=\"/docs/v/1.2.3/guide.html\">", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPrefixPathBaseForCanonical_WhenPublicOriginIsUnset()
    {
        var tree = CreatePublishedTree("path-base-recommended-canonical");
        WriteCanonicalPage(tree, "/docs/guide.html");
        var handler = CreateHandler(tree, "/docs", canonicalRootPath: "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs", pathBase: "/some-base");

        Assert.True(await handler.TryHandleAsync(request));
        var html = ReadBody(request);

        Assert.Contains("<link rel=\"canonical\" href=\"/some-base/docs/v/1.2.3/guide.html\">", html);
        Assert.Contains("href=\"/some-base/docs/guide.html\"", html);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldUseRuntimePublicOriginForCanonical_AndSkipPathBase()
    {
        var tree = CreatePublishedTree("public-origin-canonical");
        WriteCanonicalPage(tree, "/docs/guide.html");
        var handler = CreateHandler(
            tree,
            "/docs",
            canonicalRootPath: "/docs/v/1.2.3",
            publicOrigin: "https://docs.example.com");
        var request = CreateContext(HttpMethods.Get, "/docs", pathBase: "/tenant");

        Assert.True(await handler.TryHandleAsync(request));
        var html = ReadBody(request);

        Assert.Contains("<link rel=\"canonical\" href=\"https://docs.example.com/docs/v/1.2.3/guide.html\">", html);
        Assert.Contains("href=\"/tenant/docs/guide.html\"", html);
        Assert.DoesNotContain("https://docs.example.com/docs/guide.html", html);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPreserveExportedAbsoluteCanonicalOrigin_WhenPublicOriginIsUnset()
    {
        var tree = CreatePublishedTree("absolute-exported-canonical");
        WriteCanonicalPage(tree, "https://export.example/docs/guide.html?view=full#intro");
        var handler = CreateHandler(tree, "/docs", canonicalRootPath: "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs");

        Assert.True(await handler.TryHandleAsync(request));

        Assert.Contains(
            "<link rel=\"canonical\" href=\"https://export.example/docs/v/1.2.3/guide.html?view=full#intro\">",
            ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldReplaceExportedAbsoluteCanonicalOrigin_WhenPublicOriginIsSet()
    {
        var tree = CreatePublishedTree("absolute-public-origin-canonical");
        WriteCanonicalPage(tree, "https://export.example/docs/guide.html?view=full#intro");
        var handler = CreateHandler(
            tree,
            "/docs",
            canonicalRootPath: "/docs/v/1.2.3",
            publicOrigin: "https://docs.example.com");
        var request = CreateContext(HttpMethods.Get, "/docs");

        Assert.True(await handler.TryHandleAsync(request));
        var html = ReadBody(request);

        Assert.Contains(
            "<link rel=\"canonical\" href=\"https://docs.example.com/docs/v/1.2.3/guide.html?view=full#intro\">",
            html);
        Assert.DoesNotContain("https://export.example", html);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRewriteRecommendedCanonicalForCustomRouteRoot()
    {
        var tree = CreatePublishedTree("custom-root-recommended-canonical");
        WriteCanonicalPage(tree, "/docs/guide.html");
        var handler = CreateHandler(
            tree,
            "/foo/bar",
            previewRootPath: "/foo/bar/next",
            routeRootPath: "/foo/bar",
            canonicalRootPath: "/foo/bar/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/foo/bar");

        Assert.True(await handler.TryHandleAsync(request));
        var html = ReadBody(request);

        Assert.Contains("<link rel=\"canonical\" href=\"/foo/bar/v/1.2.3/guide.html\">", html);
        Assert.Contains("href=\"/foo/bar/guide.html\"", html);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRewriteRecommendedCanonicalForRootMount_AndLetExactMountWin()
    {
        var aliasTree = CreatePublishedTree("root-alias-canonical");
        var exactTree = CreatePublishedTree("root-exact-canonical");
        WriteCanonicalPage(aliasTree, "/docs/guide.html");
        File.WriteAllText(Path.Join(aliasTree, "search.css"), "body { color: #111; }");
        File.WriteAllText(Path.Join(exactTree, "search.css"), "body { color: #0ea5e9; }");
        using var aliasProvider = new PhysicalFileProvider(aliasTree);
        using var exactProvider = new PhysicalFileProvider(exactTree);
        var handler = CreateHandler(
            [
                new AppSurfaceDocsPublishedTreeMount("/", aliasProvider, canonicalRootPath: "/v/1.2.3"),
                new AppSurfaceDocsPublishedTreeMount("/v/1.2.3", exactProvider)
            ],
            previewRootPath: "/next",
            routeRootPath: "/");
        var aliasRequest = CreateContext(HttpMethods.Get, "/");
        var exactAssetRequest = CreateContext(HttpMethods.Get, "/v/1.2.3/search.css");

        Assert.True(await handler.TryHandleAsync(aliasRequest));
        var html = ReadBody(aliasRequest);
        Assert.Contains("<link rel=\"canonical\" href=\"/v/1.2.3/guide.html\">", html);
        Assert.Contains("href=\"/guide.html\"", html);
        Assert.Contains("\"docsRootPath\":\"/\"", html);
        Assert.Contains("\"docsSearchUrl\":\"/search\"", html);
        Assert.Contains("\"docsSearchIndexUrl\":\"/search-index.json\"", html);
        Assert.Contains("\"miniSearchUrl\":\"/minisearch.min.js?v=1\"", html);
        Assert.DoesNotContain("//search", html);

        Assert.True(await handler.TryHandleAsync(exactAssetRequest));
        Assert.Contains("#0ea5e9", ReadBody(exactAssetRequest));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPreserveAppRelativeCanonicalSuffixes()
    {
        var tree = CreatePublishedTree("canonical-with-suffix");
        WriteCanonicalPage(tree, "/docs/guide.html?view=full#intro");
        var handler = CreateHandler(tree, "/docs", canonicalRootPath: "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs");

        Assert.True(await handler.TryHandleAsync(request));

        Assert.Contains(
            "<link rel=\"canonical\" href=\"/docs/v/1.2.3/guide.html?view=full#intro\">",
            ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPreserveCanonicalHref_WhenValueCannotBeMappedToDocs()
    {
        var tree = CreatePublishedTree("unsupported-canonical-hrefs");
        File.WriteAllText(
            Path.Join(tree, "index.html"),
            """
            <!DOCTYPE html>
            <html>
            <head>
              <link rel="canonical" href="/external/page.html?view=full#intro">
              <link rel="canonical" href="https://export.example/external/page.html?view=full#intro">
              <link rel="canonical" href="guide.html">
            </head>
            <body>
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """);
        var handler = CreateHandler(tree, "/docs", canonicalRootPath: "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs");

        Assert.True(await handler.TryHandleAsync(request));
        var html = ReadBody(request);

        Assert.Contains("<link rel=\"canonical\" href=\"/external/page.html?view=full#intro\">", html);
        Assert.Contains("<link rel=\"canonical\" href=\"https://export.example/external/page.html?view=full#intro\">", html);
        Assert.Contains("<link rel=\"canonical\" href=\"guide.html\">", html);
        Assert.Contains("href=\"/docs/guide.html\"", html);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldTreatNonCanonicalRelLinksAsNormalMountedAssets()
    {
        var tree = CreatePublishedTree("non-canonical-rel-link");
        File.WriteAllText(
            Path.Join(tree, "index.html"),
            """
            <!DOCTYPE html>
            <html>
            <head>
              <link rel="alternate" href="/docs/search.css">
              <link href="/docs/no-rel.css">
            </head>
            <body>
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """);
        var handler = CreateHandler(tree, "/docs", canonicalRootPath: "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs", pathBase: "/tenant");

        Assert.True(await handler.TryHandleAsync(request));
        var html = ReadBody(request);

        Assert.Contains("<link rel=\"alternate\" href=\"/tenant/docs/search.css\">", html);
        Assert.Contains("<link href=\"/tenant/docs/no-rel.css\">", html);
        Assert.DoesNotContain("/docs/v/1.2.3/search.css", html);
    }

    [Fact]
    public void PublishedTreeMount_ShouldThrow_WhenFileProviderIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsPublishedTreeMount("/docs", null!));

        Assert.Equal("fileProvider", exception.ParamName);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldTrimTrailingSlashFromRequestPathBase_WhenRewritingMountedHtmlAndSearchIndex()
    {
        var tree = CreatePublishedTree("path-base-with-trailing-slash");
        File.WriteAllText(
            Path.Combine(tree, "index.html"),
            """
            <!DOCTYPE html>
            <html>
            <head>
              <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json"};</script>
            </head>
            <body>
              <a href="/docs/preview/search?tab=preview#input">Preview</a>
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """);
        var handler = CreateHandler(tree, "/docs/v/1.2.3", previewRootPath: "/docs/preview");
        var htmlRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3", pathBase: "/some-base/");
        var searchIndexRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search-index.json", pathBase: "/some-base/");

        Assert.True(await handler.TryHandleAsync(htmlRequest));
        var html = ReadBody(htmlRequest);
        Assert.Contains("href=\"/some-base/docs/preview/search?tab=preview#input\"", html);
        Assert.Contains("href=\"/some-base/docs/v/1.2.3/guide.html\"", html);
        Assert.Contains("\"docsSearchIndexUrl\":\"/some-base/docs/v/1.2.3/search-index.json\"", html);

        Assert.True(await handler.TryHandleAsync(searchIndexRequest));
        Assert.Contains("\"path\":\"/some-base/docs/v/1.2.3/guide.html\"", ReadBody(searchIndexRequest));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPreferLongestMatchingMount_WhenPublishedRootsOverlap()
    {
        var stableTree = CreatePublishedTree("stable-overlap");
        var versionedTree = CreatePublishedTree("versioned-overlap");
        File.WriteAllText(Path.Combine(stableTree, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Combine(versionedTree, "search.css"), "body { color: #0ea5e9; }");
        using var stableProvider = new PhysicalFileProvider(stableTree);
        using var versionedProvider = new PhysicalFileProvider(versionedTree);

        var handler = CreateHandler(
            [
                new AppSurfaceDocsPublishedTreeMount("/docs", stableProvider),
                new AppSurfaceDocsPublishedTreeMount("/docs/v/1.2.3", versionedProvider)
            ]);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search.css");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal("text/css", request.Response.ContentType);
        Assert.Contains("#0ea5e9", ReadBody(request));
        Assert.DoesNotContain("#fff", ReadBody(request));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private AppSurfaceDocsPublishedTreeHandler CreateHandler(
        string treePath,
        string mountRootPath,
        string previewRootPath = "/docs/next",
        string routeRootPath = DocsUrlBuilder.DocsEntryPath,
        string? canonicalRootPath = null,
        string? publicOrigin = null)
    {
        var provider = new PhysicalFileProvider(treePath, ExclusionFilters.None);
        _disposables.Add(provider);

        return CreateHandler(
            [
                new AppSurfaceDocsPublishedTreeMount(
                    mountRootPath,
                    provider,
                    new AppSurfaceDocsFrozenRouteManifestCache(provider, treePath),
                    canonicalRootPath)
            ],
            previewRootPath,
            routeRootPath,
            publicOrigin);
    }

    private AppSurfaceDocsPublishedTreeHandler CreateHandler(
        IReadOnlyList<AppSurfaceDocsPublishedTreeMount> mounts,
        string previewRootPath = "/docs/next",
        string routeRootPath = DocsUrlBuilder.DocsEntryPath,
        string? publicOrigin = null)
    {
        return new AppSurfaceDocsPublishedTreeHandler(mounts, previewRootPath, routeRootPath, publicOrigin);
    }

    private string CreatePublishedTree(string name)
    {
        var root = Path.Combine(_tempDirectory, name);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "guide"));
        Directory.CreateDirectory(Path.Combine(root, "folder-only"));

        File.WriteAllText(
            Path.Combine(root, "index.html"),
            """
            <!DOCTYPE html>
            <html>
            <head>
              <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","docsVersionsUrl":"/docs/versions"};</script>
            </head>
            <body>
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """);
        File.WriteAllText(Path.Combine(root, "guide", "index.html"), "<!DOCTYPE html><html><body>guide-index</body></html>");
        File.WriteAllText(Path.Combine(root, "folder-only", "index.html"), "<!DOCTYPE html><html><body>folder-only</body></html>");
        File.WriteAllText(Path.Join(root, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Join(root, "search-index.json"), "{\"documents\":[{\"path\":\"/docs/guide.html\",\"title\":\"Guide\"}]}");
        File.WriteAllText(Path.Join(root, "outline-client.js"), "window.__outlineClientLoaded = true;");

        return root;
    }

    private static void WriteCanonicalPage(string treePath, string canonicalHref, string rel = "canonical")
    {
        File.WriteAllText(
            Path.Join(treePath, "index.html"),
            $$"""
            <!DOCTYPE html>
            <html>
            <head>
              <link rel="{{rel}}" href="{{canonicalHref}}">
              <link rel="stylesheet" href="/docs/search.css">
              <script src="/docs/search-client.js"></script>
              <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","miniSearchUrl":"/docs/minisearch.min.js?v=1","docsVersionsUrl":"/docs/versions"};</script>
            </head>
            <body>
              <a href="/docs/guide.html">Guide</a>
              <img src="/docs/img/logo.png" srcset="/docs/img/small.png 1x, /docs/img/large.png 2x">
            </body>
            </html>
            """);
    }

    private static void WriteFrozenManifest(string treePath, string json)
    {
        File.WriteAllText(Path.Join(treePath, ".appsurface-docs-route-manifest.json"), json);
    }

    private static DefaultHttpContext CreateContext(string method, string requestPath, string? pathBase = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            context.Request.PathBase = new PathString(pathBase);
        }

        context.Request.Path = requestPath;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
