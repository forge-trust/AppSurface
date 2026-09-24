using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ForgeTrust.RazorWire.Cli;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>
/// Proves the Issue 164 typed C# Details page survives the production export and versioned published-tree pipeline.
/// </summary>
/// <remarks>
/// The test intentionally exports from a live standalone host rather than manufacturing archive HTML. It then pins the
/// exporter's release manifest in a version catalog and requests the mounted exact-version route, which exercises the
/// same published-tree handler used by production version archives.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class Issue164CSharpCompatibilityTests : IDisposable
{
    private const string NamespaceRoute = "/docs/Namespaces/Issue164.Api.html";
    private const string VersionedNamespaceRoute = "/docs/v/1.2.3/Namespaces/Issue164.Api.html";
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "AppSurfaceDocsIssue164Compatibility",
        Guid.NewGuid().ToString("N"));

    public Issue164CSharpCompatibilityTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task TypedNamespace_ShouldPreserveReaderContractsThroughExportAndVerifiedVersionedMount()
    {
        var sourceRoot = CreateSourceFixture();
        var archiveRoot = Path.Join(_root, "releases");
        var exactTree = Path.Join(archiveRoot, "1.2.3");
        var exportTree = Path.Join(_root, "export");
        Directory.CreateDirectory(archiveRoot);

        await using var sourceHost = await AppSurfaceDocsInProcessHost.StartAsync(
            "http://127.0.0.1:0",
            CreateSourceHostArgs(sourceRoot),
            configureServices: null);
        using var sourceClient = CreateClient(sourceHost.BaseUrl);
        var liveHtml = await WaitForHtmlAsync(sourceClient, NamespaceRoute, "FixtureService");
        var liveDocument = new HtmlParser().ParseDocument(liveHtml);
        AssertTypedNamespaceContract(liveDocument);
        AssertCanonicalNavigationContract(liveDocument);
        var liveSearchDocument = await WaitForSearchDocumentAsync(sourceClient, "/docs/search-index.json");
        AssertTypedSearchIndexContract(liveSearchDocument, "/docs/Namespaces/Issue164.Api.html");

        // RazorWire crawls an application host from its origin. AppSurface Docs is emitted beneath its stable `/docs`
        // subtree, which is then promoted as one exact release tree before the manifest is pinned by the catalog.
        // Keeping the initial crawl origin-wide is important: Docs-owned asset aliases legitimately redirect to package
        // static assets at `/_content/...`, which would be outside a path-scoped `/docs` export boundary.
        var exportContext = new ExportContext(
            exportTree,
            seedRoutesPath: null,
            initialSeedRoutes:
            [
                "/docs",
                "/docs/search",
                "/docs/search-index.json",
                "/docs/search.css",
                "/docs/search-client.js",
                "/docs/minisearch.min.js",
                "/docs/outline-client.js",
                NamespaceRoute,
                "/docs/Namespaces/Issue164.Api.Child.html"
            ],
            baseUrl: sourceHost.BaseUrl,
            mode: ExportMode.Cdn,
            redirectStrategy: ExportRedirectStrategy.Html);

        // Exporting the fixture fans out across the docs corpus. Coverage instrumentation can make that work
        // substantially slower than the normal integration path, so retain a bounded but CI-tolerant limit.
        using var exportCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await new ExportEngine(
                    NullLogger<ExportEngine>.Instance,
                    new LoopbackHttpClientFactory(sourceHost.BaseUrl))
                .RunAsync(exportContext, exportCancellation.Token);
        }
        catch (OperationCanceledException) when (exportCancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The Issue 164 compatibility export did not finish within 90 seconds.");
        }

        PromoteDocsSubtreeToExactTree(exportTree, exactTree);
        var releaseManifest = await ReleaseArchiveManifestWriter.WriteAsync(exactTree, CancellationToken.None);
        Assert.True(File.Exists(Path.Join(exactTree, ".appsurface-docs-release-manifest.json")));
        Assert.True(File.Exists(Path.Join(exactTree, "index.html")));
        Assert.True(File.Exists(Path.Join(exactTree, "search-index.json")));
        Assert.True(File.Exists(Path.Join(exactTree, "Namespaces", "Issue164.Api.html")));

        var catalogPath = WriteCatalog(archiveRoot, releaseManifest.Sha256);
        await using var publishedHost = await AppSurfaceDocsInProcessHost.StartAsync(
            "http://127.0.0.1:0",
            CreatePublishedHostArgs(sourceRoot, archiveRoot, catalogPath),
            configureServices: null);
        using var publishedClient = CreateClient(publishedHost.BaseUrl);
        var publishedHtml = await WaitForHtmlAsync(publishedClient, VersionedNamespaceRoute, "FixtureService");
        var publishedDocument = new HtmlParser().ParseDocument(publishedHtml);

        AssertTypedNamespaceContract(publishedDocument);
        AssertEquivalentRenderedContract(liveDocument, publishedDocument);
        AssertVersionedNavigationContract(publishedDocument);
        var publishedSearchDocument = await WaitForSearchDocumentAsync(
            publishedClient,
            "/docs/v/1.2.3/search-index.json");
        AssertTypedSearchIndexContract(publishedSearchDocument, "/docs/v/1.2.3/Namespaces/Issue164.Api.html");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateSourceFixture()
    {
        var sourceRoot = Path.Join(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        foreach (var fixture in new[]
                 {
                     "ApiFixtures.cs",
                     "ChildFixtures.cs",
                     "NAMESPACE.md",
                     "NAMESPACE.md.yml"
                 })
        {
            File.Copy(FixturePath(fixture), Path.Join(sourceRoot, fixture));
        }

        return sourceRoot;
    }

    private string WriteCatalog(string archiveRoot, string releaseManifestSha256)
    {
        var catalogPath = Path.Join(_root, "catalog.json");
        File.WriteAllText(
            catalogPath,
            JsonSerializer.Serialize(
                new
                {
                    recommendedVersion = "1.2.3",
                    versions = new[]
                    {
                        new
                        {
                            version = "1.2.3",
                            label = "1.2.3",
                            exactTreePath = "1.2.3",
                            releaseManifestSha256,
                            supportState = "Current",
                            visibility = "Public",
                            advisoryState = "None"
                        }
                    }
                }));
        return catalogPath;
    }

    private static IReadOnlyList<string> CreateSourceHostArgs(string sourceRoot)
    {
        return
        [
            "--AppSurfaceDocs:Source:RepositoryRoot",
            sourceRoot,
            "--AppSurfaceDocs:Harvest:StartupMode",
            "Disabled",
            "--AppSurfaceDocs:Contributor:SymbolSourceUrlTemplate",
            "https://source.example.test/{path}#L{line}"
        ];
    }

    private static IReadOnlyList<string> CreatePublishedHostArgs(
        string sourceRoot,
        string archiveRoot,
        string catalogPath)
    {
        return
        [
            "--AppSurfaceDocs:Source:RepositoryRoot",
            sourceRoot,
            "--AppSurfaceDocs:Versioning:Enabled",
            "true",
            "--AppSurfaceDocs:Routing:DocsRootPath",
            "/docs/next",
            "--AppSurfaceDocs:Versioning:CatalogPath",
            catalogPath,
            "--AppSurfaceDocs:Versioning:TrustedReleaseRootPath",
            archiveRoot,
            "--AppSurfaceDocs:Contributor:SymbolSourceUrlTemplate",
            "https://source.example.test/{path}#L{line}"
        ];
    }

    private static HttpClient CreateClient(string baseUrl)
    {
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(baseUrl)
        };
    }

    private static async Task<string> WaitForHtmlAsync(HttpClient client, string path, string expectedText)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        HttpRequestException? lastRequestException = null;
        try
        {
            while (true)
            {
                try
                {
                    using var response = await client.GetAsync(path, timeout.Token);
                    var html = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (response.StatusCode == HttpStatusCode.OK
                        && html.Contains(expectedText, StringComparison.Ordinal))
                    {
                        return html;
                    }
                }
                catch (HttpRequestException exception) when (!timeout.IsCancellationRequested)
                {
                    // Kestrel has not accepted the in-process request yet.
                    lastRequestException = exception;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The Issue 164 compatibility route '{path}' did not render '{expectedText}' within 30 seconds.",
                lastRequestException);
        }
    }

    private static async Task<JsonElement> WaitForSearchDocumentAsync(HttpClient client, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                try
                {
                    using var response = await client.GetAsync(path, timeout.Token);
                    var json = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        using var payload = JsonDocument.Parse(json);
                        if (payload.RootElement.TryGetProperty("documents", out var documents)
                            && documents.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var document in documents.EnumerateArray())
                            {
                                if (document.TryGetProperty("id", out var id)
                                    && string.Equals(
                                        id.GetString(),
                                        "Namespaces/Issue164.Api.html",
                                        StringComparison.Ordinal))
                                {
                                    return document.Clone();
                                }
                            }
                        }
                    }
                }
                catch (HttpRequestException) when (!timeout.IsCancellationRequested)
                {
                    // Kestrel has not accepted the in-process request yet.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The Issue 164 search record did not become available at '{path}' within 30 seconds.");
        }
    }

    private static void AssertTypedNamespaceContract(IDocument document)
    {
        Assert.Single(document.QuerySelectorAll("h1.docs-detail-title"));
        Assert.NotNull(document.QuerySelector("section.doc-type:not(.doc-enum)"));
        Assert.NotNull(document.QuerySelector("section.doc-method-group"));
        Assert.NotNull(document.QuerySelector("section.doc-enum"));

        var overloads = document.QuerySelectorAll("details.doc-overload");
        Assert.Equal(2, overloads.Length);
        Assert.True(overloads[0].HasAttribute("open"));

        var remarks = Assert.Single(document.QuerySelectorAll(".doc-remarks"));
        Assert.Contains("Hostile XML-like text: <script>must remain text</script>.", remarks.TextContent, StringComparison.Ordinal);
        Assert.Empty(remarks.QuerySelectorAll("script"));

        AssertReaderOrder(
            document.Body?.TextContent ?? string.Empty,
            "Issue 164 integration intro",
            "Common entry points",
            "FixtureService",
            "FixtureState");
    }

    private static void AssertTypedSearchIndexContract(JsonElement document, string expectedPath)
    {
        Assert.Equal("Namespaces/Issue164.Api.html", document.GetProperty("id").GetString());
        Assert.Equal(expectedPath, document.GetProperty("path").GetString());
        Assert.Equal("Issue164.Api", document.GetProperty("title").GetString());
        Assert.Equal("Namespaces/Issue164.Api", document.GetProperty("sourcePath").GetString());
        Assert.Equal("csharp", document.GetProperty("language").GetString());
        Assert.Equal("C#", document.GetProperty("languageLabel").GetString());

        var headings = document.GetProperty("headings").EnumerateArray().Select(heading => heading.GetString()).ToArray();
        Assert.Contains(headings, heading => heading?.Contains("FixtureService", StringComparison.Ordinal) == true);

        var bodyText = document.GetProperty("bodyText").GetString();
        Assert.Contains("Issue 164 integration intro", bodyText, StringComparison.Ordinal);
        Assert.Contains("FixtureService", bodyText, StringComparison.Ordinal);
        Assert.Contains("Process fixture", bodyText, StringComparison.Ordinal);
        Assert.Contains("<script>must remain text</script>", bodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("https://source.example.test", bodyText, StringComparison.Ordinal);
    }

    private static void AssertEquivalentRenderedContract(IDocument live, IDocument published)
    {
        var liveContent = Assert.IsAssignableFrom<IElement>(live.QuerySelector(".docs-content"));
        var publishedContent = Assert.IsAssignableFrom<IElement>(published.QuerySelector(".docs-content"));

        Assert.Equal(
            live.QuerySelectorAll("h1.docs-detail-title").Length,
            published.QuerySelectorAll("h1.docs-detail-title").Length);
        Assert.Equal(
            liveContent.QuerySelectorAll("[id]").Select(element => element.Id).Where(id => !string.IsNullOrWhiteSpace(id)).OrderBy(id => id),
            publishedContent.QuerySelectorAll("[id]").Select(element => element.Id).Where(id => !string.IsNullOrWhiteSpace(id)).OrderBy(id => id));
        Assert.Equal(
            NormalizeText(liveContent.TextContent),
            NormalizeText(publishedContent.TextContent));
    }

    private static void AssertVersionedNavigationContract(IDocument document)
    {
        var childNamespace = Assert.Single(
            document.QuerySelectorAll(".doc-namespace-groups a"),
            link => link.TextContent.Contains("Child", StringComparison.Ordinal));
        Assert.StartsWith("/docs/v/1.2.3/", childNamespace.GetAttribute("href"), StringComparison.Ordinal);
        Assert.EndsWith("/Namespaces/Issue164.Api.Child.html", childNamespace.GetAttribute("href"), StringComparison.Ordinal);

        var entryPoint = Assert.Single(
            document.QuerySelectorAll(".doc-namespace-entry-points a"),
            link => link.TextContent.Contains("Process fixture", StringComparison.Ordinal));
        Assert.StartsWith("#", entryPoint.GetAttribute("href"), StringComparison.Ordinal);

        var childEntryPoint = Assert.Single(
            document.QuerySelectorAll(".doc-namespace-entry-points a"),
            link => link.TextContent.Contains("Child namespace", StringComparison.Ordinal));
        Assert.Equal(
            "/docs/v/1.2.3/Namespaces/Issue164.Api.Child.html",
            childEntryPoint.GetAttribute("href"));

        var sourceLinks = document.QuerySelectorAll("a.doc-symbol-source-link");
        Assert.NotEmpty(sourceLinks);
        Assert.All(
            sourceLinks,
            sourceLink => Assert.StartsWith(
                "https://source.example.test/",
                sourceLink.GetAttribute("href"),
                StringComparison.Ordinal));
    }

    private static void AssertCanonicalNavigationContract(IDocument document)
    {
        var childNamespace = Assert.Single(
            document.QuerySelectorAll(".doc-namespace-groups a"),
            link => link.TextContent.Contains("Child", StringComparison.Ordinal));
        Assert.Equal("/docs/Namespaces/Issue164.Api.Child.html", childNamespace.GetAttribute("href"));

        var childEntryPoint = Assert.Single(
            document.QuerySelectorAll(".doc-namespace-entry-points a"),
            link => link.TextContent.Contains("Child namespace", StringComparison.Ordinal));
        Assert.Equal("/docs/Namespaces/Issue164.Api.Child.html", childEntryPoint.GetAttribute("href"));
    }

    private static void AssertReaderOrder(string text, params string[] expectedValues)
    {
        var previousIndex = -1;
        foreach (var expectedValue in expectedValues)
        {
            var index = text.IndexOf(expectedValue, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected '{expectedValue}' after the preceding reader text. Actual text: {NormalizeText(text)}");
            previousIndex = index;
        }
    }

    private static string NormalizeText(string? value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }

    private static string FixturePath(string fileName)
    {
        return TestPathUtils.PathUnder(AppContext.BaseDirectory, "TestData", "Issue164CSharpApi", fileName);
    }

    private static void PromoteDocsSubtreeToExactTree(string exportTree, string exactTree)
    {
        var docsTree = Path.Join(exportTree, "docs");
        var docsLandingPage = Path.Join(exportTree, "docs.html");
        Assert.True(Directory.Exists(docsTree), "The origin-wide export did not emit the expected docs subtree.");
        Assert.True(File.Exists(docsLandingPage), "The origin-wide export did not emit the expected Docs landing page.");

        foreach (var sourcePath in Directory.EnumerateFiles(docsTree, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(docsTree, sourcePath);
            var targetPath = TestPathUtils.PathUnder(exportTree, relativePath);
            var targetDirectory = Path.GetDirectoryName(targetPath);
            Assert.False(string.IsNullOrWhiteSpace(targetDirectory));
            Directory.CreateDirectory(targetDirectory!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        // `/docs` is a canonical non-directory route, so RazorWire materializes its full-page response as
        // `docs.html`. An exact release tree instead treats that page as its root document. Promote it rather than
        // asking the production route handler to recognize a test-only file shape.
        File.Move(docsLandingPage, Path.Join(exportTree, "index.html"));
        Directory.Delete(docsTree, recursive: true);
        Directory.Move(exportTree, exactTree);
    }

    private sealed class LoopbackHttpClientFactory(string baseUrl) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseUrl)
            };
        }
    }
}
