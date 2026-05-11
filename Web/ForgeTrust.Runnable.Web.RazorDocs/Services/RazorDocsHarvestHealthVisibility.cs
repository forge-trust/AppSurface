using Microsoft.Extensions.Hosting;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

/// <summary>
/// Resolves environment-aware visibility for RazorDocs harvest health routes and sidebar chrome.
/// </summary>
internal static class RazorDocsHarvestHealthVisibility
{
    /// <summary>
    /// Resolves whether the harvest health controller routes should be registered or allowed for the current host.
    /// </summary>
    /// <remarks>
    /// This internal helper uses <see cref="RazorDocsOptions.Harvest"/> health route settings and the supplied host
    /// environment. Missing health options fall back to <see cref="RazorDocsHarvestHealthExposure.DevelopmentOnly"/>.
    /// Callers must pass the real host environment; the method null-checks both arguments before applying the
    /// environment-gated visibility contract.
    /// </remarks>
    public static bool AreRoutesExposed(RazorDocsOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        return IsExposed(options.Harvest?.Health?.ExposeRoutes ?? RazorDocsHarvestHealthExposure.DevelopmentOnly, environment);
    }

    /// <summary>
    /// Resolves whether the sidebar should show harvest health chrome for the current host.
    /// </summary>
    /// <remarks>
    /// This internal helper uses <see cref="RazorDocsOptions.Harvest"/> health chrome settings and the supplied host
    /// environment. Missing health options fall back to <see cref="RazorDocsHarvestHealthExposure.DevelopmentOnly"/>.
    /// Callers use this for presentation chrome only; route exposure is resolved separately by
    /// <see cref="AreRoutesExposed(RazorDocsOptions, IHostEnvironment)"/>. The method null-checks both arguments before
    /// applying the environment-gated visibility contract.
    /// </remarks>
    public static bool ShouldShowChrome(RazorDocsOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        return IsExposed(options.Harvest?.Health?.ShowChrome ?? RazorDocsHarvestHealthExposure.DevelopmentOnly, environment);
    }

    private static bool IsExposed(RazorDocsHarvestHealthExposure exposure, IHostEnvironment environment)
    {
        return exposure switch
        {
            RazorDocsHarvestHealthExposure.DevelopmentOnly => environment.IsDevelopment(),
            RazorDocsHarvestHealthExposure.Always => true,
            RazorDocsHarvestHealthExposure.Never => false,
            _ => false
        };
    }
}
