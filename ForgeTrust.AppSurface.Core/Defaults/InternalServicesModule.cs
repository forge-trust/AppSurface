using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Core.Defaults;

/// <summary>
/// Internal Services Registration
/// </summary>
internal class InternalServicesModule : IAppSurfaceModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton(context.EnvironmentProvider);
        services.AddSingleton(context);
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
