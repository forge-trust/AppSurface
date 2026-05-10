using System.Net;

namespace ForgeTrust.Runnable.Web.RazorWire.Cli;

/// <summary>
/// Records the fetch and materialization state for one normalized export route.
/// </summary>
internal sealed class ExportRouteOutcome
{
    private ExportRouteOutcome(
        string route,
        bool succeeded,
        string? contentType,
        HttpStatusCode? statusCode,
        string? artifactPath,
        string? artifactUrl,
        string? textBody,
        Exception? exception)
    {
        Route = route;
        Succeeded = succeeded;
        ContentType = contentType;
        StatusCode = statusCode;
        ArtifactPath = artifactPath;
        ArtifactUrl = artifactUrl;
        TextBody = textBody;
        Exception = exception;
    }

    /// <summary>Gets the normalized root-relative route that was fetched.</summary>
    public string Route { get; }

    /// <summary>Gets a value indicating whether the route fetched successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the response media type when one was available, optionally including media-type parameters.</summary>
    public string? ContentType { get; }

    /// <summary>Gets the non-success response status code when the fetch failed at the HTTP layer.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Gets the absolute output file path for a successful route.</summary>
    public string? ArtifactPath { get; }

    /// <summary>Gets the static-host URL that should be used to reach the emitted artifact.</summary>
    public string? ArtifactUrl { get; }

    /// <summary>Gets the fetched HTML or CSS body retained until materialization.</summary>
    public string? TextBody { get; }

    /// <summary>Gets the exception that prevented the route from being fetched or written.</summary>
    public Exception? Exception { get; }

    /// <summary>Gets a value indicating whether the route was fetched as HTML.</summary>
    public bool IsHtml => MediaTypeEquals(ContentType, "text/html");

    /// <summary>Gets a value indicating whether the route was fetched as CSS.</summary>
    public bool IsCss => MediaTypeEquals(ContentType, "text/css");

    /// <summary>
    /// Creates a successful route outcome with emitted artifact details.
    /// </summary>
    /// <param name="route">Normalized root-relative route that was crawled, such as <c>/</c> or <c>/docs/start</c>.</param>
    /// <param name="contentType">Response media type when known, or <see langword="null"/> when the server omitted it.</param>
    /// <param name="artifactPath">Absolute output file path written for the route.</param>
    /// <param name="artifactUrl">Static-host URL that resolves to the emitted artifact.</param>
    /// <param name="textBody">Captured HTML or CSS body retained for later validation and rewriting, or <see langword="null"/> for streamed assets.</param>
    /// <returns>
    /// An outcome with <see cref="Succeeded"/> set to <see langword="true"/>, artifact fields populated, and no status code or exception.
    /// </returns>
    public static ExportRouteOutcome Success(
        string route,
        string? contentType,
        string artifactPath,
        string artifactUrl,
        string? textBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactUrl);

        return new ExportRouteOutcome(route, true, contentType, null, artifactPath, artifactUrl, textBody, null);
    }

    /// <summary>
    /// Creates a successful route outcome for an artifact whose body does not need deferred validation or rewriting.
    /// </summary>
    /// <param name="route">Normalized root-relative route that was crawled, such as <c>/</c> or <c>/docs/start</c>.</param>
    /// <param name="contentType">Response media type when known, or <see langword="null"/> when the server omitted it.</param>
    /// <param name="artifactPath">Absolute output file path written for the route.</param>
    /// <param name="artifactUrl">Static-host URL that resolves to the emitted artifact.</param>
    /// <returns>
    /// An outcome with <see cref="Succeeded"/> set to <see langword="true"/>, artifact fields populated, and no retained body.
    /// </returns>
    public static ExportRouteOutcome Success(
        string route,
        string? contentType,
        string artifactPath,
        string artifactUrl)
    {
        return Success(route, contentType, artifactPath, artifactUrl, textBody: null);
    }

    /// <summary>
    /// Creates an outcome for a route that completed at the HTTP layer with a non-success status code.
    /// </summary>
    /// <param name="route">Normalized root-relative route that was crawled.</param>
    /// <param name="statusCode">HTTP status code returned by the source application.</param>
    /// <returns>
    /// An outcome with <see cref="Succeeded"/> set to <see langword="false"/>, <see cref="StatusCode"/> populated, and no artifact or exception fields.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="route"/> is null, empty, or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="statusCode"/> is a successful 2xx HTTP status code.
    /// </exception>
    public static ExportRouteOutcome NonSuccess(string route, HttpStatusCode statusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        if ((int)statusCode is >= 200 and <= 299)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "A non-success route outcome requires a non-2xx HTTP status code.");
        }

        return new ExportRouteOutcome(route, false, null, statusCode, null, null, null, null);
    }

    /// <summary>
    /// Creates an outcome for a route that failed because an exception interrupted fetch or write processing.
    /// </summary>
    /// <param name="route">Normalized root-relative route that was being processed.</param>
    /// <param name="exception">Exception that prevented the route from completing.</param>
    /// <returns>
    /// An outcome with <see cref="Succeeded"/> set to <see langword="false"/>, <see cref="Exception"/> populated, and no status or artifact fields.
    /// </returns>
    public static ExportRouteOutcome Failed(string route, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(exception);

        return new ExportRouteOutcome(route, false, null, null, null, null, null, exception);
    }

    private static bool MediaTypeEquals(string? contentType, string expectedMediaType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var parameterStart = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = parameterStart >= 0
            ? contentType.AsSpan(0, parameterStart)
            : contentType.AsSpan();

        return mediaType.Trim().Equals(expectedMediaType.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }
}
