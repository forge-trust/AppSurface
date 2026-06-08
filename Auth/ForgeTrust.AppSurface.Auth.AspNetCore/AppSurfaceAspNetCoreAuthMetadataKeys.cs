namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Defines safe diagnostic metadata keys emitted by the ASP.NET Core auth adapter.
/// </summary>
/// <remarks>
/// These keys are for setup diagnostics only. The adapter does not copy raw claims, tokens, emails, display names, or
/// identity-provider payloads into result metadata.
/// </remarks>
public static class AppSurfaceAspNetCoreAuthMetadataKeys
{
    /// <summary>
    /// Metadata key for a stable adapter diagnostic code.
    /// </summary>
    public const string DiagnosticCode = "appsurface.aspnetcore.diagnostic_code";

    /// <summary>
    /// Metadata key for the requested ASP.NET Core authorization policy name.
    /// </summary>
    public const string PolicyName = "appsurface.aspnetcore.policy_name";

    /// <summary>
    /// Metadata key for the missing ASP.NET Core service type.
    /// </summary>
    public const string MissingService = "appsurface.aspnetcore.missing_service";

    /// <summary>
    /// Metadata key for the configured subject claim types checked by the adapter.
    /// </summary>
    public const string SubjectClaimTypes = "appsurface.aspnetcore.subject_claim_types";
}
