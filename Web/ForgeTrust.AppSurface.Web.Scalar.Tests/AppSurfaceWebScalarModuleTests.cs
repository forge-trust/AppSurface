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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    public async Task ConfigureEndpoints_MapsDefaultScalarApiReferenceEndpoint()
    {
        var module = new AppSurfaceWebScalarModule();
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();

        module.ConfigureEndpoints(CreateContext(module), app);

        var routeEndpoints = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>();

        Assert.Contains(
            routeEndpoints,
            endpoint => endpoint.RoutePattern.RawText?.StartsWith("/scalar", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task NoOpLifecycleMethods_AreSafeToCallWithNormalInputs()
    {
        var module = new AppSurfaceWebScalarModule();
        var context = CreateContext(module);
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

        var context = CreateContext(module);
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

    private static StartupContext CreateContext(AppSurfaceWebScalarModule module) =>
        new([], module, "ScalarTestApp");

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
