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
    /// <remarks>
    /// Use this helper when a test needs the same flow definition, in-memory host, DurableTask-facing
    /// runner/client, and product state store wiring that the lab exposes through public app/report
    /// surfaces. Prefer bespoke service wiring only for tests that intentionally vary those registrations.
    /// The default authorizer is <see cref="ProductReadinessResumeAuthorizer" />, and the readiness
    /// report service is omitted by default so workflow-only tests do not carry report dependencies.
    /// Callers own the returned provider and must dispose it, normally with <c>await using</c>, so
    /// disposable stores and hosted dependencies are released at the end of the test.
    /// </remarks>
    /// <param name="store">Product state store backing the test graph. The helper registers the supplied instance as a singleton.</param>
    /// <param name="authorizer">Optional resume authorizer override; when <see langword="null" />, the default lab authorizer is used.</param>
    /// <param name="includeReportService">Whether to include the readiness report service; defaults to <see langword="false" />.</param>
    /// <returns>A service provider owning the test graph and any disposable singleton dependencies.</returns>
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
