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
    /// <summary>
    /// Gets the framework-owned base path for browser status page preview and re-execute routes.
    /// </summary>
    /// <remarks>
    /// This prefix is reserved for Runnable middleware and tooling. Applications should not register their own
    /// endpoints under this path because the framework maps supported integer status routes beneath it.
    /// </remarks>
    internal const string ReservedRouteBase = "/_runnable/errors";

    /// <summary>
    /// Gets the conventional application or shared-library override path format for browser status page views.
    /// </summary>
    /// <remarks>
    /// The <c>{0}</c> placeholder is the HTTP status code formatted with <see cref="CultureInfo.InvariantCulture"/>.
    /// Runnable currently probes app overrides for 401, 403, and 404 by formatting this value.
    /// </remarks>
    public const string AppViewPathFormat = "~/Views/Shared/{0}.cshtml";

    /// <summary>
    /// Gets the reserved framework route used to render the unauthorized page directly.
    /// </summary>
    /// <remarks>
    /// This framework-owned preview route is intended for direct browser preview and internal re-execution of
    /// empty HTML 401 responses. It is not an application route extension point.
    /// </remarks>
    public const string ReservedUnauthorizedRoute = ReservedRouteBase + "/401";

    /// <summary>
    /// Gets the reserved framework route used to render the forbidden page directly.
    /// </summary>
    /// <remarks>
    /// This framework-owned preview route is intended for direct browser preview and internal re-execution of
    /// empty HTML 403 responses. It is not an application route extension point.
    /// </remarks>
    public const string ReservedForbiddenRoute = ReservedRouteBase + "/403";

    /// <summary>
    /// Gets the reserved framework route used to render the not-found page directly.
    /// This route is intended for framework tooling such as static export.
    /// </summary>
    /// <remarks>
    /// Static export probes this route and writes only <c>404.html</c>. Applications should override the view at
    /// <see cref="AppViewPathFormat"/> rather than mapping their own route at this path.
    /// </remarks>
    public const string ReservedNotFoundRoute = ReservedRouteBase + "/404";

    /// <summary>
    /// Gets the framework-reserved route format used for supported browser status page preview routes.
    /// </summary>
    /// <remarks>
    /// The <c>{0}</c> placeholder is an integer HTTP status code formatted with
    /// <see cref="CultureInfo.InvariantCulture"/>. Callers should pass standard status codes and then validate
    /// support with <see cref="BrowserStatusPageDescriptor.TryGet(int, out BrowserStatusPageDescriptor)"/>.
    /// </remarks>
    internal const string ReservedRouteFormat = ReservedRouteBase + "/{0}";

    /// <summary>
    /// Gets the endpoint route pattern used by Runnable to map browser status page preview routes.
    /// </summary>
    /// <remarks>
    /// The <c>statusCode:int</c> route value keeps unsupported or malformed routes out of the renderer. The
    /// endpoint still validates the parsed integer against Runnable's supported descriptors.
    /// </remarks>
    internal const string ReservedRoutePattern = ReservedRouteBase + "/{statusCode:int}";

    /// <summary>
    /// Gets the framework fallback Razor view path used when an app or shared library override is missing.
    /// </summary>
    /// <remarks>
    /// The renderer uses this shared view only after the status-specific app view cannot be resolved. Apps that
    /// need custom content should provide <see cref="AppViewPathFormat"/> overrides instead of replacing this file.
    /// </remarks>
    internal const string FrameworkFallbackViewPath = "~/Views/_Runnable/Errors/StatusPage.cshtml";

    /// <summary>
    /// Formats the conventional app override view path for the supplied HTTP status code.
    /// </summary>
    /// <param name="statusCode">
    /// The integer HTTP status code used for the view filename. Runnable currently renders 401, 403, and 404.
    /// </param>
    /// <returns>The app/shared Razor view path produced from <see cref="AppViewPathFormat"/>.</returns>
    /// <remarks>
    /// This helper does not validate support. Callers that accept arbitrary status codes should use
    /// <see cref="BrowserStatusPageDescriptor.TryGet(int, out BrowserStatusPageDescriptor)"/> before rendering.
    /// </remarks>
    internal static string GetAppViewPath(int statusCode)
    {
        return string.Format(CultureInfo.InvariantCulture, AppViewPathFormat, statusCode);
    }

    /// <summary>
    /// Formats the framework-reserved preview route for the supplied HTTP status code.
    /// </summary>
    /// <param name="statusCode">
    /// The integer HTTP status code used for the reserved route segment. Runnable currently renders 401, 403,
    /// and 404.
    /// </param>
    /// <returns>The reserved route produced from <see cref="ReservedRouteFormat"/>.</returns>
    /// <remarks>
    /// The returned path is for framework middleware, direct preview, and tooling such as static export. It should
    /// not be exposed as an application-owned route.
    /// </remarks>
    internal static string GetReservedRoute(int statusCode)
    {
        return string.Format(CultureInfo.InvariantCulture, ReservedRouteFormat, statusCode);
    }
}
