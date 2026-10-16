using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli.Tests;

public sealed class RecommendationExportTests
{
    private const string BaseUrl = "http://localhost:5000";
    private const string SearchIndexSuffix = "/search-index.json";
    private const string ReleaseManifestFileName = ".appsurface-docs-release-manifest.json";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ShouldExportARecommendationToTheDocsLanding(bool frozen)
    {
        const string indexRoute = "/docs/search-index.json";
        var indexBody = CreateSearchIndex(SingleNode(CreateMinimalRecommendation("/docs#overview")), indexRoute);
        using var handler = CreateHandler(indexRoute, indexBody, "/docs");
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, indexRoute);
        if (frozen) context.EnableReleaseArchiveManifest();

        await CreateEngine(client).RunAsync(context);

        Assert.Contains("/docs", handler.RequestedPaths);
        Assert.True(File.Exists(TestPathUtils.PathUnder(output.Path, "docs.html")));
        using var index = JsonDocument.Parse(
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, "docs", "search-index.json")));
        Assert.Equal("/docs.html#overview", index.RootElement
            .GetProperty("documents")[0].GetProperty("recommendedPages")[0].GetProperty("path").GetString());
        if (frozen)
        {
            using var archiveIndex = JsonDocument.Parse(await File.ReadAllTextAsync(
                TestPathUtils.PathUnder(output.Path, "search-index.json")));
            Assert.Equal("/docs/docs.html#overview", archiveIndex.RootElement.GetProperty("documents")[0]
                .GetProperty("recommendedPages")[0].GetProperty("path").GetString());
        }
    }

    [Theory]
    [InlineData("/docs", false)]
    [InlineData("/docs", true)]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("/reference/next", false)]
    [InlineData("/reference/next", true)]
    public async Task RunAsync_ShouldExportARecommendationToADocumentNamedNext(string docsRoot, bool frozen)
    {
        var indexRoute = docsRoot + SearchIndexSuffix;
        var targetRoute = docsRoot + "/next";
        var indexBody = CreateSearchIndex(SingleNode(CreateMinimalRecommendation(targetRoute + "?view=compact#overview")), indexRoute);
        using var handler = CreateHandler(indexRoute, indexBody, targetRoute);
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, indexRoute);
        if (frozen) context.EnableReleaseArchiveManifest();

        await CreateEngine(client).RunAsync(context);

        Assert.Contains(targetRoute, handler.RequestedPaths);
        Assert.True(File.Exists(TestPathUtils.PathUnder(output.Path, targetRoute.TrimStart('/') + ".html")));
        var genericIndexPath = frozen && docsRoot.Length == 0 ? "search-index.static.json" : indexRoute.TrimStart('/');
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, genericIndexPath)));
        Assert.Equal(targetRoute + ".html#overview", index.RootElement
            .GetProperty("documents")[0].GetProperty("recommendedPages")[0].GetProperty("path").GetString());
        if (frozen)
        {
            using var archiveIndex = JsonDocument.Parse(await File.ReadAllTextAsync(
                TestPathUtils.PathUnder(output.Path, "search-index.json")));
            Assert.Equal("/docs" + targetRoute + ".html#overview", archiveIndex.RootElement.GetProperty("documents")[0]
                .GetProperty("recommendedPages")[0].GetProperty("path").GetString());
        }
    }

    [Theory]
    [InlineData("/docs", false)]
    [InlineData("/docs", true)]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("/reference/next", false)]
    [InlineData("/reference/next", true)]
    public async Task RunAsync_ShouldProjectHtmlFramesAliasesAndDocumentPathsFromTheSameArtifact(string docsRoot, bool frozen)
    {
        var indexRoute = docsRoot + SearchIndexSuffix;
        var sourceRoute = docsRoot + "/start";
        var targetRoute = docsRoot + "/guide/next";
        var aliasRoute = docsRoot + "/legacy";
        var indexBody = CreateSearchIndex(SingleNode(CreateRecommendation(targetRoute + "#installation")), indexRoute);
        using var handler = new RecommendationHandler(path => path == indexRoute
            ? TextResponse(HttpStatusCode.OK, indexBody, "application/json")
            : path == sourceRoute
                ? TextResponse(HttpStatusCode.OK,
                    $"<html><body><a href=\"{targetRoute}#installation\">Next guide</a><turbo-frame id=\"lazy\" src=\"{targetRoute}#details\"></turbo-frame></body></html>", "text/html")
                : path == targetRoute
                    ? TextResponse(HttpStatusCode.OK, "<html><body><turbo-frame id=\"doc-content\"><h1>Next guide</h1></turbo-frame></body></html>", "text/html")
                    : TextResponse(HttpStatusCode.NotFound, "not found", "text/plain"));
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = new ExportContext(output.Path, null, [indexRoute, sourceRoute], BaseUrl);
        context.AddRedirectArtifact(aliasRoute, targetRoute);
        if (frozen) context.EnableReleaseArchiveManifest();

        await CreateEngine(client).RunAsync(context);

        var targetArtifact = targetRoute + ".html";
        var sourceArtifact = sourceRoute + ".html";
        var parser = new HtmlParser();
        var html = parser.ParseDocument(await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, sourceRoute.TrimStart('/') + ".html")));
        Assert.Equal(targetArtifact + "#installation", html.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal(targetRoute + ".partial.html#details", html.QuerySelector("turbo-frame")!.GetAttribute("src"));
        Assert.True(File.Exists(TestPathUtils.PathUnder(output.Path, targetRoute.TrimStart('/') + ".partial.html")));
        var alias = parser.ParseDocument(await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, aliasRoute.TrimStart('/') + ".html")));
        Assert.Equal(targetArtifact, alias.QuerySelector("link[rel=canonical]")!.GetAttribute("href"));
        Assert.Equal(targetArtifact, alias.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("0; url=" + targetArtifact, alias.QuerySelector("meta[http-equiv=refresh]")!.GetAttribute("content"));
        var genericIndexPath = frozen && docsRoot.Length == 0 ? "search-index.static.json" : indexRoute.TrimStart('/');
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, genericIndexPath)));
        var document = index.RootElement.GetProperty("documents")[0];
        Assert.Equal(frozen ? sourceArtifact : sourceRoute, document.GetProperty("path").GetString());
        Assert.Equal(targetArtifact + "#installation",
            document.GetProperty("recommendedPages")[0].GetProperty("path").GetString());
        Assert.Equal("guide-next-id", document.GetProperty("recommendedPages")[0].GetProperty("targetId").GetString());
        if (frozen)
        {
            using var archiveIndex = JsonDocument.Parse(await File.ReadAllTextAsync(
                TestPathUtils.PathUnder(output.Path, "search-index.json")));
            var archivedDocument = archiveIndex.RootElement.GetProperty("documents")[0];
            Assert.Equal("/docs" + sourceArtifact, archivedDocument.GetProperty("path").GetString());
            Assert.Equal("/docs" + targetArtifact + "#installation",
                archivedDocument.GetProperty("recommendedPages")[0].GetProperty("path").GetString());
            Assert.Equal(document.GetProperty("recommendedPages")[0].GetProperty("targetId").GetString(),
                archivedDocument.GetProperty("recommendedPages")[0].GetProperty("targetId").GetString());
            Assert.Equal(document.GetProperty("recommendedPages")[0].GetProperty("reasonCodes").GetRawText(),
                archivedDocument.GetProperty("recommendedPages")[0].GetProperty("reasonCodes").GetRawText());
        }
    }

    [Fact]
    public async Task RunAsync_QueuesHiddenTargetAndRewritesRecommendationToEmittedArtifact()
    {
        const string searchIndexRoute = "/docs/search-index.json";
        const string targetRoute = "/docs/guide/next";
        var indexBody = CreateSearchIndex(
            SingleNode(CreateRecommendation(targetRoute + "?campaign=search#installation")),
            searchIndexRoute);
        using var handler = CreateHandler(searchIndexRoute, indexBody, targetRoute);
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, searchIndexRoute);

        await CreateEngine(client).RunAsync(context);

        Assert.Contains(targetRoute, handler.RequestedPaths);
        var targetFile = TestPathUtils.PathUnder(output.Path, "docs", "guide", "next.html");
        Assert.True(File.Exists(targetFile));
        Assert.Contains("Next guide", await File.ReadAllTextAsync(targetFile), StringComparison.Ordinal);

        using var exportedIndex = JsonDocument.Parse(
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, "docs", "search-index.json")));
        var documents = exportedIndex.RootElement.GetProperty("documents");
        var sourceDocument = Assert.Single(documents.EnumerateArray().ToArray());
        Assert.Equal("source-page", sourceDocument.GetProperty("id").GetString());
        var recommendation = Assert.Single(sourceDocument.GetProperty("recommendedPages").EnumerateArray().ToArray());
        Assert.Equal("/docs/guide/next.html#installation", recommendation.GetProperty("path").GetString());
        Assert.Equal("guide-next-id", recommendation.GetProperty("targetId").GetString());
        Assert.Equal("future-source", recommendation.GetProperty("source").GetString());
        Assert.Equal("future-primary", recommendation.GetProperty("primaryReasonCode").GetString());
        Assert.Equal(
            ["future-primary", "future-evidence"],
            recommendation.GetProperty("reasonCodes").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal("A future reason caption.", recommendation.GetProperty("reasonText").GetString());
        Assert.Equal("A concise preview.", recommendation.GetProperty("summary").GetString());
        Assert.Equal("Guide", recommendation.GetProperty("pageTypeLabel").GetString());
        Assert.Equal("guide", recommendation.GetProperty("pageTypeVariant").GetString());
        Assert.DoesNotContain(
            recommendation.GetProperty("targetId").GetString(),
            documents.EnumerateArray().Select(document => document.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task RunAsync_AcceptsSparseRecommendationAndKeepsArchiveNavigation()
    {
        const string searchIndexRoute = "/docs/search-index.json";
        const string targetRoute = "/docs/guide/minimal";
        var indexBody = CreateSearchIndex(
            SingleNode(CreateMinimalRecommendation(targetRoute + "?display=compact#details")),
            searchIndexRoute);
        using var handler = CreateHandler(searchIndexRoute, indexBody, targetRoute);
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, searchIndexRoute);
        context.EnableReleaseArchiveManifest();

        await CreateEngine(client).RunAsync(context);

        Assert.Contains(targetRoute, handler.RequestedPaths);
        Assert.True(File.Exists(TestPathUtils.PathUnder(output.Path, "docs", "guide", "minimal.html")));
        using var exportedIndex = JsonDocument.Parse(
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, "docs", "search-index.json")));
        var recommendation = Assert.Single(exportedIndex.RootElement.GetProperty("documents")[0]
            .GetProperty("recommendedPages").EnumerateArray().ToArray());
        Assert.Equal("/docs/guide/minimal.html#details", recommendation.GetProperty("path").GetString());
        Assert.Equal("Next guide", recommendation.GetProperty("title").GetString());
        Assert.False(recommendation.TryGetProperty("targetId", out _));
        Assert.False(recommendation.TryGetProperty("source", out _));
        Assert.False(recommendation.TryGetProperty("primaryReasonCode", out _));
        Assert.False(recommendation.TryGetProperty("reasonCodes", out _));
        Assert.False(recommendation.TryGetProperty("reasonText", out _));
        using var archiveIndex = JsonDocument.Parse(await File.ReadAllTextAsync(
            TestPathUtils.PathUnder(output.Path, "search-index.json")));
        var archivedRecommendation = Assert.Single(archiveIndex.RootElement.GetProperty("documents")[0]
            .GetProperty("recommendedPages").EnumerateArray().ToArray());
        Assert.Equal("/docs/docs/guide/minimal.html#details", archivedRecommendation.GetProperty("path").GetString());
        Assert.Equal("Next guide", archivedRecommendation.GetProperty("title").GetString());
        foreach (var optional in new[] { "targetId", "source", "primaryReasonCode", "reasonCodes", "reasonText" })
        {
            Assert.False(archivedRecommendation.TryGetProperty(optional, out _));
        }
        Assert.NotNull(context.ReleaseArchiveManifest);
    }

    [Fact]
    public async Task RunAsync_PreservesLegacyPrepopulatedIndexAndBindsNormalizedCopies()
    {
        using var output = new TemporaryDirectory();
        var docsRoot = TestPathUtils.PathUnder(output.Path, "docs");
        Directory.CreateDirectory(docsRoot);
        const string body = """
                            {"metadata":{"generatedAtUtc":"2026-10-09T12:00:00.0000000Z","futureMetadata":"keep"},"documents":[]}
                            """;
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(docsRoot, "search-index.json"), body);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(docsRoot, "search.html"), "<html>Legacy search</html>");
        using var handler = new RecommendationHandler(path => path == "/"
            ? TextResponse(HttpStatusCode.OK, "<html>Home</html>", "text/html")
            : TextResponse(HttpStatusCode.NotFound, "not found", "text/plain"));
        using var client = CreateClient(handler);
        var context = new ExportContext(output.Path, null, ["/"], BaseUrl);
        context.EnableReleaseArchiveManifest();

        await CreateEngine(client).RunAsync(context);

        var rootBytes = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output.Path, "search-index.json"));
        Assert.Equal(rootBytes, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(docsRoot, "search-index.json")));
        using var index = JsonDocument.Parse(rootBytes);
        Assert.Equal("1970-01-01T00:00:00.0000000Z", index.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString());
        Assert.Equal("keep", index.RootElement.GetProperty("metadata").GetProperty("futureMetadata").GetString());
        Assert.Equal("<html>Legacy search</html>", await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, "search.html")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, "search-index.static.json")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
        foreach (var memberPath in new[] { "docs/search-index.json", "search-index.json" })
        {
            var member = Assert.Single(manifest.RootElement.GetProperty("files").EnumerateArray(),
                file => file.GetProperty("path").GetString() == memberPath);
            Assert.Equal(Hash(rootBytes), member.GetProperty("sha256").GetString());
            Assert.Equal(rootBytes.LongLength, member.GetProperty("length").GetInt64());
        }
    }

    [Theory]
    [InlineData("search-index.json")]
    [InlineData("search.html")]
    [InlineData("rich-authoring-client.js")]
    public async Task RunAsync_RejectsLegacyPrepopulatedRootAssetCollision(string fileName)
    {
        using var output = new TemporaryDirectory();
        var docsRoot = TestPathUtils.PathUnder(output.Path, "docs");
        Directory.CreateDirectory(docsRoot);
        const string contents = """{"metadata":{},"documents":[]}""";
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(docsRoot, fileName), contents);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(output.Path, fileName), contents);
        using var handler = new RecommendationHandler(path => path == "/"
            ? TextResponse(HttpStatusCode.OK, "<html>Home</html>", "text/html")
            : TextResponse(HttpStatusCode.NotFound, "not found", "text/plain"));
        using var client = CreateClient(handler);
        var context = new ExportContext(output.Path, null, ["/"], BaseUrl);
        context.EnableReleaseArchiveManifest();

        await Assert.ThrowsAsync<ExportValidationException>(() => CreateEngine(client).RunAsync(context));

        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
        Assert.Equal(contents, await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, fileName)));
    }

    [Fact]
    public async Task RunAsync_WritesIndependentGenericAndFrozenIndexesAndBindsBothInManifest()
    {
        const string searchIndexRoute = "/docs/search-index.json";
        const string targetRoute = "/docs/guide/next";
        var indexBody = CreateSearchIndex(
            SingleNode(CreateRecommendation(targetRoute + "?tracking=discard#release-notes", authored: true)),
            searchIndexRoute);
        using var handler = CreateHandler(searchIndexRoute, indexBody, targetRoute);
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, searchIndexRoute);
        context.EnableReleaseArchiveManifest();

        await CreateEngine(client).RunAsync(context);

        Assert.Contains(targetRoute, handler.RequestedPaths);
        Assert.True(File.Exists(TestPathUtils.PathUnder(output.Path, "docs", "guide", "next.html")));
        var docsIndexPath = TestPathUtils.PathUnder(output.Path, "docs", "search-index.json");
        var rootIndexPath = TestPathUtils.PathUnder(output.Path, "search-index.json");
        var docsIndex = await File.ReadAllTextAsync(docsIndexPath);
        var frozenIndex = await File.ReadAllTextAsync(rootIndexPath);
        Assert.NotEqual(docsIndex, frozenIndex);

        using var genericJson = JsonDocument.Parse(docsIndex);
        using var frozenJson = JsonDocument.Parse(frozenIndex);
        var genericRoot = genericJson.RootElement;
        var frozenRoot = frozenJson.RootElement;
        foreach (var indexRoot in new[] { genericRoot, frozenRoot })
            Assert.Equal("1970-01-01T00:00:00.0000000Z", indexRoot.GetProperty("metadata").GetProperty("generatedAtUtc").GetString());
        var genericRow = genericRoot.GetProperty("documents")[0].GetProperty("recommendedPages")[0];
        var frozenRow = frozenRoot.GetProperty("documents")[0].GetProperty("recommendedPages")[0];
        Assert.Equal("/docs/guide/next.html#release-notes", genericRow.GetProperty("path").GetString());
        Assert.Equal("/docs/docs/guide/next.html#release-notes", frozenRow.GetProperty("path").GetString());
        foreach (var row in new[] { genericRow, frozenRow })
        {
            Assert.Equal("guide-next-id", row.GetProperty("targetId").GetString());
            Assert.Equal("Next guide", row.GetProperty("title").GetString());
            Assert.Equal("A concise preview.", row.GetProperty("summary").GetString());
            Assert.Equal("Guide", row.GetProperty("pageTypeLabel").GetString());
            Assert.Equal("guide", row.GetProperty("pageTypeVariant").GetString());
            Assert.Equal("authored", row.GetProperty("source").GetString());
            Assert.Equal("authored", row.GetProperty("primaryReasonCode").GetString());
            Assert.Equal(["authored", "shared-section"], row.GetProperty("reasonCodes")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
            Assert.Equal("Selected by the author.", row.GetProperty("reasonText").GetString());
        }

        var manifestPath = TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var manifestRoot = manifest.RootElement;
        Assert.Equal("appsurface-docs-release-manifest-v1", manifestRoot.GetProperty("schema").GetString());
        var files = manifestRoot.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(files.Length, context.ReleaseArchiveManifest!.FileCount);
        Assert.Contains(files, file => file.GetProperty("path").GetString() == "docs/guide/next.html");
        Assert.Equal(Hash(manifestBytes), context.ReleaseArchiveManifest.Sha256);

        foreach (var archivedIndexPath in new[] { "docs/search-index.json", "search-index.json" })
        {
            var entry = Assert.Single(files, file => file.GetProperty("path").GetString() == archivedIndexPath);
            Assert.Equal("sha256", entry.GetProperty("hashAlgorithm").GetString());
            Assert.Equal(Hash(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output.Path, archivedIndexPath))),
                entry.GetProperty("sha256").GetString());
        }
        Assert.NotEqual(
            Assert.Single(files, file => file.GetProperty("path").GetString() == "docs/search-index.json").GetProperty("sha256").GetString(),
            Assert.Single(files, file => file.GetProperty("path").GetString() == "search-index.json").GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task RunAsync_UsesCustomMountedDocsRootForSameTreeRecommendation()
    {
        const string docsRoot = "/foo/bar/next";
        var searchIndexRoute = docsRoot + SearchIndexSuffix;
        var targetRoute = docsRoot + "/guides/next";
        var indexBody = CreateSearchIndex(
            SingleNode(CreateRecommendation(targetRoute + "?view=full#details")),
            searchIndexRoute);
        using var handler = CreateHandler(searchIndexRoute, indexBody, targetRoute);
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, searchIndexRoute);

        await CreateEngine(client).RunAsync(context);

        Assert.Contains(targetRoute, handler.RequestedPaths);
        Assert.True(File.Exists(TestPathUtils.PathUnder(output.Path, "foo", "bar", "next", "guides", "next.html")));
        using var exportedIndex = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                TestPathUtils.PathUnder(output.Path, "foo", "bar", "next", "search-index.json")));
        var recommendation = Assert.Single(exportedIndex.RootElement.GetProperty("documents")[0]
            .GetProperty("recommendedPages").EnumerateArray().ToArray());
        Assert.Equal(targetRoute + ".html#details", recommendation.GetProperty("path").GetString());
    }

    [Fact]
    public async Task RunAsync_HandlesRootMountedSearchIndexAndTarget()
    {
        const string searchIndexRoute = "/search-index.json";
        const string targetRoute = "/guides/next";
        var indexBody = CreateSearchIndex(
            SingleNode(CreateRecommendation(targetRoute + "#overview")),
            searchIndexRoute);
        using var handler = CreateHandler(searchIndexRoute, indexBody, targetRoute);
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, searchIndexRoute);

        await CreateEngine(client).RunAsync(context);

        Assert.Contains(searchIndexRoute, handler.RequestedPaths);
        Assert.Contains(targetRoute, handler.RequestedPaths);
        Assert.True(File.Exists(TestPathUtils.PathUnder(output.Path, "guides", "next.html")));
        using var exportedIndex = JsonDocument.Parse(
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, "search-index.json")));
        Assert.Equal("/guides/next.html#overview", exportedIndex.RootElement.GetProperty("documents")[0]
            .GetProperty("recommendedPages")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task RunAsync_SeparatesRootGenericAndFrozenSearchIndexes()
    {
        const string searchIndexRoute = "/search-index.json";
        const string targetRoute = "/guides/next";
        var body = CreateSearchIndex(SingleNode(CreateRecommendation(targetRoute + "#overview")), searchIndexRoute);
        using var handler = CreateHandler(searchIndexRoute, body, targetRoute);
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, searchIndexRoute);
        context.EnableReleaseArchiveManifest();

        await CreateEngine(client).RunAsync(context);

        using var generic = JsonDocument.Parse(await File.ReadAllTextAsync(
            TestPathUtils.PathUnder(output.Path, "search-index.static.json")));
        using var frozen = JsonDocument.Parse(await File.ReadAllTextAsync(
            TestPathUtils.PathUnder(output.Path, "search-index.json")));
        Assert.Equal("/guides/next.html#overview", generic.RootElement.GetProperty("documents")[0]
            .GetProperty("recommendedPages")[0].GetProperty("path").GetString());
        Assert.Equal("/docs/guides/next.html#overview", frozen.RootElement.GetProperty("documents")[0]
            .GetProperty("recommendedPages")[0].GetProperty("path").GetString());
        Assert.Equal("1970-01-01T00:00:00.0000000Z", generic.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString());
        Assert.Equal("1970-01-01T00:00:00.0000000Z", frozen.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString());
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
        var members = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Contains(members, file => file.GetProperty("path").GetString() == "search-index.static.json");
        Assert.Contains(members, file => file.GetProperty("path").GetString() == "search-index.json");
        foreach (var memberPath in new[] { "search-index.static.json", "search-index.json" })
        {
            var member = Assert.Single(members, file => file.GetProperty("path").GetString() == memberPath);
            Assert.Equal(Hash(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output.Path, memberPath))),
                member.GetProperty("sha256").GetString());
        }
    }

    [Fact]
    public async Task RunAsync_RejectsRootGenericIndexArtifactCollision()
    {
        const string indexRoute = "/search-index.json";
        var body = CreateSearchIndex(null, indexRoute, includeRecommendations: false);
        using var handler = new RecommendationHandler(path => path == indexRoute
            ? TextResponse(HttpStatusCode.OK, body, "application/json")
            : path == "/search-index.static.json"
                ? TextResponse(HttpStatusCode.OK, "occupied", "text/plain")
                : TextResponse(HttpStatusCode.NotFound, "not found", "text/plain"));
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = new ExportContext(output.Path, null, [indexRoute, "/search-index.static.json"], BaseUrl);
        context.EnableReleaseArchiveManifest();

        await Assert.ThrowsAsync<ExportValidationException>(() => CreateEngine(client).RunAsync(context));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
    }

    [Fact]
    public async Task RunAsync_RejectsMultipleSuccessfulSearchIndexRootsInFrozenArchive()
    {
        const string rootIndexRoute = "/search-index.json";
        const string mountedIndexRoute = "/docs/search-index.json";
        using var handler = new RecommendationHandler(path => path is rootIndexRoute or mountedIndexRoute
            ? TextResponse(HttpStatusCode.OK, CreateSearchIndex(null, path, includeRecommendations: false), "application/json")
            : TextResponse(HttpStatusCode.NotFound, "not found", "text/plain"));
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = new ExportContext(output.Path, null, [rootIndexRoute, mountedIndexRoute], BaseUrl);
        context.EnableReleaseArchiveManifest();

        await Assert.ThrowsAsync<ExportValidationException>(() => CreateEngine(client).RunAsync(context));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
    }

    [Theory]
    [InlineData("window.__appSurfaceDocsConfig = {\"docsSearchIndexUrl\":\"/search-index.json\",\"keep\":\"same\"};", "/search-index.static.json")]
    [InlineData("window.__appSurfaceDocsConfig = {\"docsSearchIndexUrl\":\"/broken\",};", null)]
    [InlineData("window.__appSurfaceDocsConfig = {\"docsSearchIndexUrl\":\"/other/search-index.json\"};", null)]
    public async Task RunAsync_RewritesOnlyAValidMatchingOwnedDocsSearchIndexConfig(string script, string? expectedUrl)
    {
        const string indexRoute = "/search-index.json";
        var body = CreateSearchIndex(null, indexRoute, includeRecommendations: false);
        using var handler = new RecommendationHandler(path => path == "/"
            ? TextResponse(HttpStatusCode.OK, $"<html><script>{script}</script></html>", "text/html")
            : path == indexRoute
                ? TextResponse(HttpStatusCode.OK, body, "application/json")
                : TextResponse(HttpStatusCode.NotFound, "not found", "text/plain"));
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = new ExportContext(output.Path, null, ["/", indexRoute], BaseUrl);
        context.EnableReleaseArchiveManifest();

        await CreateEngine(client).RunAsync(context);

        var html = await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, "index.html"));
        if (expectedUrl is null)
            Assert.Contains(script, html, StringComparison.Ordinal);
        else
        {
            Assert.Contains($"\"docsSearchIndexUrl\":\"{expectedUrl}\"", html, StringComparison.Ordinal);
            Assert.Contains("\"keep\":\"same\"", html, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_AcceptsLegacyIndexesWithoutRecommendationRows(bool includeEmptyRecommendationList)
    {
        const string searchIndexRoute = "/docs/search-index.json";
        var indexBody = CreateSearchIndex(
            includeEmptyRecommendationList ? new JsonArray() : null,
            searchIndexRoute,
            includeRecommendations: includeEmptyRecommendationList);
        using var handler = CreateHandler(searchIndexRoute, indexBody);
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, searchIndexRoute);

        await CreateEngine(client).RunAsync(context);

        Assert.True(File.Exists(TestPathUtils.PathUnder(output.Path, "docs", "search-index.json")));
        Assert.DoesNotContain("/docs/start", handler.RequestedPaths);
    }

    [Theory]
    [MemberData(nameof(InvalidRecommendationIndexes))]
    public async Task RunAsync_RejectsMalformedRecommendationMetadata(string caseName, string indexBody)
    {
        var run = await RunFailureAsync("/docs/search-index.json", indexBody);

        Assert.True(
            run.Exception.Diagnostics.Any(diagnostic => diagnostic.Code == "RWEXPORT011"),
            "Expected RWEXPORT011 for " + caseName + "; received " +
            string.Join(", ", run.Exception.Diagnostics.Select(diagnostic => diagnostic.Code)));
        Assert.DoesNotContain(
            run.RequestedPaths,
            requestedPath => requestedPath != "/docs/search-index.json" && requestedPath != "/_appsurface/errors/404");
    }

    [Theory]
    [MemberData(nameof(UnsafeRecommendationPaths))]
    public async Task RunAsync_RejectsUnsafeOrReservedRecommendationPaths(
        string caseName,
        string searchIndexRoute,
        string recommendationPath)
    {
        var indexBody = CreateSearchIndex(SingleNode(CreateRecommendation(recommendationPath)), searchIndexRoute);
        var run = await RunFailureAsync(searchIndexRoute, indexBody);

        Assert.True(
            run.Exception.Diagnostics.Any(diagnostic => diagnostic.Code == "RWEXPORT011"),
            "Expected RWEXPORT011 for " + caseName + "; received " +
            string.Join(", ", run.Exception.Diagnostics.Select(diagnostic => diagnostic.Code)));
        Assert.DoesNotContain(
            run.RequestedPaths,
            requestedPath => requestedPath != searchIndexRoute && requestedPath != "/_appsurface/errors/404");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RunAsync_FailsWithoutFinalArchiveWhenRecommendationTargetCannotBeFetched(HttpStatusCode statusCode)
    {
        const string searchIndexRoute = "/docs/search-index.json";
        const string targetRoute = "/docs/guide/next";
        var indexBody = CreateSearchIndex(
            SingleNode(CreateRecommendation(targetRoute + "#installation")),
            searchIndexRoute);
        var run = await RunFailureAsync(searchIndexRoute, indexBody, targetRoute, statusCode);

        Assert.Contains(targetRoute, run.RequestedPaths);
        Assert.Contains(run.Exception.Diagnostics,
            diagnostic => diagnostic.Code is "RWEXPORT011" or "RWEXPORT004");
    }

    public static IEnumerable<object[]> InvalidRecommendationIndexes()
    {
        const string searchIndexRoute = "/docs/search-index.json";

        yield return ["null recommendedPages", CreateSearchIndex(null, searchIndexRoute)];
        yield return ["non-array recommendedPages", CreateSearchIndex(JsonValue.Create("not-an-array"), searchIndexRoute)];
        yield return ["object recommendedPages", CreateSearchIndex(new JsonObject(), searchIndexRoute)];
        yield return ["null recommendation row", CreateSearchIndex(SingleNode(null), searchIndexRoute)];
        yield return ["scalar recommendation row", CreateSearchIndex(SingleNode(JsonValue.Create("not-an-object")), searchIndexRoute)];

        foreach (var requiredField in new[] { "path", "title" })
        {
            var row = CreateMinimalRecommendation("/docs/guide/next");
            row.Remove(requiredField);
            yield return ["missing " + requiredField, CreateSearchIndex(SingleNode(row), searchIndexRoute)];
        }

        var blankTitle = CreateMinimalRecommendation("/docs/guide/next", " ");
        yield return ["blank title", CreateSearchIndex(SingleNode(blankTitle), searchIndexRoute)];
        var controlTitle = CreateMinimalRecommendation("/docs/guide/next", "Invalid\nTitle");
        yield return ["control title", CreateSearchIndex(SingleNode(controlTitle), searchIndexRoute)];
        var blankPath = CreateMinimalRecommendation(" ");
        yield return ["blank path", CreateSearchIndex(SingleNode(blankPath), searchIndexRoute)];
        var nonStringTitle = CreateMinimalRecommendation("/docs/guide/next");
        nonStringTitle["title"] = JsonValue.Create(42);
        yield return ["non-string title", CreateSearchIndex(SingleNode(nonStringTitle), searchIndexRoute)];
        var nonStringPath = CreateMinimalRecommendation("/docs/guide/next");
        nonStringPath["path"] = JsonValue.Create(42);
        yield return ["non-string path", CreateSearchIndex(SingleNode(nonStringPath), searchIndexRoute)];

    }

    [Fact]
    public async Task RunAsync_PreservesOpaqueFuturePresentationMetadataWithValidNavigation()
    {
        const string indexRoute = "/docs/search-index.json";
        const string targetRoute = "/docs/guide/next";
        var row = CreateMinimalRecommendation(targetRoute);
        row["source"] = 42;
        row["reasonCodes"] = new JsonObject { ["future"] = true };
        row["futurePresentation"] = new JsonArray("future");
        using var handler = CreateHandler(indexRoute, CreateSearchIndex(SingleNode(row), indexRoute), targetRoute);
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, indexRoute);
        await CreateEngine(client).RunAsync(context);
        using var index = JsonDocument.Parse(
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(output.Path, "docs", "search-index.json")));
        var recommendation = index.RootElement.GetProperty("documents")[0].GetProperty("recommendedPages")[0];
        Assert.Equal(targetRoute + ".html", recommendation.GetProperty("path").GetString());
        Assert.Equal(42, recommendation.GetProperty("source").GetInt32());
        Assert.True(recommendation.GetProperty("reasonCodes").GetProperty("future").GetBoolean());
    }

    public static IEnumerable<object[]> UnsafeRecommendationPaths()
    {
        const string docsIndex = "/docs/search-index.json";
        foreach (var (name, path) in new[]
                 {
                     ("off selected docs root", "/about"),
                     ("external URL", "https://outside.example/docs/guide"),
                     ("protocol-relative URL", "//outside.example/docs/guide"),
                     ("mailto scheme", "mailto:help@example.com"),
                     ("javascript scheme", "javascript:alert(1)"),
                     ("encoded forward slash", "/docs/%2fprivate"),
                     ("encoded backslash", "/docs/%5cprivate"),
                     ("double-encoded separator", "/docs/guide%252fnext"),
                     ("raw traversal", "/docs/../private"),
                     ("encoded traversal", "/docs/%2e%2e/private"),
                     ("control character", "/docs/guide/\nnext"),
                     ("malformed percent in path", "/docs/guide/next%"),
                     ("malformed percent in fragment suffix", "/docs/guide/next#part%"),
                     ("leading whitespace", " /docs/guide/next"),
                     ("trailing whitespace", "/docs/guide/next "),
                     ("backslash", "/docs\\guide\\next"),
                     ("search route", "/docs/search"),
                     ("search-index file", "/docs/search-index.json"),
                     ("search-index refresh", "/docs/search-index/refresh"),
                     ("internal search-index refresh", "/docs/_search-index/refresh"),
                     ("harvest endpoint", "/docs/_harvest"),
                     ("harvest subroute", "/docs/_harvest/rebuild"),
                     ("health endpoint", "/docs/_health"),
                     ("health JSON", "/docs/_health.json"),
                     ("markdown source endpoint", "/docs/_markdown"),
                     ("markdown source asset", "/docs/_markdown/guide.md"),
                     ("search stylesheet asset", "/docs/search.css"),
                     ("search client asset", "/docs/search-client.js"),
                     ("outline client asset", "/docs/outline-client.js"),
                     ("authoring client asset", "/docs/rich-authoring-client.js"),
                     ("minisearch asset", "/docs/minisearch.min.js"),
                     ("versions route", "/docs/versions"),
                     ("route inspector", "/docs/_routes"),
                     ("route inspector JSON", "/docs/_routes.json"),
                     ("section prefix", "/docs/sections/getting-started"),
                     ("version prefix", "/docs/v/1.0/guide")
                 })
        {
            yield return [name, docsIndex, path];
        }

        yield return ["different selected version root", "/foo/bar/next/search-index.json", "/foo/bar/stable/guide"];
    }

    private static async Task<FailureRun> RunFailureAsync(
        string searchIndexRoute,
        string indexBody,
        string? failedTargetRoute = null,
        HttpStatusCode targetStatusCode = HttpStatusCode.NotFound)
    {
        using var handler = CreateHandler(searchIndexRoute, indexBody, failedTargetRoute, targetStatusCode);
        using var client = CreateClient(handler);
        using var output = new TemporaryDirectory();
        var context = CreateContext(output.Path, searchIndexRoute);
        context.EnableReleaseArchiveManifest();

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => CreateEngine(client).RunAsync(context));

        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
        return new FailureRun(exception, handler.RequestedPaths.ToArray());
    }

    private static RecommendationHandler CreateHandler(
        string searchIndexRoute,
        string indexBody,
        string? targetRoute = null,
        HttpStatusCode targetStatusCode = HttpStatusCode.OK)
    {
        return new RecommendationHandler(path =>
        {
            if (path == searchIndexRoute)
            {
                return TextResponse(HttpStatusCode.OK, indexBody, "application/json");
            }

            if (targetRoute is not null && path == targetRoute)
            {
                var isSuccess = (int)targetStatusCode is >= 200 and < 300;
                return TextResponse(
                    targetStatusCode,
                    isSuccess ? "<html><body><h1>Next guide</h1></body></html>" : "target unavailable",
                    isSuccess ? "text/html" : "text/plain");
            }

            return TextResponse(HttpStatusCode.NotFound, "not found", "text/plain");
        });
    }

    private static HttpResponseMessage TextResponse(HttpStatusCode statusCode, string body, string mediaType)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
    }

    private static ExportEngine CreateEngine(HttpClient client)
    {
        var clientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => clientFactory.CreateClient("ExportEngine")).Returns(client);
        return new ExportEngine(A.Fake<ILogger<ExportEngine>>(), clientFactory);
    }

    private static ExportContext CreateContext(string outputPath, string searchIndexRoute)
    {
        return new ExportContext(outputPath, null, [searchIndexRoute], BaseUrl);
    }

    private static string CreateSearchIndex(
        JsonNode? recommendations,
        string searchIndexRoute,
        bool includeRecommendations = true)
    {
        var docsRoot = searchIndexRoute[..^SearchIndexSuffix.Length].TrimEnd('/');
        var sourcePath = docsRoot.Length == 0 ? "/start" : docsRoot + "/start";
        var sourceDocument = new JsonObject
        {
            ["id"] = "source-page",
            ["path"] = sourcePath,
            ["title"] = "Starting point"
        };
        if (includeRecommendations)
        {
            sourceDocument["recommendedPages"] = recommendations?.DeepClone();
        }

        var documents = new JsonArray();
        documents.Add(sourceDocument);
        return new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["generatedAtUtc"] = "2026-10-09T12:00:00.0000000Z",
                ["version"] = "1",
                ["engine"] = "minisearch"
            },
            ["documents"] = documents
        }.ToJsonString();
    }

    private static JsonObject CreateRecommendation(string path, bool authored = false)
    {
        return new JsonObject
        {
            ["targetId"] = "guide-next-id",
            ["path"] = path,
            ["title"] = "Next guide",
            ["summary"] = "A concise preview.",
            ["pageTypeLabel"] = "Guide",
            ["pageTypeVariant"] = "guide",
            ["source"] = authored ? "authored" : "future-source",
            ["primaryReasonCode"] = authored ? "authored" : "future-primary",
            ["reasonCodes"] = authored
                ? new JsonArray(JsonValue.Create("authored"), JsonValue.Create("shared-section"))
                : new JsonArray(JsonValue.Create("future-primary"), JsonValue.Create("future-evidence")),
            ["reasonText"] = authored ? "Selected by the author." : "A future reason caption."
        };
    }

    private static JsonObject CreateMinimalRecommendation(string path, string title = "Next guide")
    {
        return new JsonObject
        {
            ["path"] = path,
            ["title"] = title
        };
    }

    private static JsonArray SingleNode(JsonNode? node)
    {
        var array = new JsonArray();
        array.Add(node);
        return array;
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record FailureRun(ExportValidationException Exception, IReadOnlyList<string> RequestedPaths);

    private sealed class RecommendationHandler(Func<string, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri?.AbsolutePath ?? "/";
            RequestedPaths.Add(path);
            return Task.FromResult(responseFactory(path));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("razorwire-recommendations-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
