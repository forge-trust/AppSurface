namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Provides defaults for AppSurface configuration-audit HTTP diagnostics endpoints.
/// </summary>
/// <remarks>
/// Use <see cref="DefaultRoute"/> when a host wants the standard opt-in AppSurface diagnostics path.
/// Prefer a custom route with
/// <see cref="AppSurfaceConfigAuditDiagnosticsEndpointRouteBuilderExtensions.MapAppSurfaceConfigAuditDiagnostics(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, string)"/>
/// when the deployment already reserves this prefix or needs diagnostics to live under a host-owned operations path.
/// Configuration audit output is support-sensitive even after value redaction, and the mapper hides the endpoint from
/// API Explorer/OpenAPI by default instead of treating it as a public discovery surface.
/// </remarks>
public static class AppSurfaceConfigAuditDiagnosticsDefaults
{
    /// <summary>
    /// Gets the default route used by <see cref="AppSurfaceConfigAuditDiagnosticsEndpointRouteBuilderExtensions.MapAppSurfaceConfigAuditDiagnostics(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string)"/>.
    /// </summary>
    /// <remarks>
    /// Hosts must still map this route explicitly and attach an ASP.NET Core authorization policy. Do not expose it
    /// anonymously or assume the payload is safe for broad discovery because values are sanitized.
    /// </remarks>
    public const string DefaultRoute = "/_appsurface/config/audit";
}
