using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// Defines a module that can specifically configure the <see cref="IHostBuilder"/> before and after service registration.
/// Only the root module of an application (or explicitly marked host modules) usually implements this.
/// </summary>
public interface IAppSurfaceHostModule : IAppSurfaceModule
{
    /// <summary>
    /// Configures the <see cref="IHostBuilder"/> before any modules have their services configured.
    /// </summary>
    /// <param name="context">The context for the current startup process.</param>
    /// <param name="builder">The host builder to configure.</param>
    void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder);

    /// <summary>
    /// Configures the <see cref="IHostBuilder"/> after all modules have their services configured.
    /// </summary>
    /// <param name="context">The context for the current startup process.</param>
    /// <param name="builder">The host builder to configure.</param>
    void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder);
}
