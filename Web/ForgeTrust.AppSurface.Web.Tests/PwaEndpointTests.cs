using System.Net;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class PwaEndpointTests
{
    [Fact]
    public async Task DisabledPwa_DoesNotMapManifest()
    {
        await using var app = await StartHostAsync(options => options.Pwa.Enabled = false);

        using var response = await app.Client.GetAsync("/manifest.webmanifest");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EnabledPwa_MapsManifestWithInstallMetadata()
    {
        await using var app = await StartHostAsync(options => ConfigureValidPwa(options.Pwa));

        using var response = await app.Client.GetAsync("/manifest.webmanifest");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Field Notes", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("Notes", document.RootElement.GetProperty("short_name").GetString());
        Assert.Equal("standalone", document.RootElement.GetProperty("display").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("icons").GetArrayLength());
    }

    [Fact]
    public async Task HeadRequests_ReturnMetadataWithoutBodies()
    {
        await using var app = await StartHostAsync(
            options =>
            {
                ConfigureValidPwa(options.Pwa);
                options.Pwa.Offline.Enabled = true;
                options.Pwa.Offline.OfflineFallbackPath = "/offline.html";
                options.Pwa.Offline.StaticAssetPaths = ["/css/site.css"];
            },
            Environments.Development);

        using var manifestRequest = new HttpRequestMessage(HttpMethod.Head, "/manifest.webmanifest");
        using var diagnosticsRequest = new HttpRequestMessage(HttpMethod.Head, "/_appsurface/pwa");
        using var statusRequest = new HttpRequestMessage(HttpMethod.Head, "/_appsurface/pwa/status.json");
        using var serviceWorkerRequest = new HttpRequestMessage(HttpMethod.Head, "/service-worker.js");

        using var manifest = await app.Client.SendAsync(manifestRequest);
        using var diagnostics = await app.Client.SendAsync(diagnosticsRequest);
        using var status = await app.Client.SendAsync(statusRequest);
        using var serviceWorker = await app.Client.SendAsync(serviceWorkerRequest);

        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType?.MediaType);
        Assert.Equal(string.Empty, await manifest.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
        Assert.Equal("text/html", diagnostics.Content.Headers.ContentType?.MediaType);
        Assert.Equal(string.Empty, await diagnostics.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("application/json", status.Content.Headers.ContentType?.MediaType);
        Assert.Equal(string.Empty, await status.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, serviceWorker.StatusCode);
        Assert.Equal("text/javascript", serviceWorker.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", serviceWorker.Headers.CacheControl?.ToString());
        Assert.Equal(string.Empty, await serviceWorker.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DevelopmentDiagnostics_AreMappedWithJsonStatus()
    {
        await using var app = await StartHostAsync(
            options => ConfigureValidPwa(options.Pwa),
            Environments.Development);

        using var htmlResponse = await app.Client.GetAsync("/_appsurface/pwa");
        using var jsonResponse = await app.Client.GetAsync("/_appsurface/pwa/status.json");
        var html = await htmlResponse.Content.ReadAsStringAsync();
        var json = await jsonResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, htmlResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, jsonResponse.StatusCode);
        Assert.Contains("AppSurface PWA diagnostics", html, StringComparison.Ordinal);
        Assert.Contains("&lt;link rel=&quot;manifest&quot; href=&quot;/manifest.webmanifest&quot;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;meta name=&quot;theme-color&quot; content=&quot;#2563eb&quot;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;link rel=&quot;icon&quot; href=&quot;/icons/app-192.png&quot;", html, StringComparison.Ordinal);
        Assert.Contains("\"enabled\": true", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"offlineEnabled\": false", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionDiagnostics_AreHiddenByDefault()
    {
        await using var app = await StartHostAsync(
            options => ConfigureValidPwa(options.Pwa),
            Environments.Production);

        using var response = await app.Client.GetAsync("/_appsurface/pwa/status.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DiagnosticsExposure_CanBeForcedOrHidden()
    {
        await using var forced = await StartHostAsync(
            options =>
            {
                ConfigureValidPwa(options.Pwa);
                options.Pwa.DiagnosticsExposure = PwaDiagnosticEndpointExposure.Always;
            },
            Environments.Production);
        using var forcedResponse = await forced.Client.GetAsync("/_appsurface/pwa/status.json");

        await using var hidden = await StartHostAsync(
            options =>
            {
                ConfigureValidPwa(options.Pwa);
                options.Pwa.DiagnosticsExposure = PwaDiagnosticEndpointExposure.Never;
            },
            Environments.Development);
        using var hiddenResponse = await hidden.Client.GetAsync("/_appsurface/pwa/status.json");

        Assert.Equal(HttpStatusCode.OK, forcedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenResponse.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_WarnWhenRequestIsNotSecureInstallContext()
    {
        await using var app = await StartHostAsync(
            options => ConfigureValidPwa(options.Pwa),
            Environments.Development);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_appsurface/pwa/status.json");
        request.Headers.Host = "app.example.test";

        using var response = await app.Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"code\": \"ASPWA018\"", json, StringComparison.Ordinal);
        Assert.Contains("\"severity\": \"warning\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_DoNotWarnWhenRequestIsHttps()
    {
        await using var app = await StartHostAsync(
            options => ConfigureValidPwa(options.Pwa),
            Environments.Development,
            forceHttpsScheme: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_appsurface/pwa/status.json");
        request.Headers.Host = "app.example.test";

        using var response = await app.Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("\"code\": \"ASPWA018\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineStrategy_MapsServiceWorkerOnlyWhenExplicitlyEnabled()
    {
        await using var disabled = await StartHostAsync(options => ConfigureValidPwa(options.Pwa));

        using var missing = await disabled.Client.GetAsync("/service-worker.js");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await using var enabled = await StartHostAsync(options =>
        {
            ConfigureValidPwa(options.Pwa);
            options.Pwa.Offline.Enabled = true;
            options.Pwa.Offline.OfflineFallbackPath = "/offline.html";
            options.Pwa.Offline.StaticAssetPaths = ["/css/site.css"];
        });

        using var response = await enabled.Client.GetAsync("/service-worker.js");
        var script = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"/css/site.css\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/offline.html\"", script, StringComparison.Ordinal);
        Assert.Contains("request.method !== \"GET\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("POST", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PathBase_IsAppliedToManifestDiagnosticsAndServiceWorker()
    {
        await using var app = await StartHostAsync(
            options =>
            {
                ConfigureValidPwa(options.Pwa);
                options.Pwa.Offline.Enabled = true;
                options.Pwa.Offline.OfflineFallbackPath = "/offline.html";
                options.Pwa.Offline.StaticAssetPaths = ["/css/site.css"];
            },
            Environments.Development,
            "/tenant");

        using var manifestResponse = await app.Client.GetAsync("/tenant/manifest.webmanifest");
        var manifestJson = await manifestResponse.Content.ReadAsStringAsync();
        using var manifest = JsonDocument.Parse(manifestJson);
        Assert.Equal("/tenant/", manifest.RootElement.GetProperty("start_url").GetString());
        Assert.Equal("/tenant/", manifest.RootElement.GetProperty("scope").GetString());
        Assert.Equal(
            "/tenant/icons/app-192.png",
            manifest.RootElement.GetProperty("icons")[0].GetProperty("src").GetString());

        using var diagnosticsResponse = await app.Client.GetAsync("/tenant/_appsurface/pwa/status.json");
        var diagnosticsJson = await diagnosticsResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"manifestPath\": \"/tenant/manifest.webmanifest\"", diagnosticsJson, StringComparison.Ordinal);
        Assert.Contains("\"serviceWorkerPath\": \"/tenant/service-worker.js\"", diagnosticsJson, StringComparison.Ordinal);
        Assert.Contains("\"offlineFallbackPath\": \"/tenant/offline.html\"", diagnosticsJson, StringComparison.Ordinal);

        using var diagnosticsHtmlResponse = await app.Client.GetAsync("/tenant/_appsurface/pwa");
        var diagnosticsHtml = await diagnosticsHtmlResponse.Content.ReadAsStringAsync();
        Assert.Contains("&lt;link rel=&quot;manifest&quot; href=&quot;/tenant/manifest.webmanifest&quot;", diagnosticsHtml, StringComparison.Ordinal);
        Assert.Contains(
            "&lt;meta name=&quot;appsurface:pwa-service-worker&quot; content=&quot;/tenant/service-worker.js&quot;",
            diagnosticsHtml,
            StringComparison.Ordinal);

        using var serviceWorkerResponse = await app.Client.GetAsync("/tenant/service-worker.js");
        var serviceWorker = await serviceWorkerResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"/tenant/css/site.css\"", serviceWorker, StringComparison.Ordinal);
        Assert.Contains("\"/tenant/offline.html\"", serviceWorker, StringComparison.Ordinal);
    }

    private static void ConfigureValidPwa(PwaOptions pwa)
    {
        var valid = PwaOptionsTests.CreateValidOptions();
        pwa.Enabled = true;
        pwa.Name = valid.Name;
        pwa.ShortName = valid.ShortName;
        pwa.ThemeColor = valid.ThemeColor;
        pwa.BackgroundColor = valid.BackgroundColor;
        pwa.Icons.Add(new PwaIcon { Source = "/icons/app-192.png", Sizes = "192x192", Type = "image/png" });
        pwa.Icons.Add(new PwaIcon { Source = "/icons/app-512.png", Sizes = "512x512", Type = "image/png" });
    }

    private static async Task<RunningApp> StartHostAsync(
        Action<WebOptions> configureOptions,
        string environment = "Development",
        string pathBase = "",
        bool forceHttpsScheme = false)
    {
        var module = new TestWebModule(pathBase, forceHttpsScheme);
        var startup = new TestWebStartup(module);
        startup.WithOptions(configureOptions);
        var context = new StartupContext(["--environment", environment], module);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        var host = builder.Build();
        await host.StartAsync();

        var addresses = host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            ?? [];
        var baseUrl = Assert.Single(addresses);
        return new RunningApp(host, new HttpClient { BaseAddress = new Uri(baseUrl) });
    }

    private sealed class TestWebStartup(TestWebModule module) : WebStartup<TestWebModule>
    {
        protected override TestWebModule CreateRootModule() => module;
    }

    private sealed class TestWebModule : IAppSurfaceWebModule
    {
        public TestWebModule()
        {
        }

        public TestWebModule(string pathBase, bool forceHttpsScheme)
        {
            PathBase = pathBase;
            ForceHttpsScheme = forceHttpsScheme;
        }

        private string PathBase { get; } = string.Empty;

        private bool ForceHttpsScheme { get; }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
            if (ForceHttpsScheme)
            {
                app.Use((httpContext, next) =>
                {
                    httpContext.Request.Scheme = "https";
                    return next(httpContext);
                });
            }

            if (!string.IsNullOrWhiteSpace(PathBase))
            {
                app.UsePathBase(PathBase);
            }
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }
    }

    private sealed class RunningApp(IHost host, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
