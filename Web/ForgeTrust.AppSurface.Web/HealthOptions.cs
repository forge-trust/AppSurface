namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Configures AppSurface Web platform health and readiness probe endpoints.
/// </summary>
/// <remarks>
/// AppSurface maps public, minimal health endpoints by default so container platforms can probe a service without
/// application-specific endpoint wiring. The endpoints use ASP.NET Core health checks. <see cref="HealthPath"/> runs all
/// checks; <see cref="ReadyPath"/> runs checks tagged with <see cref="ReadyTag"/>. If no checks are tagged for readiness,
/// the readiness endpoint reports healthy once the app has started.
/// </remarks>
public sealed class HealthOptions
{
    /// <summary>
    /// Gets a default enabled health options instance.
    /// </summary>
    public static HealthOptions Default => new();

    /// <summary>
    /// Gets or sets a value indicating whether AppSurface should map platform health and readiness endpoints.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the app-root-relative endpoint path that runs all registered ASP.NET Core health checks.
    /// </summary>
    public string HealthPath { get; set; } = AppSurfaceHealthEndpointDefaults.HealthPath;

    /// <summary>
    /// Gets or sets the app-root-relative endpoint path that runs readiness-tagged ASP.NET Core health checks.
    /// </summary>
    public string ReadyPath { get; set; } = AppSurfaceHealthEndpointDefaults.ReadyPath;

    /// <summary>
    /// Gets or sets the health-check tag used to select checks for the readiness endpoint.
    /// </summary>
    public string ReadyTag { get; set; } = AppSurfaceHealthCheckTags.Ready;
}
