namespace ForgeTrust.AppSurface.Web;

/// <summary>Configures the HTTP adapter for named canary evaluations.</summary>
public sealed class AppSurfaceCanaryEndpointOptions
{
    /// <summary>
    /// Gets or sets completed-result HTTP mapping. The default is
    /// <see cref="AppSurfaceCanaryCompletedResponseMode.StatusCode"/>.
    /// </summary>
    public AppSurfaceCanaryCompletedResponseMode CompletedResponseMode { get; set; }
}
