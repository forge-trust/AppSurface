using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ForgeTrust.AppSurface.Observability;

/// <summary>
/// Provides service-registration extensions for AppSurface observability.
/// </summary>
public static class AppSurfaceObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Adds AppSurface OpenTelemetry tracing and metrics to the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="context">The AppSurface startup context that supplies the default service identity.</param>
    /// <param name="configuration">Configuration used to bind <see cref="AppSurfaceObservabilityOptions"/>.</param>
    /// <param name="configure">Optional code configuration that composes after bound configuration values.</param>
    /// <returns>The supplied <paramref name="services"/>.</returns>
    /// <remarks>
    /// This extension is the app-runtime side of Aspire observability: it never references Aspire hosting packages and
    /// it registers OTLP exporters only according to <see cref="AppSurfaceObservabilityOptions.ExporterMode"/>. Repeated
    /// calls are idempotent and first registration wins, which keeps configured options aligned with the captured
    /// OpenTelemetry resource, tracing, and metrics setup.
    /// </remarks>
    public static IServiceCollection AddAppSurfaceObservability(
        this IServiceCollection services,
        StartupContext context,
        IConfiguration configuration,
        Action<AppSurfaceObservabilityOptions>? configure = null)
    {
        return services.AddAppSurfaceObservability(context, configuration, configure, registerOptions: true);
    }

    internal static IServiceCollection AddAppSurfaceObservability(
        this IServiceCollection services,
        StartupContext context,
        IConfiguration configuration,
        Action<AppSurfaceObservabilityOptions>? configure,
        bool registerOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(AppSurfaceObservabilityServicesRegistrationMarker)))
        {
            return services;
        }

        if (registerOptions)
        {
            services.ConfigureAppSurfaceObservability(configuration, configure);
        }

        var plan = AppSurfaceObservabilityPlan.Resolve(context, configuration, configure);
        services.AddSingleton(plan);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AppSurfaceObservabilityStartupDiagnostic>());
        services.AddSingleton<AppSurfaceObservabilityServicesRegistrationMarker>();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => ConfigureResource(resource, plan))
            .WithTracing(tracing => ConfigureTracing(tracing, plan))
            .WithMetrics(metrics => ConfigureMetrics(metrics, plan));

        return services;
    }

    /// <summary>
    /// Registers and validates <see cref="AppSurfaceObservabilityOptions"/> without adding OpenTelemetry providers.
    /// </summary>
    /// <param name="services">The service collection receiving the options registration.</param>
    /// <param name="configure">Optional code configuration that composes after bound configuration values.</param>
    /// <returns>The supplied <paramref name="services"/>.</returns>
    /// <remarks>
    /// Use this extension for consumers that read <see cref="IOptions{TOptions}"/> directly. Values that should affect
    /// AppSurface-owned OpenTelemetry resource or exporter setup must be supplied through the first provider-registration
    /// call, because OpenTelemetry captures that setup when providers are registered.
    /// </remarks>
    public static IServiceCollection ConfigureAppSurfaceObservability(
        this IServiceCollection services,
        Action<AppSurfaceObservabilityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = AddOptionsBuilder(services)
            .BindConfiguration(AppSurfaceObservabilityOptions.SectionName);

        ConfigureOptionsBuilder(builder, configure);

        return services;
    }

    internal static IServiceCollection ConfigureAppSurfaceObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AppSurfaceObservabilityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = AddOptionsBuilder(services)
            .Bind(configuration.GetSection(AppSurfaceObservabilityOptions.SectionName));

        ConfigureOptionsBuilder(builder, configure);

        return services;
    }

    private static OptionsBuilder<AppSurfaceObservabilityOptions> AddOptionsBuilder(IServiceCollection services)
    {
        return services.AddOptions<AppSurfaceObservabilityOptions>()
            .Validate(
                options => Enum.IsDefined(options.ExporterMode),
                $"{AppSurfaceObservabilityOptions.SectionName}:ExporterMode must be one of WhenEndpointConfigured, Always, or Never.")
            .Validate(
                options => options.OtlpEndpoint is null || options.OtlpEndpoint.IsAbsoluteUri,
                $"{AppSurfaceObservabilityOptions.SectionName}:OtlpEndpoint must be an absolute URI.");
    }

    private static void ConfigureOptionsBuilder(
        OptionsBuilder<AppSurfaceObservabilityOptions> builder,
        Action<AppSurfaceObservabilityOptions>? configure)
    {
        if (configure is not null)
        {
            builder.Configure(configure);
        }
    }

    internal static void ConfigureTracing(
        TracerProviderBuilder tracing,
        AppSurfaceObservabilityPlan plan)
    {
        tracing.AddSource(AppSurfaceTelemetrySources.StandardActivitySourceNames);

        if (!plan.ShouldRegisterExporter)
        {
            return;
        }

        tracing.AddOtlpExporter(options => ConfigureExporter(options, plan));
    }

    internal static void ConfigureMetrics(
        MeterProviderBuilder metrics,
        AppSurfaceObservabilityPlan plan)
    {
        metrics.AddMeter(AppSurfaceTelemetrySources.StandardMeterNames);

        if (!plan.ShouldRegisterExporter)
        {
            return;
        }

        metrics.AddOtlpExporter(options => ConfigureExporter(options, plan));
    }

    internal static void ConfigureExporter(OtlpExporterOptions options, AppSurfaceObservabilityPlan plan)
    {
        if (plan.Endpoint is not null)
        {
            options.Endpoint = plan.Endpoint;
        }
    }

    internal static void ConfigureResource(ResourceBuilder resource, AppSurfaceObservabilityPlan plan)
    {
        resource.AddService(
            serviceName: plan.ServiceName,
            serviceVersion: plan.ServiceVersion,
            serviceInstanceId: null);
    }
}
