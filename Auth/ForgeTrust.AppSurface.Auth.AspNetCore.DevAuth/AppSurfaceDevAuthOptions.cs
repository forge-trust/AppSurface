namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Options for AppSurface development-only authentication.
/// </summary>
public sealed class AppSurfaceDevAuthOptions
{
    /// <summary>
    /// Gets the seeded local-development personas.
    /// </summary>
    public AppSurfaceDevAuthPersonaCollection Users { get; } = new();

    /// <summary>
    /// Gets or sets the authentication scheme registered for DevAuth.
    /// </summary>
    public string SchemeName { get; set; } = AppSurfaceDevAuthDefaults.AuthenticationScheme;

    /// <summary>
    /// Gets or sets the local-only path prefix for the control page and status endpoints.
    /// </summary>
    public string PathPrefix { get; set; } = AppSurfaceDevAuthDefaults.PathPrefix;

    /// <summary>
    /// Gets or sets the cookie name that stores the selected local persona id.
    /// </summary>
    public string CookieName { get; set; } = AppSurfaceDevAuthDefaults.CookieName;

    /// <summary>
    /// Gets or sets a value indicating whether DevAuth may become the default scheme for local proof apps.
    /// </summary>
    /// <remarks>
    /// Leave this disabled for package consumers that already have real authentication. Enable it only in throwaway
    /// local proof hosts where DevAuth is intentionally the whole authentication stack.
    /// </remarks>
    public bool UseAsDefaultSchemeForLocalProof { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether DevAuth may coexist with other registered authentication schemes.
    /// </summary>
    /// <remarks>
    /// This is a loud local-only override for demos that intentionally compose real and fake schemes. It must not be
    /// used to hide production authentication conflicts.
    /// </remarks>
    public bool AllowDevAuthOverrideForLocalProof { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether control endpoints reject non-loopback requests.
    /// </summary>
    public bool RequireLoopbackControlRequests { get; set; } = true;

    /// <summary>
    /// Gets claim types that may appear in the local-only control page claims preview.
    /// </summary>
    /// <remarks>
    /// The authentication handler still issues every seeded persona claim. This allowlist affects only the HTML preview.
    /// Sensitive claim names such as tokens, secrets, passwords, keys, and emails are never rendered even when added
    /// here.
    /// </remarks>
    public ISet<string> DisplayClaimTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AppSurfaceDevAuthDefaults.SubjectClaimType,
        "role",
        "tenant",
    };
}
