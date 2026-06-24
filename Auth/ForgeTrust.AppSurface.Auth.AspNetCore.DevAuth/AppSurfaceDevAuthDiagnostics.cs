namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Stable AppSurface DevAuth diagnostic codes and safe metadata keys.
/// </summary>
public static class AppSurfaceDevAuthDiagnostics
{
    /// <summary>
    /// Safe metadata key that carries the stable AppSurface DevAuth diagnostic code.
    /// </summary>
    public const string DiagnosticCodeKey = "appsurface.devauth.diagnostic_code";

    /// <summary>
    /// DevAuth was enabled outside the Development environment.
    /// </summary>
    public const string NonDevelopmentEnvironment = "ASDEV001";

    /// <summary>
    /// DevAuth detected an existing real authentication scheme or default.
    /// </summary>
    public const string RealSchemeConflict = "ASDEV002";

    /// <summary>
    /// DevAuth was enabled without seeded personas.
    /// </summary>
    public const string NoPersonas = "ASDEV003";

    /// <summary>
    /// A selected persona did not contain the configured subject claim.
    /// </summary>
    public const string MissingSubjectClaim = "ASDEV004";

    /// <summary>
    /// DevAuth detected a reserved path conflict.
    /// </summary>
    public const string ReservedPathConflict = "ASDEV005";

    /// <summary>
    /// A persona id was invalid, unknown, stale, or tampered.
    /// </summary>
    public const string InvalidPersonaId = "ASDEV006";
}
