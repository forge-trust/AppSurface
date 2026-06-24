namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Defines default names and paths for AppSurface development authentication.
/// </summary>
public static class AppSurfaceDevAuthDefaults
{
    /// <summary>
    /// Default authentication scheme registered by AppSurface DevAuth.
    /// </summary>
    public const string AuthenticationScheme = "AppSurface.DevAuth";

    /// <summary>
    /// Default local-only path prefix for the AppSurface DevAuth control page and status endpoints.
    /// </summary>
    public const string PathPrefix = "/_appsurface/dev-auth";

    /// <summary>
    /// Default subject claim type used by seeded development personas.
    /// </summary>
    public const string SubjectClaimType = "sub";

    /// <summary>
    /// Default cookie name used to store the selected development persona id.
    /// </summary>
    public const string CookieName = ".AppSurface.DevAuth.Persona";
}
