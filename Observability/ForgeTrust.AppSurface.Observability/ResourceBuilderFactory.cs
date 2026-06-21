using OpenTelemetry.Resources;

namespace ForgeTrust.AppSurface.Observability;

internal static class ResourceBuilderFactory
{
    /// <summary>
    /// Creates the shared OpenTelemetry resource builder for AppSurface observability providers.
    /// </summary>
    /// <param name="plan">The resolved plan that supplies the service identity metadata.</param>
    /// <returns>A resource builder containing OpenTelemetry defaults plus AppSurface service identity values.</returns>
    /// <remarks>
    /// Use this factory when logging, tracing, or metrics registration must share the same resource identity. It starts
    /// from <see cref="ResourceBuilder.CreateDefault"/> so OpenTelemetry's standard SDK, process, and host defaults remain
    /// available, then applies the AppSurface service name and optional version. Hosts that need additional attributes can
    /// still add them through their own OpenTelemetry configuration after AppSurface registration.
    /// </remarks>
    internal static ResourceBuilder Create(AppSurfaceObservabilityPlan plan)
    {
        return ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: plan.ServiceName,
                serviceVersion: plan.ServiceVersion,
                serviceInstanceId: null);
    }
}
