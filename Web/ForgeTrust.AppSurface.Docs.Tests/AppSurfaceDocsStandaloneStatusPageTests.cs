using System.Net;
using System.Net.Http.Headers;
using ForgeTrust.AppSurface.Docs.Standalone;
using ForgeTrust.AppSurface.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsStandaloneStatusPageTests
{
    [Fact]
    public async Task MissingDocsRoute_ShouldRenderStandaloneDocsRecoveryPage()
    {
        await using var runningHost = await StartStandaloneHostAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/docs/missing-page");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await runningHost.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Documentation page not found", html);
        Assert.Contains("Search documentation", html);
        Assert.Contains("href=\"/docs/search\"", html);
        Assert.Contains("/docs/missing-page", html);
    }

    [Fact]
    public async Task ReservedNotFoundRoute_ShouldRenderStandaloneDocsRecoveryWithoutOriginalPath()
    {
        await using var runningHost = await StartStandaloneHostAsync();

        using var response = await runningHost.Client.GetAsync("/_appsurface/errors/404");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Documentation page not found", html);
        Assert.Contains("the requested documentation page", html);
        Assert.Contains("href=\"/docs/search\"", html);
    }

    [Fact]
    public async Task ReservedNotFoundRoute_ShouldUseLiveDocsRootForVersionedSearchRecovery()
    {
        var catalogPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "catalog.json");

        await using var runningHost = await StartStandaloneHostAsync(
            new Dictionary<string, string?>
            {
                ["AppSurfaceDocs:Routing:RouteRootPath"] = "/foo/bar",
                ["AppSurfaceDocs:Routing:DocsRootPath"] = "/foo/bar/next",
                ["AppSurfaceDocs:Versioning:Enabled"] = "true",
                ["AppSurfaceDocs:Versioning:CatalogPath"] = catalogPath
            });

        using var response = await runningHost.Client.GetAsync("/_appsurface/errors/404");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/foo/bar/next/search\"", html);
        Assert.DoesNotContain("href=\"/foo/bar/search\"", html);
    }

    [Fact]
    public async Task ReservedNotFoundRoute_ShouldRenderPathBaseAwareSearchRecovery()
    {
        await using var runningHost = await StartStandaloneHostAsync(pathBase: "/mounted");

        using var response = await runningHost.Client.GetAsync("/mounted/_appsurface/errors/404");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/mounted/docs/search\"", html);
        Assert.Contains("href=\"/mounted/docs\"", html);
    }

    [Fact]
    public async Task ConfigureOptions_ShouldAllowCallerToDisableStandaloneBrowserStatusPages()
    {
        await using var runningHost = await StartStandaloneHostAsync(
            configureOptions: options => options.Errors.DisableBrowserStatusPages());

        using var response = await runningHost.Client.GetAsync("/_appsurface/errors/404");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Documentation page not found", html);
        Assert.DoesNotContain("Search documentation", html);
    }

    private static async Task<RunningStandaloneHost> StartStandaloneHostAsync(
        IReadOnlyDictionary<string, string?>? configurationOverrides = null,
        string? pathBase = null,
        Action<WebOptions>? configureOptions = null)
    {
        var repositoryRoot = CreateRepositoryRoot();
        var configuration = new Dictionary<string, string?>
        {
            ["AppSurfaceDocs:Source:RepositoryRoot"] = repositoryRoot
        };

        if (configurationOverrides is not null)
        {
            foreach (var (key, value) in configurationOverrides)
            {
                configuration[key] = value;
            }
        }

        var builder = AppSurfaceDocsStandaloneHost.CreateBuilder(
            [],
            environmentProvider: null,
            configureOptions: configureOptions);
        builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddInMemoryCollection(configuration));
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new PathBaseStartupFilter(pathBase)));
        }

        var host = builder.Build();
        await host.StartAsync();

        var server = host.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();
        var baseAddress = Assert.Single(addresses!.Addresses);
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseAddress)
        };

        return new RunningStandaloneHost(host, client, repositoryRoot);
    }

    private static string CreateRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(repositoryRoot);
        File.WriteAllText(
            Path.Combine(repositoryRoot, "README.md"),
            """
            # Test Docs

            This source tree exists so the standalone host can harvest a small documentation snapshot.
            """);
        return repositoryRoot;
    }

    private sealed class PathBaseStartupFilter(string pathBase) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.UsePathBase(pathBase);
                next(app);
            };
        }
    }

    private sealed class RunningStandaloneHost(IHost host, HttpClient client, string repositoryRoot) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();

            try
            {
                await host.StopAsync();
            }
            finally
            {
                host.Dispose();

                if (Directory.Exists(repositoryRoot))
                {
                    Directory.Delete(repositoryRoot, recursive: true);
                }
            }
        }
    }
}
