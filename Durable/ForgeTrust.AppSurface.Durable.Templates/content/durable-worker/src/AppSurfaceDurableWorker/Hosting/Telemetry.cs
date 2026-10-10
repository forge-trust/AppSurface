using ForgeTrust.AppSurface.Observability;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace AppSurfaceDurableWorker.Hosting;

/// <summary>Registers an SDK listener for the canonical AppSurface activity source and an optional OTLP exporter.</summary>
internal static class TemplateTelemetry
{
    /// <summary>Configures sampling, canonical source listening, and the optional OTLP export destination.</summary>
    /// <param name="services">Host service collection.</param>
    /// <param name="endpoint">Validated OTLP endpoint, or null to listen without a network exporter.</param>
    internal static void Configure(IServiceCollection services, Uri? endpoint)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOpenTelemetry().WithTracing(tracing =>
        {
            tracing
                .AddSource(AppSurfaceTelemetrySources.ActivitySourceName)
                .SetSampler(new AlwaysOnSampler());
            if (endpoint is not null)
            {
                tracing.AddOtlpExporter(options => options.Endpoint = endpoint);
            }
        });
    }
}
