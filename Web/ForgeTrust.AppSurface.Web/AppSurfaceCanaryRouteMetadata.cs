namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Marks the single framework-owned endpoint in the reserved named-canary namespace.
/// </summary>
internal sealed class AppSurfaceCanaryRouteMetadata
{
    internal static AppSurfaceCanaryRouteMetadata Instance { get; } = new();

    private AppSurfaceCanaryRouteMetadata()
    {
    }
}
