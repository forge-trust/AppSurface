namespace ForgeTrust.AppSurface.Observability;

/// <summary>
/// Names the OpenTelemetry sources and meters that AppSurface-owned instrumentation should use.
/// </summary>
/// <remarks>
/// The v1 observability package registers these names so future AppSurface packages can add spans and metrics without
/// forcing each application to rediscover source names. This package does not add Flow, Auth, Docs, Intelligence, or
/// other package-specific telemetry yet.
/// </remarks>
public static class AppSurfaceTelemetrySources
{
    /// <summary>
    /// Gets the ActivitySource name reserved for AppSurface-owned spans.
    /// </summary>
    public const string ActivitySourceName = "ForgeTrust.AppSurface";

    /// <summary>
    /// Gets the Meter name reserved for AppSurface-owned metrics.
    /// </summary>
    public const string MeterName = "ForgeTrust.AppSurface";

    /// <summary>
    /// Gets common .NET activity source names that AppSurface opts into when they are emitted by the host runtime.
    /// </summary>
    /// <remarks>
    /// The returned list is read-only so callers cannot mutate global source registration for the current process.
    /// Copy the values into a new collection before adding application-specific sources.
    /// </remarks>
    public static IReadOnlyList<string> StandardActivitySourceNames { get; } = Array.AsReadOnly(
        [
            ActivitySourceName,
            "Microsoft.AspNetCore",
            "System.Net.Http"
        ]);

    /// <summary>
    /// Gets common .NET meter names that AppSurface opts into when they are emitted by the host runtime.
    /// </summary>
    /// <remarks>
    /// The returned list is read-only so callers cannot mutate global meter registration for the current process.
    /// Copy the values into a new collection before adding application-specific meters.
    /// </remarks>
    public static IReadOnlyList<string> StandardMeterNames { get; } = Array.AsReadOnly(
        [
            MeterName,
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Server.Kestrel",
            "System.Net.Http",
            "System.Runtime"
        ]);
}
