namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

/// <summary>
/// Stable diagnostic codes emitted by the AppSurface OIDC convenience package.
/// </summary>
public static class AppSurfaceOidcAuthDiagnosticCodes
{
    /// <summary>
    /// The OIDC authority option was missing.
    /// </summary>
    public const string MissingAuthority = "ASOIDC001";

    /// <summary>
    /// The OIDC client id option was missing.
    /// </summary>
    public const string MissingClientId = "ASOIDC002";

    /// <summary>
    /// The OIDC client secret option was missing while client-secret validation was enabled.
    /// </summary>
    public const string MissingClientSecret = "ASOIDC003";

    /// <summary>
    /// An OIDC remote failure occurred.
    /// </summary>
    public const string RemoteFailure = "ASOIDC004";

    /// <summary>
    /// A token-validated principal did not contain the configured subject claim.
    /// </summary>
    public const string MissingSubjectClaim = "ASOIDC005";

    /// <summary>
    /// Token persistence was explicitly enabled.
    /// </summary>
    public const string TokenPersistenceEnabled = "ASOIDC006";
}
