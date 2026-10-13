using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Unit")]
public sealed class PublishedArtifactRouteTests : IDisposable
{
    private const string CanonicalPage =
        "<!DOCTYPE html><html><body><p>canonical artifact body</p><a href=\"/docs/inside.html\">Inside</a></body></html>";
    private const string GeneratedAliasPage =
        "<!DOCTYPE html><html><body><p>generated alias fallback</p></body></html>";

    private readonly List<IDisposable> _providers = [];
    private readonly string _tempDirectory;

    public PublishedArtifactRouteTests()
    {
        _tempDirectory = TestPathUtils.PathUnder(
            Path.GetTempPath(),
            "appsurface-docs-published-artifact-route-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Theory]
    [MemberData(nameof(AliasRedirectCases))]
    public async Task TryHandleAsync_ShouldRedirectAliasesToTheSelectedWholeExportArtifact(
        string method,
        string requestPath,
        string routeRootPath,
        string pathBase,
        string expectedLocation)
    {
        var tree = CreateMappedGuideTree("alias-redirect");
        var mounts = string.Equals(routeRootPath, "/docs", StringComparison.Ordinal)
            ? new[]
            {
                CreateVerifiedMount(tree, "/docs"),
                CreateVerifiedMount(tree, "/docs/v/1.0.0")
            }
            : new[] { CreateVerifiedMount(tree, "/handbook/v/1.0.0") };
        var handler = CreateHandler(mounts, routeRootPath);
        var request = CreateContext(method, requestPath, pathBase, "?view=full");

        var handled = await handler.TryHandleAsync(request);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status301MovedPermanently, request.Response.StatusCode);
        Assert.Equal(expectedLocation, request.Response.Headers.Location);
        Assert.Equal(0L, request.Response.ContentLength.GetValueOrDefault());
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task HandleOwnedAliasAsync_ShouldRedirectAndServeTheVerifiedWholeExportArtifact(string method)
    {
        var tree = CreateMappedGuideTree("owned-alias-modern");
        var handler = CreateHandler(
            [CreateVerifiedMount(tree, "/docs/a/stable", canonicalRootPath: "/docs/v/1.0.0", aliasName: "stable")],
            "/docs");
        var selection = new AppSurfaceDocsAliasRequest("/docs/a", "stable", true);
        var redirectRequest = CreateContext(method, "/docs/a/stable/legacy-guide", "/app", "?view=full");
        redirectRequest.Features.Set(selection);

        await handler.HandleOwnedAliasAsync(redirectRequest, selection);

        Assert.Equal(StatusCodes.Status301MovedPermanently, redirectRequest.Response.StatusCode);
        Assert.Equal("/app/docs/a/stable/docs/guide.html?view=full#overview", redirectRequest.Response.Headers.Location);
        Assert.Empty(ReadBody(redirectRequest));

        var followRequest = CreateContext(method, "/docs/a/stable/docs/guide.html", "/app", "?view=full");
        followRequest.Features.Set(selection);
        await handler.HandleOwnedAliasAsync(followRequest, selection);

        Assert.Equal(StatusCodes.Status200OK, followRequest.Response.StatusCode);
        Assert.False(followRequest.Response.Headers.ContainsKey("Location"));
        if (method == "GET")
        {
            Assert.Contains("canonical artifact body", ReadBody(followRequest), StringComparison.Ordinal);
            Assert.Contains("href=\"/app/docs/a/stable/docs/inside.html\"", ReadBody(followRequest), StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(ReadBody(followRequest));
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task TryHandleAsync_ShouldRedirectLogicalCanonicalRouteToItsMappedArtifact(string method)
    {
        var tree = CreateMappedGuideTree("canonical-redirect");
        var handler = CreateHandler(
            [CreateVerifiedMount(tree, "/docs/v/1.0.0")],
            "/docs");
        var request = CreateContext(
            method,
            "/docs/v/1.0.0/guide",
            queryString: "?topic=routes");

        var handled = await handler.TryHandleAsync(request);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status301MovedPermanently, request.Response.StatusCode);
        Assert.Equal(
            "/docs/v/1.0.0/docs/guide.html?topic=routes#overview",
            request.Response.Headers.Location);
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Theory]
    [InlineData("GET", "index.html")]
    [InlineData("HEAD", "index.html")]
    [InlineData("GET", "docs.html")]
    [InlineData("HEAD", "docs.html")]
    public async Task TryHandleAsync_ShouldMapTheLogicalDocsRootToItsExportRootHtmlFile(
        string method,
        string artifactPath)
    {
        var manifest = CreateRouteManifest(
            modern: true,
            new RouteEntryFixture("", artifactPath, [], []));
        var tree = CreateTree(
            "root-" + artifactPath,
            manifest,
            (artifactPath, "<!DOCTYPE html><html><body><p>root artifact</p></body></html>"));
        var handler = CreateHandler(
            [CreateVerifiedMount(tree, "/docs/v/1.0.0")],
            "/docs");
        var request = CreateContext(
            method,
            "/docs/v/1.0.0",
            queryString: "?section=start");

        var handled = await handler.TryHandleAsync(request);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status301MovedPermanently, request.Response.StatusCode);
        Assert.Equal(
            $"/docs/v/1.0.0/{artifactPath}?section=start",
            request.Response.Headers.Location);
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldFollowAnArtifactRedirectToTheVerifiedCanonicalHtml()
    {
        var tree = CreateMappedGuideTree("follow-artifact-redirect");
        var handler = CreateHandler(
            [CreateVerifiedMount(tree, "/handbook/v/1.0.0")],
            "/handbook");
        var redirectRequest = CreateContext(
            HttpMethods.Get,
            "/handbook/v/1.0.0/legacy-guide",
            "/app",
            "?view=full");

        Assert.True(await handler.TryHandleAsync(redirectRequest));
        Assert.Equal(
            "/app/handbook/v/1.0.0/docs/guide.html?view=full#overview",
            redirectRequest.Response.Headers.Location);

        var followRequest = CreateFollowContext(
            redirectRequest.Response.Headers.Location.ToString(),
            "/app");
        Assert.True(await handler.TryHandleAsync(followRequest));

        Assert.Equal(StatusCodes.Status200OK, followRequest.Response.StatusCode);
        Assert.Contains("canonical artifact body", ReadBody(followRequest), StringComparison.Ordinal);
        Assert.Contains(
            "href=\"/app/handbook/v/1.0.0/docs/inside.html\"",
            ReadBody(followRequest),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldServeThePhysicalCanonicalArtifactWithoutRedirectingToItself()
    {
        var tree = CreateMappedGuideTree("physical-canonical-artifact");
        var handler = CreateHandler(
            [CreateVerifiedMount(tree, "/docs/v/1.0.0")],
            "/docs");
        var request = CreateContext(
            HttpMethods.Get,
            "/docs/v/1.0.0/docs/guide.html");

        Assert.True(await handler.TryHandleAsync(request));

        Assert.Equal(StatusCodes.Status200OK, request.Response.StatusCode);
        Assert.False(request.Response.Headers.ContainsKey("Location"));
        Assert.Contains("canonical artifact body", ReadBody(request), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldKeepLegacyV1AliasesPointingAtLogicalRoutes()
    {
        var manifest = CreateRouteManifest(
            modern: false,
            new RouteEntryFixture("guide#legacy-section", null, ["legacy-source"], []));
        var tree = CreateTree(
            "legacy-logical-alias",
            manifest,
            ("guide.html", "<!DOCTYPE html><html><body><p>legacy page</p></body></html>"));
        var handler = CreateHandler(
            [CreateVerifiedMount(tree, "/docs/v/1.0.0")],
            "/docs");
        var request = CreateContext(
            HttpMethods.Get,
            "/docs/v/1.0.0/legacy-source",
            queryString: "?from=legacy");

        Assert.True(await handler.TryHandleAsync(request));

        Assert.Equal(StatusCodes.Status301MovedPermanently, request.Response.StatusCode);
        Assert.Equal(
            "/docs/v/1.0.0/guide?from=legacy#legacy-section",
            request.Response.Headers.Location);
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Fact]
    public void TryVerify_ShouldRejectCanonicalArtifactsThatAreNotCoveredReleaseMembers()
    {
        var tree = CreateMappedGuideTree("uncovered-canonical-artifact");
        var digest = WriteReleaseManifest(tree, ["docs/guide.html"]);

        var verified = AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
            tree,
            digest,
            out var archive,
            out var failure);

        Assert.False(verified);
        Assert.Null(archive);
        Assert.NotNull(failure);
        Assert.Equal("ASDOCSARCHIVE009", failure.Code);
        Assert.Equal("docs/guide.html", failure.Path);
    }

    [Fact]
    public void TryVerify_ShouldRejectMappedHtmlThatIsAReparsePoint()
    {
        var tree = CreateMappedGuideTree("reparse-canonical-artifact", includeAlternate: true);
        var artifactPath = TreePath(tree, "docs/guide.html");
        File.Delete(artifactPath);
        File.CreateSymbolicLink(artifactPath, TreePath(tree, "alternate.html"));
        var digest = WriteReleaseManifest(tree);

        var verified = AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
            tree,
            digest,
            out var archive,
            out var failure);

        Assert.False(verified);
        Assert.Null(archive);
        Assert.NotNull(failure);
        Assert.Equal("ASDOCSARCHIVE005", failure.Code);
        Assert.Equal("docs/guide.html", failure.Path);
    }

    [Theory]
    [InlineData("mutated")]
    [InlineData("deleted")]
    [InlineData("reparse")]
    public async Task TryHandleAsync_ShouldRefuseAnInvalidMappedTargetWithoutServingTheGeneratedAlias(
        string invalidation)
    {
        var tree = CreateMappedGuideTree("invalid-target-" + invalidation, includeAlternate: true);
        var handler = CreateHandler(
            [CreateVerifiedMount(tree, "/docs/v/1.0.0")],
            "/docs");
        var artifactPath = TreePath(tree, "docs/guide.html");
        switch (invalidation)
        {
            case "mutated":
                File.WriteAllText(artifactPath, "<!DOCTYPE html><html><body>changed after verification</body></html>");
                break;
            case "deleted":
                File.Delete(artifactPath);
                break;
            case "reparse":
                File.Delete(artifactPath);
                File.CreateSymbolicLink(artifactPath, TreePath(tree, "alternate.html"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidation), invalidation, "Unknown fixture mutation.");
        }

        var request = CreateContext(
            HttpMethods.Get,
            "/docs/v/1.0.0/legacy-guide");
        var handled = await handler.TryHandleAsync(request);

        Assert.False(handled);
        Assert.False(request.Response.Headers.ContainsKey("Location"));
        Assert.DoesNotContain("generated alias fallback", ReadBody(request), StringComparison.Ordinal);
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldRefuseAProviderFromTheWrongVerifiedTreeWithoutServingAnAliasFile()
    {
        var archiveTree = CreateMappedGuideTree("wrong-tree-verified");
        var wrongTree = CreateMappedGuideTree("wrong-tree-provider");
        var mount = CreateVerifiedMount(
            archiveTree,
            providerRootPath: wrongTree,
            exactTreeRootPath: archiveTree,
            mountRootPath: "/docs/v/1.0.0");
        var handler = CreateHandler([mount], "/docs");
        var request = CreateContext(
            HttpMethods.Get,
            "/docs/v/1.0.0/legacy-guide");

        var handled = await handler.TryHandleAsync(request);

        Assert.False(handled);
        Assert.False(request.Response.Headers.ContainsKey("Location"));
        Assert.Equal(string.Empty, ReadBody(request));
    }

    [Theory]
    [InlineData("/", "/search-index.static.json", "/docs/v/1.0.0", "/docs", "", "/docs/v/1.0.0/search-index.json")]
    [InlineData("/docs", "/docs/search-index.json", "/docs", "/docs", "/app", "/app/docs/search-index.json")]
    [InlineData("/foo/bar/next", "/foo/bar/next/search-index.json", "/handbook/v/1.0.0", "/handbook", "/app", "/app/handbook/v/1.0.0/search-index.json")]
    public void RewriteHtml_ShouldSelectTheFrozenRootIndexInsteadOfTheGenericStaticProjection(
        string sourceRoot,
        string genericIndexUrl,
        string mountRoot,
        string routeRoot,
        string pathBase,
        string expectedIndexUrl)
    {
        var html = $$"""
                     <html><head><script>window.__appSurfaceDocsConfig = {"docsRootPath":"{{sourceRoot}}","docsSearchIndexUrl":"{{genericIndexUrl}}"};</script></head><body></body></html>
                     """;

        var rewritten = AppSurfaceDocsPublishedTreeContentRewriter.RewriteHtml(
            html,
            mountRoot,
            routeRootPath: routeRoot,
            requestPathBase: pathBase,
            htmlUsesExportRootUrls: true);
        var script = new HtmlParser().ParseDocument(rewritten).QuerySelector("script")!.TextContent;

        Assert.Contains($"\"docsSearchIndexUrl\":\"{expectedIndexUrl}\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("search-index.static.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteHtml_ShouldProjectExportRootUrlsAndPreserveOnlySupportedExceptions()
    {
        const string html =
            """
            <!DOCTYPE html>
            <html>
            <head>
              <link id="canonical-physical" rel="canonical" href="/docs/guide.html?mode=physical#top">
              <link id="canonical-live" rel="canonical" href="https://docs.example.test/docs/guide.html?mode=live#top">
              <meta id="refresh-double" http-equiv="refresh" content='5; url="/docs/refresh.html?mode=double#refresh"'>
              <meta id="refresh-single" http-equiv="refresh" content="7; url='/docs/single-refresh.html#single'">
              <meta id="refresh-malformed" http-equiv="refresh" content='9; url="/docs/broken-refresh.html'>
              <meta id="refresh-without-separator" http-equiv="refresh" content="refresh later">
              <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json","miniSearchUrl":"/docs/minisearch.min.js?v=1","docsVersionsUrl":"/docs/versions"};</script>
            </head>
            <body>
              <a id="internal" href="/docs/guide.html?from=link#section">Guide</a>
              <a id="ignored-authoring" data-rw-export-ignore href="/docs/authoring-preview">Preview</a>
              <a id="external" href="https://outside.example.test/docs/guide.html">External</a>
              <img id="protocol-relative" src="//cdn.example.test/docs/logo.svg">
              <a id="relative" href="guide.html">Relative</a>
              <img id="assets" src="/_content/ForgeTrust.AppSurface.Docs/images/logo.svg"
                   srcset="/docs/small.png 1x, /_content/ForgeTrust.AppSurface.Docs/images/large.png 2x">
            </body>
            </html>
            """;

        var rewritten = AppSurfaceDocsPublishedTreeContentRewriter.RewriteHtml(
            html,
            "/docs/v/1.0.0",
            requestPathBase: "/app",
            canonicalRootPath: "/docs/v/1.0.0",
            publicOrigin: "https://public.example.test",
            htmlUsesExportRootUrls: true);
        var document = new HtmlParser().ParseDocument(rewritten);

        Assert.Equal(
            "https://public.example.test/docs/v/1.0.0/docs/guide.html?mode=physical#top",
            document.QuerySelector("#canonical-physical")!.GetAttribute("href"));
        Assert.Equal(
            "https://public.example.test/docs/v/1.0.0/guide.html?mode=live#top",
            document.QuerySelector("#canonical-live")!.GetAttribute("href"));
        Assert.Equal(
            "/app/docs/v/1.0.0/docs/guide.html?from=link#section",
            document.QuerySelector("#internal")!.GetAttribute("href"));
        Assert.Equal(
            "/docs/authoring-preview",
            document.QuerySelector("#ignored-authoring")!.GetAttribute("href"));
        Assert.Equal(
            "https://outside.example.test/docs/guide.html",
            document.QuerySelector("#external")!.GetAttribute("href"));
        Assert.Equal(
            "//cdn.example.test/docs/logo.svg",
            document.QuerySelector("#protocol-relative")!.GetAttribute("src"));
        Assert.Equal("guide.html", document.QuerySelector("#relative")!.GetAttribute("href"));
        Assert.Equal(
            "/app/docs/v/1.0.0/_content/ForgeTrust.AppSurface.Docs/images/logo.svg",
            document.QuerySelector("#assets")!.GetAttribute("src"));
        Assert.Equal(
            "/app/docs/v/1.0.0/docs/small.png 1x, /app/docs/v/1.0.0/_content/ForgeTrust.AppSurface.Docs/images/large.png 2x",
            document.QuerySelector("#assets")!.GetAttribute("srcset"));
        Assert.Equal(
            "5;url=\"/app/docs/v/1.0.0/docs/refresh.html?mode=double#refresh\"",
            document.QuerySelector("#refresh-double")!.GetAttribute("content"));
        Assert.Equal(
            "7;url='/app/docs/v/1.0.0/docs/single-refresh.html#single'",
            document.QuerySelector("#refresh-single")!.GetAttribute("content"));
        var malformedRefresh = document.QuerySelector("#refresh-malformed")!.GetAttribute("content");
        Assert.Contains("/docs/broken-refresh.html", malformedRefresh, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "/app/docs/v/1.0.0/docs/broken-refresh.html",
            malformedRefresh,
            StringComparison.Ordinal);
        Assert.Equal(
            "refresh later",
            document.QuerySelector("#refresh-without-separator")!.GetAttribute("content"));

        var configScript = document.QuerySelector("script")!.TextContent;
        Assert.Contains("\"docsRootPath\":\"/app/docs/v/1.0.0\"", configScript, StringComparison.Ordinal);
        Assert.Contains("\"docsSearchUrl\":\"/app/docs/v/1.0.0/search\"", configScript, StringComparison.Ordinal);
        Assert.Contains("\"docsSearchIndexUrl\":\"/app/docs/v/1.0.0/search-index.json\"", configScript, StringComparison.Ordinal);
        Assert.DoesNotContain("docsVersionsUrl", configScript, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string, string, string> AliasRedirectCases
    {
        get
        {
            return new TheoryData<string, string, string, string, string>
            {
                {
                    "GET",
                    "/docs/legacy-guide",
                    "/docs",
                    "",
                    "/docs/docs/guide.html?view=full#overview"
                },
                {
                    "HEAD",
                    "/docs/legacy-guide",
                    "/docs",
                    "",
                    "/docs/docs/guide.html?view=full#overview"
                },
                {
                    "GET",
                    "/docs/v/1.0.0/legacy-guide",
                    "/docs",
                    "",
                    "/docs/v/1.0.0/docs/guide.html?view=full#overview"
                },
                {
                    "HEAD",
                    "/docs/v/1.0.0/legacy-guide",
                    "/docs",
                    "",
                    "/docs/v/1.0.0/docs/guide.html?view=full#overview"
                },
                {
                    "GET",
                    "/handbook/v/1.0.0/legacy-guide",
                    "/handbook",
                    "",
                    "/handbook/v/1.0.0/docs/guide.html?view=full#overview"
                },
                {
                    "HEAD",
                    "/handbook/v/1.0.0/legacy-guide",
                    "/handbook",
                    "",
                    "/handbook/v/1.0.0/docs/guide.html?view=full#overview"
                },
                {
                    "GET",
                    "/handbook/v/1.0.0/legacy-guide",
                    "/handbook",
                    "/app",
                    "/app/handbook/v/1.0.0/docs/guide.html?view=full#overview"
                },
                {
                    "HEAD",
                    "/handbook/v/1.0.0/legacy-guide",
                    "/handbook",
                    "/app",
                    "/app/handbook/v/1.0.0/docs/guide.html?view=full#overview"
                }
            };
        }
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private string CreateMappedGuideTree(string name, bool includeAlternate = false)
    {
        var manifest = CreateRouteManifest(
            modern: true,
            new RouteEntryFixture(
                "guide#overview",
                "docs/guide.html",
                ["legacy-guide"],
                ["declared-guide"]));
        var files = new List<(string RelativePath, string Content)>
        {
            ("docs/guide.html", CanonicalPage),
            ("legacy-guide.html", GeneratedAliasPage),
            ("declared-guide.html", GeneratedAliasPage)
        };
        if (includeAlternate)
        {
            files.Add(("alternate.html", "<!DOCTYPE html><html><body><p>alternate target</p></body></html>"));
        }

        return CreateTree(name, manifest, files.ToArray());
    }

    private string CreateTree(
        string name,
        string routeManifestJson,
        params (string RelativePath, string Content)[] files)
    {
        var tree = TestPathUtils.PathUnder(_tempDirectory, name);
        Directory.CreateDirectory(tree);
        foreach (var (relativePath, content) in files)
        {
            WriteText(tree, relativePath, content);
        }

        WriteText(tree, AppSurfaceDocsFrozenRouteManifest.FileName, routeManifestJson);
        return tree;
    }

    private AppSurfaceDocsPublishedTreeMount CreateVerifiedMount(
        string archiveRootPath,
        string mountRootPath,
        string? providerRootPath = null,
        string? exactTreeRootPath = null,
        string? canonicalRootPath = null,
        string? aliasName = null)
    {
        var releaseManifestSha256 = WriteReleaseManifest(archiveRootPath);
        Assert.True(
            AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
                archiveRootPath,
                releaseManifestSha256,
                out var archive,
                out var failure),
            failure?.Detail);

        var resolvedProviderRootPath = providerRootPath ?? archiveRootPath;
        var provider = new PhysicalFileProvider(resolvedProviderRootPath, ExclusionFilters.None);
        _providers.Add(provider);
        return new AppSurfaceDocsPublishedTreeMount(
            mountRootPath,
            provider,
            exactTreeRootPath ?? archiveRootPath,
            new AppSurfaceDocsFrozenRouteManifestCache(archive!.FrozenRouteManifest, archiveRootPath),
            AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified,
            archive,
            canonicalRootPath,
            aliasName);
    }

    private static AppSurfaceDocsPublishedTreeHandler CreateHandler(
        IEnumerable<AppSurfaceDocsPublishedTreeMount> mounts,
        string routeRootPath)
    {
        return new AppSurfaceDocsPublishedTreeHandler(
            mounts,
            routeRootPath + "/preview",
            routeRootPath);
    }

    private static string CreateRouteManifest(bool modern, params RouteEntryFixture[] entries)
    {
        var jsonEntries = entries.Select(
            entry => new
            {
                sourcePath = entry.SourcePath,
                canonicalRoutePath = entry.CanonicalRoutePath,
                recoveryAliases = entry.RecoveryAliases,
                declaredAliases = entry.DeclaredAliases,
                canonicalArtifactPath = entry.CanonicalArtifactPath
            }).ToArray();
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };
        object document = modern
            ? new
            {
                schema = "appsurface-docs-route-manifest-v1",
                htmlUrlBase = "export-root",
                entries = jsonEntries
            }
            : new
            {
                schema = "appsurface-docs-route-manifest-v1",
                entries = jsonEntries
            };
        return JsonSerializer.Serialize(document, options);
    }

    private static string WriteReleaseManifest(string treeRootPath, IReadOnlyCollection<string>? omittedPaths = null)
    {
        var omitted = omittedPaths is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : omittedPaths.ToHashSet(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(treeRootPath, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                AppSurfaceDocsReleaseArchiveVerifier.FileName,
                StringComparison.Ordinal))
            .Select(
                path =>
                {
                    var relativePath = Path.GetRelativePath(treeRootPath, path)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');
                    return new
                    {
                        path = relativePath,
                        length = new FileInfo(path).Length,
                        contentType = (string?)null,
                        hashAlgorithm = "sha256",
                        sha256 = ComputeFileSha256(path)
                    };
                })
            .Where(entry => !omitted.Contains(entry.path))
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        var manifestPath = TestPathUtils.PathUnder(treeRootPath, AppSurfaceDocsReleaseArchiveVerifier.FileName);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new
                {
                    schema = AppSurfaceDocsReleaseArchiveVerifier.Schema,
                    files
                },
                new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return ComputeFileSha256(manifestPath);
    }

    private static string TreePath(string treeRootPath, string relativePath)
    {
        return TestPathUtils.PathUnder(treeRootPath, relativePath.Split('/'));
    }

    private static void WriteText(string treeRootPath, string relativePath, string content)
    {
        var path = TreePath(treeRootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static DefaultHttpContext CreateContext(
        string method,
        string requestPath,
        string? pathBase = null,
        string? queryString = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            context.Request.PathBase = new PathString(pathBase);
        }

        context.Request.Path = new PathString(requestPath);
        if (!string.IsNullOrWhiteSpace(queryString))
        {
            context.Request.QueryString = new QueryString(queryString);
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext CreateFollowContext(string location, string pathBase)
    {
        var uri = new Uri(new Uri("https://docs.example.test"), location);
        var normalizedPathBase = pathBase.TrimEnd('/');
        var requestPath = uri.AbsolutePath.StartsWith(normalizedPathBase + "/", StringComparison.Ordinal)
            ? uri.AbsolutePath[normalizedPathBase.Length..]
            : uri.AbsolutePath;
        return CreateContext(HttpMethods.Get, requestPath, pathBase, uri.Query);
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static string ComputeFileSha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private sealed record RouteEntryFixture(
        string CanonicalRoutePath,
        string? CanonicalArtifactPath,
        string[] RecoveryAliases,
        string[] DeclaredAliases,
        string SourcePath = "guide.md");
}
