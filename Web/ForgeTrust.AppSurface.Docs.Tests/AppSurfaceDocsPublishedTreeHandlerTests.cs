using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

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
        var handler = CreateLegacyHandler(tree, "/docs/v/1.2.3");
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
        File.Delete(Path.Join(tree, "index.html"));
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
        File.WriteAllText(Path.Join(tree, "System.Text.html"), "<!DOCTYPE html><html><body>dotted-slug</body></html>");
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
        File.WriteAllText(Path.Join(tree, "guide.html"), "<!DOCTYPE html><html><body>guide page</body></html>");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/guide.html");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal("text/html", request.Response.ContentType);
        Assert.Equal("nosniff", request.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("no-referrer", request.Response.Headers["Referrer-Policy"]);
        var csp = request.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("sandbox allow-same-origin", csp);
        Assert.Contains("script-src 'none'", csp);
        Assert.Contains("guide page", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDenySvg_ForLegacyUnverifiedArchives()
    {
        var tree = CreatePublishedTree("legacy-svg");
        File.WriteAllText(TestPathUtils.PathUnder(tree, "logo.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
        var handler = CreateLegacyHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/logo.svg");

        var handled = await handler.TryHandleAsync(request);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDenyHtml_ForLegacyUnverifiedArchives()
    {
        var tree = CreatePublishedTree("legacy-html");
        var handler = CreateLegacyHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3");

        var handled = await handler.TryHandleAsync(request);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldServeSvg_ForVerifiedArchives()
    {
        var tree = CreatePublishedTree("verified-svg");
        File.WriteAllText(
            TestPathUtils.PathUnder(tree, "logo.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><title>Logo</title></svg>");
        var handler = CreateVerifiedHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/logo.svg");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal("image/svg+xml", request.Response.ContentType);
        Assert.Equal("nosniff", request.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("no-referrer", request.Response.Headers["Referrer-Policy"]);
        Assert.Contains("script-src 'none'", request.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Contains("<title>Logo</title>", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDenySvg_WhenVerifiedArchiveChangesAfterStartup()
    {
        var tree = CreatePublishedTree("tampered-svg");
        var svgPath = TestPathUtils.PathUnder(tree, "logo.svg");
        File.WriteAllText(svgPath, "<svg xmlns=\"http://www.w3.org/2000/svg\"><title>Original</title></svg>");
        var handler = CreateVerifiedHandler(tree, "/docs/v/1.2.3");
        File.WriteAllText(svgPath, "<svg xmlns=\"http://www.w3.org/2000/svg\"><title>Tampered</title></svg>");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/logo.svg");

        var handled = await handler.TryHandleAsync(request);

        Assert.False(handled);
    }

    [Theory]
    [InlineData("index.html", "/docs/v/1.2.3")]
    [InlineData("search.css", "/docs/v/1.2.3/search.css")]
    [InlineData("search-index.json", "/docs/v/1.2.3/search-index.json")]
    [InlineData("outline-client.js", "/docs/v/1.2.3/outline-client.js")]
    public async Task TryHandleAsync_ShouldDenyActiveFiles_WhenVerifiedArchiveChangesAfterStartup(
        string relativePath,
        string requestPath)
    {
        var tree = CreatePublishedTree($"tampered-active-{relativePath.Replace('.', '-')}");
        var filePath = TestPathUtils.PathUnder(tree, relativePath);
        var handler = CreateVerifiedHandler(tree, "/docs/v/1.2.3");
        File.WriteAllText(filePath, "tampered");
        var request = CreateContext(HttpMethods.Get, requestPath);

        var handled = await handler.TryHandleAsync(request);

        if (relativePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(relativePath, "search-index.json", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(handled);
            Assert.Equal(StatusCodes.Status404NotFound, request.Response.StatusCode);
            Assert.Equal(0, request.Response.ContentLength);
        }
        else
        {
            Assert.False(handled);
        }
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDenyVerifiedArchiveFilesMissingFromManifest()
    {
        var tree = CreatePublishedTree("verified-unlisted-file");
        var provider = new PhysicalFileProvider(tree, ExclusionFilters.None);
        _disposables.Add(provider);
        var archive = new AppSurfaceDocsVerifiedReleaseArchive(
            new Dictionary<string, AppSurfaceDocsReleaseArchiveFile>(StringComparer.OrdinalIgnoreCase),
            AppSurfaceDocsFrozenRouteManifest.Empty);
        var handler = CreateHandler(
            [
                new AppSurfaceDocsPublishedTreeMount(
                    "/docs/v/1.2.3",
                    provider,
                    tree,
                    new AppSurfaceDocsFrozenRouteManifestCache(AppSurfaceDocsFrozenRouteManifest.Empty, tree),
                    AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified,
                    archive)
            ]);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search.css");

        var handled = await handler.TryHandleAsync(request);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDenyVerifiedArchiveCaseMismatchedFiles()
    {
        var tree = CreatePublishedTree("verified-case-mismatched-file");
        var caseVariantPath = TestPathUtils.PathUnder(tree, "INDEX.HTML");
        File.WriteAllText(caseVariantPath, "<!DOCTYPE html><html><body>case variant</body></html>");
        var provider = new PhysicalFileProvider(tree, ExclusionFilters.None);
        _disposables.Add(provider);
        var archive = new AppSurfaceDocsVerifiedReleaseArchive(
            new Dictionary<string, AppSurfaceDocsReleaseArchiveFile>(StringComparer.OrdinalIgnoreCase)
            {
                ["index.html"] = new AppSurfaceDocsReleaseArchiveFile(
                    "index.html",
                    new FileInfo(caseVariantPath).Length,
                    "text/html",
                    ComputeFileSha256(caseVariantPath))
            },
            AppSurfaceDocsFrozenRouteManifest.Empty);
        var handler = CreateHandler(
            [
                new AppSurfaceDocsPublishedTreeMount(
                    "/docs/v/1.2.3",
                    provider,
                    tree,
                    new AppSurfaceDocsFrozenRouteManifestCache(AppSurfaceDocsFrozenRouteManifest.Empty, tree),
                    AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified,
                    archive)
            ]);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/INDEX.HTML");

        var handled = await handler.TryHandleAsync(request);

        Assert.False(handled);
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
    public async Task TryHandleAsync_ShouldUseVerifiedFrozenManifest_WhenRouteManifestChangesAfterStartup()
    {
        var tree = CreatePublishedTree("verified-route-manifest-tamper");
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
        var handler = CreateVerifiedHandler(tree, "/docs/v/1.2.3");
        WriteFrozenManifest(
            tree,
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "packages/README.md",
                  "canonicalRoutePath": "tampered",
                  "recoveryAliases": ["packages/README.md"],
                  "declaredAliases": []
                }
              ]
            }
            """);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/packages/README.md");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal("/docs/v/1.2.3/packages", request.Response.Headers.Location);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRedirectFrozenManifestAliases_WhenMountDoesNotUseExactTreeGuard()
    {
        var tree = CreatePublishedTree("release-with-unguarded-frozen-manifest");
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
        var provider = new PhysicalFileProvider(tree, ExclusionFilters.None);
        _disposables.Add(provider);
        var handler = CreateHandler(
            [
                new AppSurfaceDocsPublishedTreeMount(
                    "/docs/v/1.2.3",
                    provider,
                    frozenRouteManifest: new AppSurfaceDocsFrozenRouteManifestCache(provider, tree))
            ]);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/packages/README.md");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status301MovedPermanently, request.Response.StatusCode);
        Assert.Equal("/docs/v/1.2.3/packages", request.Response.Headers.Location);
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
        var handler = CreateLegacyHandler(tree, "/docs/v/1.2.3");
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
        var handler = CreateLegacyHandler(tree, "/docs/v/1.2.3");
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
        var handler = CreateLegacyHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/legacy");

        Assert.False(await handler.TryHandleAsync(request));
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
        var handler = CreateLegacyHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/legacy");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDisableFrozenAliasRecovery_WhenManifestIsMalformed()
    {
        var tree = CreatePublishedTree("malformed-frozen-manifest");
        Directory.CreateDirectory(Path.Join(tree, "packages"));
        File.WriteAllText(Path.Join(tree, "packages", "README.md.html"), "<!DOCTYPE html><html><body>legacy artifact</body></html>");
        WriteFrozenManifest(tree, "{");
        var handler = CreateLegacyHandler(tree, "/docs/v/1.2.3");
        var aliasRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/packages/README.md");
        var manifestRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/.appsurface-docs-route-manifest.json");

        Assert.False(await handler.TryHandleAsync(aliasRequest));
        Assert.False(await handler.TryHandleAsync(manifestRequest));

        var fileFallbackRequest = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/packages/README.md.html");
        Assert.False(await handler.TryHandleAsync(fileFallbackRequest));
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
        File.WriteAllText(Path.Join(tree, "asset.weird"), "custom-asset");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/asset.weird");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectExactFilesUnderDotSegments()
    {
        var tree = CreatePublishedTree("dot-segment-asset");
        Directory.CreateDirectory(Path.Join(tree, ".private"));
        File.WriteAllText(Path.Join(tree, ".private", "search.css"), "body { color: red; }");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/.private/search.css");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectHiddenDirectoriesThroughIndexFallback()
    {
        var tree = CreatePublishedTree("dot-segment-index");
        Directory.CreateDirectory(Path.Join(tree, ".private"));
        File.WriteAllText(Path.Join(tree, ".private", "index.html"), "<html>secret</html>");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/.private/");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectNonContractTextArtifacts()
    {
        var tree = CreatePublishedTree("text-artifact");
        File.WriteAllText(Path.Join(tree, "notes.txt"), "notes");
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/notes.txt");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldServeEmbeddedImageAssets()
    {
        var tree = CreatePublishedTree("embedded-image");
        Directory.CreateDirectory(Path.Join(tree, "img"));
        File.WriteAllBytes(Path.Join(tree, "img", "hero.png"), [0x89, 0x50, 0x4E, 0x47]);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/img/hero.png");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal("image/png", request.Response.ContentType);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectSymlinkedEmbeddedAssets()
    {
        Assert.True(
            TryCreateSymbolicLinkTestFile(out var targetPath, out _),
            "symlink support is required to verify embedded asset rejection.");

        var tree = CreatePublishedTree("symlinked-asset");
        var imageDirectory = Path.Join(tree, "img");
        Directory.CreateDirectory(imageDirectory);
        File.WriteAllBytes(targetPath, [0x89, 0x50, 0x4E, 0x47]);
        File.CreateSymbolicLink(Path.Join(imageDirectory, "hero.png"), targetPath);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/img/hero.png");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectAssetsUnderSymlinkedDirectories()
    {
        Assert.True(
            TryCreateSymbolicLinkTestDirectory(out var targetPath, out var linkPath),
            "symlink support is required to verify symlinked directory rejection.");

        var tree = CreatePublishedTree("symlinked-asset-directory");
        var nestedDirectory = Path.Join(targetPath, "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllBytes(Path.Join(nestedDirectory, "hero.png"), [0x89, 0x50, 0x4E, 0x47]);
        Directory.Move(linkPath, Path.Join(tree, "linked-img"));
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/linked-img/nested/hero.png");

        Assert.False(await handler.TryHandleAsync(request));
    }


    [Fact]
    public async Task TryHandleAsync_ShouldRejectSymlinkedFallbackHtml()
    {
        Assert.True(
            TryCreateSymbolicLinkTestFile(out var targetPath, out _),
            "symlink support is required to verify fallback HTML rejection.");

        var tree = CreatePublishedTree("symlinked-fallback");
        File.WriteAllText(targetPath, "<!DOCTYPE html><html><body>external</body></html>");
        File.CreateSymbolicLink(Path.Join(tree, "external.html"), targetPath);
        var handler = CreateHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/external");

        Assert.False(await handler.TryHandleAsync(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldIgnoreSymlinkedFrozenRouteManifest()
    {
        Assert.True(
            TryCreateSymbolicLinkTestFile(out var targetPath, out _),
            "symlink support is required to verify frozen route manifest rejection.");

        var tree = CreatePublishedTree("symlinked-manifest");
        File.WriteAllText(
            targetPath,
            """
            {
              "aliases": [
                { "aliasRoute": "old-guide", "canonicalRoute": "guide" }
              ]
            }
            """);
        File.CreateSymbolicLink(Path.Join(tree, ".appsurface-docs-route-manifest.json"), targetPath);
        var handler = CreateLegacyHandler(tree, "/docs/v/1.2.3");
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/old-guide");

        Assert.False(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status200OK, request.Response.StatusCode);
        Assert.False(request.Response.Headers.ContainsKey("Location"));
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

    [Theory]
    [InlineData("oversized.html", "text/html")]
    [InlineData("search-index.json", "application/json")]
    public async Task TryHandleAsync_ShouldRejectOversizedRewrittenFilesBeforeOpeningStream(string relativeFilePath, string expectedContentTypePrefix)
    {
        var fileInfo = new TestFileInfo(
            relativeFilePath,
            length: 9,
            streamFactory: () => throw new InvalidOperationException("Oversized rewritten files must not be opened."));
        var provider = new TestFileProvider((relativeFilePath, fileInfo));
        var handler = CreateHandler(
            [CreateVerifiedTestMount("/docs/v/1.2.3", provider, CreateArchiveFile(relativeFilePath, 9))],
            maxRewrittenFileSizeBytes: 8);
        var request = CreateContext(HttpMethods.Get, $"/docs/v/1.2.3/{relativeFilePath}");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status404NotFound, request.Response.StatusCode);
        Assert.Equal(0, request.Response.ContentLength);
        Assert.Null(request.Response.ContentType);
        Assert.Equal(0, fileInfo.OpenCount);
        Assert.DoesNotContain(expectedContentTypePrefix, request.Response.Headers.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectUnknownLengthRewrittenFilesBeforeOpeningStream()
    {
        var fileInfo = new TestFileInfo(
            "unknown.html",
            length: -1,
            streamFactory: () => throw new InvalidOperationException("Unknown-length rewritten files must not be opened."));
        var provider = new TestFileProvider(("unknown.html", fileInfo));
        var handler = CreateHandler(
            [CreateVerifiedTestMount("/docs/v/1.2.3", provider, CreateArchiveFile("unknown.html", -1))],
            maxRewrittenFileSizeBytes: 8);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/unknown.html");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status404NotFound, request.Response.StatusCode);
        Assert.Equal(0, fileInfo.OpenCount);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRejectRewrittenFilesThatGrowPastLimitWhileReading()
    {
        var fileInfo = new TestFileInfo(
            "race.html",
            length: 8,
            streamFactory: () => new MemoryStream(Encoding.UTF8.GetBytes("<html>123</html>")));
        var provider = new TestFileProvider(("race.html", fileInfo));
        var handler = CreateHandler(
            [CreateVerifiedTestMount("/docs/v/1.2.3", provider, CreateArchiveFile("race.html", 8))],
            maxRewrittenFileSizeBytes: 8);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/race.html");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status404NotFound, request.Response.StatusCode);
        Assert.Equal(1, fileInfo.OpenCount);
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldAllowRewrittenFilesAtConfiguredLimit()
    {
        var html = "<p>1</p>";
        var fileInfo = new TestFileInfo(
            "at-limit.html",
            length: Encoding.UTF8.GetByteCount(html),
            streamFactory: () => new MemoryStream(Encoding.UTF8.GetBytes(html)));
        var provider = new TestFileProvider(("at-limit.html", fileInfo));
        var handler = CreateHandler(
            [CreateVerifiedTestMount("/docs/v/1.2.3", provider, CreateArchiveFileFromText("at-limit.html", html))],
            maxRewrittenFileSizeBytes: Encoding.UTF8.GetByteCount(html));
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/at-limit.html");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status200OK, request.Response.StatusCode);
        Assert.Equal("text/html", request.Response.ContentType);
        Assert.Contains("<p>1</p>", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldNotApplyRewriteLimitToStaticAssets()
    {
        var fileInfo = new TestFileInfo(
            "search.css",
            length: Encoding.UTF8.GetByteCount("body { color: #fff; }"),
            streamFactory: () => new MemoryStream(Encoding.UTF8.GetBytes("body { color: #fff; }")));
        var provider = new TestFileProvider(("search.css", fileInfo));
        var handler = CreateHandler(
            [CreateVerifiedTestMount("/docs/v/1.2.3", provider, CreateArchiveFileFromText("search.css", "body { color: #fff; }"))],
            maxRewrittenFileSizeBytes: 8);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search.css");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status200OK, request.Response.StatusCode);
        Assert.Equal("text/css", request.Response.ContentType);
        Assert.Contains("body { color: #fff; }", ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDenyStaticAsset_WhenVerifiedBytesChangeBetweenCheckAndServe()
    {
        const string original = "body { color: #fff; }";
        const string tampered = "body { color: #000; }";
        var openCount = 0;
        var fileInfo = new TestFileInfo(
            "search.css",
            length: Encoding.UTF8.GetByteCount(original),
            streamFactory: () =>
            {
                openCount++;
                return new MemoryStream(Encoding.UTF8.GetBytes(openCount == 1 ? original : tampered));
            });
        var provider = new TestFileProvider(("search.css", fileInfo));
        var handler = CreateHandler(
            [CreateVerifiedTestMount("/docs/v/1.2.3", provider, CreateArchiveFileFromText("search.css", original))],
            maxRewrittenFileSizeBytes: 8);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search.css");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status404NotFound, request.Response.StatusCode);
        Assert.Equal(2, fileInfo.OpenCount);
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDenyStaticAsset_WhenVerifiedLengthNoLongerMatches()
    {
        const string original = "body { color: #fff; }";
        TestFileInfo? fileInfo = null;
        fileInfo = new TestFileInfo(
            "search.css",
            length: Encoding.UTF8.GetByteCount(original),
            streamFactory: () => new MemoryStream(Encoding.UTF8.GetBytes(original)),
            lengthFactory: () => fileInfo!.OpenCount == 0
                ? Encoding.UTF8.GetByteCount(original)
                : Encoding.UTF8.GetByteCount(original) + 1);
        var provider = new TestFileProvider(("search.css", fileInfo));
        var handler = CreateHandler(
            [CreateVerifiedTestMount("/docs/v/1.2.3", provider, CreateArchiveFileFromText("search.css", original))],
            maxRewrittenFileSizeBytes: 8);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search.css");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status404NotFound, request.Response.StatusCode);
        Assert.Equal(1, fileInfo.OpenCount);
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDenyStaticAsset_WhenVerifiedReadEndsEarly()
    {
        const string original = "body { color: #fff; }";
        var openCount = 0;
        var fileInfo = new TestFileInfo(
            "search.css",
            length: Encoding.UTF8.GetByteCount(original),
            streamFactory: () =>
            {
                openCount++;
                return new MemoryStream(Encoding.UTF8.GetBytes(openCount == 1 ? original : "body"));
            });
        var provider = new TestFileProvider(("search.css", fileInfo));
        var handler = CreateHandler(
            [CreateVerifiedTestMount("/docs/v/1.2.3", provider, CreateArchiveFileFromText("search.css", original))],
            maxRewrittenFileSizeBytes: 8);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search.css");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status404NotFound, request.Response.StatusCode);
        Assert.Equal(2, fileInfo.OpenCount);
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldDenyStaticAsset_WhenVerifiedReadFails()
    {
        const string original = "body { color: #fff; }";
        var openCount = 0;
        var fileInfo = new TestFileInfo(
            "search.css",
            length: Encoding.UTF8.GetByteCount(original),
            streamFactory: () =>
            {
                openCount++;
                return openCount == 1
                    ? new MemoryStream(Encoding.UTF8.GetBytes(original))
                    : throw new IOException("The active asset disappeared.");
            });
        var provider = new TestFileProvider(("search.css", fileInfo));
        var handler = CreateHandler(
            [CreateVerifiedTestMount("/docs/v/1.2.3", provider, CreateArchiveFileFromText("search.css", original))],
            maxRewrittenFileSizeBytes: 8);
        var request = CreateContext(HttpMethods.Get, "/docs/v/1.2.3/search.css");

        Assert.True(await handler.TryHandleAsync(request));
        Assert.Equal(StatusCodes.Status404NotFound, request.Response.StatusCode);
        Assert.Equal(2, fileInfo.OpenCount);
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldLogOnlyFirstOversizedRewriteWarningPerHandler()
    {
        var firstFile = new TestFileInfo("first.html", length: 9, streamFactory: () => Stream.Null);
        var secondFile = new TestFileInfo("second.html", length: 10, streamFactory: () => Stream.Null);
        var provider = new TestFileProvider(("first.html", firstFile), ("second.html", secondFile));
        var logger = new RecordingLogger<AppSurfaceDocsPublishedTreeHandler>();
        var handler = CreateHandler(
            [
                CreateVerifiedTestMount(
                    "/docs/v/1.2.3",
                    provider,
                    CreateArchiveFile("first.html", 9),
                    CreateArchiveFile("second.html", 10))
            ],
            maxRewrittenFileSizeBytes: 8,
            logger: logger);

        Assert.True(await handler.TryHandleAsync(CreateContext(HttpMethods.Get, "/docs/v/1.2.3/first.html")));
        Assert.True(await handler.TryHandleAsync(CreateContext(HttpMethods.Get, "/docs/v/1.2.3/second.html")));

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes", warning.Message, StringComparison.Ordinal);
        Assert.Contains("404", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Published tree rewrite limit", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33_554_433)]
    public void Ctor_ShouldRejectUnsupportedRewriteLimits(long maxRewrittenFileSizeBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateHandler(
                [new AppSurfaceDocsPublishedTreeMount("/docs/v/1.2.3", new TestFileProvider())],
                maxRewrittenFileSizeBytes: maxRewrittenFileSizeBytes));
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
            Path.Join(tree, "index.html"),
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
            Path.Join(tree, "index.html"),
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
            Path.Join(tree, "index.html"),
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
            Path.Join(tree, "index.html"),
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
        File.WriteAllText(Path.Join(stableTree, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Join(versionedTree, "search.css"), "body { color: #0ea5e9; }");

        var handler = new AppSurfaceDocsPublishedTreeHandler(
            [
                CreateVerifiedMount(stableTree, "/docs"),
                CreateVerifiedMount(versionedTree, "/docs/v/1.2.3")
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
            Path.Join(tree, "index.html"),
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
        var handler = CreateHandler(
            [
                CreateVerifiedMount(aliasTree, "/", canonicalRootPath: "/v/1.2.3"),
                CreateVerifiedMount(exactTree, "/v/1.2.3")
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
            Path.Join(tree, "index.html"),
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
        File.WriteAllText(Path.Join(stableTree, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Join(versionedTree, "search.css"), "body { color: #0ea5e9; }");

        var handler = CreateHandler(
            [
                CreateVerifiedMount(stableTree, "/docs"),
                CreateVerifiedMount(versionedTree, "/docs/v/1.2.3")
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
        var manifestDigest = WriteReleaseManifest(treePath);
        Assert.True(AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
            treePath,
            manifestDigest,
            out var archive,
            out var failure), failure?.Detail);

        var provider = new PhysicalFileProvider(treePath, ExclusionFilters.None);
        _disposables.Add(provider);

        return CreateHandler(
            [
                new AppSurfaceDocsPublishedTreeMount(
                    mountRootPath,
                    provider,
                    treePath,
                    new AppSurfaceDocsFrozenRouteManifestCache(archive!.FrozenRouteManifest, treePath),
                    AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified,
                    archive,
                    canonicalRootPath)
            ],
            previewRootPath,
            routeRootPath,
            publicOrigin);
    }

    private AppSurfaceDocsPublishedTreeHandler CreateLegacyHandler(
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
                    treePath,
                    new AppSurfaceDocsFrozenRouteManifestCache(provider, treePath),
                    canonicalRootPath: canonicalRootPath)
            ],
            previewRootPath,
            routeRootPath,
            publicOrigin);
    }

    private AppSurfaceDocsPublishedTreeHandler CreateVerifiedHandler(string treePath, string mountRootPath)
    {
        return CreateHandler(treePath, mountRootPath);
    }

    private AppSurfaceDocsPublishedTreeMount CreateVerifiedMount(string treePath, string mountRootPath, string? canonicalRootPath = null)
    {
        var manifestDigest = WriteReleaseManifest(treePath);
        Assert.True(AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
            treePath,
            manifestDigest,
            out var archive,
            out var failure), failure?.Detail);

        var provider = new PhysicalFileProvider(treePath, ExclusionFilters.None);
        _disposables.Add(provider);
        return new AppSurfaceDocsPublishedTreeMount(
            mountRootPath,
            provider,
            treePath,
            new AppSurfaceDocsFrozenRouteManifestCache(archive!.FrozenRouteManifest, treePath),
            AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified,
            archive,
            canonicalRootPath);
    }

    private static AppSurfaceDocsPublishedTreeMount CreateVerifiedTestMount(
        string mountRootPath,
        IFileProvider provider,
        params AppSurfaceDocsReleaseArchiveFile[] files)
    {
        var archive = new AppSurfaceDocsVerifiedReleaseArchive(
            files.ToDictionary(file => file.Path, StringComparer.Ordinal),
            AppSurfaceDocsFrozenRouteManifest.Empty);

        return new AppSurfaceDocsPublishedTreeMount(
            mountRootPath,
            provider,
            archiveVerificationState: AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified,
            verifiedReleaseArchive: archive);
    }

    private AppSurfaceDocsPublishedTreeHandler CreateHandler(
        IReadOnlyList<AppSurfaceDocsPublishedTreeMount> mounts,
        string previewRootPath = "/docs/next",
        string routeRootPath = DocsUrlBuilder.DocsEntryPath,
        string? publicOrigin = null,
        long maxRewrittenFileSizeBytes = AppSurfaceDocsVersioningOptions.DefaultMaxRewrittenFileSizeBytes,
        ILogger<AppSurfaceDocsPublishedTreeHandler>? logger = null)
    {
        return new AppSurfaceDocsPublishedTreeHandler(
            mounts,
            previewRootPath,
            routeRootPath,
            publicOrigin,
            maxRewrittenFileSizeBytes,
            logger);
    }

    private string CreatePublishedTree(string name)
    {
        var root = Path.Join(_tempDirectory, name);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Join(root, "guide"));
        Directory.CreateDirectory(Path.Join(root, "folder-only"));

        File.WriteAllText(
            Path.Join(root, "index.html"),
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
        File.WriteAllText(Path.Join(root, "guide", "index.html"), "<!DOCTYPE html><html><body>guide-index</body></html>");
        File.WriteAllText(Path.Join(root, "folder-only", "index.html"), "<!DOCTYPE html><html><body>folder-only</body></html>");
        File.WriteAllText(Path.Join(root, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Join(root, "search-index.json"), "{\"documents\":[{\"path\":\"/docs/guide.html\",\"title\":\"Guide\"}]}");
        File.WriteAllText(Path.Join(root, "outline-client.js"), "window.__outlineClientLoaded = true;");

        return root;
    }

    private bool TryCreateSymbolicLinkTestFile(out string targetPath, out string linkPath)
    {
        targetPath = Path.Join(_tempDirectory, $"symlink-target-{Guid.NewGuid():N}.txt");
        linkPath = Path.Join(_tempDirectory, $"symlink-link-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(targetPath, "target");
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private bool TryCreateSymbolicLinkTestDirectory(out string targetPath, out string linkPath)
    {
        targetPath = Path.Join(_tempDirectory, $"symlink-target-{Guid.NewGuid():N}");
        linkPath = Path.Join(_tempDirectory, $"symlink-link-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(targetPath);
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
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

    private static string WriteReleaseManifest(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), AppSurfaceDocsReleaseArchiveVerifier.FileName, StringComparison.Ordinal))
            .Where(path => ShouldIncludeReleaseManifestFile(root, path))
            .Select(
                path => new
                {
                    path = NormalizeManifestPath(root, path),
                    length = new FileInfo(path).Length,
                    contentType = (string?)null,
                    hashAlgorithm = "sha256",
                    sha256 = ComputeFileSha256(path)
                })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        var manifestPath = TestPathUtils.PathUnder(root, AppSurfaceDocsReleaseArchiveVerifier.FileName);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new { schema = AppSurfaceDocsReleaseArchiveVerifier.Schema, files },
                new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return ComputeFileSha256(manifestPath);
    }

    private static bool ShouldIncludeReleaseManifestFile(string root, string path)
    {
        var relativePath = NormalizeManifestPath(root, path);
        if (AppSurfaceDocsPublishedTreeHandler.IsHandlerServeableFilePath(relativePath, allowSvg: true))
        {
            return true;
        }

        if (!string.Equals(relativePath, AppSurfaceDocsFrozenRouteManifest.FileName, StringComparison.Ordinal))
        {
            return false;
        }

        return AppSurfaceDocsFrozenRouteManifest.TryLoadVerified(
            File.ReadAllBytes(path),
            out _,
            out _);
    }

    private static string NormalizeManifestPath(string root, string path)
    {
        return Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string ComputeFileSha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private static AppSurfaceDocsReleaseArchiveFile CreateArchiveFile(string path, long length)
    {
        return new AppSurfaceDocsReleaseArchiveFile(path, length, null, new string('0', 64));
    }

    private static AppSurfaceDocsReleaseArchiveFile CreateArchiveFileFromText(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new AppSurfaceDocsReleaseArchiveFile(
            path,
            bytes.Length,
            null,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
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

    private sealed class TestFileProvider(params (string Path, TestFileInfo FileInfo)[] files) : IFileProvider
    {
        private readonly Dictionary<string, TestFileInfo> _files = files.ToDictionary(
            file => file.Path,
            file => file.FileInfo,
            StringComparer.OrdinalIgnoreCase);

        public IFileInfo GetFileInfo(string subpath)
        {
            return _files.TryGetValue(subpath.TrimStart('/'), out var fileInfo)
                ? fileInfo
                : new NotFoundFileInfo(subpath);
        }

        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            return NotFoundDirectoryContents.Singleton;
        }

        public IChangeToken Watch(string filter)
        {
            return NullChangeToken.Singleton;
        }
    }

    private sealed class TestFileInfo(
        string name,
        long length,
        Func<Stream> streamFactory,
        Func<long>? lengthFactory = null) : IFileInfo
    {
        public bool Exists => true;

        public long Length => lengthFactory?.Invoke() ?? length;

        public string? PhysicalPath => null;

        public string Name => name;

        public DateTimeOffset LastModified { get; } = DateTimeOffset.UtcNow;

        public bool IsDirectory => false;

        public int OpenCount { get; private set; }

        public Stream CreateReadStream()
        {
            OpenCount++;
            return streamFactory();
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
