using Microsoft.AspNetCore.Http;

namespace ForgeTrust.RazorWire;

/// <summary>
/// Defines the RazorWire-owned request marker used by static exporters to request safe auth projection.
/// </summary>
/// <remarks>
/// The marker is non-secret and only downgrades RazorWire auth projection helpers into static-safe output. It must not
/// be interpreted as authorization, authentication, or permission to render protected content.
/// </remarks>
public static class RazorWireStaticExportRequest
{
    /// <summary>
    /// Gets the HTTP header name static exporters send to request safe anonymous auth projection.
    /// </summary>
    public const string HeaderName = "X-RazorWire-Static-Export";

    /// <summary>
    /// Gets the only header value recognized for v1 static anonymous auth projection.
    /// </summary>
    public const string HeaderValue = "auth-anonymous-v1";
}

/// <summary>
/// Describes RazorWire static export behavior requested for the current HTTP request.
/// </summary>
public sealed class RazorWireStaticExportContext
{
    private static readonly RazorWireStaticExportContext None = new(false);
    private static readonly RazorWireStaticExportContext StaticAuthProjection = new(true);

    private RazorWireStaticExportContext(bool isStaticAuthProjection)
    {
        IsStaticAuthProjection = isStaticAuthProjection;
    }

    /// <summary>
    /// Gets a value indicating whether auth projection helpers must render static-safe anonymous output.
    /// </summary>
    public bool IsStaticAuthProjection { get; }

    /// <summary>
    /// Resolves static export behavior from the current request headers.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <returns>The resolved static export context.</returns>
    public static RazorWireStaticExportContext Resolve(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Request.Headers.TryGetValue(RazorWireStaticExportRequest.HeaderName, out var values)
               && values.Any(value => string.Equals(
                   value?.Trim(),
                   RazorWireStaticExportRequest.HeaderValue,
                   StringComparison.Ordinal))
            ? StaticAuthProjection
            : None;
    }
}
