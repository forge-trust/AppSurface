using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire.Bridge;
using ForgeTrust.RazorWire.Forms;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireWebModuleTests
{
    [Fact]
    public void IncludeAsApplicationPart_IsTrue()
    {
        var module = new RazorWireWebModule();
        Assert.True(module.IncludeAsApplicationPart);
    }

    [Fact]
    public void ConfigureWebOptions_InDevelopment_UpgradesMvcAndAddsConfigureMvc()
    {
        // Arrange
        var module = new RazorWireWebModule();
        var context = CreateContext(isDevelopment: true);
        var options = new WebOptions
        {
            Mvc = new MvcOptions { MvcSupportLevel = MvcSupport.Controllers }
        };

        // Act
        module.ConfigureWebOptions(context, options);

        // Assert
        Assert.Equal(MvcSupport.ControllersWithViews, options.Mvc.MvcSupportLevel);
        Assert.NotNull(options.Mvc.ConfigureMvc);
        AssertConfigureMvcAddsAntiforgeryFilter(options.Mvc.ConfigureMvc);
    }

    [Fact]
    public void ConfigureWebOptions_InProduction_WithSufficientMvc_PreservesSupportAndAddsConfigureMvc()
    {
        // Arrange
        var module = new RazorWireWebModule();
        var context = CreateContext(isDevelopment: false);
        Action<IMvcBuilder>? configure = _ => { };
        var options = new WebOptions
        {
            Mvc = new MvcOptions
            {
                MvcSupportLevel = MvcSupport.Full,
                ConfigureMvc = configure
            }
        };

        // Act
        module.ConfigureWebOptions(context, options);

        // Assert
        Assert.Equal(MvcSupport.Full, options.Mvc.MvcSupportLevel);
        Assert.NotNull(options.Mvc.ConfigureMvc);
        Assert.NotSame(configure, options.Mvc.ConfigureMvc);
        Assert.Contains(options.Mvc.ConfigureMvc!.GetInvocationList(), callback => ReferenceEquals(callback, configure));
        AssertConfigureMvcAddsAntiforgeryFilter(options.Mvc.ConfigureMvc);
    }

    [Fact]
    public void ConfigureWebOptions_InProduction_WithSufficientMvcAndNoExistingConfigure_AddsConfigureMvc()
    {
        // Arrange
        var module = new RazorWireWebModule();
        var context = CreateContext(isDevelopment: false);
        var options = new WebOptions
        {
            Mvc = new MvcOptions
            {
                MvcSupportLevel = MvcSupport.Full
            }
        };

        // Act
        module.ConfigureWebOptions(context, options);

        // Assert
        Assert.Equal(MvcSupport.Full, options.Mvc.MvcSupportLevel);
        Assert.NotNull(options.Mvc.ConfigureMvc);
        AssertConfigureMvcAddsAntiforgeryFilter(options.Mvc.ConfigureMvc);
    }

    [Fact]
    public void ConfigureServices_RegistersRazorWireAndOutputCacheServices()
    {
        // Arrange
        var module = new RazorWireWebModule();
        var services = new ServiceCollection();

        // Act
        module.ConfigureServices(CreateContext(isDevelopment: false), services);
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.NotNull(provider.GetService<IRazorWireStreamHub>());
        Assert.NotNull(provider.GetService<IRazorWireChannelAuthorizer>());
        Assert.NotNull(provider.GetService<IOptions<RazorWireOptions>>());
        Assert.NotNull(provider.GetService<IOptions<OutputCacheOptions>>());
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IRazorPartialRenderer));
    }

    [Fact]
    public async Task ConfigureEndpoints_MapsRazorWireStreamEndpoint()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(new RazorWireOptions());
        builder.Services.AddSingleton<IRazorWireChannelAuthorizer, DefaultRazorWireChannelAuthorizer>();
        builder.Services.AddSingleton<IRazorWireStreamHub, InMemoryRazorWireStreamHub>();
        await using var app = builder.Build();
        var module = new RazorWireWebModule();

        // Act
        module.ConfigureEndpoints(CreateContext(isDevelopment: false), app);

        // Assert
        var routeBuilder = (IEndpointRouteBuilder)app;
        var routeEndpoint = Assert.Single(
            routeBuilder.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>());

        Assert.Equal("/_rw/streams/{channel}", routeEndpoint.RoutePattern.RawText);
    }

    [Fact]
    public void NoOpMethods_DoNotThrow()
    {
        var module = new RazorWireWebModule();
        var context = CreateContext(isDevelopment: false);
        var hostBuilder = Host.CreateDefaultBuilder();

        module.RegisterDependentModules(new ModuleDependencyBuilder());
        module.ConfigureHostBeforeServices(context, hostBuilder);
        module.ConfigureHostAfterServices(context, hostBuilder);
    }

    private static StartupContext CreateContext(bool isDevelopment)
    {
        return new StartupContext(
            [],
            new DummyRootModule(),
            EnvironmentProvider: new TestEnvironmentProvider(isDevelopment));
    }

    private static void AssertConfigureMvcAddsAntiforgeryFilter(Action<IMvcBuilder>? configureMvc)
    {
        Assert.NotNull(configureMvc);
        var services = new ServiceCollection();
        var builder = services.AddControllersWithViews();

        configureMvc(builder);

        using var provider = services.BuildServiceProvider();
        var mvcOptions = provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.MvcOptions>>().Value;
        Assert.Contains(
            mvcOptions.Filters,
            filter => filter is Microsoft.AspNetCore.Mvc.ServiceFilterAttribute serviceFilter
                      && serviceFilter.ServiceType == typeof(RazorWireAntiforgeryFailureFilter));
    }

    private sealed class DummyRootModule : IAppSurfaceHostModule
    {
        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }

    private sealed class TestEnvironmentProvider : IEnvironmentProvider
    {
        public TestEnvironmentProvider(bool isDevelopment)
        {
            IsDevelopment = isDevelopment;
            Environment = isDevelopment ? "Development" : "Production";
        }

        public string Environment { get; }

        public bool IsDevelopment { get; }

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
    }
}
