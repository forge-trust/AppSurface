using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

namespace ForgeTrust.AppSurface.Observability;

/// <summary>
/// Provides logging-builder extensions for AppSurface observability.
/// </summary>
public static class AppSurfaceObservabilityLoggingBuilderExtensions
{
    /// <summary>
    /// Adds AppSurface OpenTelemetry logging.
    /// </summary>
    /// <param name="builder">The logging builder to configure.</param>
    /// <param name="context">The AppSurface startup context that supplies the default service identity.</param>
    /// <param name="configuration">Configuration used to bind <see cref="AppSurfaceObservabilityOptions"/>.</param>
    /// <param name="configure">Optional code configuration that composes after bound configuration values.</param>
    /// <returns>The supplied <paramref name="builder"/>.</returns>
    /// <remarks>
    /// Repeated calls are safe and first registration wins, which keeps configured options aligned with the captured
    /// OpenTelemetry logger setup.
    /// </remarks>
    public static ILoggingBuilder AddAppSurfaceObservabilityLogging(
        this ILoggingBuilder builder,
        StartupContext context,
        IConfiguration configuration,
        Action<AppSurfaceObservabilityOptions>? configure = null)
    {
        return builder.AddAppSurfaceObservabilityLogging(context, configuration, configure, registerOptions: true);
    }

    /// <summary>
    /// Adds AppSurface OpenTelemetry logging with explicit control over options registration.
    /// </summary>
    /// <param name="builder">The logging builder receiving OpenTelemetry logging configuration.</param>
    /// <param name="context">The startup context used to resolve default service identity metadata.</param>
    /// <param name="configuration">Configuration used to bind and resolve observability options.</param>
    /// <param name="configure">Optional code configuration applied after bound configuration values.</param>
    /// <param name="registerOptions">
    /// <see langword="true"/> to bind and validate options in DI; <see langword="false"/> when another AppSurface
    /// registration path has already registered the same options.
    /// </param>
    /// <returns>The supplied <paramref name="builder"/>.</returns>
    /// <remarks>
    /// This overload is used by the host module so logging can be configured before service-provider OpenTelemetry.
    /// Registration is idempotent and first registration wins. The resolved plan is captured by the logging options
    /// callback, so callers should supply their intended endpoint and service identity on the first call.
    /// </remarks>
    internal static ILoggingBuilder AddAppSurfaceObservabilityLogging(
        this ILoggingBuilder builder,
        StartupContext context,
        IConfiguration configuration,
        Action<AppSurfaceObservabilityOptions>? configure,
        bool registerOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configuration);

        var services = builder.Services;
        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(AppSurfaceObservabilityLoggingRegistrationMarker)))
        {
            return builder;
        }

        if (registerOptions)
        {
            services.ConfigureAppSurfaceObservability(configuration, configure);
        }

        services.AddSingleton<AppSurfaceObservabilityLoggingRegistrationMarker>();

        var plan = AppSurfaceObservabilityPlan.Resolve(context, configuration, configure);
        builder.AddOpenTelemetry(options => ConfigureLogging(options, plan));

        return builder;
    }

    /// <summary>
    /// Applies AppSurface OpenTelemetry logging defaults to logger options.
    /// </summary>
    /// <param name="options">The logger options captured by OpenTelemetry logging.</param>
    /// <param name="plan">The resolved plan that controls resource identity and exporter registration.</param>
    /// <remarks>
    /// AppSurface opts into formatted messages, scopes, and parsed state values so structured log attributes remain useful
    /// when exported through OTLP. Exporter registration follows <paramref name="plan"/>: when the plan skips export, the
    /// host can still add its own logging exporter later.
    /// </remarks>
    internal static void ConfigureLogging(
        OpenTelemetryLoggerOptions options,
        AppSurfaceObservabilityPlan plan)
    {
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
        options.ParseStateValues = true;
        options.SetResourceBuilder(ResourceBuilderFactory.Create(plan));

        if (plan.ShouldRegisterExporter)
        {
            options.AddOtlpExporter(exporterOptions =>
                AppSurfaceObservabilityServiceCollectionExtensions.ConfigureExporter(exporterOptions, plan));
        }
    }
}
