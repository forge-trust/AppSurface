using ForgeTrust.AppSurface.Web.OpenApi;

namespace ForgeTrust.AppSurface.Web.Scalar;

/// <summary>
/// Configures AppSurface's Scalar API reference endpoint mapping behavior.
/// </summary>
/// <remarks>
/// Scalar depends on the AppSurface-owned OpenAPI document in the default integration. The Scalar UI is mapped only
/// when this option allows Scalar exposure and <see cref="AppSurfaceWebOpenApiOptions.ExposeEndpoint"/> also allows the
/// backing OpenAPI endpoint in the same environment. This option does not add authentication or authorization.
/// </remarks>
public sealed class AppSurfaceWebScalarOptions
{
    /// <summary>
    /// Gets the configuration section used for AppSurface Scalar options.
    /// </summary>
    public const string SectionName = "AppSurfaceWebScalar";

    /// <summary>
    /// Gets or sets when the Scalar API reference endpoint should be mapped.
    /// </summary>
    public AppSurfaceApiDocumentationEndpointExposure ExposeEndpoint { get; set; } =
        AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly;
}
