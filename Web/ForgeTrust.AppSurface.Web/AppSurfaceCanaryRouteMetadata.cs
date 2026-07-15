namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Marks the single framework-owned endpoint in the reserved named-canary namespace.
/// </summary>
internal sealed class AppSurfaceCanaryRouteMetadata
{
    /// <summary>Gets the shared marker instance attached to the package-owned endpoint.</summary>
    internal static AppSurfaceCanaryRouteMetadata Instance { get; } = new();

    private AppSurfaceCanaryRouteMetadata()
    {
    }
}
