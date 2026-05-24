using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Resolves environment-aware visibility for AppSurface Docs maintainer diagnostics routes.
/// </summary>
internal static class AppSurfaceDocsDiagnosticsVisibility
{
    /// <summary>
    /// Resolves whether the route-inspector controller routes should return responses for the current host.
    /// </summary>
    /// <remarks>
    /// The route inspector exposes route identity intended for local development and trusted operators. Missing
    /// diagnostics options fall back to <see cref="AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly"/>. Setting the
    /// exposure to <see cref="AppSurfaceDocsHarvestHealthExposure.Always"/> only allows the controller response; hosts
    /// remain responsible for authentication, authorization, or network controls in production.
    /// </remarks>
    public static bool IsRouteInspectorExposed(AppSurfaceDocsOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var exposure = options.Diagnostics?.ExposeRouteInspector ?? AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly;
        return exposure switch
        {
            AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly => environment.IsDevelopment(),
            AppSurfaceDocsHarvestHealthExposure.Always => true,
            AppSurfaceDocsHarvestHealthExposure.Never => false,
            _ => false
        };
    }
}
