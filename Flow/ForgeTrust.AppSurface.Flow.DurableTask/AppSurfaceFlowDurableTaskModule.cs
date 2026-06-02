using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Registers the AppSurface Flow Durable Task adapter boundary.
/// </summary>
/// <remarks>
/// The module is passive. It declares the core Flow dependency, registers durable adapter services, and leaves Durable
/// Task worker/client hosting to the application. This package intentionally does not register storage providers,
/// background workers, endpoints, authentication handlers, or Semantic Kernel services.
/// </remarks>
public class AppSurfaceFlowDurableTaskModule : IAppSurfaceModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddOptions<AppSurfaceFlowDurableTaskOptions>();
        services.TryAddSingleton<IFlowContextSerializer, SystemTextJsonFlowContextSerializer>();
        services.TryAddSingleton<FlowContextSerializationValidator>();
        services.TryAddSingleton<IFlowResumeAuthorizer, DenyAllFlowResumeAuthorizer>();
        services.TryAddSingleton(typeof(IDurableTaskFlowRunner<>), typeof(DurableTaskFlowRunner<>));
        services.TryAddSingleton(typeof(IDurableTaskFlowClient<>), typeof(DurableTaskFlowClient<>));
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceFlowModule>();
    }
}
