using System.Net;
using System.Reflection;
using System.Text.Json;
using FakeItEasy;
using ForgeTrust.Runnable.Core;
using ForgeTrust.Runnable.Web;
using ForgeTrust.Runnable.Web.RazorDocs.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

// Regression: ISSUE-001 — standalone host lost RazorDocs search assets after the package split
// Found by /qa on 2026-04-02
// Report: .gstack/qa-reports/qa-report-localhost-2026-04-02.md
[Trait("Category", "Integration")]
public class RazorDocsWebModuleRegressionTests
{
    private const string PackagedAssetBasePath = "/_content/ForgeTrust.Runnable.Web.RazorDocs/docs";
    private const string PackagedStylesheetPath = "/_content/ForgeTrust.Runnable.Web.RazorDocs/css/site.gen.css";
    private const string RootStylesheetPath = "/css/site.gen.css";
    private const string ReferencedRazorWireScriptPath = "/_content/ForgeTrust.Runnable.Web.RazorWire/razorwire/razorwire.js";

    [Fact]
    public void ConfigureWebOptions_Issue001_EnablesStaticWebAssets()
    {
        var module = new RazorDocsWebModule();
        var options = new WebOptions();

        module.ConfigureWebOptions(CreateStartupContext(), options);

        Assert.True(options.StaticFiles.EnableStaticWebAssets);
    }

    [Fact]
    public async Task ConfigureWebOptions_Issue001_ServesLegacySearchCssEndToEnd()
    {
        var module = new RazorDocsWebModule();
        var startup = new TestRazorDocsStartup(module);
        var context = CreatePackagedModuleStartupContext(module);
        var builder = ((IRunnableStartup)startup).CreateHostBuilder(context);

        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var server = host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient
            {
                BaseAddress = new Uri(baseAddress)
            };

            using var response = await client.GetAsync("/docs/search.css");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("/docs/search.css", response.RequestMessage?.RequestUri?.AbsolutePath);
            Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
            Assert.False(string.IsNullOrWhiteSpace(body));
            Assert.Contains("#docs-search-input", body);
            Assert.Contains(".docs-search-page-results", body);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureWebOptions_Issue001_ServesRootStylesheetEndToEnd()
    {
        var module = new RazorDocsWebModule();
        var startup = new TestRazorDocsStartup(module);
        var context = CreatePackagedModuleStartupContext(module);
        var builder = ((IRunnableStartup)startup).CreateHostBuilder(context);

        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var server = host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient
            {
                BaseAddress = new Uri(baseAddress)
            };

            using var response = await client.GetAsync(RootStylesheetPath);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(RootStylesheetPath, response.RequestMessage?.RequestUri?.AbsolutePath);
            Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
            Assert.False(string.IsNullOrWhiteSpace(body));
            Assert.Contains(".docs-content", body);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public Task ConfigureWebOptions_Issue130_UsesProcessEntryAssemblyForHostIdentity_WhenRootModuleAssemblyDiffers()
    {
        var module = new RazorDocsWebModule();
        var startup = new TestRazorDocsStartup(module);
        var context = new StartupContext([], module, "CustomDocsHost");
        var builder = ((IRunnableStartup)startup).CreateHostBuilder(context);
        var expectedHostApplicationName = Assembly.GetEntryAssembly()?.GetName().Name
            ?? typeof(RazorDocsWebModule).Assembly.GetName().Name;

        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();

        var environment = host.Services.GetRequiredService<IHostEnvironment>();

        Assert.Equal(expectedHostApplicationName, environment.ApplicationName);

        if (!string.Equals(expectedHostApplicationName, typeof(RazorDocsWebModule).Assembly.GetName().Name, StringComparison.Ordinal))
        {
            Assert.NotEqual(typeof(RazorDocsWebModule).Assembly.GetName().Name, environment.ApplicationName);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConfigureWebOptions_Issue130_ServesRootStylesheet_WhenApplicationNameIsCustomized()
    {
        var module = new RazorDocsWebModule();
        var startup = new TestRazorDocsStartup(module);
        var hostAssembly = typeof(RazorDocsWebModule).Assembly;
        var context = new StartupContext([], module, "CustomDocsHost")
        {
            OverrideEntryPointAssembly = hostAssembly
        };
        var builder = ((IRunnableStartup)startup).CreateHostBuilder(context);

        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var server = host.Services.GetRequiredService<IServer>();
            var environment = host.Services.GetRequiredService<IHostEnvironment>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient
            {
                BaseAddress = new Uri(baseAddress)
            };

            Assert.Equal(hostAssembly.GetName().Name, environment.ApplicationName);

            using var docsResponse = await client.GetAsync("/docs");
            var html = await docsResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, docsResponse.StatusCode);
            Assert.Contains("href=\"/css/site.gen.css", html);
            Assert.DoesNotContain("/_content/ForgeTrust.Runnable.Web.RazorDocs/css/site.gen.css", html);

            using var stylesheetResponse = await client.GetAsync(RootStylesheetPath);
            var stylesheet = await stylesheetResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, stylesheetResponse.StatusCode);
            Assert.Equal(RootStylesheetPath, stylesheetResponse.RequestMessage?.RequestUri?.AbsolutePath);
            Assert.Equal("text/css", stylesheetResponse.Content.Headers.ContentType?.MediaType);
            Assert.False(string.IsNullOrWhiteSpace(stylesheet));
            Assert.Contains(".docs-content", stylesheet);

            using var referencedAssetResponse = await client.GetAsync(ReferencedRazorWireScriptPath);
            var referencedAsset = await referencedAssetResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, referencedAssetResponse.StatusCode);
            Assert.Equal(ReferencedRazorWireScriptPath, referencedAssetResponse.RequestMessage?.RequestUri?.AbsolutePath);
            Assert.Equal("text/javascript", referencedAssetResponse.Content.Headers.ContentType?.MediaType);
            Assert.False(string.IsNullOrWhiteSpace(referencedAsset));
            Assert.Contains("RazorWire Core Client Runtime", referencedAsset);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureWebApplication_Versioning_ServesRecommendedAliasAndRewritesExactVersionTrees()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "razordocs-published-tree-regression-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
            var publishedTree = CreatePublishedExactTree(tempDirectory, "1.2.3");
            var catalogPath = Path.Combine(tempDirectory, "catalog.json");
            File.WriteAllText(
                catalogPath,
                """
                {
                  "recommendedVersion": "1.2.3",
                  "versions": [
                    {
                      "version": "1.2.3",
                      "label": "1.2.3",
                      "exactTreePath": "1.2.3",
                      "supportState": "Current",
                      "visibility": "Public",
                      "advisoryState": "None"
                    }
                  ]
                }
                """);

            var module = new RazorDocsWebModule();
            var startup = new TestRazorDocsStartup(module);
            var context = CreatePackagedModuleStartupContext(module);
            var builder = ((IRunnableStartup)startup).CreateHostBuilder(context);

            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["RazorDocs:Source:RepositoryRoot"] = repoRoot,
                            ["RazorDocs:Routing:DocsRootPath"] = "/docs/next",
                            ["RazorDocs:Versioning:Enabled"] = "true",
                            ["RazorDocs:Versioning:CatalogPath"] = catalogPath
                        });
                });
            builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

            using (var host = builder.Build())
            {
                await host.StartAsync();

                try
                {
                    var server = host.Services.GetRequiredService<IServer>();
                    var addresses = server.Features.Get<IServerAddressesFeature>();
                    var baseAddress = Assert.Single(addresses!.Addresses);

                    using var client = new HttpClient
                    {
                        BaseAddress = new Uri(baseAddress)
                    };

                    using var docsResponse = await client.GetAsync("/docs");
                    var docsHtml = await docsResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, docsResponse.StatusCode);
                    Assert.Contains("data-tree=\"release-1.2.3\"", docsHtml);
                    Assert.Contains("href=\"/docs/search.css\"", docsHtml);
                    Assert.Contains("\"docsRootPath\":\"/docs\"", docsHtml);

                    using var docsSearchResponse = await client.GetAsync("/docs/search");
                    var docsSearchHtml = await docsSearchResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, docsSearchResponse.StatusCode);
                    Assert.Contains("data-tree=\"release-search\"", docsSearchHtml);

                    using var recommendedAssetResponse = await client.GetAsync("/docs/search-client.js");
                    var recommendedAssetBody = await recommendedAssetResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, recommendedAssetResponse.StatusCode);
                    Assert.Contains("window.__releaseTree = true;", recommendedAssetBody);

                    using var archiveResponse = await client.GetAsync("/docs/versions");
                    var archiveHtml = await archiveResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, archiveResponse.StatusCode);
                    Assert.Contains("Documentation versions", archiveHtml);
                    Assert.DoesNotContain("data-tree=\"release-1.2.3\"", archiveHtml);

                    using var previewResponse = await client.GetAsync("/docs/next");
                    var previewHtml = await previewResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
                    Assert.DoesNotContain("data-tree=\"release-1.2.3\"", previewHtml);

                    using var previewAssetResponse = await client.GetAsync("/docs/next/search-client.js");
                    var previewAssetBody = await previewAssetResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, previewAssetResponse.StatusCode);
                    Assert.DoesNotContain("window.__releaseTree = true;", previewAssetBody);
                    Assert.Contains("const rawConfig = window.__razorDocsConfig || {};", previewAssetBody);

                    using var exactVersionResponse = await client.GetAsync("/docs/v/1.2.3");
                    var exactVersionHtml = await exactVersionResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, exactVersionResponse.StatusCode);
                    Assert.Contains("href=\"/docs/v/1.2.3/search.css\"", exactVersionHtml);
                    Assert.Contains("href=\"/docs/v/1.2.3/guide.html\"", exactVersionHtml);
                    Assert.Contains("href=\"/docs/versions\"", exactVersionHtml);
                    Assert.Contains("\"docsRootPath\":\"/docs/v/1.2.3\"", exactVersionHtml);
                    Assert.Contains("\"docsSearchUrl\":\"/docs/v/1.2.3/search\"", exactVersionHtml);
                    Assert.Contains("\"docsSearchIndexUrl\":\"/docs/v/1.2.3/search-index.json\"", exactVersionHtml);
                    Assert.DoesNotContain("docsVersionsUrl", exactVersionHtml);

                    using var exactSearchResponse = await client.GetAsync("/docs/v/1.2.3/search");
                    var exactSearchHtml = await exactSearchResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, exactSearchResponse.StatusCode);
                    Assert.Contains("data-tree=\"release-search\"", exactSearchHtml);
                    Assert.Contains("href=\"/docs/v/1.2.3/guide.html\"", exactSearchHtml);

                    using var searchIndexResponse = await client.GetAsync("/docs/v/1.2.3/search-index.json");
                    var searchIndexJson = await searchIndexResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, searchIndexResponse.StatusCode);
                    Assert.Contains("\"path\":\"/docs/v/1.2.3/guide.html\"", searchIndexJson);
                }
                finally
                {
                    await host.StopAsync();
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StandaloneBuild_Issue001_IncludesGeneratedStylesheetInRuntimeAndPackManifests()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var buildCoordinates = GetCurrentBuildCoordinates();

        var standaloneRuntimeManifestPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.Runnable.Web.RazorDocs.Standalone",
            "bin",
            buildCoordinates.Configuration,
            buildCoordinates.TargetFramework,
            "ForgeTrust.Runnable.Web.RazorDocs.Standalone.staticwebassets.runtime.json");
        var razorDocsRuntimeManifestPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.Runnable.Web.RazorDocs.Standalone",
            "bin",
            buildCoordinates.Configuration,
            buildCoordinates.TargetFramework,
            "ForgeTrust.Runnable.Web.RazorDocs.staticwebassets.runtime.json");
        var razorDocsPackManifestPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.Runnable.Web.RazorDocs",
            "obj",
            buildCoordinates.Configuration,
            buildCoordinates.TargetFramework,
            "staticwebassets.pack.json");

        Assert.True(
            File.Exists(standaloneRuntimeManifestPath),
            $"Expected the standalone host build to emit '{standaloneRuntimeManifestPath}'.");
        Assert.True(
            File.Exists(razorDocsRuntimeManifestPath),
            $"Expected the standalone host build to emit '{razorDocsRuntimeManifestPath}'.");
        Assert.True(
            File.Exists(razorDocsPackManifestPath),
            $"Expected the RazorDocs package build to emit '{razorDocsPackManifestPath}'.");

        await AssertRuntimeManifestContainsSubPathAsync(standaloneRuntimeManifestPath, "css/site.gen.css");
        await AssertRuntimeManifestContainsSubPathAsync(razorDocsRuntimeManifestPath, "css/site.gen.css");
        await AssertPackManifestContainsPathAsync(razorDocsPackManifestPath, "staticwebassets/css/site.gen.css");
    }

    [Fact]
    public async Task ConfigureEndpoints_Issue001_RedirectsLegacySearchAssetsToPackagedContent()
    {
        var module = new RazorDocsWebModule();
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);

        using var app = builder.Build();
        module.ConfigureEndpoints(context, app);

        await app.StartAsync();

        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress)
            };

            await AssertRedirectAsync(client, "/docs/search.css", $"{PackagedAssetBasePath}/search.css");
            await AssertRedirectAsync(client, "/docs/minisearch.min.js", $"{PackagedAssetBasePath}/minisearch.min.js");
            await AssertRedirectAsync(client, "/docs/search-client.js", $"{PackagedAssetBasePath}/search-client.js");
            await AssertRedirectAsync(client, "/docs/outline-client.js", $"{PackagedAssetBasePath}/outline-client.js");
            await AssertRedirectAsync(client, "/docs/search.css?v=42", $"{PackagedAssetBasePath}/search.css?v=42");
            await AssertRedirectAsync(client, HttpMethod.Head, "/docs/search.css", $"{PackagedAssetBasePath}/search.css");
            await AssertRedirectAsync(client, HttpMethod.Head, "/docs/minisearch.min.js", $"{PackagedAssetBasePath}/minisearch.min.js");
            await AssertRedirectAsync(client, HttpMethod.Head, "/docs/search-client.js", $"{PackagedAssetBasePath}/search-client.js");
            await AssertRedirectAsync(client, HttpMethod.Head, "/docs/outline-client.js", $"{PackagedAssetBasePath}/outline-client.js");
            await AssertRedirectAsync(
                client,
                HttpMethod.Head,
                "/docs/search-client.js?cache=abc",
                $"{PackagedAssetBasePath}/search-client.js?cache=abc");
            await AssertRedirectAsync(
                client,
                HttpMethod.Head,
                "/docs/outline-client.js?cache=abc",
                $"{PackagedAssetBasePath}/outline-client.js?cache=abc");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_Versioning_ServesPreviewSearchAssetsFromLiveWebRoot_WhenRazorDocsIsRootModule()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "razordocs-preview-asset-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDirectory, "docs"));

        try
        {
            File.WriteAllText(Path.Combine(tempDirectory, "docs", "search.css"), "body { background: #111827; }");
            File.WriteAllText(Path.Combine(tempDirectory, "docs", "minisearch.min.js"), "window.MiniSearch = {};");
            File.WriteAllText(Path.Combine(tempDirectory, "docs", "search-client.js"), "window.__previewAsset = true;");
            File.WriteAllText(Path.Combine(tempDirectory, "docs", "outline-client.js"), "window.__outlineAsset = true;");

            var module = new RazorDocsWebModule();
            var context = new StartupContext([], module);
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    WebRootPath = tempDirectory
                });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(
                new RazorDocsOptions
                {
                    Routing = new RazorDocsRoutingOptions
                    {
                        DocsRootPath = "/docs/next"
                    },
                    Versioning = new RazorDocsVersioningOptions
                    {
                        Enabled = true
                    }
                });
            builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);

            using (var app = builder.Build())
            {
                module.ConfigureEndpoints(context, app);

                await app.StartAsync();

                try
                {
                    var server = app.Services.GetRequiredService<IServer>();
                    var addresses = server.Features.Get<IServerAddressesFeature>();
                    var baseAddress = Assert.Single(addresses!.Addresses);

                    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                    {
                        BaseAddress = new Uri(baseAddress)
                    };

                    using var cssResponse = await client.GetAsync("/docs/next/search.css");
                    var cssBody = await cssResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
                    Assert.Contains("background: #111827", cssBody);
                    Assert.Equal("/docs/next/search.css", cssResponse.RequestMessage?.RequestUri?.AbsolutePath);

                    using var jsResponse = await client.GetAsync("/docs/next/search-client.js");
                    var jsBody = await jsResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, jsResponse.StatusCode);
                    Assert.Contains("window.__previewAsset = true;", jsBody);

                    using var outlineResponse = await client.GetAsync("/docs/next/outline-client.js");
                    var outlineBody = await outlineResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, outlineResponse.StatusCode);
                    Assert.Contains("window.__outlineAsset = true;", outlineBody);
                    Assert.Equal("/docs/next/outline-client.js", outlineResponse.RequestMessage?.RequestUri?.AbsolutePath);

                    using var outlineHeadResponse = await client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Head, "/docs/next/outline-client.js?cache=abc"));
                    Assert.Equal(HttpStatusCode.OK, outlineHeadResponse.StatusCode);
                    Assert.Equal("text/javascript", outlineHeadResponse.Content.Headers.ContentType?.MediaType);
                    Assert.Equal("/docs/next/outline-client.js", outlineHeadResponse.RequestMessage?.RequestUri?.AbsolutePath);

                    using var headResponse = await client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Head, "/docs/next/minisearch.min.js?cache=abc"));
                    Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
                    Assert.Equal("text/javascript", headResponse.Content.Headers.ContentType?.MediaType);
                }
                finally
                {
                    await app.StopAsync();
                }
            }
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_Versioning_ReturnsNotFoundForPreviewAssets_WhenWebRootFileProviderIsMissing()
    {
        var module = new RazorDocsWebModule();
        var context = new StartupContext([], module);
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.WebRootFileProvider).Returns(null!);
        builder.Services.AddSingleton(environment);
        builder.Services.AddSingleton(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "/docs/next"
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = true
                }
            });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);

        using var app = builder.Build();
        module.ConfigureEndpoints(context, app);

        await app.StartAsync();

        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress)
            };

            using var response = await client.GetAsync("/docs/next/search.css");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_Versioning_ReturnsNotFoundForPreviewAssets_WhenWebRootAssetFileIsMissing()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "razordocs-preview-asset-missing-file-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDirectory, "docs"));

        try
        {
            var module = new RazorDocsWebModule();
            var context = new StartupContext([], module);
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    WebRootPath = tempDirectory
                });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(
                new RazorDocsOptions
                {
                    Routing = new RazorDocsRoutingOptions
                    {
                        DocsRootPath = "/docs/next"
                    },
                    Versioning = new RazorDocsVersioningOptions
                    {
                        Enabled = true
                    }
                });
            builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);

            using (var app = builder.Build())
            {
                module.ConfigureEndpoints(context, app);

                await app.StartAsync();

                try
                {
                    var server = app.Services.GetRequiredService<IServer>();
                    var addresses = server.Features.Get<IServerAddressesFeature>();
                    var baseAddress = Assert.Single(addresses!.Addresses);

                    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                    {
                        BaseAddress = new Uri(baseAddress)
                    };

                    using var getResponse = await client.GetAsync("/docs/next/search.css");
                    Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);

                    using var headResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/docs/next/search-client.js"));
                    Assert.Equal(HttpStatusCode.NotFound, headResponse.StatusCode);
                }
                finally
                {
                    await app.StopAsync();
                }
            }
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigureWebApplication_Versioning_KeepsPreviewSearchAssetsAvailable_WhenRecommendedReleaseIsUnavailable()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "razordocs-versioning-degraded-asset-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
            var brokenTree = Path.Combine(tempDirectory, "broken");
            Directory.CreateDirectory(brokenTree);
            File.WriteAllText(Path.Combine(brokenTree, "index.html"), "<html>broken</html>");

            var catalogPath = Path.Combine(tempDirectory, "catalog.json");
            File.WriteAllText(
                catalogPath,
                """
                {
                  "recommendedVersion": "2.0.0",
                  "versions": [
                    {
                      "version": "2.0.0",
                      "label": "2.0.0",
                      "exactTreePath": "broken",
                      "supportState": "Current",
                      "visibility": "Public",
                      "advisoryState": "None"
                    }
                  ]
                }
                """);

            var module = new RazorDocsWebModule();
            var startup = new TestRazorDocsStartup(module);
            var context = CreatePackagedModuleStartupContext(module);
            var builder = ((IRunnableStartup)startup).CreateHostBuilder(context);

            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["RazorDocs:Source:RepositoryRoot"] = repoRoot,
                            ["RazorDocs:Routing:DocsRootPath"] = "/docs/preview",
                            ["RazorDocs:Versioning:Enabled"] = "true",
                            ["RazorDocs:Versioning:CatalogPath"] = catalogPath
                        });
                });
            builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

            using (var host = builder.Build())
            {
                await host.StartAsync();

                try
                {
                    var server = host.Services.GetRequiredService<IServer>();
                    var addresses = server.Features.Get<IServerAddressesFeature>();
                    var baseAddress = Assert.Single(addresses!.Addresses);

                    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                    {
                        BaseAddress = new Uri(baseAddress)
                    };

                    using var entryResponse = await client.GetAsync("/docs");
                    var entryHtml = await entryResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, entryResponse.StatusCode);
                    Assert.Contains("No healthy recommended release tree", entryHtml, StringComparison.OrdinalIgnoreCase);

                    using var rootAssetResponse = await client.GetAsync("/docs/search.css");
                    var rootAssetBody = await rootAssetResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, rootAssetResponse.StatusCode);
                    Assert.Equal("/docs/search.css", rootAssetResponse.RequestMessage?.RequestUri?.AbsolutePath);
                    Assert.Contains(".docs-search-page-results", rootAssetBody);

                    using var previewAssetResponse = await client.GetAsync("/docs/preview/search-client.js");
                    var previewAssetBody = await previewAssetResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, previewAssetResponse.StatusCode);
                    Assert.DoesNotContain("window.__releaseTree = true;", previewAssetBody);
                    Assert.Contains("const rawConfig = window.__razorDocsConfig || {};", previewAssetBody);

                    using var previewCssResponse = await client.GetAsync("/docs/preview/search.css");
                    var previewCssBody = await previewCssResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, previewCssResponse.StatusCode);
                    Assert.Contains(".docs-search-page-results", previewCssBody);

                    using var previewMiniSearchResponse = await client.GetAsync("/docs/preview/minisearch.min.js");
                    var previewMiniSearchBody = await previewMiniSearchResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, previewMiniSearchResponse.StatusCode);
                    Assert.DoesNotContain("window.__releaseTree = true;", previewMiniSearchBody);
                    Assert.Contains("MiniSearch", previewMiniSearchBody, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    await host.StopAsync();
                }
            }
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_Issue001_PreservesPathBaseInLegacyAssetRedirects()
    {
        var module = new RazorDocsWebModule();
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);

        using var app = builder.Build();
        app.UsePathBase("/some-base");
        module.ConfigureEndpoints(context, app);

        await app.StartAsync();

        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress)
            };

            await AssertRedirectAsync(
                client,
                "/some-base/docs/search.css?v=42",
                $"/some-base{PackagedAssetBasePath}/search.css?v=42");
            await AssertRedirectAsync(
                client,
                HttpMethod.Head,
                "/some-base/docs/search-client.js?cache=abc",
                $"/some-base{PackagedAssetBasePath}/search-client.js?cache=abc");
            await AssertRedirectAsync(
                client,
                HttpMethod.Head,
                "/some-base/docs/outline-client.js?cache=abc",
                $"/some-base{PackagedAssetBasePath}/outline-client.js?cache=abc");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_Issue001_RedirectsRootStylesheetToPackagedContent_WhenRazorDocsIsRootModule()
    {
        var module = new RazorDocsWebModule();
        var context = new StartupContext([], module);
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);

        using var app = builder.Build();
        module.ConfigureEndpoints(context, app);

        await app.StartAsync();

        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress)
            };

            await AssertRedirectAsync(client, RootStylesheetPath, PackagedStylesheetPath);
            await AssertRedirectAsync(client, $"{RootStylesheetPath}?v=42", $"{PackagedStylesheetPath}?v=42");
            await AssertRedirectAsync(client, HttpMethod.Head, RootStylesheetPath, PackagedStylesheetPath);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_Issue001_DoesNotRedirectRootStylesheet_WhenRazorDocsIsEmbedded()
    {
        var module = new RazorDocsWebModule();
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);

        using var app = builder.Build();
        module.ConfigureEndpoints(context, app);

        await app.StartAsync();

        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress)
            };

            await AssertDoesNotRedirectAsync(client, RootStylesheetPath);
            await AssertDoesNotRedirectAsync(client, HttpMethod.Head, $"{RootStylesheetPath}?v=42");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_Issue001_PreservesPathBaseInRootStylesheetRedirect_WhenRazorDocsIsRootModule()
    {
        var module = new RazorDocsWebModule();
        var context = new StartupContext([], module);
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);

        using var app = builder.Build();
        app.UsePathBase("/some-base");
        module.ConfigureEndpoints(context, app);

        await app.StartAsync();

        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress)
            };

            await AssertRedirectAsync(
                client,
                $"/some-base{RootStylesheetPath}?v=42",
                $"/some-base{PackagedStylesheetPath}?v=42");
            await AssertRedirectAsync(
                client,
                HttpMethod.Head,
                $"/some-base{RootStylesheetPath}?cache=abc",
                $"/some-base{PackagedStylesheetPath}?cache=abc");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task AssertRedirectAsync(HttpClient client, string requestPath, string expectedLocation)
    {
        await AssertRedirectAsync(client, HttpMethod.Get, requestPath, expectedLocation);
    }

    private static async Task AssertRedirectAsync(HttpClient client, HttpMethod method, string requestPath, string expectedLocation)
    {
        using var request = new HttpRequestMessage(method, requestPath);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
    }

    private static async Task AssertDoesNotRedirectAsync(HttpClient client, string requestPath)
    {
        await AssertDoesNotRedirectAsync(client, HttpMethod.Get, requestPath);
    }

    private static async Task AssertDoesNotRedirectAsync(HttpClient client, HttpMethod method, string requestPath)
    {
        using var request = new HttpRequestMessage(method, requestPath);
        using var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    private static async Task AssertRuntimeManifestContainsSubPathAsync(string manifestPath, string expectedSubPath)
    {
        await using var stream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(stream);

        Assert.True(
            JsonContainsPropertyValue(document.RootElement, "SubPath", expectedSubPath),
            $"Expected '{manifestPath}' to contain a SubPath of '{expectedSubPath}'.");
    }

    private static async Task AssertPackManifestContainsPathAsync(string manifestPath, string expectedPackagePath)
    {
        await using var stream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(stream);

        var files = document.RootElement.GetProperty("Files");
        Assert.Contains(
            files.EnumerateArray(),
            file => file.TryGetProperty("PackagePath", out var packagePath)
                && string.Equals(packagePath.GetString(), expectedPackagePath, StringComparison.Ordinal));
    }

    private static bool JsonContainsPropertyValue(JsonElement element, string propertyName, string expectedValue)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.Ordinal)
                        && string.Equals(property.Value.GetString(), expectedValue, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (JsonContainsPropertyValue(property.Value, propertyName, expectedValue))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (JsonContainsPropertyValue(item, propertyName, expectedValue))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static StartupContext CreateStartupContext()
    {
        var rootModule = A.Fake<IRunnableHostModule>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        return new StartupContext(Array.Empty<string>(), rootModule, "TestApp", environmentProvider);
    }

    private static StartupContext CreatePackagedModuleStartupContext(
        RazorDocsWebModule module,
        string? applicationName = null)
    {
        return new StartupContext([], module, applicationName)
        {
            OverrideEntryPointAssembly = typeof(RazorDocsWebModule).Assembly
        };
    }

    private static string CreatePublishedExactTree(string parentDirectory, string version)
    {
        var root = Path.Combine(parentDirectory, version);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "index.html"),
            $$"""
            <!DOCTYPE html>
            <html>
            <head>
              <link rel="stylesheet" href="/docs/search.css" />
              <link rel="preload" href="/docs/search-index.json" as="fetch" crossorigin="use-credentials" />
              <script>window.__razorDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json"};</script>
              <script src="/docs/search-client.js"></script>
              <script src="/docs/outline-client.js"></script>
            </head>
            <body data-tree="release-{{version}}">
              <a id="home" href="/docs">Home</a>
              <a id="guide" href="/docs/guide.html">Guide</a>
              <a id="search" href="/docs/search">Search</a>
              <a id="archive" href="/docs/versions">Archive</a>
            </body>
            </html>
            """);
        File.WriteAllText(
            Path.Combine(root, "search.html"),
            """
            <!DOCTYPE html>
            <html>
            <head>
              <script src="/docs/minisearch.min.js"></script>
              <script>window.__razorDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json"};</script>
            </head>
            <body data-tree="release-search">
              <a href="/docs/guide.html">Guide</a>
            </body>
            </html>
            """);
        File.WriteAllText(Path.Combine(root, "guide.html"), "<!DOCTYPE html><html><body data-tree=\"release-guide\">Guide</body></html>");
        File.WriteAllText(Path.Combine(root, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Combine(root, "search-client.js"), "window.__releaseTree = true;");
        File.WriteAllText(Path.Combine(root, "outline-client.js"), "window.__outlineClientLoaded = true;");
        File.WriteAllText(Path.Combine(root, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");
        File.WriteAllText(Path.Combine(root, "search-index.json"), "{\"documents\":[{\"path\":\"/docs/guide.html\",\"title\":\"Guide\"}]}");
        return root;
    }

    private static BuildCoordinates GetCurrentBuildCoordinates()
    {
        var trimmedBaseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = new DirectoryInfo(trimmedBaseDirectory);
        var segmentsBelowBin = new List<string>();

        while (current is not null && !string.Equals(current.Name, "bin", StringComparison.OrdinalIgnoreCase))
        {
            segmentsBelowBin.Add(current.Name);
            current = current.Parent;
        }

        if (current is null || segmentsBelowBin.Count < 2)
        {
            throw new InvalidOperationException(
                $"Could not determine build configuration and target framework from '{AppContext.BaseDirectory}'.");
        }

        return new BuildCoordinates(
            segmentsBelowBin[^1],
            segmentsBelowBin[^2]);
    }

    private sealed class TestRazorDocsStartup : WebStartup<RazorDocsWebModule>
    {
        private readonly RazorDocsWebModule _module;

        public TestRazorDocsStartup(RazorDocsWebModule module)
        {
            _module = module;
        }

        protected override RazorDocsWebModule CreateRootModule() => _module;
    }

    private sealed record BuildCoordinates(string Configuration, string TargetFramework);
}
