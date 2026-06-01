using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Registers the AppSurface Flow core services.
/// </summary>
/// <remarks>
/// The module is passive: it adds the in-memory runner, definition registry, and options used by local tests and
/// host-specific adapters. It does not start background workers, create storage, schedule timers, or replace AppSurface
/// startup composition.
/// </remarks>
public class AppSurfaceFlowModule : IAppSurfaceModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddOptions<AppSurfaceFlowOptions>();
        services.TryAddSingleton<IFlowDefinitionRegistry, FlowDefinitionRegistry>();
        services.TryAddSingleton(typeof(IFlowRunner<>), typeof(InMemoryFlowRunner<>));
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
