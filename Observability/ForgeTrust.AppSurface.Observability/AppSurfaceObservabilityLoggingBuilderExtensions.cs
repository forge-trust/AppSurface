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
