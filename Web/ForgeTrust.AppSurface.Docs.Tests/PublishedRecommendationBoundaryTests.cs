using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Unit")]
public sealed class PublishedRecommendationBoundaryTests
{
    private const string Version = "1.2.3";
    private const string RecommendationContractDocsLink = "https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs/README.md#published-version-catalog";

    public static TheoryData<string, string, string, string> ServingMountCases => new()
    {
        { "/docs", "/docs", "", "/docs/nonsearch" },
        { "/docs", "/docs", "/tenant", "/tenant/docs/nonsearch" },
        { "/docs", "/docs/v/1.2.3", "", "/docs/v/1.2.3/nonsearch" },
        { "/manual", "/manual", "/base", "/base/manual/nonsearch" },
        { "/manual", "/manual/v/1.2.3", "/tenant", "/tenant/manual/v/1.2.3/nonsearch" }
    };

    public static TheoryData<string, string> HostileNestedRecommendationUrlCases => new()
    {
        { "docs/guide.html", "not-root-relative" },
        { "javascript:alert(1)", "scheme-url" },
        { "data:text/html,hi", "scheme-url" },
        { "1docs:guide/path", "scheme-url" },
        { "https://evil.example/docs/guide.html", "absolute-url" },
        { "//evil.example/docs/guide.html", "protocol-relative" },
        { "/admin", "outside-docs-root" },
        { "/foo/bar/guide.html", "outside-docs-root" },
        { "/tenant/docs/guide.html", "outside-docs-root" },
        { "/docs/%", "malformed-percent-encoding" },
        { "/docs/%GG", "malformed-percent-encoding" },
        { "/docs/../admin", "encoded-traversal" },
        { "/docs/%2e%2e/admin", "encoded-traversal" },
        { "/docs/%2E%2E/admin", "encoded-traversal" },
        { "/docs/%252e%252e/admin", "encoded-traversal" },
        { "/docs/%2fadmin", "encoded-separator" },
        { "/docs/%2Fadmin", "encoded-separator" },
        { "/docs/%252fadmin", "encoded-separator" },
        { "/docs/%5cadmin", "encoded-separator" },
        { "/docs/%255cadmin", "encoded-separator" },
        { "/docs/guide.html\\evil", "backslash" },
        { "/docs/guide.html?q=bad\\path", "backslash" },
        { "/docs/%0a", "control-character" },
        { "/docs/guide.html?q=%0a", "control-character" },
        { "/docs/guide.html?q=%250a", "control-character" },
        { "/docs/guide.html#section-%0a", "control-character" },
        { "/docs/guide.html#section-%250a", "control-character" },
        { "/docs/%C2%85", "control-character" },
        { "/docs2/guide.html", "outside-docs-root" },
        { "/docs-tenant/guide.html", "outside-docs-root" },
        { "/docs/../admin/guide.html", "encoded-traversal" },
        { "/docs/%2e%2e/admin/guide.html", "encoded-traversal" },
        { "/docs/%53earch", "reserved-route" },
        { "/docs/%73EARCH", "reserved-route" },
        { "/docs/%73ections/slug", "reserved-route" },
        { "/docs/%53earch-index/refresh", "reserved-route" },
        { "/docs/v/9.9.9/guide.html", "wrong-version" },
        { "/docs/versions/9.9.9/guide.html", "wrong-version" },
        { "/docs/v/1.2.3/guide.html", "wrong-version" },
        { "/docs/versions/1.2.3/guide.html", "wrong-version" }
    };

    public static IEnumerable<object[]> MalformedRecommendationCases()
    {
        yield return ["null", "documents[0].recommendedPages", "malformed recommendation list", ""];
        yield return ["{}", "documents[0].recommendedPages", "malformed recommendation list", ""];
        yield return ["7", "documents[0].recommendedPages", "malformed recommendation list", ""];
        yield return ["[null]", "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return ["[7]", "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return ["[{}]", "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return ["[{\"path\":\"/docs/index.html\"}]", "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return ["[{\"title\":\"Index\"}]", "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return ["[{\"title\":null,\"path\":\"/docs/index.html\"}]", "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return ["[{\"title\":\"Index\",\"path\":null}]", "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return [CreateRecommendationArray(" ", "/docs/index.html"), "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return [CreateRecommendationArray("Index", " "), "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return [CreateRecommendationArray("Bad\nTitle", "/docs/index.html"), "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return [CreateRecommendationArray("Index", "/docs/nonsearch\n"), "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return ["[{\"title\":9,\"path\":\"/docs/index.html\"}]", "documents[0].recommendedPages[0]", "malformed recommendation row", ""];
        yield return ["[{\"title\":\"Index\",\"path\":9}]", "documents[0].recommendedPages[0]", "malformed recommendation row", ""];

        foreach (var path in ReservedNestedRecommendationPaths)
        {
            yield return [CreateRecommendationArray("Index", path), "documents[0].recommendedPages[0]", "unsafe recommendation URL", ""];
        }

        yield return [CreateRecommendationArray("Index", "https://evil.example/private/secret?token=do-not-leak"), "documents[0].recommendedPages[0]", "unsafe recommendation URL", "evil.example"];
        yield return [CreateRecommendationArray("Index", "//evil.example/private/secret"), "documents[0].recommendedPages[0]", "unsafe recommendation URL", "evil.example"];
        yield return [CreateRecommendationArray("Index", "javascript:alert(1)"), "documents[0].recommendedPages[0]", "unsafe recommendation URL", "alert(1)"];
        yield return [CreateRecommendationArray("Index", "/docs/%2e%2e/private/secret"), "documents[0].recommendedPages[0]", "unsafe recommendation URL", "private/secret"];
        yield return [CreateRecommendationArray("Index", "/docs/a%2fb.html"), "documents[0].recommendedPages[0]", "unsafe recommendation URL", "a%2fb.html"];
        yield return [CreateRecommendationArray("Index", "/docs\\private\\secret.html"), "documents[0].recommendedPages[0]", "unsafe recommendation URL", "private\\secret.html"];
        yield return [CreateRecommendationArray("Index", "/docs/v/9.9.9/guide.html"), "documents[0].recommendedPages[0]", "unsafe recommendation URL", "9.9.9"];
        yield return [CreateRecommendationArray("Index", "/docs/v/1.2.3/guide.html"), "documents[0].recommendedPages[0]", "unsafe recommendation URL", ""];
        yield return [CreateRecommendationArray("Index", "/custom/docs/guide.html"), "documents[0].recommendedPages[0]", "unsafe recommendation URL", "custom/docs"];
    }

    [Theory]
    [MemberData(nameof(ServingMountCases))]
    public async Task CatalogAndHandler_ShouldValidateThenRebaseMinimalAndFutureRows(
        string routeRootPath,
        string mountRootPath,
        string pathBase,
        string expectedNonsearchPath)
    {
        using var fixture = new BoundaryFixture();
        var searchIndex = $$"""
            {
              "documents": [
                {
                  "path": "/docs/index.html",
                  "title": "Index",
                  "recommendedPages": [
                    {
                      "title": "Non-search target",
                      "path": "/docs/nonsearch?tracking=discard-me#safe-fragment"
                    },
                    {
                      "title": "Future row",
                      "path": "/docs/future.html?tracking=also-discard#future-fragment",
                      "source": "future-authored-source",
                      "primaryReasonCode": "future-primary-code",
                      "reasonCodes": ["future-support-code", "future-primary-code"],
                      "futurePresentation": { "mode": "opaque", "rank": 42 }
                    }
                  ]
                }
              ]
            }
            """;
        var version = fixture.ResolveAvailableVersion(searchIndex);
        Assert.True(version.IsAvailable, version.AvailabilityIssue);
        Assert.NotNull(version.VerifiedReleaseArchive);

        using var fileProvider = new PhysicalFileProvider(version.ExactTreePath!);
        var handler = new AppSurfaceDocsPublishedTreeHandler(
            [
                new AppSurfaceDocsPublishedTreeMount(
                    mountRootPath,
                    fileProvider,
                    version.ExactTreePath,
                    new AppSurfaceDocsFrozenRouteManifestCache(
                        version.VerifiedReleaseArchive!.FrozenRouteManifest,
                        version.ExactTreePath!),
                    version.ArchiveVerificationState,
                    version.VerifiedReleaseArchive)
            ],
            "/docs/next",
            routeRootPath);
        var searchIndexRequest = CreateContext(HttpMethods.Get, mountRootPath + "/search-index.json", pathBase);

        Assert.True(await handler.TryHandleAsync(searchIndexRequest));
        using var rewrittenDocument = JsonDocument.Parse(ReadBody(searchIndexRequest));
        var rows = rewrittenDocument.RootElement.GetProperty("documents")[0].GetProperty("recommendedPages");
        var minimalRow = rows[0];
        Assert.Equal("Non-search target", minimalRow.GetProperty("title").GetString());
        Assert.Equal(expectedNonsearchPath + "#safe-fragment", minimalRow.GetProperty("path").GetString());
        Assert.False(minimalRow.TryGetProperty("targetId", out _));
        Assert.DoesNotContain("discard-me", minimalRow.GetProperty("path").GetString(), StringComparison.Ordinal);

        var futureRow = rows[1];
        Assert.Equal("future-authored-source", futureRow.GetProperty("source").GetString());
        Assert.Equal("future-primary-code", futureRow.GetProperty("primaryReasonCode").GetString());
        Assert.Equal(
            new string?[] { "future-support-code", "future-primary-code" },
            futureRow.GetProperty("reasonCodes").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal("opaque", futureRow.GetProperty("futurePresentation").GetProperty("mode").GetString());
        Assert.Equal(
            mountRootPath + "/future.html#future-fragment",
            futureRow.GetProperty("path").GetString()![pathBase.Length..]);

        var targetPath = CreateContext(HttpMethods.Get, mountRootPath + "/nonsearch", pathBase);
        Assert.True(await handler.TryHandleAsync(targetPath));
        Assert.Contains("selected exact tree nonsearch target", ReadBody(targetPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Catalog_ShouldAcceptLegacyMissingOrEmptyRecommendationArray(bool includeEmptyRecommendationArray)
    {
        using var fixture = new BoundaryFixture();
        var recommendationMember = includeEmptyRecommendationArray ? "[]" : null;
        var version = fixture.ResolveAvailableVersion(BoundaryFixture.CreateSearchIndex(recommendationMember));

        Assert.True(version.IsAvailable, version.AvailabilityIssue);
    }

    [Theory]
    [MemberData(nameof(MalformedRecommendationCases))]
    public void Catalog_ShouldMakeArchiveUnavailableWithRedactedLocationAndRemediation(
        string recommendedPagesJson,
        string expectedLocation,
        string expectedProblem,
        string prohibitedDiagnosticText)
    {
        using var fixture = new BoundaryFixture();
        var logger = new RecordingLogger<AppSurfaceDocsVersionCatalogService>();
        var version = fixture.ResolveVersion(
            BoundaryFixture.CreateSearchIndex(recommendedPagesJson),
            logger);

        Assert.False(version.IsAvailable);
        Assert.Contains(expectedLocation, version.AvailabilityIssue, StringComparison.Ordinal);
        Assert.Contains(RecommendationContractDocsLink, version.AvailabilityIssue, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(prohibitedDiagnosticText))
        {
            Assert.DoesNotContain(prohibitedDiagnosticText, version.AvailabilityIssue, StringComparison.Ordinal);
        }

        var diagnostic = Assert.Single(logger.Messages, message => message.Contains(expectedLocation, StringComparison.Ordinal));
        Assert.Contains($"problem '{expectedProblem}'", diagnostic, StringComparison.Ordinal);
        Assert.Contains("cause '", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Fix: ", diagnostic, StringComparison.Ordinal);
        Assert.Contains(RecommendationContractDocsLink, diagnostic, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(prohibitedDiagnosticText))
        {
            Assert.DoesNotContain(prohibitedDiagnosticText, diagnostic, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(HostileNestedRecommendationUrlCases))]
    public void Catalog_ShouldRejectHostileNestedRecommendationUrlWithSpecificDiagnostic(
        string path,
        string expectedReason)
    {
        using var fixture = new BoundaryFixture();
        var logger = new RecordingLogger<AppSurfaceDocsVersionCatalogService>();
        var version = fixture.ResolveVersion(
            BoundaryFixture.CreateSearchIndex(CreateRecommendationArray("Hostile target", path)),
            logger);

        Assert.False(version.IsAvailable);
        Assert.DoesNotContain(path, version.AvailabilityIssue, StringComparison.Ordinal);
        Assert.Contains(RecommendationContractDocsLink, version.AvailabilityIssue, StringComparison.Ordinal);

        const string location = "documents[0].recommendedPages[0]";
        var diagnostic = Assert.Single(logger.Messages, message => message.Contains(location, StringComparison.Ordinal));
        Assert.Contains("problem 'unsafe recommendation URL'", diagnostic, StringComparison.Ordinal);
        Assert.Contains($"category '{expectedReason}'", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Fix: ", diagnostic, StringComparison.Ordinal);
        Assert.Contains(RecommendationContractDocsLink, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(path, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendationOnlyReservedPrefixes_ShouldNotChangeTopLevelDocumentPathPolicy()
    {
        var context = new PublishedSearchIndexArchivePathContext(Version);

        Assert.True(PublishedSearchIndexDocumentPathPolicy.ValidateArchivePath("/docs/sections/slug", context).IsValid);
        Assert.True(PublishedSearchIndexDocumentPathPolicy.ValidateArchivePath("/docs/search-index/refresh", context).IsValid);
        Assert.False(PublishedSearchIndexDocumentPathPolicy.ValidateRecommendationArchivePath("/docs/sections/slug", context).IsValid);
        Assert.False(PublishedSearchIndexDocumentPathPolicy.ValidateRecommendationArchivePath("/docs/search-index/refresh", context).IsValid);
    }

    [Fact]
    public void Rewriter_ShouldNotRebaseAnUnvalidatedAbsoluteRecommendationUrl()
    {
        const string unsafeUrl = "https://evil.example/docs/nonsearch?tracking=secret#fragment";
        var searchIndex = $$"""
            {"documents":[{"path":"/docs/index.html","title":"Index","recommendedPages":[{"title":"Unsafe","path":"{{unsafeUrl}}"}]}]}
            """;

        var rewritten = AppSurfaceDocsPublishedTreeContentRewriter.RewriteSearchIndexJson(
            searchIndex,
            "/manual",
            routeRootPath: "/manual");
        using var rewrittenDocument = JsonDocument.Parse(rewritten);
        var recommendationPath = rewrittenDocument.RootElement
            .GetProperty("documents")[0]
            .GetProperty("recommendedPages")[0]
            .GetProperty("path")
            .GetString();

        Assert.Equal(unsafeUrl, recommendationPath);
        Assert.DoesNotContain("/manual/nonsearch", recommendationPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_ShouldRejectTargetThatExistsOnlyInSiblingExactTree()
    {
        using var fixture = new BoundaryFixture();
        var siblingRoot = Path.Join(fixture.RootPath, "sibling-tree");
        Directory.CreateDirectory(siblingRoot);
        File.WriteAllText(Path.Join(siblingRoot, "only-in-sibling.html"), "sibling target");
        var logger = new RecordingLogger<AppSurfaceDocsVersionCatalogService>();
        var version = fixture.ResolveVersion(
            BoundaryFixture.CreateSearchIndex(CreateRecommendationArray("Sibling target", "/docs/only-in-sibling.html")),
            logger);

        Assert.False(version.IsAvailable);
        Assert.Contains("invalid recommended-page data", version.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recommendedPages[0]", version.AvailabilityIssue, StringComparison.Ordinal);
        Assert.Contains("no ordinary HTML candidate exists in the selected exact release tree", Assert.Single(logger.Messages, message => message.Contains("recommendedPages[0]", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Catalog_ShouldRejectNestedRecommendationCandidateReparsePoint(bool linkDirectory)
    {
        using var fixture = new BoundaryFixture();
        var externalTargetDirectory = Path.Join(fixture.RootPath, "external-candidate");
        Directory.CreateDirectory(externalTargetDirectory);
        const string directoryTargetContent = "<!doctype html><html><body>manifest-owned directory target</body></html>";
        const string fileTargetContent = "<!doctype html><html><body>manifest-owned file target</body></html>";
        File.WriteAllText(Path.Join(externalTargetDirectory, "index.html"), directoryTargetContent);
        var externalTargetFile = Path.Join(fixture.RootPath, "external-linked-file.html");
        File.WriteAllText(externalTargetFile, fileTargetContent);

        var nestedCandidatePath = Path.Join(
            fixture.ExactTreePath,
            linkDirectory ? "reparse-directory" : "reparse-file.html");
        var recommendationPath = linkDirectory ? "/docs/reparse-directory" : "/docs/reparse-file.html";
        var logger = new RecordingLogger<AppSurfaceDocsVersionCatalogService>();
        var reparsePointCreated = false;

        // The fixture pins the manifest while both candidates are ordinary files, then swaps in a link to byte-identical
        // content outside the exact tree. This keeps archive ownership valid so the catalog reaches candidate validation.
        var version = fixture.ResolveVersion(
            BoundaryFixture.CreateSearchIndex(CreateRecommendationArray("Linked target", recommendationPath)),
            logger,
            afterManifestWritten: () =>
            {
                if (linkDirectory)
                {
                    Directory.Delete(nestedCandidatePath, recursive: true);
                    reparsePointCreated = TryCreateSymbolicLink(
                        nestedCandidatePath,
                        externalTargetDirectory,
                        isDirectory: true);
                }
                else
                {
                    File.Delete(nestedCandidatePath);
                    reparsePointCreated = TryCreateSymbolicLink(
                        nestedCandidatePath,
                        externalTargetFile,
                        isDirectory: false);
                }
            });

        if (!reparsePointCreated)
        {
            return;
        }

        Assert.False(version.IsAvailable);
        Assert.Contains("invalid recommended-page data", version.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        const string location = "documents[0].recommendedPages[0]";
        var diagnostic = Assert.Single(logger.Messages, message => message.Contains(location, StringComparison.Ordinal));
        Assert.Contains("problem 'recommendation target is unavailable'", diagnostic, StringComparison.Ordinal);
        Assert.Contains("reparse segment", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(externalTargetDirectory, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(externalTargetFile, diagnostic, StringComparison.Ordinal);
    }

    private static bool TryCreateSymbolicLink(string linkPath, string targetPath, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            else
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static readonly string[] ReservedNestedRecommendationPaths =
    [
        "/docs/search",
        "/docs/search-index.json",
        "/docs/search.css",
        "/docs/search-client.js",
        "/docs/outline-client.js",
        "/docs/rich-authoring-client.js",
        "/docs/minisearch.min.js",
        "/docs/_harvest",
        "/docs/_health",
        "/docs/_health.json",
        "/docs/_routes",
        "/docs/_routes.json",
        "/docs/_search-index",
        "/docs/_search-index/refresh",
        "/docs/v",
        "/docs/versions",
        "/docs/search-index/refresh",
        "/docs/sections/slug"
    ];

    private static string CreateRecommendationArray(string title, string path)
    {
        return JsonSerializer.Serialize(new[] { new Dictionary<string, string> { ["title"] = title, ["path"] = path } });
    }

    private static DefaultHttpContext CreateContext(string method, string requestPath, string pathBase)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = requestPath;
        context.Request.PathBase = string.IsNullOrEmpty(pathBase) ? PathString.Empty : new PathString(pathBase);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(
            httpContext.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        return reader.ReadToEnd();
    }

    private sealed class BoundaryFixture : IDisposable
    {
        private readonly string _treePath;

        public BoundaryFixture()
        {
            RootPath = Path.Join(Path.GetTempPath(), "appsurfacedocs-recommendation-boundary-tests", Guid.NewGuid().ToString("N"));
            _treePath = Path.Join(RootPath, "release");
            Directory.CreateDirectory(Path.Join(_treePath, "nonsearch"));
            Directory.CreateDirectory(Path.Join(_treePath, "reparse-directory"));
            File.WriteAllText(Path.Join(_treePath, "index.html"), "<!doctype html><html><body>index</body></html>");
            File.WriteAllText(Path.Join(_treePath, "search.html"), "<!doctype html><html><body>search</body></html>");
            File.WriteAllText(Path.Join(_treePath, "search.css"), "body { color: #fff; }");
            File.WriteAllText(Path.Join(_treePath, "search-client.js"), "window.__searchClientLoaded = true;");
            File.WriteAllText(Path.Join(_treePath, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");
            File.WriteAllText(Path.Join(_treePath, "nonsearch", "index.html"), "<!doctype html><html><body>selected exact tree nonsearch target</body></html>");
            File.WriteAllText(Path.Join(_treePath, "future.html"), "<!doctype html><html><body>selected exact tree future target</body></html>");
            File.WriteAllText(
                Path.Join(_treePath, "reparse-directory", "index.html"),
                "<!doctype html><html><body>manifest-owned directory target</body></html>");
            File.WriteAllText(
                Path.Join(_treePath, "reparse-file.html"),
                "<!doctype html><html><body>manifest-owned file target</body></html>");
        }

        public string RootPath { get; }

        public string ExactTreePath => _treePath;

        public static string CreateSearchIndex(string? recommendedPagesJson)
        {
            var indexEntry = new Dictionary<string, object?>
            {
                ["path"] = "/docs/index.html",
                ["title"] = "Index"
            };
            if (recommendedPagesJson is not null)
            {
                using var recommendationDocument = JsonDocument.Parse(recommendedPagesJson);
                indexEntry["recommendedPages"] = recommendationDocument.RootElement.Clone();
            }

            return JsonSerializer.Serialize(new { documents = new[] { indexEntry } });
        }

        public AppSurfaceDocsResolvedVersion ResolveAvailableVersion(string searchIndexJson)
        {
            var version = ResolveVersion(searchIndexJson, new RecordingLogger<AppSurfaceDocsVersionCatalogService>());
            Assert.True(version.IsAvailable, version.AvailabilityIssue);
            return version;
        }

        public AppSurfaceDocsResolvedVersion ResolveVersion(
            string searchIndexJson,
            RecordingLogger<AppSurfaceDocsVersionCatalogService>? logger = null,
            Action? afterManifestWritten = null)
        {
            File.WriteAllText(Path.Join(_treePath, "search-index.json"), searchIndexJson);
            var manifestDigest = WriteReleaseManifest(_treePath);
            afterManifestWritten?.Invoke();
            var catalogPath = Path.Join(RootPath, "catalog.json");
            File.WriteAllText(
                catalogPath,
                JsonSerializer.Serialize(
                    new
                    {
                        recommendedVersion = Version,
                        versions = new[]
                        {
                            new
                            {
                                version = Version,
                                exactTreePath = Path.GetRelativePath(RootPath, _treePath),
                                releaseManifestSha256 = manifestDigest
                            }
                        }
                    }));

            var options = new AppSurfaceDocsOptions
            {
                Versioning = new AppSurfaceDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = catalogPath
                }
            };
            var environment = new TestWebHostEnvironment
            {
                ContentRootPath = RootPath,
                WebRootPath = RootPath
            };
            var service = new AppSurfaceDocsVersionCatalogService(
                options,
                environment,
                logger ?? new RecordingLogger<AppSurfaceDocsVersionCatalogService>());

            return Assert.Single(service.GetCatalog().PublicVersions);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private static string WriteReleaseManifest(string root)
        {
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFileName(path), AppSurfaceDocsReleaseArchiveVerifier.FileName, StringComparison.Ordinal))
                .Where(path => AppSurfaceDocsPublishedTreeHandler.IsHandlerServeableFilePath(NormalizeManifestPath(root, path), allowSvg: true))
                .Select(
                    path => new
                    {
                        path = NormalizeManifestPath(root, path),
                        length = new FileInfo(path).Length,
                        contentType = (string?)null,
                        hashAlgorithm = "sha256",
                        sha256 = ComputeSha256(path)
                    })
                .OrderBy(entry => entry.path, StringComparer.Ordinal)
                .ToArray();
            var manifestPath = TestPathUtils.PathUnder(root, AppSurfaceDocsReleaseArchiveVerifier.FileName);
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(new { schema = AppSurfaceDocsReleaseArchiveVerifier.Schema, files }));
            return ComputeSha256(manifestPath);
        }

        private static string NormalizeManifestPath(string root, string path)
        {
            return Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static string ComputeSha256(string path)
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppSurfaceDocsRecommendationBoundaryTests";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
