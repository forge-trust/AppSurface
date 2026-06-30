namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Provides the default AppSurface Web platform probe endpoint paths.
/// </summary>
public static class AppSurfaceHealthEndpointDefaults
{
    /// <summary>
    /// Gets the default liveness and aggregate health endpoint path.
    /// </summary>
    public const string HealthPath = "/health";

    /// <summary>
    /// Gets the default readiness endpoint path.
    /// </summary>
    public const string ReadyPath = "/ready";
}
