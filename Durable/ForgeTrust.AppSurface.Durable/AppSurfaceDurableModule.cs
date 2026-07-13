using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Registers host-neutral durable contracts and registries without starting storage or workers.
/// </summary>
/// <remarks>
/// Add the PostgreSQL module to select storage and explicitly opt into hosted execution. This module alone performs no
/// network access, schema creation, polling, scheduling, or provider work.
/// </remarks>
public sealed class AppSurfaceDurableModule : IAppSurfaceModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDurablePayloadCodecRegistry, DurablePayloadCodecRegistry>();
        services.TryAddSingleton<IDurableWorkRegistry, DurableWorkRegistry>();
        services.TryAddSingleton<IDurableFlowRegistry, DurableFlowRegistry>();
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddModule<AppSurfaceFlowModule>();
        builder.AddModule<AppSurfaceWorkersModule>();
    }
}
