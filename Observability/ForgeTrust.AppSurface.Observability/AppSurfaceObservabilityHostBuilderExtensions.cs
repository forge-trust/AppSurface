using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Observability;

/// <summary>
/// Provides host-builder extensions for enabling AppSurface observability outside module discovery.
/// </summary>
public static class AppSurfaceObservabilityHostBuilderExtensions
{
    private const string RegistrationKey = "ForgeTrust.AppSurface.Observability.HostBuilderRegistration";

    /// <summary>
    /// Configures OpenTelemetry logging, tracing, and metrics for an AppSurface host.
    /// </summary>
    /// <param name="builder">The host builder to configure.</param>
    /// <param name="context">The AppSurface startup context that supplies the default service identity.</param>
    /// <param name="configure">Optional code configuration that composes after bound configuration values.</param>
    /// <returns>The supplied <paramref name="builder"/>.</returns>
    /// <remarks>
    /// Use this extension when a custom host does not activate <see cref="AppSurfaceObservabilityModule"/> through the
    /// module dependency graph. Multiple calls are safe and first registration wins, which keeps configured options
    /// aligned with the OpenTelemetry providers and exporters captured during registration.
    /// </remarks>
    public static IHostBuilder ConfigureAppSurfaceObservability(
        this IHostBuilder builder,
        StartupContext context,
        Action<AppSurfaceObservabilityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(context);

        if (builder.Properties.ContainsKey(RegistrationKey))
        {
            return builder;
        }

        builder.Properties[RegistrationKey] = true;

        builder.ConfigureServices((hostContext, services) =>
            services.ConfigureAppSurfaceObservability(hostContext.Configuration, configure));
        builder.ConfigureLogging((hostContext, logging) =>
            logging.AddAppSurfaceObservabilityLogging(
                context,
                hostContext.Configuration,
                configure,
                registerOptions: false));
        builder.ConfigureServices((hostContext, services) =>
            services.AddAppSurfaceObservability(
                context,
                hostContext.Configuration,
                configure,
                registerOptions: false));

        return builder;
    }
}
