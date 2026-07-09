namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Defines DevAuth marker tokens that static exporters must treat as private development-auth evidence.
/// </summary>
/// <remarks>
/// DevAuth renders these tokens only on local development surfaces. Static export auditors consume the same constants so
/// marker renames are compiler-checked across the renderer and artifact-audit boundary.
/// </remarks>
public static class AppSurfaceDevAuthStaticExportMarkers
{
    /// <summary>
    /// Attribute name emitted by DevAuth marker and control-page markup.
    /// </summary>
    public const string MarkerAttributeName = "data-appsurface-dev-auth";

    /// <summary>
    /// Default CSS class emitted by the DevAuth floating marker.
    /// </summary>
    public const string MarkerCssClass = "appsurface-dev-auth-marker";
}
