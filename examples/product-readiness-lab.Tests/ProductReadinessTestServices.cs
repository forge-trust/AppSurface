using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Flow.DurableTask;
using Microsoft.Extensions.DependencyInjection;

namespace ProductReadinessLab.Tests;

/// <summary>
/// Builds product-readiness lab service providers for tests that exercise the public app/report surfaces.
/// </summary>
internal static class ProductReadinessTestServices
{
    /// <summary>
    /// Creates the shared in-process host graph used by report and workflow regression tests.
    /// </summary>
    /// <param name="store">Product state store backing the test graph.</param>
    /// <param name="authorizer">Optional resume authorizer override for caller-capture tests.</param>
    /// <param name="includeReportService">Whether to include the readiness report service.</param>
    /// <returns>A service provider owning the test graph.</returns>
    public static ServiceProvider BuildProvider(
        IProductStateStore store,
        IFlowResumeAuthorizer? authorizer = null,
        bool includeReportService = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton(store);
        services.AddSingleton(ProductReadinessFlowDefinition.Build());
        services.AddSingleton<IFlowDefinitionRegistry>(sp =>
        {
            var registry = new FlowDefinitionRegistry();
            registry.Register(sp.GetRequiredService<FlowDefinition<ProductApprovalState>>());
            return registry;
        });
        services.AddSingleton(authorizer ?? new ProductReadinessResumeAuthorizer());
        services.AddSingleton<FlowContextSerializationValidator>();
        services.AddSingleton<IFlowContextSerializer, SystemTextJsonFlowContextSerializer>();
        services.AddOptions<AppSurfaceFlowDurableTaskOptions>();
        services.AddSingleton(typeof(IDurableTaskFlowRunner<>), typeof(DurableTaskFlowRunner<>));
        services.AddSingleton(typeof(IDurableTaskFlowClient<>), typeof(DurableTaskFlowClient<>));
        services.AddSingleton<ProductApprovalInProcessHost>();

        if (includeReportService)
        {
            services.AddSingleton<ProductReadinessReportService>();
        }

        return services.BuildServiceProvider();
    }
}
