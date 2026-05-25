using System.Net;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.OpenApi.Tests;

public sealed class AppSurfaceWebOpenApiModuleTests
{
    [Fact]
    public void ConfigureServices_RegistersOpenApiAndEndpointApiExplorerServices()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var services = new ServiceCollection();

        module.ConfigureServices(CreateContext(module, Environments.Development), services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OpenApiOptions>>().Get("v1");

        Assert.Equal("v1", options.DocumentName);
        Assert.Contains(services, service => service.ServiceType == typeof(IApiDescriptionGroupCollectionProvider));
    }

    [Fact]
    public void ConfigureServices_UsesDevelopmentOnlyEndpointExposureByDefault()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var services = CreateServicesWithConfiguration();

        module.ConfigureServices(CreateContext(module, Environments.Development), services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceWebOpenApiOptions>>().Value;

        Assert.Equal(AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly, options.ExposeEndpoint);
    }

    [Fact]
    public void ConfigureServices_BindsEndpointExposureFromConfiguration()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var services = CreateServicesWithConfiguration(new Dictionary<string, string?>
        {
            [$"{AppSurfaceWebOpenApiOptions.SectionName}:ExposeEndpoint"] = "Always"
        });

        module.ConfigureServices(CreateContext(module, Environments.Production), services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceWebOpenApiOptions>>().Value;

        Assert.Equal(AppSurfaceApiDocumentationEndpointExposure.Always, options.ExposeEndpoint);
    }

    [Fact]
    public void ConfigureServices_AppliesCodeFirstEndpointExposureOverrides()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var services = CreateServicesWithConfiguration();

        module.ConfigureServices(CreateContext(module, Environments.Development), services);
        services.Configure<AppSurfaceWebOpenApiOptions>(options =>
            options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Never);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceWebOpenApiOptions>>().Value;

        Assert.Equal(AppSurfaceApiDocumentationEndpointExposure.Never, options.ExposeEndpoint);
    }

    [Fact]
    public void ConfigureServices_RejectsInvalidEndpointExposureValues()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var services = CreateServicesWithConfiguration(new Dictionary<string, string?>
        {
            [$"{AppSurfaceWebOpenApiOptions.SectionName}:ExposeEndpoint"] = "99"
        });

        module.ConfigureServices(CreateContext(module, Environments.Development), services);

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AppSurfaceWebOpenApiOptions>>().Value);

        Assert.Contains($"{AppSurfaceWebOpenApiOptions.SectionName}:ExposeEndpoint", exception.Message);
        Assert.Contains(nameof(AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly), exception.Message);
        Assert.Contains(nameof(AppSurfaceApiDocumentationEndpointExposure.Always), exception.Message);
        Assert.Contains(nameof(AppSurfaceApiDocumentationEndpointExposure.Never), exception.Message);
    }

    [Fact]
    public void AppSurfaceApiDocumentationEndpointExposure_PreservesNumericValues()
    {
        Assert.Equal(0, (int)AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly);
        Assert.Equal(1, (int)AppSurfaceApiDocumentationEndpointExposure.Always);
        Assert.Equal(2, (int)AppSurfaceApiDocumentationEndpointExposure.Never);
    }

    [Fact]
    public async Task ConfigureEndpoints_MapsDefaultOpenApiEndpointInDevelopment()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var routePatterns = await GetMappedOpenApiRoutePatternsAsync(
            module,
            CreateContext(module, Environments.Development));

        Assert.Contains("/openapi/{documentName}.json", routePatterns);
    }

    [Fact]
    public async Task ConfigureEndpoints_DoesNotMapDefaultOpenApiEndpointOutsideDevelopment()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var routePatterns = await GetMappedOpenApiRoutePatternsAsync(
            module,
            CreateContext(module, Environments.Production));

        Assert.DoesNotContain("/openapi/{documentName}.json", routePatterns);
    }

    [Fact]
    public async Task ConfigureEndpoints_MapsOpenApiEndpointOutsideDevelopmentWhenExposureIsAlways()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var routePatterns = await GetMappedOpenApiRoutePatternsAsync(
            module,
            CreateContext(module, Environments.Production),
            services => services.Configure<AppSurfaceWebOpenApiOptions>(options =>
                options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Always));

        Assert.Contains("/openapi/{documentName}.json", routePatterns);
    }

    [Fact]
    public async Task ConfigureEndpoints_DoesNotMapOpenApiEndpointInDevelopmentWhenExposureIsNever()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var routePatterns = await GetMappedOpenApiRoutePatternsAsync(
            module,
            CreateContext(module, Environments.Development),
            services => services.Configure<AppSurfaceWebOpenApiOptions>(options =>
                options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Never));

        Assert.DoesNotContain("/openapi/{documentName}.json", routePatterns);
    }

    [Fact]
    public async Task AppSurfaceWebApp_GeneratesDocumentTitleFromStartupContext()
    {
        using var document = await GetOpenApiDocumentAsync(endpoints =>
        {
            endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        });

        Assert.Equal(
            "OpenApiTestApp | v1",
            document.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task AppSurfaceWebApp_HidesOpenApiEndpointOutsideDevelopmentByDefault()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var startup = new TestOpenApiStartup();
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

            using var response = await client.GetAsync("/openapi/v1.json");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task AppSurfaceWebApp_RemovesAppSurfaceWebDocumentTags()
    {
        using var document = await GetOpenApiDocumentAsync(endpoints =>
        {
            endpoints
                .MapGet("/appsurface", () => Results.Ok())
                .WithTags("ForgeTrust.AppSurface.Web", "DocumentApi");
        });

        var documentTags = GetDocumentTags(document.RootElement);

        Assert.DoesNotContain("ForgeTrust.AppSurface.Web", documentTags);
        Assert.Contains("DocumentApi", documentTags);
    }

    [Fact]
    public async Task AppSurfaceWebApp_RemovesAppSurfaceWebOperationTagsAndPreservesUnrelatedTags()
    {
        using var document = await GetOpenApiDocumentAsync(endpoints =>
        {
            endpoints
                .MapGet("/mixed-tags", () => Results.Ok())
                .WithTags("ForgeTrust.AppSurface.Web", "PublicApi");
        });

        var operationTags = GetOperationTags(document.RootElement, "/mixed-tags", "get");

        Assert.DoesNotContain("ForgeTrust.AppSurface.Web", operationTags);
        Assert.Contains("PublicApi", operationTags);
    }

    [Fact]
    public async Task NoOpLifecycleMethods_AreSafeToCallWithNormalInputs()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var context = CreateContext(module);
        var hostBuilder = Host.CreateDefaultBuilder();
        var appBuilder = WebApplication.CreateBuilder();
        await using var app = appBuilder.Build();

        var exception = Record.Exception(() =>
        {
            module.RegisterDependentModules(new ModuleDependencyBuilder());
            module.ConfigureHostBeforeServices(context, hostBuilder);
            module.ConfigureHostAfterServices(context, hostBuilder);
            module.ConfigureWebApplication(context, app);
        });

        Assert.Null(exception);
    }

    private static StartupContext CreateContext(AppSurfaceWebOpenApiModule module) =>
        CreateContext(module, Environments.Production);

    private static StartupContext CreateContext(AppSurfaceWebOpenApiModule module, string environment) =>
        new(["--environment", environment], module, "OpenApiTestApp");

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

    private static async Task<string[]> GetMappedOpenApiRoutePatternsAsync(
        AppSurfaceWebOpenApiModule module,
        StartupContext context,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        module.ConfigureServices(context, builder.Services);
        configureServices?.Invoke(builder.Services);
        await using var app = builder.Build();

        module.ConfigureEndpoints(context, app);

        return ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .OfType<string>()
            .ToArray();
    }

    private static async Task<JsonDocument> GetOpenApiDocumentAsync(Action<IEndpointRouteBuilder> mapEndpoints)
    {
        var startup = new TestOpenApiStartup();
        startup.WithOptions(options => options.MapEndpoints = mapEndpoints);

        var module = new AppSurfaceWebOpenApiModule();
        var context = CreateContext(module, Environments.Development);
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

            using var response = await client.GetAsync("/openapi/v1.json");
            var openApiJson = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(openApiJson);
        }
        finally
        {
            await host.StopAsync();
        }
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

    private static string[] GetDocumentTags(JsonElement document)
    {
        if (!document.TryGetProperty("tags", out var tags))
        {
            return [];
        }

        return tags
            .EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString())
            .OfType<string>()
            .ToArray();
    }

    private static string[] GetOperationTags(JsonElement document, string path, string method)
    {
        return document
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method)
            .GetProperty("tags")
            .EnumerateArray()
            .Select(tag => tag.GetString())
            .OfType<string>()
            .ToArray();
    }

    private sealed class TestOpenApiStartup : WebStartup<AppSurfaceWebOpenApiModule>;
}
