namespace ForgeTrust.Runnable.Web;

/// <summary>
/// Represents the model passed to Runnable's conventional browser status page view.
/// </summary>
/// <param name="StatusCode">The HTTP status code being rendered, currently 401, 403, or 404.</param>
/// <param name="OriginalPath">The original request path that produced the status response, when available.</param>
/// <param name="OriginalQueryString">The original request query string that produced the status response, when available.</param>
public sealed record BrowserStatusPageModel(
    int StatusCode,
    string? OriginalPath,
    string? OriginalQueryString);
