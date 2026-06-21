using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Observability;

/// <summary>
/// AppSurface host module that registers application-side OpenTelemetry logging, tracing, and metrics.
/// </summary>
/// <remarks>
/// Add this module to an AppSurface application's dependency graph when the app should publish operational telemetry to
/// Aspire or another OTLP collector. The module reads <see cref="AppSurfaceObservabilityOptions"/> from configuration,
/// uses <see cref="StartupContext.ApplicationName"/> as the default service name, and skips exporter registration when
/// <see cref="AppSurfaceOtlpExporterMode.WhenEndpointConfigured"/> is active without an endpoint. It does not define
/// Aspire resources, dashboards, product analytics, request-body capture, or package-specific AppSurface spans.
/// </remarks>
public sealed class AppSurfaceObservabilityModule : IAppSurfaceHostModule
{
    /// <summary>
    /// Registers options metadata for service consumers that inspect <see cref="AppSurfaceObservabilityOptions"/>.
    /// </summary>
    /// <param name="context">The current startup context.</param>
    /// <param name="services">The service collection receiving options registration.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.ConfigureAppSurfaceObservability();
    }

    /// <summary>
    /// Registers OpenTelemetry logging with access to host configuration.
    /// </summary>
    /// <param name="context">The current startup context.</param>
    /// <param name="builder">The host builder to configure.</param>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
        builder.ConfigureLogging((hostContext, logging) =>
            logging.AddAppSurfaceObservabilityLogging(context, hostContext.Configuration));
    }

    /// <summary>
    /// Registers OpenTelemetry tracing and metrics with access to host configuration.
    /// </summary>
    /// <param name="context">The current startup context.</param>
    /// <param name="builder">The host builder to configure.</param>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
        builder.ConfigureServices((hostContext, services) =>
            services.AddAppSurfaceObservability(context, hostContext.Configuration));
    }

    /// <summary>
    /// Registers dependent modules; no dependencies are required.
    /// </summary>
    /// <param name="builder">The dependency builder.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
