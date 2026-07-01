using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public void HealthOptions_DefaultOptions_HaveExpectedDefaults()
    {
        var options = new HealthOptions();

        Assert.True(HealthOptions.Default.Enabled);
        Assert.True(options.Enabled);
        Assert.Equal(AppSurfaceHealthEndpointDefaults.HealthPath, options.HealthPath);
        Assert.Equal(AppSurfaceHealthEndpointDefaults.ReadyPath, options.ReadyPath);
        Assert.Equal(AppSurfaceHealthCheckTags.Ready, options.ReadyTag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("health")]
    [InlineData("//health")]
    [InlineData("/health?verbose=true")]
    [InlineData("/health#status")]
    [InlineData("/../health")]
    [InlineData("/health/{id}")]
    public void HealthOptionsValidator_RejectsUnsafeHealthPaths(string path)
    {
        var options = new HealthOptions { HealthPath = path };

        var diagnostics = HealthOptionsValidator.Validate(options);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "ASPHEALTH001");
    }

    [Theory]
    [InlineData("")]
    [InlineData("ready")]
    [InlineData("//ready")]
    [InlineData("/ready?verbose=true")]
    [InlineData("/ready#status")]
    [InlineData("/../ready")]
    [InlineData("/ready/{id}")]
    public void HealthOptionsValidator_RejectsUnsafeReadyPaths(string path)
    {
        var options = new HealthOptions { ReadyPath = path };

        var diagnostics = HealthOptionsValidator.Validate(options);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "ASPHEALTH002");
    }

    [Fact]
    public void HealthOptionsValidator_RejectsDuplicateNormalizedPaths()
    {
        var options = new HealthOptions
        {
            HealthPath = "/Probe",
            ReadyPath = "/probe/"
        };

        var diagnostics = HealthOptionsValidator.Validate(options);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "ASPHEALTH003");
    }

    [Fact]
    public void HealthOptionsValidator_RejectsBlankReadyTag()
    {
        var options = new HealthOptions { ReadyTag = " " };

        var diagnostics = HealthOptionsValidator.Validate(options);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "ASPHEALTH004");
    }

    [Fact]
    public async Task DefaultEndpoints_ReturnHealthy_WhenNoChecksRegistered()
    {
        await using var app = await StartHostAsync();

        using var health = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.HealthPath);
        using var ready = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.ReadyPath);

        await AssertProbeResponseAsync(health, HttpStatusCode.OK, "Healthy");
        await AssertProbeResponseAsync(ready, HttpStatusCode.OK, "Healthy");
    }

    [Fact]
    public async Task HeadRequests_ReturnNoBody_WithNoStoreHeaders()
    {
        await using var app = await StartHostAsync();

        using var healthRequest = new HttpRequestMessage(HttpMethod.Head, AppSurfaceHealthEndpointDefaults.HealthPath);
        using var readyRequest = new HttpRequestMessage(HttpMethod.Head, AppSurfaceHealthEndpointDefaults.ReadyPath);
        using var health = await app.Client.SendAsync(healthRequest);
        using var ready = await app.Client.SendAsync(readyRequest);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        AssertNoStore(health);
        AssertNoStore(ready);
        Assert.Equal("text/plain", health.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/plain", ready.Content.Headers.ContentType?.MediaType);
        Assert.Equal(string.Empty, await health.Content.ReadAsStringAsync());
        Assert.Equal(string.Empty, await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_RunsAllRegisteredChecks()
    {
        var untagged = new ProbeCounter();
        var ready = new ProbeCounter();
        await using var app = await StartHostAsync(
            configureServices: services => RegisterChecks(
                services,
                new ProbeRegistration("untagged", HealthStatus.Healthy, untagged),
                new ProbeRegistration("ready", HealthStatus.Healthy, ready, AppSurfaceHealthCheckTags.Ready)));

        using var response = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.HealthPath);

        await AssertProbeResponseAsync(response, HttpStatusCode.OK, "Healthy");
        Assert.Equal(1, untagged.Count);
        Assert.Equal(1, ready.Count);
    }

    [Fact]
    public async Task Ready_RunsOnlyReadyTaggedChecks()
    {
        var untagged = new ProbeCounter();
        var ready = new ProbeCounter();
        await using var app = await StartHostAsync(
            configureServices: services => RegisterChecks(
                services,
                new ProbeRegistration("untagged", HealthStatus.Healthy, untagged),
                new ProbeRegistration("ready", HealthStatus.Healthy, ready, AppSurfaceHealthCheckTags.Ready)));

        using var response = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.ReadyPath);

        await AssertProbeResponseAsync(response, HttpStatusCode.OK, "Healthy");
        Assert.Equal(0, untagged.Count);
        Assert.Equal(1, ready.Count);
    }

    [Fact]
    public async Task Ready_UsesConfiguredReadyTag()
    {
        var defaultTagged = new ProbeCounter();
        var customTagged = new ProbeCounter();
        await using var app = await StartHostAsync(
            options => options.Health.ReadyTag = "startup",
            services => RegisterChecks(
                services,
                new ProbeRegistration("default-ready", HealthStatus.Healthy, defaultTagged, AppSurfaceHealthCheckTags.Ready),
                new ProbeRegistration("startup", HealthStatus.Healthy, customTagged, "startup")));

        using var response = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.ReadyPath);

        await AssertProbeResponseAsync(response, HttpStatusCode.OK, "Healthy");
        Assert.Equal(0, defaultTagged.Count);
        Assert.Equal(1, customTagged.Count);
    }

    [Fact]
    public async Task UntaggedFailure_FailsHealthButNotReady()
    {
        await using var app = await StartHostAsync(
            configureServices: services => RegisterChecks(
                services,
                new ProbeRegistration("database", HealthStatus.Unhealthy, new ProbeCounter())));

        using var health = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.HealthPath);
        using var ready = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.ReadyPath);

        await AssertProbeResponseAsync(health, HttpStatusCode.ServiceUnavailable, "Unhealthy");
        await AssertProbeResponseAsync(ready, HttpStatusCode.OK, "Healthy");
    }

    [Fact]
    public async Task ReadyTaggedFailure_FailsHealthAndReady()
    {
        await using var app = await StartHostAsync(
            configureServices: services => RegisterChecks(
                services,
                new ProbeRegistration("database", HealthStatus.Unhealthy, new ProbeCounter(), AppSurfaceHealthCheckTags.Ready)));

        using var health = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.HealthPath);
        using var ready = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.ReadyPath);

        await AssertProbeResponseAsync(health, HttpStatusCode.ServiceUnavailable, "Unhealthy");
        await AssertProbeResponseAsync(ready, HttpStatusCode.ServiceUnavailable, "Unhealthy");
    }

    [Fact]
    public async Task DegradedMapsTo503()
    {
        await using var app = await StartHostAsync(
            configureServices: services => RegisterChecks(
                services,
                new ProbeRegistration("database", HealthStatus.Degraded, new ProbeCounter(), AppSurfaceHealthCheckTags.Ready)));

        using var response = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.ReadyPath);

        await AssertProbeResponseAsync(response, HttpStatusCode.ServiceUnavailable, "Degraded");
    }

    [Fact]
    public async Task DisabledOptions_RemoveBothEndpoints()
    {
        await using var app = await StartHostAsync(options => options.Health.Enabled = false);

        using var health = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.HealthPath);
        using var ready = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.ReadyPath);

        Assert.Equal(HttpStatusCode.NotFound, health.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ready.StatusCode);
    }

    [Fact]
    public async Task CustomPaths_Work()
    {
        await using var app = await StartHostAsync(options =>
        {
            options.Health.HealthPath = "/internal/live";
            options.Health.ReadyPath = "/internal/ready";
        });

        using var defaultHealth = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.HealthPath);
        using var defaultReady = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.ReadyPath);
        using var customHealth = await app.Client.GetAsync("/internal/live");
        using var customReady = await app.Client.GetAsync("/internal/ready");

        Assert.Equal(HttpStatusCode.NotFound, defaultHealth.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, defaultReady.StatusCode);
        await AssertProbeResponseAsync(customHealth, HttpStatusCode.OK, "Healthy");
        await AssertProbeResponseAsync(customReady, HttpStatusCode.OK, "Healthy");
    }

    [Fact]
    public async Task Endpoints_AreExcludedFromDescription()
    {
        await using var app = await StartHostAsync();

        var health = Assert.Single(app.RouteEndpoints, endpoint => endpoint.RoutePattern.RawText == AppSurfaceHealthEndpointDefaults.HealthPath);
        var ready = Assert.Single(app.RouteEndpoints, endpoint => endpoint.RoutePattern.RawText == AppSurfaceHealthEndpointDefaults.ReadyPath);

        AssertEndpointMetadata(health);
        AssertEndpointMetadata(ready);
    }

    [Fact]
    public async Task Endpoints_BypassFallbackAuthorizationPolicy()
    {
        await using var app = await StartHostAsync(
            configureServices: services =>
            {
                services
                    .AddAuthentication(FallbackHeaderAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, FallbackHeaderAuthenticationHandler>(
                        FallbackHeaderAuthenticationHandler.SchemeName,
                        _ => { });
                services.AddAuthorization(options =>
                {
                    options.FallbackPolicy = new AuthorizationPolicyBuilder(FallbackHeaderAuthenticationHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .Build();
                });
            },
            configureEndpointAwareMiddleware: app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
            });

        using var health = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.HealthPath);
        using var ready = await app.Client.GetAsync(AppSurfaceHealthEndpointDefaults.ReadyPath);

        await AssertProbeResponseAsync(health, HttpStatusCode.OK, "Healthy");
        await AssertProbeResponseAsync(ready, HttpStatusCode.OK, "Healthy");
    }

    [Fact]
    public async Task InvalidOptions_FailStartup()
    {
        var exception = await Record.ExceptionAsync(() => StartHostAsync(options => options.Health.HealthPath = "health"));

        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("ASPHEALTH001", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouteCollision_FailsStartup()
    {
        var exception = await Record.ExceptionAsync(
            () => StartHostAsync(configureEndpoints: endpoints => endpoints.MapGet(AppSurfaceHealthEndpointDefaults.HealthPath, () => "host")));

        AssertHealthPathConflict(exception, AppSurfaceHealthEndpointDefaults.HealthPath);
    }

    [Fact]
    public async Task ReadyRouteCollision_FailsStartup()
    {
        var exception = await Record.ExceptionAsync(
            () => StartHostAsync(configureEndpoints: endpoints => endpoints.MapGet(AppSurfaceHealthEndpointDefaults.ReadyPath, () => "host")));

        AssertHealthPathConflict(exception, AppSurfaceHealthEndpointDefaults.ReadyPath);
    }

    [Fact]
    public async Task ControllerRouteCollision_FailsStartup()
    {
        const string controllerRoute = "/controller-health-collision";

        var exception = await Record.ExceptionAsync(
            () => StartHostAsync(options =>
            {
                options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.Controllers };
                options.Health.HealthPath = controllerRoute;
            }));

        AssertHealthPathConflict(exception, controllerRoute);
    }

    [Fact]
    public async Task PwaRouteCollision_FailsStartup()
    {
        var exception = await Record.ExceptionAsync(
            () => StartHostAsync(options =>
            {
                ConfigureValidPwa(options.Pwa);
                options.Health.HealthPath = options.Pwa.ManifestPath;
            }));

        AssertHealthPathConflict(exception, "/manifest.webmanifest");
    }

    [Fact]
    public async Task BrowserStatusRouteCollision_FailsStartup()
    {
        var exception = await Record.ExceptionAsync(
            () => StartHostAsync(options =>
            {
                options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews };
                options.Errors.UseConventionalBrowserStatusPages();
                options.Health.HealthPath = BrowserStatusPageDefaults.ReservedNotFoundRoute;
            }));

        AssertHealthPathConflict(exception, BrowserStatusPageDefaults.ReservedNotFoundRoute);
    }

    private static async Task AssertProbeResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode,
        string expectedBody)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        AssertNoStore(response);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
    }

    private static void AssertEndpointMetadata(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        Assert.NotNull(methods);
        Assert.Contains(HttpMethods.Get, methods);
        Assert.Contains(HttpMethods.Head, methods);
        Assert.Contains(endpoint.Metadata.OfType<IAllowAnonymous>(), _ => true);
        Assert.Contains(endpoint.Metadata.OfType<IExcludeFromDescriptionMetadata>(), metadata => metadata.ExcludeFromDescription);
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(value => value.Name));
    }

    private static void RegisterChecks(IServiceCollection services, params ProbeRegistration[] registrations)
    {
        var builder = services.AddHealthChecks();
        foreach (var registration in registrations)
        {
            builder.Add(
                new HealthCheckRegistration(
                    registration.Name,
                    _ => new ProbeHealthCheck(registration.Counter, registration.Status),
                    null,
                    registration.Tags));
        }
    }

    private static void ConfigureValidPwa(PwaOptions pwa)
    {
        pwa.Enabled = true;
        pwa.Name = "Field Notes";
        pwa.ShortName = "Notes";
        pwa.ThemeColor = "#2563eb";
        pwa.BackgroundColor = "#ffffff";
        pwa.Icons.Add(new PwaIcon { Source = "/icons/app-192.png", Sizes = "192x192", Type = "image/png" });
        pwa.Icons.Add(new PwaIcon { Source = "/icons/app-512.png", Sizes = "512x512", Type = "image/png" });
    }

    private static void AssertHealthPathConflict(Exception? exception, string path)
    {
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("health endpoint path conflict", invalidOperation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(path, invalidOperation.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<RunningApp> StartHostAsync(
        Action<WebOptions>? configureOptions = null,
        Action<IServiceCollection>? configureServices = null,
        Action<IEndpointRouteBuilder>? configureEndpoints = null,
        Action<IApplicationBuilder>? configureEndpointAwareMiddleware = null)
    {
        var module = new TestWebModule(configureServices, configureEndpoints, configureEndpointAwareMiddleware);
        var startup = new TestWebStartup(module);
        if (configureOptions is not null)
        {
            startup.WithOptions(configureOptions);
        }

        var context = new StartupContext(["--environment", Environments.Development], module);
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
        private readonly Action<IServiceCollection>? _configureServices;
        private readonly Action<IEndpointRouteBuilder>? _configureEndpoints;
        private readonly Action<IApplicationBuilder>? _configureEndpointAwareMiddleware;

        public TestWebModule()
        {
        }

        public TestWebModule(
            Action<IServiceCollection>? configureServices,
            Action<IEndpointRouteBuilder>? configureEndpoints,
            Action<IApplicationBuilder>? configureEndpointAwareMiddleware)
        {
            _configureServices = configureServices;
            _configureEndpoints = configureEndpoints;
            _configureEndpointAwareMiddleware = configureEndpointAwareMiddleware;
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            _configureServices?.Invoke(services);
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
        }

        public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
        {
            _configureEndpointAwareMiddleware?.Invoke(app);
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
            _configureEndpoints?.Invoke(endpoints);
        }
    }

    private sealed class RunningApp(IHost host, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public IReadOnlyList<RouteEndpoint> RouteEndpoints { get; } =
            host.Services.GetServices<EndpointDataSource>()
                .SelectMany(dataSource => dataSource.Endpoints)
                .OfType<RouteEndpoint>()
                .ToArray();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    private sealed record ProbeRegistration(
        string Name,
        HealthStatus Status,
        ProbeCounter Counter,
        params string[] Tags);

    private sealed class ProbeCounter
    {
        private int _count;

        public int Count => _count;

        public void Increment()
        {
            Interlocked.Increment(ref _count);
        }
    }

    private sealed class ProbeHealthCheck(ProbeCounter counter, HealthStatus status) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return Task.FromResult(new HealthCheckResult(status));
        }
    }

    private sealed class FallbackHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "HealthFallbackHeader";
        private const string UserHeaderName = "X-Health-Test-User";

        public FallbackHeaderAuthenticationHandler(
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
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, userName),
                    new Claim(ClaimTypes.NameIdentifier, userName)
                ],
                SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}

[ApiController]
[Route("controller-health-collision")]
public sealed class HealthEndpointCollisionController : ControllerBase
{
    [HttpGet]
    public string Get()
    {
        return "controller";
    }
}
