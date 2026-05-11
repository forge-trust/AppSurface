using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// Defines the entry point for starting and running an AppSurface application.
/// </summary>
/// <remarks>
/// <see cref="IAppSurfaceStartup.CreateHostBuilder"/> is called before <see cref="IAppSurfaceStartup.RunAsync"/>. The
/// implementation configures an <see cref="IHostBuilder"/>, but the framework or caller owns starting, stopping, and
/// disposing the built <see cref="IHost"/>. <see cref="CreateHostBuilder"/> must not start the host or perform blocking
/// I/O and should be idempotent unless documented otherwise. <see cref="RunAsync"/> may be called once per
/// <see cref="StartupContext"/>, should remain asynchronous, and should honor cancellation exposed by the configured
/// host.
/// </remarks>
public interface IAppSurfaceStartup
{
    /// <summary>
    /// Creates and configures the <see cref="IHostBuilder"/> for the application.
    /// </summary>
    /// <param name="context">The startup context containing configuration and arguments.</param>
    /// <returns>A configured host builder.</returns>
    IHostBuilder CreateHostBuilder(StartupContext context);

    /// <summary>
    /// Runs the application asynchronously using the provided startup context.
    /// </summary>
    /// <param name="context">The startup context for the application.</param>
    /// <returns>A task that represents the asynchronous run operation.</returns>
    Task RunAsync(StartupContext context);
}
