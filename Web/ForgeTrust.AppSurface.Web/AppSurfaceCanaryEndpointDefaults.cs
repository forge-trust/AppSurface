namespace ForgeTrust.AppSurface.Web;

/// <summary>Provides the conventional named-canary route contract.</summary>
public static class AppSurfaceCanaryEndpointDefaults
{
    /// <summary>The fixed GET route pattern for evaluating one registered named canary.</summary>
    public const string RoutePattern = "/_appsurface/canaries/{name}";
}
