using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ConsoleAppExample;

public class ExampleModule : IAppSurfaceHostModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        // Register services for the application here if needed
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        // Add module dependencies here if needed
        builder.AddModule<AppSurfaceConfigModule>();
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}
