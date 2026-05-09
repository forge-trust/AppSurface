using Microsoft.Extensions.Hosting;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

/// <summary>
/// Resolves environment-aware visibility for RazorDocs harvest health routes and sidebar chrome.
/// </summary>
internal static class RazorDocsHarvestHealthVisibility
{
    public static bool AreRoutesExposed(RazorDocsOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        return IsExposed(options.Harvest?.Health?.ExposeRoutes ?? RazorDocsHarvestHealthExposure.DevelopmentOnly, environment);
    }

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
