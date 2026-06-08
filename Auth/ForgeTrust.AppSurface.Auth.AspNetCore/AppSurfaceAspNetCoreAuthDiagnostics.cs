namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Builds safe diagnostic metadata dictionaries for ASP.NET Core auth adapter setup failures.
/// </summary>
/// <remarks>
/// Diagnostics identify missing services, policy names, and stable adapter diagnostic codes. They must not include
/// raw claims, tokens, email addresses, display names, or other request-user secrets.
/// </remarks>
internal static class AppSurfaceAspNetCoreAuthDiagnostics
{
    /// <summary>
    /// Builds metadata for a missing ASP.NET Core service dependency.
    /// </summary>
    /// <param name="serviceType">The service type that was required but unavailable.</param>
    /// <param name="diagnosticCode">A stable adapter diagnostic code describing the missing dependency.</param>
    /// <param name="policyName">Optional policy name associated with the setup failure.</param>
    /// <returns>A safe metadata dictionary for an auth setup-failure result.</returns>
    public static IReadOnlyDictionary<string, string> MissingService(
        Type serviceType,
        string diagnosticCode,
        string? policyName = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode] = diagnosticCode,
            [AppSurfaceAspNetCoreAuthMetadataKeys.MissingService] = serviceType.FullName ?? serviceType.Name,
        };

        if (!string.IsNullOrWhiteSpace(policyName))
        {
            metadata[AppSurfaceAspNetCoreAuthMetadataKeys.PolicyName] = policyName;
        }

        return metadata;
    }

    /// <summary>
    /// Builds metadata for a policy lookup or policy-related setup failure.
    /// </summary>
    /// <param name="diagnosticCode">A stable adapter diagnostic code describing the policy failure.</param>
    /// <param name="policyName">The requested ASP.NET Core authorization policy name.</param>
    /// <returns>A safe metadata dictionary for an auth setup-failure result.</returns>
    public static IReadOnlyDictionary<string, string> Policy(string diagnosticCode, string policyName)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode] = diagnosticCode,
            [AppSurfaceAspNetCoreAuthMetadataKeys.PolicyName] = policyName,
        };
    }
}
