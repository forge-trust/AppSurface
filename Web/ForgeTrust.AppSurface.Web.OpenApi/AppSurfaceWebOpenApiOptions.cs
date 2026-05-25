namespace ForgeTrust.AppSurface.Web.OpenApi;

/// <summary>
/// Configures AppSurface's OpenAPI endpoint mapping behavior.
/// </summary>
/// <remarks>
/// The default <see cref="ExposeEndpoint"/> value keeps the generated OpenAPI document available during development
/// while avoiding accidental production metadata exposure. Set <see cref="ExposeEndpoint"/> to
/// <see cref="AppSurfaceApiDocumentationEndpointExposure.Always"/> only when the endpoint is intentionally available
/// outside development and is protected by host-owned controls such as authorization, private networking, or a reverse
/// proxy policy.
/// </remarks>
public sealed class AppSurfaceWebOpenApiOptions
{
    /// <summary>
    /// Gets the configuration section used for AppSurface OpenAPI options.
    /// </summary>
    public const string SectionName = "AppSurfaceWebOpenApi";

    /// <summary>
    /// Gets or sets when the generated OpenAPI document endpoint should be mapped.
    /// </summary>
    public AppSurfaceApiDocumentationEndpointExposure ExposeEndpoint { get; set; } =
        AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly;
}
