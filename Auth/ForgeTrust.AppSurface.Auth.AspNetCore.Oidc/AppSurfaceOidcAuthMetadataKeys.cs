namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

/// <summary>
/// Defines safe diagnostic metadata keys emitted by the AppSurface OIDC convenience package.
/// </summary>
/// <remarks>
/// These keys identify setup and handler-state diagnostics only. They must not carry raw tokens, raw claims, emails,
/// display names, client secrets, ID-token payloads, or provider response bodies.
/// </remarks>
public static class AppSurfaceOidcAuthMetadataKeys
{
    /// <summary>
    /// Metadata key for a stable OIDC diagnostic code.
    /// </summary>
    public const string DiagnosticCode = "appsurface.oidc.diagnostic_code";

    /// <summary>
    /// Metadata key for an affected option name.
    /// </summary>
    public const string OptionName = "appsurface.oidc.option_name";

    /// <summary>
    /// Metadata key for the configured cookie scheme.
    /// </summary>
    public const string CookieScheme = "appsurface.oidc.cookie_scheme";

    /// <summary>
    /// Metadata key for the configured OpenID Connect scheme.
    /// </summary>
    public const string OidcScheme = "appsurface.oidc.scheme";

    /// <summary>
    /// Metadata key for a sanitized handler event name.
    /// </summary>
    public const string EventName = "appsurface.oidc.event_name";

    /// <summary>
    /// Metadata key for a sanitized exception type name.
    /// </summary>
    public const string ExceptionType = "appsurface.oidc.exception_type";
}
