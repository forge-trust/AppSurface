namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Provides defaults for AppSurface configuration-audit HTTP diagnostics endpoints.
/// </summary>
public static class AppSurfaceConfigAuditDiagnosticsDefaults
{
    /// <summary>
    /// Gets the default route used by <see cref="AppSurfaceConfigAuditDiagnosticsEndpointRouteBuilderExtensions.MapAppSurfaceConfigAuditDiagnostics(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string)"/>.
    /// </summary>
    public const string DefaultRoute = "/_appsurface/config/audit";
}
