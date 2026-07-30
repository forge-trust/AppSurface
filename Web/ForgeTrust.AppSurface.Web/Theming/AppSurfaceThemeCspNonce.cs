using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>Provides the documented request-item key for a host-generated AppSurface theme CSP nonce.</summary>
public static class AppSurfaceThemeCspNonce
{
    /// <summary>Gets the <see cref="HttpContext.Items"/> key consumed by package-owned layouts.</summary>
    public const string HttpContextItemKey = "ForgeTrust.AppSurface.Web.Theming.CspNonce";

    /// <summary>Gets the host-generated nonce from the current request, when one is available.</summary>
    /// <param name="httpContext">Current request context.</param>
    /// <returns>The nonce supplied by the host, or <see langword="null"/> when the host does not use a nonce-based CSP.</returns>
    public static string? Get(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Items.TryGetValue(HttpContextItemKey, out var value)
            ? value as string
            : null;
    }
}
