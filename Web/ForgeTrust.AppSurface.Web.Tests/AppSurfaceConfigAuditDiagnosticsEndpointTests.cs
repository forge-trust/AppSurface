using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfaceConfigAuditDiagnosticsEndpointTests
{
    private const string PolicyName = "ConfigAuditRead";

    [Fact]
    public async Task MapAppSurfaceConfigAuditDiagnostics_IsAbsentUntilHostMapsIt()
    {
        await using var host = await StartHostAsync(mapEndpoint: false);

        using var request = CreateAuthorizedRequest(AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void MapAppSurfaceConfigAuditDiagnostics_ValidatesArguments()
    {
        IEndpointRouteBuilder endpoints = null!;
        using var app = CreateUnstartedApp();

        Assert.Throws<ArgumentNullException>(() => endpoints.MapAppSurfaceConfigAuditDiagnostics(PolicyName));
        Assert.Throws<ArgumentException>(() => app.MapAppSurfaceConfigAuditDiagnostics(" "));
        Assert.Throws<ArgumentException>(() => app.MapAppSurfaceConfigAuditDiagnostics(" ", PolicyName));
        Assert.Throws<ArgumentException>(() => app.MapAppSurfaceConfigAuditDiagnostics("/audit", " "));
    }

    [Fact]
    public void MapAppSurfaceConfigAuditDiagnostics_AttachesPolicyAndHidesApiDescription()
    {
        using var app = CreateUnstartedApp();

        app.MapAppSurfaceConfigAuditDiagnostics(PolicyName);

        var endpoint = Assert.Single(GetRouteEndpoints(app));
        Assert.Equal(AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute, endpoint.RoutePattern.RawText);
        Assert.Contains(
            endpoint.Metadata.OfType<IAuthorizeData>(),
            metadata => metadata.Policy == PolicyName);
        Assert.Contains(endpoint.Metadata.OfType<IExcludeFromDescriptionMetadata>(), metadata => metadata.ExcludeFromDescription);
    }

    [Fact]
    public void MapAppSurfaceConfigAuditDiagnostics_UsesCustomRoute()
    {
        using var app = CreateUnstartedApp();

        app.MapAppSurfaceConfigAuditDiagnostics("/admin/config-audit", PolicyName);

        var endpoint = Assert.Single(GetRouteEndpoints(app));
        Assert.Equal("/admin/config-audit", endpoint.RoutePattern.RawText);
    }

    [Fact]
    public async Task MapAppSurfaceConfigAuditDiagnostics_RejectsUnauthorizedRequests()
    {
        await using var host = await StartHostAsync();

        using var response = await host.Client.GetAsync(AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MapAppSurfaceConfigAuditDiagnostics_FailsClosedWhenPolicyIsMissing()
    {
        await using var host = await StartHostAsync(registerPolicy: false);

        using var request = CreateAuthorizedRequest(AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task MapAppSurfaceConfigAuditDiagnostics_FailsClosedWhenAuthorizationMiddlewareIsMissing()
    {
        await using var host = await StartHostAsync(registerAuthorizationMiddleware: false);

        using var request = CreateAuthorizedRequest(AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task MapAppSurfaceConfigAuditDiagnostics_ReturnsSanitizedReportJsonForAuthorizedRequest()
    {
        await using var host = await StartHostAsync();

        using var request = CreateAuthorizedRequest(AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute);
        using var response = await host.Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(value => value.Name));
        Assert.Contains("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("raw-secret", json, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("Production", root.GetProperty("Environment").GetString());
        Assert.Equal("[redacted]", root.GetProperty("Entries")[0].GetProperty("DisplayValue").GetString());
        Assert.True(root.GetProperty("Entries")[0].GetProperty("IsRedacted").GetBoolean());
        Assert.Equal(0, root.GetProperty("Entries")[0].GetProperty("State").GetInt32());

        var entrySource = root.GetProperty("Entries")[0].GetProperty("Sources")[0];
        Assert.Equal("JsonFile", entrySource.GetProperty("ProviderName").GetString());
        Assert.Equal(12, entrySource.GetProperty("Location").GetProperty("LineNumber").GetInt32());
        Assert.Equal(0, entrySource.GetProperty("Role").GetInt32());
        Assert.Equal(2, entrySource.GetProperty("Sensitivity").GetInt32());

        var childEntry = root.GetProperty("Entries")[0].GetProperty("Children")[0];
        Assert.Equal(JsonValueKind.Null, childEntry.GetProperty("DisplayValue").ValueKind);
        Assert.Equal(0, childEntry.GetProperty("Element").GetProperty("Kind").GetInt32());
        Assert.Equal(0, childEntry.GetProperty("Element").GetProperty("Index").GetInt32());

        var discoveredKey = root.GetProperty("DiscoveredKeys")[0];
        Assert.Equal(4, discoveredKey.GetProperty("ValueDisplayState").GetInt32());
        Assert.Equal("JsonFile", discoveredKey.GetProperty("Sources")[0].GetProperty("ProviderName").GetString());
        Assert.Equal("inventory-warning", discoveredKey.GetProperty("Diagnostics")[0].GetProperty("Code").GetString());

        var diagnostic = root.GetProperty("Diagnostics")[0];
        Assert.Equal("config-audit-warning", diagnostic.GetProperty("Code").GetString());
        Assert.Equal("JsonFile", diagnostic.GetProperty("Source").GetProperty("ProviderName").GetString());

        var redaction = root.GetProperty("Redaction");
        Assert.True(redaction.GetProperty("Enabled").GetBoolean());
        Assert.Equal("[redacted]", redaction.GetProperty("Placeholder").GetString());
        Assert.Equal("secret", redaction.GetProperty("MatchedFragments")[0].GetString());
    }

    [Fact]
    public void ConfigPackage_DoesNotReferenceAspNetCoreOrWeb()
    {
        var references = typeof(ConfigAuditReport)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => name is not null)
            .ToList();

        Assert.DoesNotContain(references, name => name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name == "ForgeTrust.AppSurface.Web");
    }

    [Fact]
    public async Task MapAppSurfaceConfigAuditDiagnostics_ReturnsSafeProblemWhenReporterIsMissing()
    {
        await using var host = await StartHostAsync(registerReporter: false);

        using var request = CreateAuthorizedRequest(AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute);
        using var response = await host.Client.SendAsync(request);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertProblem(problem, "AppSurface config audit services are unavailable.", "IConfigAuditReporter");
        AssertNoStore(response);
    }

    [Fact]
    public async Task MapAppSurfaceConfigAuditDiagnostics_ReturnsSafeProblemWhenEnvironmentProviderIsMissing()
    {
        await using var host = await StartHostAsync(registerEnvironmentProvider: false);

        using var request = CreateAuthorizedRequest(AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute);
        using var response = await host.Client.SendAsync(request);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertProblem(problem, "AppSurface environment services are unavailable.", "IEnvironmentProvider");
        AssertNoStore(response);
    }

    [Fact]
    public async Task MapAppSurfaceConfigAuditDiagnostics_ReturnsSafeProblemWhenEnvironmentIsBlank()
    {
        await using var host = await StartHostAsync(environmentName: " ");

        using var request = CreateAuthorizedRequest(AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute);
        using var response = await host.Client.SendAsync(request);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertProblem(problem, "AppSurface environment is empty.", "empty environment name");
        AssertNoStore(response);
    }

    [Fact]
    public async Task MapAppSurfaceConfigAuditDiagnostics_ReturnsSafeProblemWhenReporterThrows()
    {
        await using var host = await StartHostAsync(reporterThrows: true);

        using var request = CreateAuthorizedRequest(AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute);
        using var response = await host.Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertProblem(problem, "AppSurface config audit failed.", "sanitized report");
        Assert.DoesNotContain("raw-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", json, StringComparison.Ordinal);
        AssertNoStore(response);
    }

    private static WebApplication CreateUnstartedApp()
    {
        var builder = WebApplication.CreateBuilder();
        return builder.Build();
    }

    private static IReadOnlyList<RouteEndpoint> GetRouteEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

    private static async Task<StartedHost> StartHostAsync(
        bool mapEndpoint = true,
        bool registerPolicy = true,
        bool registerAuthorizationMiddleware = true,
        bool registerReporter = true,
        bool registerEnvironmentProvider = true,
        bool reporterThrows = false,
        string environmentName = "Production")
    {
        if (!registerAuthorizationMiddleware)
        {
            return await StartHostWithoutAuthorizationMiddlewareAsync();
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        ConfigureServices(
            builder.Services,
            registerPolicy,
            registerReporter,
            registerEnvironmentProvider,
            reporterThrows,
            environmentName);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        if (mapEndpoint)
        {
            app.MapAppSurfaceConfigAuditDiagnostics(PolicyName);
        }

        await app.StartAsync();
        var client = new HttpClient
        {
            BaseAddress = new Uri(GetBaseAddress(app))
        };
        return new StartedHost(app, client);
    }

    private static async Task<StartedHost> StartHostWithoutAuthorizationMiddlewareAsync()
    {
        var host = Host
            .CreateDefaultBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseKestrel();
                webBuilder.UseUrls("http://127.0.0.1:0");
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    ConfigureServices(
                        services,
                        registerPolicy: true,
                        registerReporter: true,
                        registerEnvironmentProvider: true,
                        reporterThrows: false,
                        environmentName: "Production");
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAppSurfaceConfigAuditDiagnostics(PolicyName);
                    });
                });
            })
            .Build();

        await host.StartAsync();
        var client = new HttpClient
        {
            BaseAddress = new Uri(GetBaseAddress(host))
        };
        return new StartedHost(host, client);
    }

    private static void ConfigureServices(
        IServiceCollection services,
        bool registerPolicy,
        bool registerReporter,
        bool registerEnvironmentProvider,
        bool reporterThrows,
        string environmentName)
    {
        services
            .AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                HeaderAuthenticationHandler.SchemeName,
                _ => { });
        services.AddAuthorization(options =>
        {
            if (registerPolicy)
            {
                options.AddPolicy(PolicyName, policy => policy.RequireAuthenticatedUser());
            }
        });
        if (registerReporter)
        {
            services.AddSingleton<IConfigAuditReporter>(
                reporterThrows ? new ThrowingConfigAuditReporter() : new TestConfigAuditReporter());
        }

        if (registerEnvironmentProvider)
        {
            services.AddSingleton<IEnvironmentProvider>(new TestEnvironmentProvider(environmentName));
        }
    }

    private static HttpRequestMessage CreateAuthorizedRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(HeaderAuthenticationHandler.UserHeaderName, "alice");
        return request;
    }

    private static string GetBaseAddress(IHost host)
    {
        var addresses = host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;

        return Assert.Single(addresses ?? []);
    }

    private static void AssertProblem(JsonDocument problem, string title, string expectedCauseFragment)
    {
        var root = problem.RootElement;
        Assert.Equal(title, root.GetProperty("title").GetString());
        Assert.Contains(expectedCauseFragment, root.GetProperty("cause").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("problem").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("fix").GetString()));
        Assert.Equal(
            "Web/ForgeTrust.AppSurface.Web/README.md#config-audit-http-diagnostics",
            root.GetProperty("docsLink").GetString());
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(value => value.Name));
    }

    private sealed class StartedHost(IHost host, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    private sealed class HeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "HeaderTest";
        public const string UserHeaderName = "X-Test-User";

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

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userValues[0]!)],
                SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class TestEnvironmentProvider(string environmentName) : IEnvironmentProvider
    {
        public string Environment => environmentName;

        public bool IsDevelopment => string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
    }

    private sealed class TestConfigAuditReporter : IConfigAuditReporter
    {
        public ConfigAuditReport GetReport(string environment) =>
            CreateReport(environment);
    }

    private sealed class ThrowingConfigAuditReporter : IConfigAuditReporter
    {
        public ConfigAuditReport GetReport(string environment) =>
            throw new InvalidOperationException("raw-secret should never leak");
    }

    private static ConfigAuditReport CreateReport(string environment) =>
        new()
        {
            Environment = environment,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Providers =
            [
                new ConfigAuditProvider
                {
                    Name = "JsonFile",
                    Priority = 10,
                    Precedence = 0
                }
            ],
            Entries =
            [
                new ConfigAuditEntry
                {
                    Key = "Secrets:ApiKey",
                    DeclaredType = typeof(string).FullName,
                    State = ConfigAuditEntryState.Resolved,
                    DisplayValue = "[redacted]",
                    IsRedacted = true,
                    Sources =
                    [
                        new ConfigAuditSourceRecord
                        {
                            Kind = ConfigAuditSourceKind.File,
                            ProviderName = "JsonFile",
                            ProviderPriority = 10,
                            FilePath = "/app/appsettings.Production.json",
                            ConfigPath = "Secrets:ApiKey",
                            Role = ConfigAuditSourceRole.Base,
                            Location = new ConfigAuditSourceLocation(12, 9),
                            Sensitivity = ConfigAuditSensitivity.Sensitive
                        }
                    ],
                    Children =
                    [
                        new ConfigAuditEntry
                        {
                            Key = "Secrets:ApiKey:Child",
                            State = ConfigAuditEntryState.Missing,
                            DisplayValue = null,
                            IsRedacted = false,
                            Element = new ConfigAuditElementIdentity
                            {
                                Kind = ConfigAuditElementKind.ArrayItem,
                                Index = 0
                            }
                        }
                    ]
                }
            ],
            DiscoveredKeys =
            [
                new ConfigAuditDiscoveredKey
                {
                    Key = "Unknown:Inventory",
                    Classification = ConfigAuditDiscoveredKeyClassification.Unknown,
                    ValueDisplayState = ConfigAuditDiscoveredValueDisplayState.OmittedInventory,
                    Sources =
                    [
                        new ConfigAuditSourceRecord
                        {
                            Kind = ConfigAuditSourceKind.File,
                            ProviderName = "JsonFile",
                            ProviderPriority = 10,
                            FilePath = "/app/appsettings.Production.json",
                            ConfigPath = "Unknown:Inventory",
                            AppliedToPath = "Unknown:Inventory",
                            Role = ConfigAuditSourceRole.Base
                        }
                    ],
                    Diagnostics =
                    [
                        new ConfigAuditDiagnostic
                        {
                            Severity = ConfigAuditDiagnosticSeverity.Warning,
                            Code = "inventory-warning",
                            Message = "Display-safe inventory warning."
                        }
                    ]
                }
            ],
            Diagnostics =
            [
                new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Warning,
                    Code = "config-audit-warning",
                    Message = "Display-safe warning.",
                    Source = new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.File,
                        ProviderName = "JsonFile",
                        ProviderPriority = 10,
                        FilePath = "/app/appsettings.Production.json",
                        ConfigPath = "Secrets:ApiKey",
                        Role = ConfigAuditSourceRole.Base
                    }
                }
            ],
            Redaction = new ConfigAuditRedaction
            {
                Enabled = true,
                Placeholder = "[redacted]",
                MatchedFragments = ["secret"]
            }
        };
}
