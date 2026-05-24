namespace ForgeTrust.AppSurface.Web.OpenApi;

/// <summary>
/// Controls when AppSurface-owned API documentation endpoints are mapped by web modules.
/// </summary>
/// <remarks>
/// Use <see cref="DevelopmentOnly"/> for the safe default: local development hosts get zero-configuration API docs,
/// while non-development hosts must opt into exposure. Use <see cref="Always"/> only when the host protects the
/// endpoint with authentication, authorization, network controls, or another deployment boundary. This setting only
/// controls whether AppSurface maps the endpoint; it does not add authorization by itself.
/// </remarks>
public enum AppSurfaceApiDocumentationEndpointExposure
{
    /// <summary>
    /// Map the endpoint only when <see cref="Core.StartupContext.IsDevelopment"/> is <see langword="true"/>.
    /// </summary>
    DevelopmentOnly = 0,

    /// <summary>
    /// Map the endpoint in every host environment.
    /// </summary>
    Always = 1,

    /// <summary>
    /// Never map the endpoint, including in development.
    /// </summary>
    Never = 2
}
