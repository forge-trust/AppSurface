using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Observability;

internal sealed class AppSurfaceObservabilityStartupDiagnostic(
    AppSurfaceObservabilityPlan plan,
    ILogger<AppSurfaceObservabilityStartupDiagnostic> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (plan.ShouldLogSkippedExporterDiagnostic)
        {
            logger.LogInformation(
                "AppSurface observability registered logging, tracing, and metrics without OTLP export because no endpoint was configured. Set {Section}:OtlpEndpoint, {Section}__OtlpEndpoint, or {OtlpEndpointEnvironmentVariable}, or set ExporterMode to Always or Never.",
                AppSurfaceObservabilityOptions.SectionName,
                AppSurfaceObservabilityOptions.SectionName,
                AppSurfaceObservabilityOptions.OtlpEndpointEnvironmentVariable);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
