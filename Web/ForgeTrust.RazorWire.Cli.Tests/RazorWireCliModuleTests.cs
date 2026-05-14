using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.RazorWire.Cli.Tests;

public class RazorWireCliModuleTests
{
    [Fact]
    public void ConfigureServices_Should_Register_Expected_Services()
    {
        var module = new RazorWireCliModule();
        var services = new ServiceCollection();
        var context = new StartupContext([], new TestHostModule());

        module.ConfigureServices(context, services);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<ExportEngine>());
        Assert.NotNull(provider.GetService<ExportSourceRequestFactory>());
        Assert.NotNull(provider.GetService<ExportSourceResolver>());
        Assert.NotNull(provider.GetService<ITargetAppProcessFactory>());
        Assert.NotNull(provider.GetService<IHttpClientFactory>());
    }

    [Fact]
    public void Noop_Host_Methods_Should_Not_Throw()
    {
        var module = new RazorWireCliModule();
        var context = new StartupContext([], new TestHostModule());
        var hostBuilder = Host.CreateDefaultBuilder([]);
        var depBuilder = new ModuleDependencyBuilder();

        module.ConfigureHostBeforeServices(context, hostBuilder);
        module.ConfigureHostAfterServices(context, hostBuilder);
        module.RegisterDependentModules(depBuilder);
    }

    private sealed class TestHostModule : IAppSurfaceHostModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services) { }
        public void RegisterDependentModules(ModuleDependencyBuilder builder) { }
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) { }
        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }
    }
}
