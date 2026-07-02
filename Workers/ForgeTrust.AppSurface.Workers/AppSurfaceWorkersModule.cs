using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Registers the passive AppSurface durable worker contract surface.
/// </summary>
/// <remarks>
/// The module intentionally does not register queues, storage providers, background services, Durable Task workers,
/// EF Core contexts, Postgres connections, endpoints, or authentication handlers. Host applications own runtime
/// infrastructure and persistence; this package provides the shared worker contracts.
/// </remarks>
public sealed class AppSurfaceWorkersModule : IAppSurfaceModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
    }
}
