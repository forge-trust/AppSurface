using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Configuration;

namespace ForgeTrust.AppSurface.Observability;

/// <summary>
/// Captures the immutable observability registration plan resolved from AppSurface configuration.
/// </summary>
/// <param name="ExporterMode">The validated exporter registration mode.</param>
/// <param name="Endpoint">The resolved OTLP endpoint, or <see langword="null"/> when the host should use OpenTelemetry defaults or skip export.</param>
/// <param name="ServiceName">The OpenTelemetry resource service name that tracing, metrics, and logging should share.</param>
/// <param name="ServiceVersion">The optional OpenTelemetry resource service version.</param>
/// <param name="ShouldRegisterExporter">Whether AppSurface should add OTLP exporters to the OpenTelemetry builders.</param>
/// <param name="ShouldLogSkippedExporterDiagnostic">Whether startup should explain that endpoint-driven export was skipped.</param>
/// <remarks>
/// The plan is resolved once per first registration path. Later option registrations do not rewrite providers because
/// OpenTelemetry captures resource and exporter setup when providers are built.
/// </remarks>
internal sealed record AppSurfaceObservabilityPlan(
    AppSurfaceOtlpExporterMode ExporterMode,
    Uri? Endpoint,
    string ServiceName,
    string? ServiceVersion,
    bool ShouldRegisterExporter,
    bool ShouldLogSkippedExporterDiagnostic)
{
    /// <summary>
    /// Resolves the effective OpenTelemetry registration plan.
    /// </summary>
    /// <param name="context">The AppSurface startup context that supplies the default service name.</param>
    /// <param name="configuration">Configuration used to bind AppSurface observability options and optional OTEL keys.</param>
    /// <param name="configure">Optional code configuration applied after bound configuration values.</param>
    /// <param name="environment">Optional environment reader used for deterministic tests; production uses process environment variables.</param>
    /// <returns>The resolved immutable plan used by logging, tracing, metrics, and resource registration.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="context"/> or <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="AppSurfaceObservabilityOptions.ExporterMode"/> is undefined or an endpoint value is not an
    /// absolute URI.
    /// </exception>
    /// <remarks>
    /// Endpoint precedence is AppSurface options first, then the raw
    /// <see cref="AppSurfaceObservabilityOptions.AppSurfaceOtlpEndpointEnvironmentVariable"/> environment variable for
    /// hosts that did not add environment variables to configuration, then the standard
    /// <see cref="AppSurfaceObservabilityOptions.OtlpEndpointEnvironmentVariable"/> value from configuration, then the
    /// same standard variable from the process environment. This keeps AppSurface-owned endpoint configuration more
    /// specific than generic OTEL defaults while still honoring collector settings supplied by Aspire.
    /// </remarks>
    internal static AppSurfaceObservabilityPlan Resolve(
        StartupContext context,
        IConfiguration configuration,
        Action<AppSurfaceObservabilityOptions>? configure = null,
        IAppSurfaceEnvironmentReader? environment = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AppSurfaceObservabilityOptions();
        configuration.GetSection(AppSurfaceObservabilityOptions.SectionName).Bind(options);
        configure?.Invoke(options);

        if (!Enum.IsDefined(options.ExporterMode))
        {
            throw new InvalidOperationException(
                $"{AppSurfaceObservabilityOptions.SectionName}:ExporterMode must be one of WhenEndpointConfigured, Always, or Never.");
        }

        if (options.OtlpEndpoint is not null && !options.OtlpEndpoint.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                $"{AppSurfaceObservabilityOptions.SectionName}:OtlpEndpoint must be an absolute URI.");
        }

        environment ??= AppSurfaceEnvironmentReader.Instance;
        var endpoint = options.OtlpEndpoint
            ?? ResolveUri(
                environment.GetEnvironmentVariable(AppSurfaceObservabilityOptions.AppSurfaceOtlpEndpointEnvironmentVariable),
                AppSurfaceObservabilityOptions.AppSurfaceOtlpEndpointEnvironmentVariable)
            ?? ResolveUri(configuration[AppSurfaceObservabilityOptions.OtlpEndpointEnvironmentVariable])
            ?? ResolveUri(environment.GetEnvironmentVariable(AppSurfaceObservabilityOptions.OtlpEndpointEnvironmentVariable));

        var shouldRegisterExporter = options.ExporterMode == AppSurfaceOtlpExporterMode.Always
            || (options.ExporterMode == AppSurfaceOtlpExporterMode.WhenEndpointConfigured && endpoint is not null);

        var serviceName = string.IsNullOrWhiteSpace(options.ServiceName)
            ? context.ApplicationName
            : options.ServiceName.Trim();

        var serviceVersion = string.IsNullOrWhiteSpace(options.ServiceVersion)
            ? null
            : options.ServiceVersion.Trim();

        return new AppSurfaceObservabilityPlan(
            options.ExporterMode,
            endpoint,
            serviceName,
            serviceVersion,
            shouldRegisterExporter,
            options.ExporterMode == AppSurfaceOtlpExporterMode.WhenEndpointConfigured && endpoint is null);
    }

    /// <summary>
    /// Resolves an optional absolute URI from configuration or environment input.
    /// </summary>
    /// <param name="value">The configured value to parse.</param>
    /// <param name="settingName">The setting name used in validation failures.</param>
    /// <returns>The parsed absolute URI, or <see langword="null"/> when <paramref name="value"/> is blank.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="value"/> is present but is not an absolute URI.
    /// </exception>
    /// <remarks>
    /// Blank values are treated as absent so an empty environment variable does not accidentally enable export.
    /// Relative URI values fail closed because OTLP exporters need a concrete collector endpoint.
    /// </remarks>
    private static Uri? ResolveUri(
        string? value,
        string settingName = AppSurfaceObservabilityOptions.OtlpEndpointEnvironmentVariable)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException(
                $"{settingName} must be an absolute URI.");
    }
}
