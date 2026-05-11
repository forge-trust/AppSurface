using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Core.Defaults;

/// <summary>
/// A basic implementation of <see cref="IAppSurfaceHostModule"/> that does nothing.
/// This is useful for providing implementations of apps that do not require a module.
/// </summary>
public class NoHostModule : IAppSurfaceHostModule
{
    /// <summary>
    /// Configures services for the module. This implementation is empty.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="services">The service collection.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    /// <summary>
    /// Registers dependent modules. This implementation is empty.
    /// </summary>
    /// <param name="builder">The module dependency builder.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    /// <summary>
    /// Configures the host before services are registered. This implementation is empty.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="builder">The host builder.</param>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Configures the host after services are registered. This implementation is empty.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="builder">The host builder.</param>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}
