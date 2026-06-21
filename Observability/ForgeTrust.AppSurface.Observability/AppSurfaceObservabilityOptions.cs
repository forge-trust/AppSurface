namespace ForgeTrust.AppSurface.Observability;

/// <summary>
/// Configures AppSurface's application-side OpenTelemetry registration.
/// </summary>
/// <remarks>
/// These options live under the <c>AppSurfaceObservability</c> configuration section. The default exporter mode keeps
/// local and test runs quiet unless Aspire or another host supplies an OTLP endpoint. The service identity values are
/// written to OpenTelemetry resource metadata; they do not change the .NET Generic Host application identity used for
/// static web assets or framework hosting behavior.
/// </remarks>
public sealed class AppSurfaceObservabilityOptions
{
    /// <summary>
    /// Gets the configuration section used for AppSurface observability options.
    /// </summary>
    public const string SectionName = "AppSurfaceObservability";

    /// <summary>
    /// Gets the standard OpenTelemetry environment variable used as the fallback OTLP endpoint.
    /// </summary>
    public const string OtlpEndpointEnvironmentVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Gets the environment-variable form of <c>AppSurfaceObservability:OtlpEndpoint</c>.
    /// </summary>
    public const string AppSurfaceOtlpEndpointEnvironmentVariable = "AppSurfaceObservability__OtlpEndpoint";

    /// <summary>
    /// Gets or sets when AppSurface should add OTLP exporters.
    /// </summary>
    public AppSurfaceOtlpExporterMode ExporterMode { get; set; } =
        AppSurfaceOtlpExporterMode.WhenEndpointConfigured;

    /// <summary>
    /// Gets or sets the AppSurface-owned OTLP endpoint.
    /// </summary>
    /// <remarks>
    /// When set, this endpoint takes precedence over <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>. Use
    /// <c>AppSurfaceObservability:OtlpEndpoint</c> in JSON-style configuration or
    /// <see cref="AppSurfaceOtlpEndpointEnvironmentVariable"/> in environment-variable configuration.
    /// </remarks>
    public Uri? OtlpEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the OpenTelemetry resource service name.
    /// </summary>
    /// <remarks>
    /// When omitted, AppSurface uses <see cref="Core.StartupContext.ApplicationName"/> and then the root module
    /// assembly name fallback.
    /// </remarks>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Gets or sets the OpenTelemetry resource service version.
    /// </summary>
    /// <remarks>
    /// AppSurface emits <c>service.version</c> only when this value is configured.
    /// </remarks>
    public string? ServiceVersion { get; set; }
}
