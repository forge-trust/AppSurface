using OpenTelemetry.Resources;

namespace ForgeTrust.AppSurface.Observability;

internal static class ResourceBuilderFactory
{
    internal static ResourceBuilder Create(AppSurfaceObservabilityPlan plan)
    {
        return ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: plan.ServiceName,
                serviceVersion: plan.ServiceVersion,
                serviceInstanceId: null);
    }
}
