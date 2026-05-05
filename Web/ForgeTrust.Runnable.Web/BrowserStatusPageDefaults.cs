using System.Globalization;

namespace ForgeTrust.Runnable.Web;

/// <summary>
/// Defines the conventional paths used by Runnable's built-in browser-friendly status page handling.
/// </summary>
/// <remarks>
/// Runnable owns browser status pages for 401, 403, and 404 responses in this release. Production exception
/// pages, including conventional 500 handling, are intentionally separate because ASP.NET Core routes
/// exceptions through exception-handling middleware instead of status-code pages.
/// </remarks>
public static class BrowserStatusPageDefaults
{
    internal const string ReservedRouteBase = "/_runnable/errors";

    /// <summary>
    /// Gets the conventional application or shared-library override path format for browser status page views.
    /// </summary>
    public const string AppViewPathFormat = "~/Views/Shared/{0}.cshtml";

    /// <summary>
    /// Gets the reserved framework route used to render the unauthorized page directly.
    /// </summary>
    public const string ReservedUnauthorizedRoute = ReservedRouteBase + "/401";

    /// <summary>
    /// Gets the reserved framework route used to render the forbidden page directly.
    /// </summary>
    public const string ReservedForbiddenRoute = ReservedRouteBase + "/403";

    /// <summary>
    /// Gets the reserved framework route used to render the not-found page directly.
    /// This route is intended for framework tooling such as static export.
    /// </summary>
    public const string ReservedNotFoundRoute = ReservedRouteBase + "/404";

    internal const string ReservedRouteFormat = ReservedRouteBase + "/{0}";
    internal const string ReservedRoutePattern = ReservedRouteBase + "/{statusCode:int}";
    internal const string FrameworkFallbackViewPath = "~/Views/_Runnable/Errors/StatusPage.cshtml";

    internal static string GetAppViewPath(int statusCode)
    {
        return string.Format(CultureInfo.InvariantCulture, AppViewPathFormat, statusCode);
    }

    internal static string GetReservedRoute(int statusCode)
    {
        return string.Format(CultureInfo.InvariantCulture, ReservedRouteFormat, statusCode);
    }
}
