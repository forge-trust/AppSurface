using System.Net;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Scalar.Tests;

public sealed class AppSurfaceWebScalarModuleTests
{
    [Fact]
    public void RegisterDependentModules_AddsOpenApiModule()
    {
        var module = new AppSurfaceWebScalarModule();
        var builder = new ModuleDependencyBuilder();

        module.RegisterDependentModules(builder);

        Assert.Contains(builder.Modules, dependency => dependency is AppSurfaceWebOpenApiModule);
    }

    [Fact]
    public void ConfigureServices_UsesDevelopmentOnlyEndpointExposureByDefault()
    {
        var module = new AppSurfaceWebScalarModule();
        var services = CreateServicesWithConfiguration();

        module.ConfigureServices(CreateContext(module, Environments.Development), services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceWebScalarOptions>>().Value;

        Assert.Equal(AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly, options.ExposeEndpoint);
    }

    [Fact]
    public void ConfigureServices_BindsEndpointExposureFromConfiguration()
    {
        var module = new AppSurfaceWebScalarModule();
        var services = CreateServicesWithConfiguration(new Dictionary<string, string?>
        {
            [$"{AppSurfaceWebScalarOptions.SectionName}:ExposeEndpoint"] = "Always"
        });

        module.ConfigureServices(CreateContext(module, Environments.Production), services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceWebScalarOptions>>().Value;

        Assert.Equal(AppSurfaceApiDocumentationEndpointExposure.Always, options.ExposeEndpoint);
    }

    [Fact]
    public void ConfigureServices_AppliesCodeFirstEndpointExposureOverrides()
    {
        var module = new AppSurfaceWebScalarModule();
        var services = CreateServicesWithConfiguration();

        module.ConfigureServices(CreateContext(module, Environments.Development), services);
        services.Configure<AppSurfaceWebScalarOptions>(options =>
            options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Never);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceWebScalarOptions>>().Value;

        Assert.Equal(AppSurfaceApiDocumentationEndpointExposure.Never, options.ExposeEndpoint);
    }

    [Fact]
    public void ConfigureServices_RejectsInvalidEndpointExposureValues()
    {
        var module = new AppSurfaceWebScalarModule();
        var services = CreateServicesWithConfiguration(new Dictionary<string, string?>
        {
            [$"{AppSurfaceWebScalarOptions.SectionName}:ExposeEndpoint"] = "99"
        });

        module.ConfigureServices(CreateContext(module, Environments.Development), services);

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AppSurfaceWebScalarOptions>>().Value);

        Assert.Contains($"{AppSurfaceWebScalarOptions.SectionName}:ExposeEndpoint", exception.Message);
        Assert.Contains(nameof(AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly), exception.Message);
        Assert.Contains(nameof(AppSurfaceApiDocumentationEndpointExposure.Always), exception.Message);
        Assert.Contains(nameof(AppSurfaceApiDocumentationEndpointExposure.Never), exception.Message);
    }

    [Fact]
    public async Task ConfigureEndpoints_MapsDefaultScalarApiReferenceEndpointInDevelopment()
    {
        var routePatterns = await GetMappedDocumentationRoutePatternsAsync(
            CreateContext(new AppSurfaceWebScalarModule(), Environments.Development));

        Assert.Contains(routePatterns, pattern => pattern.StartsWith("/scalar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigureEndpoints_DoesNotMapDefaultScalarApiReferenceEndpointOutsideDevelopment()
    {
        var routePatterns = await GetMappedDocumentationRoutePatternsAsync(
            CreateContext(new AppSurfaceWebScalarModule(), Environments.Production));

        Assert.DoesNotContain(routePatterns, pattern => pattern.StartsWith("/scalar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigureEndpoints_MapsScalarOutsideDevelopmentWhenScalarAndOpenApiExposureAreAlways()
    {
        var routePatterns = await GetMappedDocumentationRoutePatternsAsync(
            CreateContext(new AppSurfaceWebScalarModule(), Environments.Production),
            services =>
            {
                services.Configure<AppSurfaceWebOpenApiOptions>(options =>
                    options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Always);
                services.Configure<AppSurfaceWebScalarOptions>(options =>
                    options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Always);
            });

        Assert.Contains("/openapi/{documentName}.json", routePatterns);
        Assert.Contains(routePatterns, pattern => pattern.StartsWith("/scalar", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Development", AppSurfaceApiDocumentationEndpointExposure.Always, AppSurfaceApiDocumentationEndpointExposure.Never, true, false)]
    [InlineData("Development", AppSurfaceApiDocumentationEndpointExposure.Never, AppSurfaceApiDocumentationEndpointExposure.Always, false, false)]
    [InlineData("Development", AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly, AppSurfaceApiDocumentationEndpointExposure.Never, true, false)]
    [InlineData("Development", AppSurfaceApiDocumentationEndpointExposure.Never, AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly, false, false)]
    [InlineData("Production", AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly, AppSurfaceApiDocumentationEndpointExposure.Always, false, false)]
    [InlineData("Production", AppSurfaceApiDocumentationEndpointExposure.Always, AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly, true, false)]
    [InlineData("Production", AppSurfaceApiDocumentationEndpointExposure.Never, AppSurfaceApiDocumentationEndpointExposure.Always, false, false)]
    [InlineData("Production", AppSurfaceApiDocumentationEndpointExposure.Always, AppSurfaceApiDocumentationEndpointExposure.Never, true, false)]
    public async Task ConfigureEndpoints_RequiresScalarAndOpenApiExposureInCurrentEnvironment(
        string environment,
        AppSurfaceApiDocumentationEndpointExposure openApiExposure,
        AppSurfaceApiDocumentationEndpointExposure scalarExposure,
        bool expectedOpenApiMapped,
        bool expectedScalarMapped)
    {
        var routePatterns = await GetMappedDocumentationRoutePatternsAsync(
            CreateContext(new AppSurfaceWebScalarModule(), environment),
            services =>
            {
                services.Configure<AppSurfaceWebOpenApiOptions>(options =>
                    options.ExposeEndpoint = openApiExposure);
                services.Configure<AppSurfaceWebScalarOptions>(options =>
                    options.ExposeEndpoint = scalarExposure);
            });

        Assert.Equal(expectedOpenApiMapped, routePatterns.Contains("/openapi/{documentName}.json"));
        Assert.Equal(
            expectedScalarMapped,
            routePatterns.Any(pattern => pattern.StartsWith("/scalar", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ConfigureEndpoints_DoesNotMapOpenApiEndpointItself()
    {
        var routePatterns = await GetMappedScalarOnlyRoutePatternsAsync(
            CreateContext(new AppSurfaceWebScalarModule(), Environments.Production),
            services =>
            {
                services.Configure<AppSurfaceWebOpenApiOptions>(options =>
                    options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Always);
                services.Configure<AppSurfaceWebScalarOptions>(options =>
                    options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Always);
            });

        Assert.DoesNotContain("/openapi/{documentName}.json", routePatterns);
        Assert.Contains(routePatterns, pattern => pattern.StartsWith("/scalar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoOpLifecycleMethods_AreSafeToCallWithNormalInputs()
    {
        var module = new AppSurfaceWebScalarModule();
        var context = CreateContext(module, Environments.Development);
        var services = new ServiceCollection();
        var hostBuilder = Host.CreateDefaultBuilder();
        var appBuilder = WebApplication.CreateBuilder();
        await using var app = appBuilder.Build();

        var exception = Record.Exception(() =>
        {
            module.ConfigureServices(context, services);
            module.ConfigureHostBeforeServices(context, hostBuilder);
            module.ConfigureHostAfterServices(context, hostBuilder);
            module.ConfigureWebApplication(context, app);
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task AppSurfaceWebApp_ComposesScalarAndOpenApiEndpoints()
    {
        var module = new AppSurfaceWebScalarModule();
        var startup = new TestScalarStartup();
        startup.WithOptions(options =>
        {
            options.MapEndpoints = endpoints =>
            {
                endpoints
                    .MapGet("/health", () => Results.Ok(new { status = "ok" }))
                    .WithName("Health");
            };
        });

        var context = CreateContext(module, Environments.Development);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var baseAddress = GetBaseAddress(host);
            using var client = new HttpClient
            {
                BaseAddress = new Uri(baseAddress)
            };

            using var openApiResponse = await client.GetAsync("/openapi/v1.json");
            var openApiJson = await openApiResponse.Content.ReadAsStringAsync();

            using var scalarResponse = await client.GetAsync("/scalar/");
            var scalarHtml = await scalarResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, openApiResponse.StatusCode);
            using var openApiDocument = JsonDocument.Parse(openApiJson);
            Assert.Equal(
                "ScalarTestApp | v1",
                openApiDocument.RootElement.GetProperty("info").GetProperty("title").GetString());
            Assert.True(openApiDocument.RootElement.GetProperty("paths").TryGetProperty("/health", out _));

            Assert.Equal(HttpStatusCode.OK, scalarResponse.StatusCode);
            Assert.Equal("text/html", scalarResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Scalar", scalarHtml, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task AppSurfaceWebApp_HidesScalarAndOpenApiEndpointsOutsideDevelopmentByDefault()
    {
        var module = new AppSurfaceWebScalarModule();
        var startup = new TestScalarStartup();
        var context = CreateContext(module, Environments.Production);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(GetBaseAddress(host))
            };

            using var openApiResponse = await client.GetAsync("/openapi/v1.json");
            using var scalarResponse = await client.GetAsync("/scalar/");

            Assert.Equal(HttpStatusCode.NotFound, openApiResponse.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, scalarResponse.StatusCode);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static StartupContext CreateContext(AppSurfaceWebScalarModule module) =>
        CreateContext(module, Environments.Production);

    private static StartupContext CreateContext(AppSurfaceWebScalarModule module, string environment) =>
        new(["--environment", environment], module, "ScalarTestApp");

    private static ServiceCollection CreateServicesWithConfiguration(
        Dictionary<string, string?>? configurationValues = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        return services;
    }

    private static async Task<string[]> GetMappedDocumentationRoutePatternsAsync(
        StartupContext context,
        Action<IServiceCollection>? configureServices = null)
    {
        var openApiModule = new AppSurfaceWebOpenApiModule();
        var scalarModule = new AppSurfaceWebScalarModule();
        var builder = WebApplication.CreateBuilder();
        openApiModule.ConfigureServices(context, builder.Services);
        scalarModule.ConfigureServices(context, builder.Services);
        configureServices?.Invoke(builder.Services);
        await using var app = builder.Build();

        openApiModule.ConfigureEndpoints(context, app);
        scalarModule.ConfigureEndpoints(context, app);

        return GetRoutePatterns(app);
    }

    private static async Task<string[]> GetMappedScalarOnlyRoutePatternsAsync(
        StartupContext context,
        Action<IServiceCollection>? configureServices = null)
    {
        var openApiModule = new AppSurfaceWebOpenApiModule();
        var scalarModule = new AppSurfaceWebScalarModule();
        var builder = WebApplication.CreateBuilder();
        openApiModule.ConfigureServices(context, builder.Services);
        scalarModule.ConfigureServices(context, builder.Services);
        configureServices?.Invoke(builder.Services);
        await using var app = builder.Build();

        scalarModule.ConfigureEndpoints(context, app);

        return GetRoutePatterns(app);
    }

    private static string[] GetRoutePatterns(IEndpointRouteBuilder endpoints) =>
        endpoints
            .DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .OfType<string>()
            .ToArray();

    private static string GetBaseAddress(IHost host)
    {
        var addresses = host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;

        return Assert.Single(addresses ?? []);
    }

    private sealed class TestScalarStartup : WebStartup<AppSurfaceWebScalarModule>;
}
