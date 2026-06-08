namespace ForgeTrust.AppSurface.Auth.AspNetCore;

internal static class AppSurfaceAspNetCoreAuthDiagnostics
{
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

    public static IReadOnlyDictionary<string, string> Policy(string diagnosticCode, string policyName)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode] = diagnosticCode,
            [AppSurfaceAspNetCoreAuthMetadataKeys.PolicyName] = policyName,
        };
    }
}
