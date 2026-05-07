namespace ForgeTrust.Runnable.Web;

/// <summary>
/// Represents the model passed to Runnable's conventional production exception view.
/// </summary>
/// <param name="StatusCode">The HTTP status code being rendered.</param>
/// <param name="RequestId">The request identifier that app logs can use to correlate the failure.</param>
/// <remarks>
/// This model intentionally excludes exception details, request headers, cookies, route values, and form fields.
/// Production error pages should help users recover and help operators correlate logs without disclosing request
/// internals or implementation details.
/// </remarks>
public sealed record ExceptionPageModel(
    int StatusCode,
    string RequestId);
