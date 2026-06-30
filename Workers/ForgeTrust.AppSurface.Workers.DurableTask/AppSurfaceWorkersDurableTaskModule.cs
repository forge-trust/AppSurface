using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Flow.DurableTask;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Workers.DurableTask;

/// <summary>
/// Registers the passive AppSurface Workers Durable Task adapter boundary.
/// </summary>
/// <remarks>
/// The module depends on AppSurface Workers and Flow DurableTask, registers decision-mapping services, and leaves
/// Durable Task worker/client hosting, storage providers, endpoints, authentication, and background services to the
/// application.
/// </remarks>
public sealed class AppSurfaceWorkersDurableTaskModule : IAppSurfaceModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AppSurfaceWorkersDurableTaskOptions>();
        services.TryAddSingleton(typeof(IDurableTaskWorkerChainRunner<,,>), typeof(DurableTaskWorkerChainRunner<,,>));
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModule<AppSurfaceWorkersModule>();
        builder.AddModule<AppSurfaceFlowDurableTaskModule>();
    }
}
