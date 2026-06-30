using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Workers.Tests;

public sealed class AppSurfaceWorkersModuleTests
{
    [Fact]
    public void ConfigureServices_DoesNotRegisterRuntimeInfrastructure()
    {
        var services = new ServiceCollection();

        new AppSurfaceWorkersModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);

        Assert.Empty(services);
    }

    [Fact]
    public void RegisterDependentModules_AddsNoRuntimeDependencies()
    {
        var builder = new ModuleDependencyBuilder();

        new AppSurfaceWorkersModule().RegisterDependentModules(builder);

        Assert.Empty(builder.Modules);
    }

    private sealed class TestHostModule : IAppSurfaceHostModule
    {
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }
}
