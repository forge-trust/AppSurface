using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli.Tests;

public sealed class ArchiveRouteExportTests
{
    private const string BaseUrl = "http://localhost:5000";
    private const string RouteManifestFileName = ".appsurface-docs-route-manifest.json";
    private const string ReleaseManifestFileName = ".appsurface-docs-release-manifest.json";

    [Theory]
    [InlineData("/", "", "index.html")]
    [InlineData("/docs/guide", "guide", "docs/guide.html")]
    [InlineData("/docs/guide#installation", "guide#installation", "docs/guide.html")]
    [InlineData("/docs/guide#installation?tab=api", "guide#installation?tab=api", "docs/guide.html")]
    [InlineData("/foo/bar/guide", "guide", "foo/bar/guide.html")]
    public async Task RunAsync_FinalizesStagedRoutesFromActualHtmlOutcomesAndPinsFinalBytes(
        string liveUrl,
        string canonicalRoutePath,
        string expectedArtifactPath)
    {
        using var output = new TemporaryDirectory();
        await WriteRouteManifestAsync(
            output.Path,
            new RouteManifestEntry(liveUrl, canonicalRoutePath));
        using var handler = CreateHandler(_ => HtmlResponse());
        using var client = CreateClient(handler);
        var context = CreateContext(output.Path, liveUrl);

        await CreateEngine(client).RunAsync(context);

        var routeManifestPath = TestPathUtils.PathUnder(output.Path, RouteManifestFileName);
        var routeManifestBytes = await File.ReadAllBytesAsync(routeManifestPath);
        using var routeManifest = JsonDocument.Parse(routeManifestBytes);
        var root = routeManifest.RootElement;
        Assert.Equal("export-root", root.GetProperty("htmlUrlBase").GetString());
        var entry = Assert.Single(root.GetProperty("entries").EnumerateArray().ToArray());
        Assert.Equal(expectedArtifactPath, entry.GetProperty("canonicalArtifactPath").GetString());
        Assert.False(entry.TryGetProperty("exportCanonicalLiveUrl", out _));
        Assert.True(File.Exists(TestPathUtils.PathUnder(output.Path, expectedArtifactPath.Split('/'))));

        var releaseManifestPath = TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName);
        var releaseManifestBytes = await File.ReadAllBytesAsync(releaseManifestPath);
        using var releaseManifest = JsonDocument.Parse(releaseManifestBytes);
        var routeManifestFile = Assert.Single(
            releaseManifest.RootElement.GetProperty("files").EnumerateArray(),
            file => file.GetProperty("path").GetString() == RouteManifestFileName);
        Assert.Equal(Hash(routeManifestBytes), routeManifestFile.GetProperty("sha256").GetString());
        Assert.Equal(Hash(releaseManifestBytes), context.ReleaseArchiveManifest!.Sha256);
    }

    [Fact]
    public async Task RunAsync_LeavesLegacyNonStagedRouteManifestBytesUnchanged()
    {
        using var output = new TemporaryDirectory();
        await WriteRouteManifestAsync(
            output.Path,
            new RouteManifestEntry("/docs/guide", "guide", IncludeStagingUrl: false));
        var routeManifestPath = TestPathUtils.PathUnder(output.Path, RouteManifestFileName);
        var originalBytes = await File.ReadAllBytesAsync(routeManifestPath);
        using var handler = CreateHandler(_ => HtmlResponse());
        using var client = CreateClient(handler);
        var context = CreateContext(output.Path, "/docs/guide");

        await CreateEngine(client).RunAsync(context);

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(routeManifestPath));
        using var routeManifest = JsonDocument.Parse(originalBytes);
        Assert.False(routeManifest.RootElement.TryGetProperty("htmlUrlBase", out _));
        using var releaseManifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
        var routeManifestFile = Assert.Single(
            releaseManifest.RootElement.GetProperty("files").EnumerateArray(),
            file => file.GetProperty("path").GetString() == RouteManifestFileName);
        Assert.Equal(Hash(originalBytes), routeManifestFile.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task RunAsync_FailsClosedWhenAnyStagedEntryIsMissingItsLiveUrl()
    {
        using var output = new TemporaryDirectory();
        await WriteRouteManifestAsync(
            output.Path,
            new RouteManifestEntry("/docs/first", "first"),
            new RouteManifestEntry("/docs/second", "second", IncludeStagingUrl: false));
        var routeManifestPath = TestPathUtils.PathUnder(output.Path, RouteManifestFileName);
        var originalBytes = await File.ReadAllBytesAsync(routeManifestPath);
        using var handler = CreateHandler(_ => HtmlResponse());
        using var client = CreateClient(handler);
        var context = CreateContext(output.Path, "/docs/first", "/docs/second");

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => CreateEngine(client).RunAsync(context));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "RWEXPORT012");
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(routeManifestPath));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
    }

    [Fact]
    public async Task RunAsync_FailsClosedWhenStagingValueIsPresentButNull()
    {
        using var output = new TemporaryDirectory();
        await WriteRouteManifestAsync(
            output.Path,
            new RouteManifestEntry("/docs/guide", "guide", StagingUrlIsNull: true));
        var routeManifestPath = TestPathUtils.PathUnder(output.Path, RouteManifestFileName);
        var originalBytes = await File.ReadAllBytesAsync(routeManifestPath);
        using var handler = CreateHandler(_ => HtmlResponse());
        using var client = CreateClient(handler);
        var context = CreateContext(output.Path, "/docs/guide");

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => CreateEngine(client).RunAsync(context));

        Assert.Contains(exception.Diagnostics, diagnostic =>
            diagnostic.Code == "RWEXPORT012"
            && diagnostic.Message.Contains("staging-invalid", StringComparison.Ordinal));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(routeManifestPath));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
    }

    [Fact]
    public async Task RunAsync_RejectsStagedManifestsWithUnknownSchema()
    {
        using var output = new TemporaryDirectory();
        await WriteRouteManifestWithSchemaAsync(
            output.Path,
            "appsurface-docs-route-manifest-v2",
            new RouteManifestEntry("/docs/guide", "guide"));
        var routeManifestPath = TestPathUtils.PathUnder(output.Path, RouteManifestFileName);
        var originalBytes = await File.ReadAllBytesAsync(routeManifestPath);
        using var handler = CreateHandler(_ => HtmlResponse());
        using var client = CreateClient(handler);
        var context = CreateContext(output.Path, "/docs/guide");

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => CreateEngine(client).RunAsync(context));

        Assert.Contains(exception.Diagnostics, diagnostic =>
            diagnostic.Code == "RWEXPORT012"
            && diagnostic.Message.Contains("manifest-schema-unsupported", StringComparison.Ordinal));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(routeManifestPath));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
    }

    [Theory]
    [InlineData("", null, "alias-route-invalid")]
    [InlineData("#section", null, "alias-route-invalid")]
    [InlineData("legacy#one", "legacy#two", "alias-route-collision")]
    public async Task RunAsync_RejectsInvalidOrFragmentDuplicateAliases(
        string firstAlias,
        string? secondAlias,
        string expectedReason)
    {
        using var output = new TemporaryDirectory();
        IReadOnlyList<string> aliases = secondAlias is { } additionalAlias
            ? [firstAlias, additionalAlias]
            : [firstAlias];
        await WriteRouteManifestAsync(
            output.Path,
            new RouteManifestEntry("/docs/guide", "guide", aliases));
        using var handler = CreateHandler(_ => HtmlResponse());
        using var client = CreateClient(handler);
        var context = CreateContext(output.Path, "/docs/guide");

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => CreateEngine(client).RunAsync(context));

        Assert.Contains(exception.Diagnostics, diagnostic =>
            diagnostic.Code == "RWEXPORT012"
            && diagnostic.Message.Contains(expectedReason, StringComparison.Ordinal));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
    }

    [Fact]
    public async Task RunAsync_FailsClosedWhenStagedCanonicalRouteWasNotMaterialized()
    {
        using var output = new TemporaryDirectory();
        await WriteRouteManifestAsync(output.Path, new RouteManifestEntry("/docs/not-seeded", "not-seeded"));
        using var handler = CreateHandler(_ => HtmlResponse());
        using var client = CreateClient(handler);
        var context = CreateContext(output.Path, "/docs/actual");

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => CreateEngine(client).RunAsync(context));

        Assert.Contains(exception.Diagnostics, diagnostic =>
            diagnostic.Code == "RWEXPORT012"
            && diagnostic.Message.Contains("canonical-html-unavailable", StringComparison.Ordinal));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
    }

    [Fact]
    public async Task RunAsync_FailsClosedWhenStagedCanonicalOutcomeIsNotHtml()
    {
        using var output = new TemporaryDirectory();
        await WriteRouteManifestAsync(output.Path, new RouteManifestEntry("/docs/guide.json", "guide"));
        using var handler = CreateHandler(path =>
            path == "/docs/guide.json"
                ? TextResponse(HttpStatusCode.OK, "{}", "application/json")
                : NotFoundResponse());
        using var client = CreateClient(handler);
        var context = CreateContext(output.Path, "/docs/guide.json");

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => CreateEngine(client).RunAsync(context));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "RWEXPORT012");
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
    }

    [Fact]
    public async Task RunAsync_RejectsArtifactPathsWithPercentEncodingAmbiguity()
    {
        const string route = "/docs/guide%25";
        using var output = new TemporaryDirectory();
        await WriteRouteManifestAsync(output.Path, new RouteManifestEntry(route, "guide"));
        using var handler = CreateHandler(_ => HtmlResponse());
        using var client = CreateClient(handler);
        var context = CreateContext(output.Path, route);

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => CreateEngine(client).RunAsync(context));

        Assert.Contains(exception.Diagnostics, diagnostic =>
            diagnostic.Code == "RWEXPORT012"
            && diagnostic.Message.Contains("artifact-path-invalid", StringComparison.Ordinal));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
    }

    [Fact]
    public async Task RunAsync_RejectsAnAliasThatCollidesWithItsOwnCanonicalArtifactPath()
    {
        using var output = new TemporaryDirectory();
        await WriteRouteManifestAsync(
            output.Path,
            new RouteManifestEntry("/docs/guide", "guide", ["docs/guide.html"]));
        using var handler = CreateHandler(_ => HtmlResponse());
        using var client = CreateClient(handler);
        var context = CreateContext(output.Path, "/docs/guide");

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => CreateEngine(client).RunAsync(context));

        Assert.Contains(exception.Diagnostics, diagnostic =>
            diagnostic.Code == "RWEXPORT012"
            && diagnostic.Message.Contains("alias-artifact-collision", StringComparison.Ordinal));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output.Path, ReleaseManifestFileName)));
    }

    private static ExportContext CreateContext(string outputPath, params string[] seedRoutes)
    {
        var context = new ExportContext(outputPath, null, seedRoutes, BaseUrl);
        context.EnableReleaseArchiveManifest();
        return context;
    }

    private static ExportEngine CreateEngine(HttpClient client)
    {
        var clientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => clientFactory.CreateClient("ExportEngine")).Returns(client);
        return new ExportEngine(A.Fake<ILogger<ExportEngine>>(), clientFactory);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
    }

    private static ArchiveRouteHandler CreateHandler(Func<string, HttpResponseMessage> responseFactory)
    {
        return new ArchiveRouteHandler(path =>
            path == "/_appsurface/errors/404" ? NotFoundResponse() : responseFactory(path));
    }

    private static HttpResponseMessage HtmlResponse()
    {
        return TextResponse(HttpStatusCode.OK, "<html><body><h1>Archive page</h1></body></html>", "text/html");
    }

    private static HttpResponseMessage NotFoundResponse()
    {
        return TextResponse(HttpStatusCode.NotFound, "not found", "text/plain");
    }

    private static HttpResponseMessage TextResponse(HttpStatusCode statusCode, string body, string mediaType)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
    }

    private static Task WriteRouteManifestAsync(string outputPath, params RouteManifestEntry[] entries)
    {
        return WriteRouteManifestWithSchemaAsync(
            outputPath,
            "appsurface-docs-route-manifest-v1",
            entries);
    }

    private static async Task WriteRouteManifestWithSchemaAsync(
        string outputPath,
        string schema,
        params RouteManifestEntry[] entries)
    {
        var jsonEntries = new JsonArray();
        foreach (var manifestEntry in entries)
        {
            var sourceRoute = manifestEntry.CanonicalRoutePath.Split('#')[0];
            var entry = new JsonObject
            {
                ["sourcePath"] = sourceRoute.Length == 0
                    ? "README.md"
                    : sourceRoute + ".md",
                ["canonicalRoutePath"] = manifestEntry.CanonicalRoutePath,
                ["recoveryAliases"] = ToJsonArray(manifestEntry.RecoveryAliases ?? []),
                ["declaredAliases"] = ToJsonArray(manifestEntry.DeclaredAliases ?? [])
            };
            if (manifestEntry.IncludeStagingUrl)
            {
                entry["exportCanonicalLiveUrl"] = manifestEntry.StagingUrlIsNull
                    ? null
                    : JsonValue.Create(manifestEntry.CanonicalLiveUrl);
            }

            jsonEntries.Add(entry);
        }

        var manifest = new JsonObject
        {
            ["schema"] = schema,
            ["entries"] = jsonEntries
        };
        await File.WriteAllTextAsync(
            TestPathUtils.PathUnder(outputPath, RouteManifestFileName),
            manifest.ToJsonString());
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record RouteManifestEntry(
        string CanonicalLiveUrl,
        string CanonicalRoutePath,
        IReadOnlyList<string>? RecoveryAliases = null,
        IReadOnlyList<string>? DeclaredAliases = null,
        bool IncludeStagingUrl = true,
        bool StagingUrlIsNull = false);

    private sealed class ArchiveRouteHandler(Func<string, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request.RequestUri?.AbsolutePath ?? "/"));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("razorwire-archive-routes-").FullName;
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
