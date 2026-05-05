using Microsoft.AspNetCore.Http;

namespace ForgeTrust.Runnable.Web;

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
    public string FrameworkFallbackViewPath => BrowserStatusPageDefaults.FrameworkFallbackViewPath;

    public static readonly BrowserStatusPageDescriptor Unauthorized = new(
        StatusCodes.Status401Unauthorized,
        BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status401Unauthorized),
        BrowserStatusPageDefaults.ReservedUnauthorizedRoute,
        "Sign In Required",
        "Runnable default 401",
        "Sign in required",
        "You need to sign in or refresh your session before this app can show that page.",
        "Return home");

    public static readonly BrowserStatusPageDescriptor Forbidden = new(
        StatusCodes.Status403Forbidden,
        BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status403Forbidden),
        BrowserStatusPageDefaults.ReservedForbiddenRoute,
        "Access Forbidden",
        "Runnable default 403",
        "Access forbidden",
        "You are signed in, but this app does not allow your account to open that page.",
        "Return home");

    public static readonly BrowserStatusPageDescriptor NotFound = new(
        StatusCodes.Status404NotFound,
        BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status404NotFound),
        BrowserStatusPageDefaults.ReservedNotFoundRoute,
        "Page Not Found",
        "Runnable default 404",
        "Page not found",
        "The route may have moved, the link may be stale, or this app may not expose that page.",
        "Return home");

    public static IReadOnlyList<BrowserStatusPageDescriptor> Supported { get; } =
    [
        Unauthorized,
        Forbidden,
        NotFound
    ];

    public static bool TryGet(int statusCode, out BrowserStatusPageDescriptor descriptor)
    {
        descriptor = statusCode switch
        {
            StatusCodes.Status401Unauthorized => Unauthorized,
            StatusCodes.Status403Forbidden => Forbidden,
            StatusCodes.Status404NotFound => NotFound,
            _ => null!
        };

        return descriptor is not null;
    }
}
