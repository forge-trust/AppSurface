using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AspireAppHostExample;

/// <summary>
/// Defines the AppSurface module used by the Aspire AppHost example.
/// </summary>
/// <remarks>
/// The example module intentionally keeps the host lifecycle hooks empty because
/// the Aspire-specific behavior is supplied by <see cref="LocalProfile"/> and
/// its components.
/// </remarks>
public sealed class ExampleModule : IAppSurfaceHostModule
{
    /// <summary>
    /// Registers application services for the example module.
    /// </summary>
    /// <param name="context">The AppSurface startup context for the current host.</param>
    /// <param name="services">The service collection that receives module services.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    /// <summary>
    /// Registers dependent modules required by the example module.
    /// </summary>
    /// <param name="builder">The dependency builder used to compose module startup order.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    /// <summary>
    /// Configures the host before services are built.
    /// </summary>
    /// <param name="context">The AppSurface startup context for the current host.</param>
    /// <param name="builder">The host builder being prepared by AppSurface.</param>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Configures the host after module services are registered.
    /// </summary>
    /// <param name="context">The AppSurface startup context for the current host.</param>
    /// <param name="builder">The host builder being prepared by AppSurface.</param>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}
