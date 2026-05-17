using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Resolves environment-aware visibility for AppSurface Docs harvest health routes and sidebar chrome.
/// </summary>
internal static class AppSurfaceDocsHarvestHealthVisibility
{
    /// <summary>
    /// Resolves whether the harvest health controller routes should be registered or allowed for the current host.
    /// </summary>
    /// <remarks>
    /// This internal helper uses <see cref="AppSurfaceDocsOptions.Harvest"/> health route settings and the supplied host
    /// environment. Missing health options fall back to <see cref="AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly"/>.
    /// Callers must pass the real host environment; the method null-checks both arguments before applying the
    /// environment-gated visibility contract.
    /// </remarks>
    public static bool AreRoutesExposed(AppSurfaceDocsOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        return IsExposed(options.Harvest?.Health?.ExposeRoutes ?? AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly, environment);
    }

    /// <summary>
    /// Resolves whether the sidebar should show harvest health chrome for the current host.
    /// </summary>
    /// <remarks>
    /// This internal helper uses <see cref="AppSurfaceDocsOptions.Harvest"/> health chrome settings and the supplied host
    /// environment. Missing health options fall back to <see cref="AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly"/>.
    /// Callers use this for presentation chrome only; route exposure is resolved separately by
    /// <see cref="AreRoutesExposed(AppSurfaceDocsOptions, IHostEnvironment)"/>. The method null-checks both arguments before
    /// applying the environment-gated visibility contract.
    /// </remarks>
    public static bool ShouldShowChrome(AppSurfaceDocsOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        return IsExposed(options.Harvest?.Health?.ShowChrome ?? AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly, environment);
    }

    private static bool IsExposed(AppSurfaceDocsHarvestHealthExposure exposure, IHostEnvironment environment)
    {
        return exposure switch
        {
            AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly => environment.IsDevelopment(),
            AppSurfaceDocsHarvestHealthExposure.Always => true,
            AppSurfaceDocsHarvestHealthExposure.Never => false,
            _ => false
        };
    }
}
