namespace ForgeTrust.Runnable.Web;

/// <summary>
/// Represents the model passed to Runnable's conventional browser status page view.
/// </summary>
/// <param name="StatusCode">The HTTP status code being rendered. Runnable currently produces 401, 403, or 404.</param>
/// <param name="OriginalPath">
/// The nullable original request path that produced the status response, when middleware can provide it.
/// </param>
/// <param name="OriginalQueryString">
/// The nullable original request query string that produced the status response, when middleware can provide it.
/// </param>
/// <remarks>
/// The framework renderer normalizes missing or malformed route status values to 404 before creating the default
/// model, but explicit status values must be supported by the built-in browser status page descriptors. Custom
/// producers should pass only status codes their view understands. <paramref name="OriginalPath"/> and
/// <paramref name="OriginalQueryString"/> are null for direct preview requests and can also be absent when upstream
/// middleware strips or replaces status-code re-execution metadata.
///
/// Status-page views should prefer this model for user-facing recovery copy because it captures the original failed
/// request after Runnable re-executes the framework route. Read <c>HttpContext.Request</c> only for current-request
/// concerns such as URL generation; during re-execution it points at the reserved framework route. Treat the query
/// string as display-only metadata, not as authorization or security input, and do not assume the path has a trailing
/// slash or app-specific normalization.
/// </remarks>
public sealed record BrowserStatusPageModel(
    int StatusCode,
    string? OriginalPath,
    string? OriginalQueryString);
