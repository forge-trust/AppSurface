using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Docs.Tests;

/// <summary>
/// Compiles and runs a small downstream-consumer example against the published Docs API surface.
/// </summary>
/// <remarks>
/// Although the test project can access internals, the calls in this sample use only public APIs and demonstrate
/// downstream source compatibility without a separate API-baseline tool.
/// </remarks>
public sealed class AppSurfaceDocsVersionAliasPublicApiConsumerTests
{
    [Fact]
    public void PublicCatalogAndAliasApis_ShouldPreserveLegacyConstructionAndExposeAdditiveAliasSurface()
    {
        // Existing four-value construction and deconstruction remain valid to downstream source consumers.
        var legacyCatalog = new AppSurfaceDocsResolvedVersionCatalog(
            AppSurfaceDocsResolvedVersionCatalogStatus.Resolved,
            "catalog.json",
            [],
            null);
        var (status, catalogPath, versions, recommendedVersion) = legacyCatalog;

        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Resolved, status);
        Assert.Equal("catalog.json", catalogPath);
        Assert.Empty(versions);
        Assert.Null(recommendedVersion);
        Assert.Empty(legacyCatalog.Aliases);
        Assert.False(legacyCatalog.IsAliasNamespaceActive);

        // New catalog and resolved-alias APIs are usable entirely through public constructors and properties.
        var descriptor = new AppSurfaceDocsVersionAlias
        {
            Name = " Stable ",
            Version = "1.2.3",
            Label = "Stable",
            Summary = "Current supported release",
            Visibility = AppSurfaceDocsVersionVisibility.Public
        };
        var resolvedTarget = new AppSurfaceDocsResolvedVersion(
            Version: "1.2.3",
            Label: "1.2.3",
            Summary: null,
            ExactTreePath: null,
            ExactRootUrl: "/docs/v/1.2.3",
            SupportState: AppSurfaceDocsVersionSupportState.Current,
            Visibility: AppSurfaceDocsVersionVisibility.Public,
            AdvisoryState: AppSurfaceDocsVersionAdvisoryState.None,
            IsAvailable: true,
            AvailabilityIssue: null);
        var resolvedAlias = new AppSurfaceDocsResolvedVersionAlias
        {
            Name = "stable",
            Label = descriptor.Label,
            Summary = descriptor.Summary,
            Visibility = descriptor.Visibility,
            ConfiguredVersion = descriptor.Version,
            TargetVersion = resolvedTarget,
            RootUrl = "/docs/a/stable",
            IsAvailable = true,
            IsDefinitionValid = true,
            HasExplicitPublicVisibility = true
        };
        var catalog = legacyCatalog with
        {
            Aliases = [resolvedAlias],
            IsAliasNamespaceActive = true
        };

        Assert.True(catalog.IsAliasNamespaceActive);
        Assert.Same(resolvedAlias, Assert.Single(catalog.Aliases));
        Assert.Same(resolvedTarget, resolvedAlias.TargetVersion);
        Assert.Equal("1.2.3", resolvedAlias.ConfiguredVersion);
        Assert.True(resolvedAlias.IsAvailable);
        Assert.True(resolvedAlias.IsDefinitionValid);
        Assert.True(resolvedAlias.HasExplicitPublicVisibility);
    }

    [Theory]
    [InlineData("/docs", "/docs/a/stable", "/docs/a/stable/guide%20one#quick%20start")]
    [InlineData("/guide/product", "/guide/product/a/v2", "/guide/product/a/v2/guide%20one#quick%20start")]
    [InlineData("/", "/a/x", "/a/x/guide%20one#quick%20start")]
    public void AliasUrlBuilder_ShouldBeConsumableForConfiguredRouteFamilies(
        string routeRoot,
        string expectedRoot,
        string expectedDocument)
    {
        var builder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    RouteRootPath = routeRoot,
                    DocsRootPath = routeRoot == "/" ? "/next" : routeRoot + "/next"
                },
                Versioning = new AppSurfaceDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = "catalog.json"
                }
            });
        var aliasName = routeRoot == "/" ? "X" : routeRoot == "/docs" ? " STABLE " : "V2";

        Assert.Equal(expectedRoot, builder.BuildAliasRootUrl(aliasName));
        Assert.Equal(expectedDocument, builder.BuildAliasDocUrl(aliasName, "guide one#quick start"));
    }

    [Fact]
    public async Task NamedAliasQuickstart_ShouldFinalizeTheConfiguredAliasEndpoint()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"appsurface-docs-alias-quickstart-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(fixtureRoot, "source");
        var trustedReleaseRoot = Path.Combine(fixtureRoot, "releases");
        var exactTreeRoot = Path.Combine(trustedReleaseRoot, "1.2.3");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(exactTreeRoot);

        try
        {
            File.WriteAllText(Path.Combine(exactTreeRoot, "index.html"), "<html>Quickstart release</html>");
            File.WriteAllText(Path.Combine(exactTreeRoot, "guide.html"), "<html>Guide</html>");
            File.WriteAllText(Path.Combine(exactTreeRoot, "search.html"), "<html>Search</html>");
            File.WriteAllText(Path.Combine(exactTreeRoot, "search-index.json"), "{\"documents\":[]}");
            File.WriteAllText(Path.Combine(exactTreeRoot, "search.css"), "body { color: black; }");
            File.WriteAllText(Path.Combine(exactTreeRoot, "search-client.js"), "window.searchReady = true;");
            File.WriteAllText(Path.Combine(exactTreeRoot, "minisearch.min.js"), "window.MiniSearch = {};");
            File.WriteAllText(
                Path.Combine(exactTreeRoot, ".appsurface-docs-route-manifest.json"),
                "{\"schema\":\"appsurface-docs-route-manifest-v1\",\"entries\":[{\"sourcePath\":\"guide.md\",\"canonicalRoutePath\":\"guide\",\"recoveryAliases\":[\"guide.md\"],\"declaredAliases\":[]}]}");

            var releaseManifestPath = Path.Combine(exactTreeRoot, ".appsurface-docs-release-manifest.json");
            var files = Directory.EnumerateFiles(exactTreeRoot, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    path = Path.GetRelativePath(exactTreeRoot, path)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/'),
                    length = new FileInfo(path).Length,
                    contentType = (string?)null,
                    hashAlgorithm = "sha256",
                    sha256 = ComputeSha256(path)
                })
                .OrderBy(entry => entry.path, StringComparer.Ordinal)
                .ToArray();
            File.WriteAllText(
                releaseManifestPath,
                JsonSerializer.Serialize(new { schema = "appsurface-docs-release-manifest-v1", files }));

            var catalogPath = Path.Combine(fixtureRoot, "catalog.json");
            File.WriteAllText(
                catalogPath,
                JsonSerializer.Serialize(new
                {
                    recommendedVersion = "1.2.3",
                    versions = new[]
                    {
                        new
                        {
                            version = "1.2.3",
                            exactTreePath = "1.2.3",
                            releaseManifestSha256 = ComputeSha256(releaseManifestPath)
                        }
                    },
                    aliases = new[] { new { name = "stable", version = "1.2.3", visibility = "Public" } }
                }));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AppSurfaceDocs:Public:Source:RepositoryRoot"] = sourceRoot,
                    ["AppSurfaceDocs:Public:Routing:RouteRootPath"] = "/docs",
                    ["AppSurfaceDocs:Public:Routing:DocsRootPath"] = "/docs/next",
                    ["AppSurfaceDocs:Public:Versioning:Enabled"] = "true",
                    ["AppSurfaceDocs:Public:Versioning:CatalogPath"] = catalogPath,
                    ["AppSurfaceDocs:Public:Versioning:TrustedReleaseRootPath"] = trustedReleaseRoot
                })
                .Build();

            await using var app = BuildNamedAliasHost(configuration);
            var aliasEndpoints = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(dataSource => dataSource.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("/a/", StringComparison.Ordinal) == true
                    && endpoint.Metadata.GetMetadata<AppSurfaceDocsEndpointMetadata>()?.Name == "public")
                .ToArray();

            Assert.NotEmpty(aliasEndpoints);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    // docs:snippet docs-alias-named-host:start
    private static WebApplication ConfigureNamedAliasHost(WebApplicationBuilder builder)
    {
        var publicDocs = builder.Services.AddAppSurfaceDocs(
            "public",
            builder.Configuration.GetSection("AppSurfaceDocs:Public"));
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UsePathBase("/products");
        app.UseAppSurfaceDocsAliases();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        IEndpointRouteBuilder endpoints = app;
        endpoints.MapRazorWire();
        publicDocs.MapEndpoints(endpoints).AllowAnonymous();
        endpoints.FinalizeAppSurfaceDocsInstances();

        return app;
    }
    // docs:snippet docs-alias-named-host:end

    private static WebApplication BuildNamedAliasHost(IConfiguration configuration)
    {
        var sourceRoot = configuration["AppSurfaceDocs:Public:Source:RepositoryRoot"]!;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(AppSurfaceDocsWebModule).Assembly.FullName,
            ContentRootPath = Directory.GetParent(sourceRoot)!.FullName
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddConfiguration(configuration);

        return ConfigureNamedAliasHost(builder);
    }

    private static string ComputeSha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
}
