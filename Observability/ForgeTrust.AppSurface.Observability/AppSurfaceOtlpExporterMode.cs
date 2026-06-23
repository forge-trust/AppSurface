namespace ForgeTrust.AppSurface.Observability;

/// <summary>
/// Controls when AppSurface configures OpenTelemetry Protocol exporters.
/// </summary>
/// <remarks>
/// The default, <see cref="WhenEndpointConfigured"/>, is designed for Aspire and other collector-backed development
/// environments: export is enabled only when an OTLP endpoint is present in AppSurface configuration or the standard
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable. Use <see cref="Always"/> only when the host intentionally
/// wants OpenTelemetry's exporter defaults or host-owned OTLP configuration to apply. Use <see cref="Never"/> for tests
/// and hosts that install their own exporters.
/// </remarks>
public enum AppSurfaceOtlpExporterMode
{
    /// <summary>
    /// Adds OTLP exporters only when an endpoint is configured through AppSurface options or standard OTLP environment.
    /// </summary>
    WhenEndpointConfigured = 0,

    /// <summary>
    /// Always adds OTLP exporters, using AppSurface's endpoint when present and OpenTelemetry defaults otherwise.
    /// </summary>
    Always = 1,

    /// <summary>
    /// Never adds AppSurface-owned OTLP exporters.
    /// </summary>
    Never = 2
}
