using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.Tests;

public sealed class AppSurfaceAuthModuleTests
{
    [Fact]
    public void ConfigureServices_RegistersAuthOptions()
    {
        var services = new ServiceCollection();
        var context = new StartupContext([], new TestHostModule());
        var module = new AppSurfaceAuthModule();

        module.ConfigureServices(context, services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceAuthOptions>>();

        Assert.NotNull(options.Value);
    }

    [Fact]
    public void RegisterDependentModules_DoesNothing()
    {
        var module = new AppSurfaceAuthModule();
        var builder = new ModuleDependencyBuilder();

        module.RegisterDependentModules(builder);

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
