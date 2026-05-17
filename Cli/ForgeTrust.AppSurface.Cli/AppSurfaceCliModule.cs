using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Represents the AppSurface CLI root module used to bootstrap command execution.
/// </summary>
/// <remarks>
/// The module is intentionally empty today: the CLI owns command registration in <see cref="AppSurfaceCliApp"/> and
/// delegates web-host behavior to RazorDocs-specific runners. Add dependencies here only when every AppSurface CLI
/// command needs the same module-level dependency graph or host lifecycle hook. Prefer command-local services or custom
/// test registrations for isolated behavior, and do not place runtime command logic in this module.
/// </remarks>
internal sealed class AppSurfaceCliModule : IAppSurfaceHostModule
{
    /// <summary>
    /// Configures shared CLI services after the default command runtime registrations have been added.
    /// </summary>
    /// <param name="context">Startup context for the CLI run.</param>
    /// <param name="services">Service collection that will back command construction.</param>
    /// <remarks>
    /// The default implementation is a no-op. Keep it empty unless a service truly applies to the whole CLI surface.
    /// </remarks>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    /// <summary>
    /// Configures a Generic Host builder before services are registered.
    /// </summary>
    /// <param name="context">Startup context for the CLI run.</param>
    /// <param name="builder">Host builder that would be configured by host-based startup paths.</param>
    /// <remarks>
    /// The command runtime does not currently build a Generic Host through this module, so this hook is intentionally a
    /// no-op and exists to satisfy the shared AppSurface host-module contract.
    /// </remarks>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Configures a Generic Host builder after services are registered.
    /// </summary>
    /// <param name="context">Startup context for the CLI run.</param>
    /// <param name="builder">Host builder that would be configured by host-based startup paths.</param>
    /// <remarks>
    /// Keep this empty until the CLI adopts a host-backed lifecycle. Command behavior should stay in command classes.
    /// </remarks>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Registers root-module dependencies for the AppSurface CLI module graph.
    /// </summary>
    /// <param name="builder">Dependency builder used by AppSurface startup composition.</param>
    /// <remarks>
    /// The CLI has no module dependencies by default. Add dependencies here only for cross-command infrastructure that
    /// must participate in AppSurface module ordering.
    /// </remarks>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
