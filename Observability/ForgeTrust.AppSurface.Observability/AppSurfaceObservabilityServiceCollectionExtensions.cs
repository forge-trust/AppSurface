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

    /// <summary>
    /// Adds AppSurface tracing and metrics with explicit control over options registration.
    /// </summary>
    /// <param name="services">The service collection receiving OpenTelemetry services.</param>
    /// <param name="context">The startup context used to resolve default service identity metadata.</param>
    /// <param name="configuration">Configuration used to bind and resolve observability options.</param>
    /// <param name="configure">Optional code configuration applied after bound configuration values.</param>
    /// <param name="registerOptions">
    /// <see langword="true"/> to bind and validate options in DI; <see langword="false"/> when logging or a host hook has
    /// already registered them.
    /// </param>
    /// <returns>The supplied <paramref name="services"/>.</returns>
    /// <remarks>
    /// The registration marker makes this path idempotent. First registration wins because OpenTelemetry captures resource,
    /// tracing, metrics, and exporter setup when providers are registered.
    /// </remarks>
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

    /// <summary>
    /// Registers and validates AppSurface observability options from a supplied configuration instance.
    /// </summary>
    /// <param name="services">The service collection receiving the options registration.</param>
    /// <param name="configuration">The configuration source to bind from.</param>
    /// <param name="configure">Optional code configuration applied after bound configuration values.</param>
    /// <returns>The supplied <paramref name="services"/>.</returns>
    /// <remarks>
    /// This overload avoids DI configuration split-brain for hosts that pass a specific configuration object into
    /// registration. Provider setup must use the same configuration instance that options binding uses.
    /// </remarks>
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

    /// <summary>
    /// Creates the shared options builder and validation rules.
    /// </summary>
    /// <param name="services">The service collection receiving options services.</param>
    /// <returns>The configured options builder.</returns>
    /// <remarks>
    /// Validation fails closed for undefined exporter modes and relative OTLP endpoints so invalid telemetry configuration
    /// is surfaced during options validation or plan resolution rather than producing partial provider setup.
    /// </remarks>
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

    /// <summary>
    /// Applies optional code configuration to the shared options builder.
    /// </summary>
    /// <param name="builder">The options builder to configure.</param>
    /// <param name="configure">Optional code configuration applied after configuration binding.</param>
    /// <remarks>
    /// Code configuration composes after bound values so applications can set environment-specific defaults in
    /// configuration and still make last-mile adjustments in startup code.
    /// </remarks>
    private static void ConfigureOptionsBuilder(
        OptionsBuilder<AppSurfaceObservabilityOptions> builder,
        Action<AppSurfaceObservabilityOptions>? configure)
    {
        if (configure is not null)
        {
            builder.Configure(configure);
        }
    }

    /// <summary>
    /// Adds AppSurface tracing sources and the optional OTLP trace exporter.
    /// </summary>
    /// <param name="tracing">The tracing builder to configure.</param>
    /// <param name="plan">The resolved plan that controls exporter setup.</param>
    /// <remarks>
    /// This method always registers AppSurface's standard activity sources. It adds an OTLP exporter only when
    /// <paramref name="plan"/> requires AppSurface-owned export; otherwise hosts may add their own exporters.
    /// </remarks>
    internal static void ConfigureTracing(
        TracerProviderBuilder tracing,
        AppSurfaceObservabilityPlan plan)
    {
        ConfigureTracing(tracing, plan, static (builder, resolvedPlan) =>
            builder.AddOtlpExporter(options => ConfigureExporter(options, resolvedPlan)));
    }

    /// <summary>
    /// Adds AppSurface tracing sources and delegates optional exporter registration.
    /// </summary>
    /// <param name="tracing">The tracing builder to configure.</param>
    /// <param name="plan">The resolved plan that controls exporter setup.</param>
    /// <param name="addExporter">Callback used to register the trace exporter when the plan requires export.</param>
    /// <remarks>
    /// The callback is an internal test seam. Production passes the OpenTelemetry OTLP exporter registration callback, and
    /// tests can assert that exporter registration was requested without depending on OpenTelemetry internals.
    /// </remarks>
    internal static void ConfigureTracing(
        TracerProviderBuilder tracing,
        AppSurfaceObservabilityPlan plan,
        Action<TracerProviderBuilder, AppSurfaceObservabilityPlan> addExporter)
    {
        tracing.AddSource(AppSurfaceTelemetrySources.StandardActivitySourceNames.ToArray());

        if (!plan.ShouldRegisterExporter)
        {
            return;
        }

        addExporter(tracing, plan);
    }

    /// <summary>
    /// Adds AppSurface metric meters and the optional OTLP metric exporter.
    /// </summary>
    /// <param name="metrics">The metrics builder to configure.</param>
    /// <param name="plan">The resolved plan that controls exporter setup.</param>
    /// <remarks>
    /// This method always registers AppSurface's standard meter names. It adds an OTLP exporter only when
    /// <paramref name="plan"/> requires AppSurface-owned export; otherwise hosts may add their own exporters.
    /// </remarks>
    internal static void ConfigureMetrics(
        MeterProviderBuilder metrics,
        AppSurfaceObservabilityPlan plan)
    {
        ConfigureMetrics(metrics, plan, static (builder, resolvedPlan) =>
            builder.AddOtlpExporter(options => ConfigureExporter(options, resolvedPlan)));
    }

    /// <summary>
    /// Adds AppSurface metric meters and delegates optional exporter registration.
    /// </summary>
    /// <param name="metrics">The metrics builder to configure.</param>
    /// <param name="plan">The resolved plan that controls exporter setup.</param>
    /// <param name="addExporter">Callback used to register the metric exporter when the plan requires export.</param>
    /// <remarks>
    /// The callback is an internal test seam. Production passes the OpenTelemetry OTLP exporter registration callback, and
    /// tests can assert that exporter registration was requested without depending on OpenTelemetry internals.
    /// </remarks>
    internal static void ConfigureMetrics(
        MeterProviderBuilder metrics,
        AppSurfaceObservabilityPlan plan,
        Action<MeterProviderBuilder, AppSurfaceObservabilityPlan> addExporter)
    {
        metrics.AddMeter(AppSurfaceTelemetrySources.StandardMeterNames.ToArray());

        if (!plan.ShouldRegisterExporter)
        {
            return;
        }

        addExporter(metrics, plan);
    }

    /// <summary>
    /// Applies AppSurface endpoint configuration to an OTLP exporter.
    /// </summary>
    /// <param name="options">The exporter options supplied by OpenTelemetry.</param>
    /// <param name="plan">The resolved plan that may contain an endpoint override.</param>
    /// <remarks>
    /// When no endpoint is present, this method intentionally leaves OpenTelemetry defaults untouched so hosts can rely on
    /// standard OTEL environment variables or SDK defaults in <see cref="AppSurfaceOtlpExporterMode.Always"/> mode.
    /// </remarks>
    internal static void ConfigureExporter(OtlpExporterOptions options, AppSurfaceObservabilityPlan plan)
    {
        if (plan.Endpoint is not null)
        {
            options.Endpoint = plan.Endpoint;
        }
    }

    /// <summary>
    /// Adds AppSurface service identity metadata to an OpenTelemetry resource builder.
    /// </summary>
    /// <param name="resource">The resource builder captured by OpenTelemetry providers.</param>
    /// <param name="plan">The resolved plan that supplies service name and version.</param>
    /// <remarks>
    /// AppSurface does not set a service instance id. Hosts that need per-instance identity should add it in their own
    /// OpenTelemetry resource configuration after AppSurface registration.
    /// </remarks>
    internal static void ConfigureResource(ResourceBuilder resource, AppSurfaceObservabilityPlan plan)
    {
        resource.AddService(
            serviceName: plan.ServiceName,
            serviceVersion: plan.ServiceVersion,
            serviceInstanceId: null);
    }
}
