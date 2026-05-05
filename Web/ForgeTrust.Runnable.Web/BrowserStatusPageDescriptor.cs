using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.Runnable.Web;

/// <summary>
/// Describes one built-in browser status page that Runnable can preview, re-execute, and render.
/// </summary>
/// <param name="StatusCode">The supported HTTP status code for this descriptor.</param>
/// <param name="AppViewPath">The conventional status-specific app or shared-library override view path.</param>
/// <param name="ReservedRoute">The framework-owned preview and re-execute route for this status code.</param>
/// <param name="Title">The document title used by the framework fallback view.</param>
/// <param name="Eyebrow">The short label rendered above the fallback heading.</param>
/// <param name="Heading">The main fallback heading shown to browser users.</param>
/// <param name="Description">The fallback explanation of what happened and how to recover.</param>
/// <param name="PrimaryActionText">The fallback primary recovery action label.</param>
/// <remarks>
/// Descriptors are internal framework metadata for the current built-in status set: 401, 403, and 404. Use
/// <see cref="TryGet(int, out BrowserStatusPageDescriptor)"/> before routing or rendering arbitrary status codes;
/// unsupported codes should not be re-executed through the browser status page pipeline.
/// </remarks>
internal sealed record BrowserStatusPageDescriptor(
    int StatusCode,
    string AppViewPath,
    string ReservedRoute,
    string Title,
    string Eyebrow,
    string Heading,
    string Description,
    string PrimaryActionText)
{
    /// <summary>
    /// Gets the shared framework fallback view path used when <see cref="AppViewPath"/> cannot be resolved.
    /// </summary>
    /// <remarks>
    /// The fallback is shared across all supported statuses. App and shared Razor Class Library overrides remain
    /// status-specific through <see cref="AppViewPath"/>.
    /// </remarks>
    public string FrameworkFallbackViewPath => BrowserStatusPageDefaults.FrameworkFallbackViewPath;

    /// <summary>
    /// Gets the built-in descriptor for 401 Unauthorized browser responses.
    /// </summary>
    /// <remarks>
    /// The fallback copy assumes a browser user must sign in again or refresh an expired session.
    /// </remarks>
    public static readonly BrowserStatusPageDescriptor Unauthorized = new(
        StatusCodes.Status401Unauthorized,
        BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status401Unauthorized),
        BrowserStatusPageDefaults.ReservedUnauthorizedRoute,
        "Sign In Required",
        "Runnable default 401",
        "Sign in required",
        "You need to sign in or refresh your session before this app can show that page.",
        "Return home");

    /// <summary>
    /// Gets the built-in descriptor for 403 Forbidden browser responses.
    /// </summary>
    /// <remarks>
    /// The fallback copy assumes the user is known to the app but does not have permission for the requested page.
    /// </remarks>
    public static readonly BrowserStatusPageDescriptor Forbidden = new(
        StatusCodes.Status403Forbidden,
        BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status403Forbidden),
        BrowserStatusPageDefaults.ReservedForbiddenRoute,
        "Access Forbidden",
        "Runnable default 403",
        "Access forbidden",
        "You are signed in, but this app does not allow your account to open that page.",
        "Return home");

    /// <summary>
    /// Gets the built-in descriptor for 404 Not Found browser responses.
    /// </summary>
    /// <remarks>
    /// The fallback copy assumes the route is missing, stale, or intentionally not exposed by the app.
    /// </remarks>
    public static readonly BrowserStatusPageDescriptor NotFound = new(
        StatusCodes.Status404NotFound,
        BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status404NotFound),
        BrowserStatusPageDefaults.ReservedNotFoundRoute,
        "Page Not Found",
        "Runnable default 404",
        "Page not found",
        "The route may have moved, the link may be stale, or this app may not expose that page.",
        "Return home");

    /// <summary>
    /// Gets all built-in browser status page descriptors in the order Runnable validates them.
    /// </summary>
    /// <remarks>
    /// This list is the built-in set for the current release, not a promise that every browser-relevant status is
    /// supported. Callers should use <see cref="TryGet(int, out BrowserStatusPageDescriptor)"/> for lookup instead
    /// of assuming a status code is present.
    /// </remarks>
    public static IReadOnlyList<BrowserStatusPageDescriptor> Supported { get; } =
    [
        Unauthorized,
        Forbidden,
        NotFound
    ];

    /// <summary>
    /// Attempts to resolve a built-in browser status page descriptor for an HTTP status code.
    /// </summary>
    /// <param name="statusCode">The HTTP status code to resolve.</param>
    /// <param name="descriptor">
    /// When this method returns <see langword="true"/>, the descriptor for <paramref name="statusCode"/>; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> for supported status codes 401, 403, and 404; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Unknown status codes are intentionally rejected so production exception pages and future status families can
    /// be designed separately instead of accidentally using the 401/403/404 browser-page contract.
    /// </remarks>
    public static bool TryGet(int statusCode, [NotNullWhen(true)] out BrowserStatusPageDescriptor? descriptor)
    {
        descriptor = statusCode switch
        {
            StatusCodes.Status401Unauthorized => Unauthorized,
            StatusCodes.Status403Forbidden => Forbidden,
            StatusCodes.Status404NotFound => NotFound,
            _ => null
        };

        return descriptor is not null;
    }
}
