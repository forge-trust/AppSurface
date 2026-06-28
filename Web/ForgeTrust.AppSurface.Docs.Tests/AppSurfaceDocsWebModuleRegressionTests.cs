using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs.Tests;

// Regression: ISSUE-001 — standalone host lost AppSurfaceDocs search assets after the package split
// Found by /qa on 2026-04-02
// Report: .gstack/qa-reports/qa-report-localhost-2026-04-02.md
[Trait("Category", "Integration")]
public class AppSurfaceDocsWebModuleRegressionTests
{
    private const string PackagedAssetBasePath = "/_content/ForgeTrust.AppSurface.Docs/docs";
    private const string PackagedStylesheetPath = "/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css";
    private const string RootFaviconPath = "/favicon.ico";
    private const string RootStylesheetPath = "/css/site.gen.css";
    private const string ReferencedRazorWireScriptPath = "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js";

    [Fact]
    public async Task ConfigureWebOptions_ShouldStart_WhenHarvestFailsAndStrictModeIsDisabled()
    {
        var module = new AppSurfaceDocsWebModule();
        var startup = new TestAppSurfaceDocsStartup(module);
        var context = CreatePackagedModuleStartupContext(module);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        builder.ConfigureServices(
            services =>
            {
                services.RemoveAll<IDocHarvester>();
                services.AddSingleton<IDocHarvester, FailingHarvester>();
            });
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var server = host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            Assert.NotNull(addresses);
            Assert.NotEmpty(addresses!.Addresses);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureWebOptions_ShouldFailStartup_WhenHarvestFailsAndStrictModeIsEnabled()
    {
        var module = new AppSurfaceDocsWebModule();
        var startup = new TestAppSurfaceDocsStartup(module);
        var context = CreatePackagedModuleStartupContext(module);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        builder.ConfigureAppConfiguration(
            (_, configuration) =>
            {
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:FailOnFailure"] = "true"
                    });
            });
        builder.ConfigureServices(
            services =>
            {
                services.RemoveAll<IDocHarvester>();
                services.AddSingleton<IDocHarvester, FailingHarvester>();
            });
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<AppSurfaceDocsHarvestFailedException>(async () => await host.StartAsync());

        Assert.Contains(DocHarvestDiagnosticCodes.HarvesterFailed, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FailingHarvester.RawFailureMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigureWebOptions_Issue001_EnablesStaticWebAssets()
    {
        var module = new AppSurfaceDocsWebModule();
        var options = new WebOptions();

        module.ConfigureWebOptions(CreateStartupContext(), options);

        Assert.True(options.StaticFiles.EnableStaticWebAssets);
    }

    [Fact]
    public async Task ConfigureWebOptions_Issue001_ServesLegacySearchCssEndToEnd()
    {
        var module = new AppSurfaceDocsWebModule();
        var startup = new TestAppSurfaceDocsStartup(module);
        var context = CreatePackagedModuleStartupContext(module);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

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
            Assert.Contains("--docs-search-color-surface-canvas: var(--docs-color-surface-canvas, #050b17);", body);
            Assert.Contains("var(--docs-search-color-border-default)", body);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SearchIndexRefreshEndpoint_ShouldRequirePostAndAntiForgeryToken()
    {
        var module = new AppSurfaceDocsWebModule();
        var startup = new TestAppSurfaceDocsStartup(module);
        var context = CreatePackagedModuleStartupContext(module);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        builder.ConfigureAppConfiguration(
            (_, configuration) =>
            {
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Diagnostics:SearchIndexRefreshPolicy"] = "DocsRefresh"
                    });
            });
        builder.ConfigureServices(
            services =>
            {
                services.AddAuthorization(
                    options =>
                    {
                        options.AddPolicy("DocsRefresh", policy => policy.RequireAuthenticatedUser());
                    });
            });
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();
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

            using var getResponse = await client.GetAsync("/docs/_search-index/refresh");
            using var formContent = new FormUrlEncodedContent([]);
            using var postResponse = await client.PostAsync(
                "/docs/_search-index/refresh",
                formContent);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, getResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, postResponse.StatusCode);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData("/docs/_health")]
    [InlineData("/docs/_health.json")]
    public async Task HarvestHealthAuthorizationPolicy_ShouldProtectDevelopmentDefaultHealthRoutes(string requestPath)
    {
        await using var host = await StartHealthAuthorizationHostAsync(Environments.Development);

        using var anonymous = await host.Client.GetAsync(requestPath);
        using var forbiddenRequest = CreateHealthRequest(requestPath, "alice", "docs.other");
        using var forbidden = await host.Client.SendAsync(forbiddenRequest);
        using var authorizedRequest = CreateHealthRequest(requestPath, "alice", "docs.health.read");
        using var authorized = await host.Client.SendAsync(authorizedRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }

    [Theory]
    [InlineData("/docs/_health")]
    [InlineData("/docs/_health.json")]
    public async Task HarvestHealthAuthorizationPolicy_ShouldProtectProductionHealthRoutes_WhenExplicitlyExposed(string requestPath)
    {
        await using var host = await StartHealthAuthorizationHostAsync(
            Environments.Production,
            exposeHealthRoutes: true);

        using var anonymous = await host.Client.GetAsync(requestPath);
        using var authorizedRequest = CreateHealthRequest(requestPath, "alice", "docs.health.read");
        using var authorized = await host.Client.SendAsync(authorizedRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }

    [Theory]
    [InlineData("/docs/_health", null)]
    [InlineData("/docs/_health", "docs.other")]
    [InlineData("/docs/_health", "docs.health.read")]
    [InlineData("/docs/_health.json", null)]
    [InlineData("/docs/_health.json", "docs.other")]
    [InlineData("/docs/_health.json", "docs.health.read")]
    public async Task HarvestHealthAuthorizationPolicy_ShouldStillReturnNotFound_WhenProductionHealthRoutesAreHidden(
        string requestPath,
        string? scope)
    {
        await using var host = await StartHealthAuthorizationHostAsync(
            Environments.Production,
            exposeHealthRoutes: false);

        using var request = scope is null
            ? new HttpRequestMessage(HttpMethod.Get, requestPath)
            : CreateHealthRequest(requestPath, "alice", scope);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HarvestHealthAuthorizationPolicy_ShouldFailClosed_WhenNamedPolicyIsNotRegistered()
    {
        await using var host = await StartHealthAuthorizationHostAsync(
            Environments.Development,
            registerPolicy: false);

        using var authorizedRequest = CreateHealthRequest("/docs/_health.json", "alice", "docs.health.read");
        using var response = await host.Client.SendAsync(authorizedRequest);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task HarvestHealthAuthorizationPolicy_ShouldFailClosed_WhenAuthorizationMiddlewareIsMissing()
    {
        await using var host = await StartHealthAuthorizationHostAsync(
            Environments.Development,
            registerAuthorizationMiddleware: false);

        using var authorizedRequest = CreateHealthRequest("/docs/_health.json", "alice", "docs.health.read");
        using var response = await host.Client.SendAsync(authorizedRequest);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task ConfigureWebOptions_Issue001_ServesRootStylesheetEndToEnd()
    {
        var module = new AppSurfaceDocsWebModule();
        var startup = new TestAppSurfaceDocsStartup(module);
        var context = CreatePackagedModuleStartupContext(module);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

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
            Assert.Contains("--docs-color-surface-canvas", body);
            Assert.Contains("var(--docs-color-border-default)", body);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public Task ConfigureWebOptions_Issue130_UsesProcessEntryAssemblyForHostIdentity_WhenRootModuleAssemblyDiffers()
    {
        var module = new AppSurfaceDocsWebModule();
        var startup = new TestAppSurfaceDocsStartup(module);
        var context = new StartupContext([], module, "CustomDocsHost");
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        var expectedHostApplicationName = Assembly.GetEntryAssembly()?.GetName().Name
            ?? typeof(AppSurfaceDocsWebModule).Assembly.GetName().Name;

        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();

        var environment = host.Services.GetRequiredService<IHostEnvironment>();

        Assert.Equal(expectedHostApplicationName, environment.ApplicationName);

        if (!string.Equals(expectedHostApplicationName, typeof(AppSurfaceDocsWebModule).Assembly.GetName().Name, StringComparison.Ordinal))
        {
            Assert.NotEqual(typeof(AppSurfaceDocsWebModule).Assembly.GetName().Name, environment.ApplicationName);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConfigureWebOptions_Issue130_ServesRootStylesheet_WhenApplicationNameIsCustomized()
    {
        var module = new AppSurfaceDocsWebModule();
        var startup = new TestAppSurfaceDocsStartup(module);
        var hostAssembly = typeof(AppSurfaceDocsWebModule).Assembly;
        var context = new StartupContext([], module, "CustomDocsHost")
        {
            OverrideEntryPointAssembly = hostAssembly
        };
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

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
            Assert.DoesNotContain("/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css", html);

            using var stylesheetResponse = await client.GetAsync(RootStylesheetPath);
            var stylesheet = await stylesheetResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, stylesheetResponse.StatusCode);
            Assert.Equal(RootStylesheetPath, stylesheetResponse.RequestMessage?.RequestUri?.AbsolutePath);
            Assert.Equal("text/css", stylesheetResponse.Content.Headers.ContentType?.MediaType);
            Assert.False(string.IsNullOrWhiteSpace(stylesheet));
            Assert.Contains(".docs-content", stylesheet);
            Assert.Contains("--docs-color-surface-canvas", stylesheet);
            Assert.Contains("var(--docs-color-border-default)", stylesheet);

            using var referencedAssetResponse = await client.GetAsync(ReferencedRazorWireScriptPath);
            var referencedAsset = await referencedAssetResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, referencedAssetResponse.StatusCode);
            Assert.Equal(ReferencedRazorWireScriptPath, referencedAssetResponse.RequestMessage?.RequestUri?.AbsolutePath);
            Assert.Equal("text/javascript", referencedAssetResponse.Content.Headers.ContentType?.MediaType);
            Assert.False(string.IsNullOrWhiteSpace(referencedAsset));
            Assert.Contains("Generated from assets/src/razorwire.ts", referencedAsset);
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
            "appsurfacedocs-published-tree-regression-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
            var publishedTree = CreatePublishedExactTree(tempDirectory, "1.2.3");
            var releaseManifestSha256 = WriteReleaseManifest(publishedTree);
            var catalogPath = Path.Combine(tempDirectory, "catalog.json");
            File.WriteAllText(
                catalogPath,
                $$"""
                {
                  "recommendedVersion": "1.2.3",
                  "versions": [
                    {
                      "version": "1.2.3",
                      "label": "1.2.3",
                      "exactTreePath": "1.2.3",
                      "releaseManifestSha256": "{{releaseManifestSha256}}",
                      "supportState": "Current",
                      "visibility": "Public",
                      "advisoryState": "None"
                    }
                  ]
                }
                """);

            var module = new AppSurfaceDocsWebModule();
            var startup = new TestAppSurfaceDocsStartup(module);
            var context = CreatePackagedModuleStartupContext(module);
            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSurfaceDocs:Source:RepositoryRoot"] = repoRoot,
                            ["AppSurfaceDocs:Routing:DocsRootPath"] = "/docs/next",
                            ["AppSurfaceDocs:Versioning:Enabled"] = "true",
                            ["AppSurfaceDocs:Versioning:CatalogPath"] = catalogPath
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
                    Assert.Contains("<link rel=\"canonical\" href=\"/docs/v/1.2.3\">", docsHtml);
                    Assert.Contains("href=\"/docs/search.css\"", docsHtml);
                    Assert.Contains("href=\"/docs/guide.html\"", docsHtml);
                    Assert.Contains("\"docsRootPath\":\"/docs\"", docsHtml);

                    using var recommendedGuideResponse = await client.GetAsync("/docs/guide.html");
                    var recommendedGuideHtml = await recommendedGuideResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, recommendedGuideResponse.StatusCode);
                    Assert.Equal("/docs/guide.html", recommendedGuideResponse.RequestMessage?.RequestUri?.AbsolutePath);
                    Assert.Contains("data-tree=\"release-guide\"", recommendedGuideHtml);

                    using var docsSearchResponse = await client.GetAsync("/docs/search");
                    var docsSearchHtml = await docsSearchResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, docsSearchResponse.StatusCode);
                    Assert.Contains("data-tree=\"release-search\"", docsSearchHtml);

                    using var recommendedAssetResponse = await client.GetAsync("/docs/search-client.js");
                    var recommendedAssetBody = await recommendedAssetResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, recommendedAssetResponse.StatusCode);
                    Assert.Contains("window.__releaseTree = true;", recommendedAssetBody);

                    using var recommendedOutlineResponse = await client.GetAsync("/docs/outline-client.js");
                    var recommendedOutlineBody = await recommendedOutlineResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, recommendedOutlineResponse.StatusCode);
                    Assert.Contains("window.__outlineClientLoaded = true;", recommendedOutlineBody);

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
                    Assert.Contains("Generated from assets/src/search-client.ts", previewAssetBody);
                    Assert.Contains("__appSurfaceDocsConfig", previewAssetBody);

                    using var exactVersionResponse = await client.GetAsync("/docs/v/1.2.3");
                    var exactVersionHtml = await exactVersionResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, exactVersionResponse.StatusCode);
                    Assert.Contains("<link rel=\"canonical\" href=\"/docs/v/1.2.3\">", exactVersionHtml);
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

                    using var redirectClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                    {
                        BaseAddress = new Uri(baseAddress)
                    };
                    using var exactAliasResponse = await redirectClient.GetAsync("/docs/v/1.2.3/guide.md?view=compact");
                    Assert.Equal(HttpStatusCode.MovedPermanently, exactAliasResponse.StatusCode);
                    Assert.Equal("/docs/v/1.2.3/guide?view=compact", exactAliasResponse.Headers.Location?.ToString());

                    using var recommendedAliasResponse = await redirectClient.GetAsync("/docs/guide.md");
                    Assert.Equal(HttpStatusCode.MovedPermanently, recommendedAliasResponse.StatusCode);
                    Assert.Equal("/docs/guide", recommendedAliasResponse.Headers.Location?.ToString());

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
            "ForgeTrust.AppSurface.Docs.Standalone",
            "bin",
            buildCoordinates.Configuration,
            buildCoordinates.TargetFramework,
            "ForgeTrust.AppSurface.Docs.Standalone.staticwebassets.runtime.json");
        var razorDocsRuntimeManifestPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs.Standalone",
            "bin",
            buildCoordinates.Configuration,
            buildCoordinates.TargetFramework,
            "ForgeTrust.AppSurface.Docs.staticwebassets.runtime.json");
        var razorDocsPackManifestPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs",
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
            $"Expected the AppSurface Docs package build to emit '{razorDocsPackManifestPath}'.");

        await AssertRuntimeManifestContainsSubPathAsync(standaloneRuntimeManifestPath, "css/site.gen.css");
        await AssertRuntimeManifestContainsSubPathAsync(razorDocsRuntimeManifestPath, "css/site.gen.css");
        await AssertPackManifestContainsPathAsync(razorDocsPackManifestPath, "staticwebassets/css/site.gen.css");
    }

    [Fact]
    public async Task ConfigureEndpoints_Issue001_RedirectsLegacySearchAssetsToPackagedContent()
    {
        var module = new AppSurfaceDocsWebModule();
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
    public async Task ConfigureEndpoints_Issue007_PreservesHostileLookingQueryAsQueryDataInLocalAssetRedirect()
    {
        var module = new AppSurfaceDocsWebModule();
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

            await AssertRedirectAsync(
                client,
                "/docs/search.css?next=https://evil.example/login",
                $"{PackagedAssetBasePath}/search.css?next=https://evil.example/login");
            await AssertRedirectAsync(
                client,
                HttpMethod.Head,
                "/docs/search-client.js?next=//evil.example/login",
                $"{PackagedAssetBasePath}/search-client.js?next=//evil.example/login");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("https://evil.example/asset.css")]
    [InlineData("//evil.example/asset.css")]
    [InlineData("/\\evil.example/asset.css")]
    [InlineData("/safe\\asset.css")]
    public void BuildLegacyAssetRedirectPath_Issue007_RejectsUnsafeTargetPath(string targetPath)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsWebModule.BuildLegacyAssetRedirectPath(
                PathString.Empty,
                targetPath,
                QueryString.Empty));

        Assert.Contains("redirect target", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(targetPath, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/safe\\base")]
    [InlineData("/safe\u001fbase")]
    public void BuildLegacyAssetRedirectPath_Issue007_RejectsUnsafePathBase(string pathBase)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsWebModule.BuildLegacyAssetRedirectPath(
                new PathString(pathBase),
                $"{PackagedAssetBasePath}/search.css",
                QueryString.Empty));

        Assert.Contains("path base", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(pathBase, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/")]
    public void BuildLegacyAssetRedirectPath_Issue007_TreatsEmptyOrRootPathBaseAsEmpty(string? pathBase)
    {
        var redirectPath = AppSurfaceDocsWebModule.BuildLegacyAssetRedirectPath(
            pathBase is null ? default : new PathString(pathBase),
            $"{PackagedAssetBasePath}/search.css",
            QueryString.Empty);

        Assert.Equal($"{PackagedAssetBasePath}/search.css", redirectPath);
    }

    [Theory]
    [InlineData("/docs preview", "/docs%20preview/_content/ForgeTrust.AppSurface.Docs/docs/search.css?v=42")]
    [InlineData("/docs/éclair", "/docs/%C3%A9clair/_content/ForgeTrust.AppSurface.Docs/docs/search.css?v=42")]
    public void BuildLegacyAssetRedirectPath_Issue007_EscapesSafePathBaseForRedirectHeader(
        string pathBase,
        string expectedRedirectPath)
    {
        var redirectPath = AppSurfaceDocsWebModule.BuildLegacyAssetRedirectPath(
            new PathString(pathBase),
            $"{PackagedAssetBasePath}/search.css",
            new QueryString("?v=42"));

        Assert.Equal(expectedRedirectPath, redirectPath);
    }

    [Fact]
    public async Task ConfigureEndpoints_Versioning_ServesPreviewSearchAssetsFromLiveWebRoot_WhenAppSurfaceDocsIsRootModule()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "appsurfacedocs-preview-asset-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDirectory, "docs"));

        try
        {
            File.WriteAllText(Path.Combine(tempDirectory, "docs", "search.css"), "body { background: #111827; }");
            File.WriteAllText(Path.Combine(tempDirectory, "docs", "minisearch.min.js"), "window.MiniSearch = {};");
            File.WriteAllText(Path.Combine(tempDirectory, "docs", "search-client.js"), "window.__previewAsset = true;");
            File.WriteAllText(Path.Combine(tempDirectory, "docs", "outline-client.js"), "window.__outlineAsset = true;");

            var module = new AppSurfaceDocsWebModule();
            var context = new StartupContext([], module);
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    WebRootPath = tempDirectory
                });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(
                new AppSurfaceDocsOptions
                {
                    Routing = new AppSurfaceDocsRoutingOptions
                    {
                        DocsRootPath = "/docs/next"
                    },
                    Versioning = new AppSurfaceDocsVersioningOptions
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
        var module = new AppSurfaceDocsWebModule();
        var context = new StartupContext([], module);
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.WebRootFileProvider).Returns(null!);
        builder.Services.AddSingleton(environment);
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "/docs/next"
                },
                Versioning = new AppSurfaceDocsVersioningOptions
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
    public async Task ConfigureEndpoints_Versioning_ServesEmbeddedPreviewAssets_WhenWebRootAssetFileIsMissing()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "appsurfacedocs-preview-asset-missing-file-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDirectory, "docs"));

        try
        {
            var module = new AppSurfaceDocsWebModule();
            var context = new StartupContext([], module);
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    WebRootPath = tempDirectory
                });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(
                new AppSurfaceDocsOptions
                {
                    Routing = new AppSurfaceDocsRoutingOptions
                    {
                        DocsRootPath = "/docs/next"
                    },
                    Versioning = new AppSurfaceDocsVersioningOptions
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
                    Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
                    Assert.Equal("text/css", getResponse.Content.Headers.ContentType?.MediaType);
                    Assert.Contains(
                        "docs-search",
                        await getResponse.Content.ReadAsStringAsync(),
                        StringComparison.Ordinal);

                    using var headResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/docs/next/search-client.js"));
                    Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
                    Assert.Equal("text/javascript", headResponse.Content.Headers.ContentType?.MediaType);
                    Assert.True(headResponse.Content.Headers.ContentLength > 0);
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
            "appsurfacedocs-versioning-degraded-asset-tests",
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

            var module = new AppSurfaceDocsWebModule();
            var startup = new TestAppSurfaceDocsStartup(module);
            var context = CreatePackagedModuleStartupContext(module);
            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSurfaceDocs:Source:RepositoryRoot"] = repoRoot,
                            ["AppSurfaceDocs:Routing:DocsRootPath"] = "/docs/preview",
                            ["AppSurfaceDocs:Versioning:Enabled"] = "true",
                            ["AppSurfaceDocs:Versioning:CatalogPath"] = catalogPath
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
                    Assert.DoesNotContain("rel=\"canonical\"", entryHtml, StringComparison.OrdinalIgnoreCase);

                    using var rootAssetResponse = await client.GetAsync("/docs/search.css");
                    var rootAssetBody = await rootAssetResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, rootAssetResponse.StatusCode);
                    Assert.Equal("/docs/search.css", rootAssetResponse.RequestMessage?.RequestUri?.AbsolutePath);
                    Assert.Contains(".docs-search-page-results", rootAssetBody);

                    using var previewAssetResponse = await client.GetAsync("/docs/preview/search-client.js");
                    var previewAssetBody = await previewAssetResponse.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, previewAssetResponse.StatusCode);
                    Assert.DoesNotContain("window.__releaseTree = true;", previewAssetBody);
                    Assert.Contains("Generated from assets/src/search-client.ts", previewAssetBody);
                    Assert.Contains("__appSurfaceDocsConfig", previewAssetBody);

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
        var module = new AppSurfaceDocsWebModule();
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
    public async Task ConfigureEndpoints_Issue001_RedirectsRootStylesheetToPackagedContent_WhenAppSurfaceDocsIsRootModule()
    {
        var module = new AppSurfaceDocsWebModule();
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
    public async Task ConfigureEndpoints_ShouldServePackagedAssetsFromEmbeddedResources_WhenStaticWebAssetsAreUnavailable()
    {
        var module = new AppSurfaceDocsWebModule();
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

            using var stylesheetResponse = await client.GetAsync(PackagedStylesheetPath);
            Assert.Equal(HttpStatusCode.OK, stylesheetResponse.StatusCode);
            Assert.Equal("text/css", stylesheetResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "--docs",
                await stylesheetResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var searchClientResponse = await client.GetAsync($"{PackagedAssetBasePath}/search-client.js");
            Assert.Equal(HttpStatusCode.OK, searchClientResponse.StatusCode);
            Assert.Contains(
                "appSurfaceDocsConfig",
                await searchClientResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            // Regression: ISSUE-001 — packaged appsurface docs rendered a broken brand image.
            // Found by /qa on 2026-05-19.
            // Report: .gstack/qa-reports/qa-report-appsurface-docs-2026-05-19.md
            using var brandIconResponse = await client.GetAsync($"{PackagedAssetBasePath}/appsurface-docs-icon.svg");
            Assert.Equal(HttpStatusCode.OK, brandIconResponse.StatusCode);
            Assert.Equal("image/svg+xml", brandIconResponse.Content.Headers.ContentType?.MediaType);
            var brandIcon = await brandIconResponse.Content.ReadAsStringAsync();
            Assert.Contains(
                "<svg",
                brandIcon,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "AppSurface layered document mark",
                brandIcon,
                StringComparison.Ordinal);

            using var packagedSearchCssResponse = await client.GetAsync($"{PackagedAssetBasePath}/search.css");
            Assert.Equal(HttpStatusCode.OK, packagedSearchCssResponse.StatusCode);
            Assert.Equal("text/css", packagedSearchCssResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "--docs-search",
                await packagedSearchCssResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var miniSearchResponse = await client.GetAsync($"{PackagedAssetBasePath}/minisearch.min.js");
            Assert.Equal(HttpStatusCode.OK, miniSearchResponse.StatusCode);
            Assert.Equal("text/javascript", miniSearchResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "MiniSearch",
                await miniSearchResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var outlineHeadRequest = new HttpRequestMessage(HttpMethod.Head, $"{PackagedAssetBasePath}/outline-client.js");
            using var outlineHeadResponse = await client.SendAsync(outlineHeadRequest);
            Assert.Equal(HttpStatusCode.OK, outlineHeadResponse.StatusCode);
            Assert.Equal("text/javascript", outlineHeadResponse.Content.Headers.ContentType?.MediaType);
            Assert.True(outlineHeadResponse.Content.Headers.ContentLength > 0);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_ShouldServeRootFaviconAsPackagedDocumentLayersSvg_WhenAppSurfaceDocsIsRootModule()
    {
        var module = new AppSurfaceDocsWebModule();
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

            using var response = await client.GetAsync(RootFaviconPath);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
            var favicon = await response.Content.ReadAsStringAsync();
            Assert.Contains(
                "AppSurface layered document mark",
                favicon,
                StringComparison.Ordinal);
            Assert.Contains(
                "Four rounded isometric document layers in navy, blue, teal, and violet with document lines and a folded corner",
                favicon,
                StringComparison.Ordinal);

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, RootFaviconPath);
            using var headResponse = await client.SendAsync(headRequest);
            Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
            Assert.Equal("image/svg+xml", headResponse.Content.Headers.ContentType?.MediaType);
            Assert.True(headResponse.Content.Headers.ContentLength > 0);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("/branding/appsurface-site-icon.svg", "/branding/appsurface-site-icon.svg")]
    [InlineData("~/branding/appsurface-site-icon.svg", "/branding/appsurface-site-icon.svg")]
    public async Task ConfigureEndpoints_ShouldRedirectRootFaviconToConfiguredSvg_WhenRootModuleOverridesFavicon(
        string configuredPath,
        string expectedPath)
    {
        var module = new AppSurfaceDocsWebModule();
        var context = new StartupContext([], module);
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    Favicon = new AppSurfaceDocsFaviconOptions
                    {
                        SvgPath = configuredPath
                    }
                }
            });

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

            await AssertRedirectAsync(client, RootFaviconPath, expectedPath);
            await AssertRedirectAsync(client, HttpMethod.Head, RootFaviconPath, expectedPath);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_ShouldServePackagedRootFavicon_WhenConfiguredSvgPointsToRootFavicon()
    {
        var module = new AppSurfaceDocsWebModule();
        var context = new StartupContext([], module);
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    Favicon = new AppSurfaceDocsFaviconOptions
                    {
                        SvgPath = RootFaviconPath
                    }
                }
            });

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

            using var response = await client.GetAsync(RootFaviconPath);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_ShouldServeConfiguredBrandingAssetsFromContentRootDefaultPath()
    {
        var module = new AppSurfaceDocsWebModule();
        var context = new StartupContext([], module);
        var tempDirectoryName = $"{nameof(ConfigureEndpoints_ShouldServeConfiguredBrandingAssetsFromContentRootDefaultPath)}-{Guid.NewGuid():N}";
        var tempDirectory = Path.Join(Path.GetTempPath(), Path.GetFileName(tempDirectoryName));
        var brandingDirectory = Path.Join(tempDirectory, "branding");
        Directory.CreateDirectory(brandingDirectory);
        File.WriteAllText(Path.Join(brandingDirectory, "favicon.png"), "Default branding path favicon");
        File.WriteAllText(Path.Join(brandingDirectory, "favicon.svg"), "<svg><title>Denied favicon</title></svg>");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = tempDirectory });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions
                    {
                        DirectoryPath = "branding"
                    }
                }
            });

        using var app = builder.Build();

        try
        {
            module.ConfigureEndpoints(context, app);
            await app.StartAsync();

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress)
            };

            using var response = await client.GetAsync("/branding/favicon.png");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Default branding path favicon", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var svgResponse = await client.GetAsync("/branding/favicon.svg");
            Assert.Equal(HttpStatusCode.NotFound, svgResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ConfigureEndpoints_ShouldRejectMissingBrandingAssetsDirectory()
    {
        var module = new AppSurfaceDocsWebModule();
        var context = new StartupContext([], module);
        var missingDirectory = Path.Join(
            Path.GetTempPath(),
            Path.GetFileName($"{nameof(ConfigureEndpoints_ShouldRejectMissingBrandingAssetsDirectory)}-{Guid.NewGuid():N}"));
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions
                    {
                        DirectoryPath = missingDirectory
                    }
                }
            });

        using var app = builder.Build();

        var ex = Assert.Throws<DirectoryNotFoundException>(() => module.ConfigureEndpoints(context, app));
        Assert.Contains("AppSurfaceDocs:Identity:BrandingAssets:DirectoryPath", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("branding")]
    [InlineData("/")]
    public async Task ConfigureEndpoints_ShouldSkipBrandingAssets_WhenRequestPathIsNotDedicatedBrowserRoot(string requestPath)
    {
        var module = new AppSurfaceDocsWebModule();
        var context = new StartupContext([], module);
        var tempDirectoryName = $"{nameof(ConfigureEndpoints_ShouldSkipBrandingAssets_WhenRequestPathIsNotDedicatedBrowserRoot)}-{Guid.NewGuid():N}";
        var tempDirectory = Path.Join(Path.GetTempPath(), Path.GetFileName(tempDirectoryName));
        var brandingDirectory = Path.Join(tempDirectory, "branding");
        Directory.CreateDirectory(brandingDirectory);
        File.WriteAllText(
            Path.Join(brandingDirectory, "favicon.svg"),
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">
              <title>Skipped branding path favicon</title>
              <rect width="16" height="16" fill="#123456" />
            </svg>
            """);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions
                    {
                        DirectoryPath = brandingDirectory,
                        RequestPath = requestPath
                    }
                }
            });

        using var app = builder.Build();

        try
        {
            module.ConfigureEndpoints(context, app);
            await app.StartAsync();

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress)
            };

            using var response = await client.GetAsync("/branding/favicon.svg");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_ShouldServeConfiguredBrandingAssetsFromRepositoryRootDirectory()
    {
        var module = new AppSurfaceDocsWebModule();
        var context = new StartupContext([], module);
        var tempDirectoryName = $"{nameof(ConfigureEndpoints_ShouldServeConfiguredBrandingAssetsFromRepositoryRootDirectory)}-{Guid.NewGuid():N}";
        var tempDirectory = Path.Join(Path.GetTempPath(), Path.GetFileName(tempDirectoryName));
        var brandingDirectory = Path.Join(tempDirectory, "branding");
        Directory.CreateDirectory(brandingDirectory);
        File.WriteAllText(Path.Join(brandingDirectory, "favicon.png"), "Consumer favicon");
        File.WriteAllText(Path.Join(brandingDirectory, "favicon.svg"), "<svg><title>Denied consumer favicon</title></svg>");
        File.WriteAllText(Path.Join(brandingDirectory, "notes.txt"), "not a public brand asset");
        File.WriteAllText(Path.Join(tempDirectory, "secret.png"), "Secret");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Source = new AppSurfaceDocsSourceOptions
                {
                    RepositoryRoot = tempDirectory
                },
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions
                    {
                        DirectoryPath = "branding",
                        RequestPath = "~/brand"
                    },
                    Favicon = new AppSurfaceDocsFaviconOptions
                    {
                        PngPath = "/brand/favicon.png"
                    }
                }
            });

        using var app = builder.Build();

        try
        {
            module.ConfigureEndpoints(context, app);
            await app.StartAsync();

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress)
            };

            using var response = await client.GetAsync("/brand/favicon.png");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Consumer favicon", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/brand/favicon.png");
            using var headResponse = await client.SendAsync(headRequest);
            Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
            Assert.Equal("image/png", headResponse.Content.Headers.ContentType?.MediaType);
            Assert.True(headResponse.Content.Headers.ContentLength > 0);

            using var svgResponse = await client.GetAsync("/brand/favicon.svg");
            Assert.Equal(HttpStatusCode.NotFound, svgResponse.StatusCode);

            using var traversalResponse = await client.GetAsync("/brand/%2e%2e/secret.png");
            Assert.Equal(HttpStatusCode.NotFound, traversalResponse.StatusCode);

            using var unsupportedFileResponse = await client.GetAsync("/brand/notes.txt");
            Assert.Equal(HttpStatusCode.NotFound, unsupportedFileResponse.StatusCode);

            using var emptyAssetResponse = await client.GetAsync("/brand/");
            Assert.Equal(HttpStatusCode.NotFound, emptyAssetResponse.StatusCode);

            using var backslashAssetResponse = await client.GetAsync("/brand/%5Csecret.png");
            Assert.Equal(HttpStatusCode.NotFound, backslashAssetResponse.StatusCode);

            using var rootedAssetResponse = await client.GetAsync("/brand/%2Fsecret.png");
            Assert.Equal(HttpStatusCode.NotFound, rootedAssetResponse.StatusCode);

            using var controlCharacterAssetResponse = await client.GetAsync("/brand/%01secret.png");
            Assert.Equal(HttpStatusCode.NotFound, controlCharacterAssetResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_ShouldServeConfiguredSvgBrandingAssets_WhenExplicitlyAllowed()
    {
        var module = new AppSurfaceDocsWebModule();
        var context = new StartupContext([], module);
        var tempDirectoryName = $"{nameof(ConfigureEndpoints_ShouldServeConfiguredSvgBrandingAssets_WhenExplicitlyAllowed)}-{Guid.NewGuid():N}";
        var tempDirectory = Path.Join(Path.GetTempPath(), Path.GetFileName(tempDirectoryName));
        var brandingDirectory = Path.Join(tempDirectory, "branding");
        Directory.CreateDirectory(brandingDirectory);
        File.WriteAllText(
            Path.Join(brandingDirectory, "favicon.svg"),
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">
              <title>Trusted consumer favicon</title>
              <rect width="16" height="16" fill="#123456" />
            </svg>
            """);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions
                    {
                        DirectoryPath = brandingDirectory,
                        AllowSvgAssets = true
                    }
                }
            });

        using var app = builder.Build();

        try
        {
            module.ConfigureEndpoints(context, app);
            await app.StartAsync();

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress)
            };

            using var response = await client.GetAsync("/branding/favicon.svg");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Trusted consumer favicon", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/branding/favicon.svg");
            using var headResponse = await client.SendAsync(headRequest);
            Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
            Assert.Equal("image/svg+xml", headResponse.Content.Headers.ContentType?.MediaType);
            Assert.True(headResponse.Content.Headers.ContentLength > 0);
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(BrandingAssetPathCases))]
    public void TryResolveSafeBrandingAssetPath_ShouldAcceptOnlyRelativeBrandImageAssets(
        object? routeValue,
        bool allowSvgAssets,
        bool expectedResult,
        string expectedAssetPath)
    {
        var result = AppSurfaceDocsWebModule.TryResolveSafeBrandingAssetPath(routeValue, allowSvgAssets, out var assetPath);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedAssetPath, assetPath);
    }

    public static TheoryData<object?, bool, bool, string> BrandingAssetPathCases()
    {
        return new TheoryData<object?, bool, bool, string>
        {
            { null, false, false, string.Empty },
            { string.Empty, false, false, string.Empty },
            { "/secret.png", false, false, string.Empty },
            { "nested\\secret.png", false, false, string.Empty },
            { "bad%zz.png", false, false, string.Empty },
            { "bad%.png", false, false, string.Empty },
            { "bad\u0001.png", false, false, string.Empty },
            { "../secret.png", false, false, string.Empty },
            { "notes.txt", false, false, string.Empty },
            { "literal%25.png", false, true, "literal%.png" },
            { "favicon.png", false, true, "favicon.png" },
            { "favicon.SVG", false, false, string.Empty },
            { "favicon.SVG", true, true, "favicon.SVG" },
            { "nested/favicon.svg", false, false, string.Empty },
            { "nested/favicon.svg", true, true, "nested/favicon.svg" }
        };
    }

    [Fact]
    public void ResolveBrandingAssetsDirectoryPath_ShouldReturnNull_WhenBrandingDirectoryIsNotConfigured()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        Assert.Null(
            AppSurfaceDocsWebModule.ResolveBrandingAssetsDirectoryPath(
                provider,
                new AppSurfaceDocsOptions { Identity = null! }));
        Assert.Null(
            AppSurfaceDocsWebModule.ResolveBrandingAssetsDirectoryPath(
                provider,
                new AppSurfaceDocsOptions
                {
                    Identity = new AppSurfaceDocsIdentityOptions { BrandingAssets = null! }
                }));
    }

    [Fact]
    public void ResolveBrandingAssetsDirectoryPath_ShouldResolveConfiguredDirectoryAgainstExpectedBase()
    {
        using var noEnvironmentProvider = new ServiceCollection().BuildServiceProvider();
        var currentDirectoryExpectedPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath("branding", Directory.GetCurrentDirectory()));
        var currentDirectoryResult = AppSurfaceDocsWebModule.ResolveBrandingAssetsDirectoryPath(
            noEnvironmentProvider,
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions { DirectoryPath = "branding" }
                }
            });

        Assert.Equal(currentDirectoryExpectedPath, currentDirectoryResult);

        var tempDirectoryName = $"{nameof(ResolveBrandingAssetsDirectoryPath_ShouldResolveConfiguredDirectoryAgainstExpectedBase)}-{Guid.NewGuid():N}";
        var contentRoot = Path.Join(Path.GetTempPath(), Path.GetFileName(tempDirectoryName));
        var absoluteRepositoryRoot = Path.Join(contentRoot, "absolute-repo");
        var absoluteBrandingDirectory = Path.Join(contentRoot, "absolute-branding");
        Directory.CreateDirectory(contentRoot);

        try
        {
            using var provider = CreateServiceProviderWithContentRoot(contentRoot);

            var missingSourceResult = AppSurfaceDocsWebModule.ResolveBrandingAssetsDirectoryPath(
                provider,
                new AppSurfaceDocsOptions
                {
                    Source = null!,
                    Identity = new AppSurfaceDocsIdentityOptions
                    {
                        BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions { DirectoryPath = "branding" }
                    }
                });
            Assert.Equal(Path.GetFullPath(Path.Join(contentRoot, "branding")), missingSourceResult);

            var absoluteDirectoryResult = AppSurfaceDocsWebModule.ResolveBrandingAssetsDirectoryPath(
                provider,
                new AppSurfaceDocsOptions
                {
                    Identity = new AppSurfaceDocsIdentityOptions
                    {
                        BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions
                        {
                            DirectoryPath = absoluteBrandingDirectory + Path.DirectorySeparatorChar
                        }
                    }
                });
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(absoluteBrandingDirectory)), absoluteDirectoryResult);

            var absoluteRepositoryResult = AppSurfaceDocsWebModule.ResolveBrandingAssetsDirectoryPath(
                provider,
                new AppSurfaceDocsOptions
                {
                    Source = new AppSurfaceDocsSourceOptions { RepositoryRoot = absoluteRepositoryRoot },
                    Identity = new AppSurfaceDocsIdentityOptions
                    {
                        BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions { DirectoryPath = "branding" }
                    }
                });
            Assert.Equal(Path.GetFullPath(Path.Join(absoluteRepositoryRoot, "branding")), absoluteRepositoryResult);

            var relativeRepositoryResult = AppSurfaceDocsWebModule.ResolveBrandingAssetsDirectoryPath(
                provider,
                new AppSurfaceDocsOptions
                {
                    Source = new AppSurfaceDocsSourceOptions { RepositoryRoot = "relative-repo" },
                    Identity = new AppSurfaceDocsIdentityOptions
                    {
                        BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions { DirectoryPath = "branding" }
                    }
                });
            Assert.Equal(Path.GetFullPath(Path.Join(contentRoot, "relative-repo", "branding")), relativeRepositoryResult);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, "/branding")]
    [InlineData("/brand/", "/brand")]
    [InlineData("~/brand", "/brand")]
    [InlineData("branding", null)]
    [InlineData("/", null)]
    public void ResolveBrandingAssetsRequestPath_ShouldNormalizeConfiguredDedicatedBrowserRoot(
        string? configuredRequestPath,
        string? expectedRequestPath)
    {
        if (configuredRequestPath is null)
        {
            Assert.Equal(
                AppSurfaceDocsBrandingAssetsOptions.DefaultRequestPath,
                AppSurfaceDocsWebModule.ResolveBrandingAssetsRequestPath(
                    new AppSurfaceDocsOptions
                    {
                        Identity = new AppSurfaceDocsIdentityOptions { BrandingAssets = null! }
                    }));
        }

        var options = configuredRequestPath is null
            ? new AppSurfaceDocsOptions { Identity = null! }
            : new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions
                    {
                        RequestPath = configuredRequestPath
                    }
                }
            };

        var result = AppSurfaceDocsWebModule.ResolveBrandingAssetsRequestPath(options);

        Assert.Equal(expectedRequestPath, result);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("favicon.svg", null)]
    [InlineData("/favicon.ico", null)]
    [InlineData("~/brand/favicon.svg", "/brand/favicon.svg")]
    [InlineData("/brand/favicon.svg", "/brand/favicon.svg")]
    public void ResolveConfiguredRootFaviconRedirectPath_ShouldReturnOnlyCustomSvgBrowserPaths(
        string? configuredSvgPath,
        string? expectedRedirectPath)
    {
        if (configuredSvgPath is null)
        {
            Assert.Null(
                AppSurfaceDocsWebModule.ResolveConfiguredRootFaviconRedirectPath(
                    new AppSurfaceDocsOptions
                    {
                        Identity = new AppSurfaceDocsIdentityOptions { Favicon = null! }
                    }));
        }

        var options = configuredSvgPath is null
            ? new AppSurfaceDocsOptions { Identity = null! }
            : new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    Favicon = new AppSurfaceDocsFaviconOptions
                    {
                        SvgPath = configuredSvgPath
                    }
                }
            };

        var result = AppSurfaceDocsWebModule.ResolveConfiguredRootFaviconRedirectPath(options);

        Assert.Equal(expectedRedirectPath, result);
    }

    [Fact]
    public async Task ConfigureEndpoints_Issue001_DoesNotRedirectRootStylesheet_WhenAppSurfaceDocsIsEmbedded()
    {
        var module = new AppSurfaceDocsWebModule();
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
    public async Task ConfigureEndpoints_ShouldLeaveRootFaviconToOwningApplication_WhenAppSurfaceDocsIsEmbedded()
    {
        var module = new AppSurfaceDocsWebModule();
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

            using var response = await client.GetAsync(RootFaviconPath);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ConfigureEndpoints_Issue001_PreservesPathBaseInRootStylesheetRedirect_WhenAppSurfaceDocsIsRootModule()
    {
        var module = new AppSurfaceDocsWebModule();
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
        var rootModule = A.Fake<IAppSurfaceHostModule>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        return new StartupContext(Array.Empty<string>(), rootModule, "TestApp", environmentProvider);
    }

    private static HttpRequestMessage CreateHealthRequest(string requestPath, string userName, string scope)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
        request.Headers.Add(HeaderAuthenticationHandler.UserHeaderName, userName);
        request.Headers.Add(HeaderAuthenticationHandler.ScopeHeaderName, scope);
        return request;
    }

    private static async Task<StartedHealthAuthorizationHost> StartHealthAuthorizationHostAsync(
        string environmentName,
        bool exposeHealthRoutes = false,
        bool registerPolicy = true,
        bool registerAuthorizationMiddleware = true)
    {
        var root = new HealthAuthorizationRootModule
        {
            RegisterPolicy = registerPolicy,
            RegisterAuthorizationMiddleware = registerAuthorizationMiddleware
        };
        var startup = new TestHealthAuthorizationStartup(root);
        var context = new StartupContext(["--environment", environmentName], root, "TestApp")
        {
            OverrideEntryPointAssembly = typeof(AppSurfaceDocsWebModule).Assembly
        };
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        builder.ConfigureAppConfiguration(
            (_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["AppSurfaceDocs:Harvest:Health:AuthorizationPolicy"] = "DocsHealthRead"
                };
                if (exposeHealthRoutes)
                {
                    values["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always";
                }

                configuration.AddInMemoryCollection(values);
            });
        builder.ConfigureServices(
            services =>
            {
                services.RemoveAll<IDocHarvester>();
                services.AddSingleton<IDocHarvester, HealthyHarvester>();
            });
        builder.ConfigureWebHost(
            webHost =>
            {
                webHost.UseEnvironment(environmentName);
                webHost.UseUrls("http://127.0.0.1:0");
            });

        var host = builder.Build();
        await host.StartAsync();

        var server = host.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();
        var baseAddress = Assert.Single(addresses!.Addresses);
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(baseAddress)
        };

        return new StartedHealthAuthorizationHost(host, client);
    }

    private static ServiceProvider CreateServiceProviderWithContentRoot(string contentRootPath)
    {
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(contentRootPath);

        var services = new ServiceCollection();
        services.AddSingleton(environment);
        return services.BuildServiceProvider();
    }

    private static StartupContext CreatePackagedModuleStartupContext(
        AppSurfaceDocsWebModule module,
        string? applicationName = null)
    {
        return new StartupContext([], module, applicationName)
        {
            OverrideEntryPointAssembly = typeof(AppSurfaceDocsWebModule).Assembly
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
              <link rel="canonical" href="/docs" />
              <link rel="stylesheet" href="/docs/search.css" />
              <link rel="preload" href="/docs/search-index.json" as="fetch" crossorigin="use-credentials" />
              <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json"};</script>
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
              <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/docs","docsSearchUrl":"/docs/search","docsSearchIndexUrl":"/docs/search-index.json"};</script>
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
        File.WriteAllText(
            Path.Join(root, ".appsurface-docs-route-manifest.json"),
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "guide.md",
                  "canonicalRoutePath": "guide",
                  "recoveryAliases": ["guide.md", "guide.md.html"],
                  "declaredAliases": []
                }
              ]
            }
            """);
        return root;
    }

    private static string WriteReleaseManifest(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), AppSurfaceDocsReleaseArchiveVerifier.FileName, StringComparison.Ordinal))
            .Select(
                path => new
                {
                    path = Path.GetRelativePath(root, path)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/'),
                    length = new FileInfo(path).Length,
                    contentType = (string?)null,
                    hashAlgorithm = "sha256",
                    sha256 = ComputeFileSha256(path)
                })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        var manifestFileName = AppSurfaceDocsReleaseArchiveVerifier.FileName;
        if (Path.IsPathRooted(manifestFileName))
        {
            throw new InvalidOperationException("Release manifest file name must not be rooted.");
        }

        var manifestPath = TestPathUtils.PathUnder(root, manifestFileName);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new { schema = AppSurfaceDocsReleaseArchiveVerifier.Schema, files },
                new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return ComputeFileSha256(manifestPath);
    }

    private static string ComputeFileSha256(string path)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
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

    private sealed class TestAppSurfaceDocsStartup : WebStartup<AppSurfaceDocsWebModule>
    {
        private readonly AppSurfaceDocsWebModule _module;

        public TestAppSurfaceDocsStartup(AppSurfaceDocsWebModule module)
        {
            _module = module;
        }

        protected override AppSurfaceDocsWebModule CreateRootModule() => _module;
    }

    private sealed class TestHealthAuthorizationStartup : WebStartup<HealthAuthorizationRootModule>
    {
        private readonly HealthAuthorizationRootModule _module;

        public TestHealthAuthorizationStartup(HealthAuthorizationRootModule module)
        {
            _module = module;
        }

        protected override HealthAuthorizationRootModule CreateRootModule() => _module;
    }

    private sealed class HealthAuthorizationRootModule : IAppSurfaceWebModule
    {
        public bool RegisterPolicy { get; init; } = true;

        public bool RegisterAuthorizationMiddleware { get; init; } = true;

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services
                .AddAuthentication(HeaderAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                    HeaderAuthenticationHandler.SchemeName,
                    _ => { });
            services.AddAuthorization(
                options =>
                {
                    if (RegisterPolicy)
                    {
                        options.AddPolicy(
                            "DocsHealthRead",
                            policy => policy.RequireAuthenticatedUser()
                                .RequireClaim("scope", "docs.health.read"));
                    }
                });
        }

        public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
        {
            if (!RegisterAuthorizationMiddleware)
            {
                return;
            }

            app.UseAuthentication();
            app.UseAuthorization();
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
            builder.AddModule<AppSurfaceDocsWebModule>();
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    private sealed class HeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "HeaderTest";
        public const string UserHeaderName = "X-Test-User";
        public const string ScopeHeaderName = "X-Test-Scope";

        public HeaderAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeaderName, out var userValues)
                || string.IsNullOrWhiteSpace(userValues[0]))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var userName = userValues[0]!;
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, userName),
                new(ClaimTypes.NameIdentifier, userName)
            };
            if (Request.Headers.TryGetValue(ScopeHeaderName, out var scopeValues))
            {
                claims.AddRange(
                    scopeValues
                        .SelectMany(value => value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [])
                        .Select(scope => new Claim("scope", scope)));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class HealthyHarvester : IDocHarvester
    {
        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DocNode> docs =
            [
                new("Health Test", "health-test.md", "# Health Test")
            ];
            return Task.FromResult(docs);
        }
    }

    private sealed class StartedHealthAuthorizationHost : IAsyncDisposable
    {
        private readonly IHost _host;

        public StartedHealthAuthorizationHost(IHost host, HttpClient client)
        {
            _host = host;
            Client = client;
        }

        public HttpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private sealed class FailingHarvester : IDocHarvester
    {
        public const string RawFailureMessage = "raw failing harvester message";

        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(RawFailureMessage);
        }
    }

    private sealed record BuildCoordinates(string Configuration, string TargetFramework);
}
