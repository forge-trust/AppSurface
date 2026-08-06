namespace ForgeTrust.AppSurface.Observability;

using ForgeTrust.AppSurface.Core;

/// <summary>
/// Provides compatible OpenTelemetry source and meter name surfaces for AppSurface-owned instrumentation.
/// </summary>
/// <remarks>
/// <para>
/// This type keeps the existing v1 public shape for telemetry names while delegating the canonical AppSurface source name
/// to <see cref="AppSurfaceActivitySources"/> to avoid a hard dependency on this package from framework code.
/// </para>
/// <para>
/// The v1 observability package registers these names so future AppSurface packages can add spans and metrics without
/// forcing each application to rediscover source names. This package does not add Flow, Auth, Docs, Intelligence, or
/// other package-specific telemetry yet.
/// </para>
/// </remarks>
public static class AppSurfaceTelemetrySources
{
    /// <summary>
    /// Gets the canonical ActivitySource name reserved for AppSurface-owned spans.
    /// </summary>
    public const string ActivitySourceName = AppSurfaceActivitySources.ActivitySourceName;

    /// <summary>
    /// Gets the canonical meter name reserved for AppSurface-owned metrics.
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
            AppSurfaceActivitySources.ActivitySourceName,
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
