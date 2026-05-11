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

        module.ConfigureServices(CreateContext(module), services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OpenApiOptions>>().Get("v1");

        Assert.Equal("v1", options.DocumentName);
        Assert.Contains(services, service => service.ServiceType == typeof(IApiDescriptionGroupCollectionProvider));
    }

    [Fact]
    public async Task ConfigureEndpoints_MapsDefaultOpenApiEndpoint()
    {
        var module = new AppSurfaceWebOpenApiModule();
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();

        module.ConfigureEndpoints(CreateContext(module), app);

        var routeEndpoints = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>();

        Assert.Contains(
            routeEndpoints,
            endpoint => endpoint.RoutePattern.RawText == "/openapi/{documentName}.json");
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
        new([], module, "OpenApiTestApp");

    private static async Task<JsonDocument> GetOpenApiDocumentAsync(Action<IEndpointRouteBuilder> mapEndpoints)
    {
        var startup = new TestOpenApiStartup();
        startup.WithOptions(options => options.MapEndpoints = mapEndpoints);

        var module = new AppSurfaceWebOpenApiModule();
        var context = CreateContext(module);
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
