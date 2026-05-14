using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Core.Defaults;

/// <summary>
/// A basic implementation of <see cref="IAppSurfaceHostModule"/> that does nothing.
/// This is useful for providing implementations of apps that do not require a module.
/// </summary>
/// <remarks>
/// Use <see cref="NoHostModule"/> only when no part of the application expects module-provided services, dependency
/// registrations, middleware, endpoints, or host configuration. It is safe for minimal hosts that rely entirely on
/// framework defaults. Create a real <see cref="IAppSurfaceHostModule"/> when the app needs service registration,
/// dependency wiring, or host lifecycle hooks.
/// </remarks>
public class NoHostModule : IAppSurfaceHostModule
{
    /// <summary>
    /// Configures services for the module. This implementation is empty.
    /// </summary>
    /// <remarks>
    /// No services are added. Callers that expect module-owned services must not use <see cref="NoHostModule"/>.
    /// </remarks>
    /// <param name="context">The startup context.</param>
    /// <param name="services">The service collection.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    /// <summary>
    /// Registers dependent modules. This implementation is empty.
    /// </summary>
    /// <remarks>
    /// No dependencies are declared. Create a concrete module when startup ordering or dependency modules matter.
    /// </remarks>
    /// <param name="builder">The module dependency builder.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    /// <summary>
    /// Configures the host before services are registered. This implementation is empty.
    /// </summary>
    /// <remarks>
    /// No host configuration is applied before service registration.
    /// </remarks>
    /// <param name="context">The startup context.</param>
    /// <param name="builder">The host builder.</param>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Configures the host after services are registered. This implementation is empty.
    /// </summary>
    /// <remarks>
    /// No host configuration is applied after service registration.
    /// </remarks>
    /// <param name="context">The startup context.</param>
    /// <param name="builder">The host builder.</param>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}
