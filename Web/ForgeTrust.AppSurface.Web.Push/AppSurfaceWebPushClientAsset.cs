using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Web.Push;

/// <summary>Serves the package-owned browser client from one fixed app-root-relative path.</summary>
/// <remarks>
/// Map the Web Push rail on the application-root endpoint builder so route-group prefixes cannot move this asset.
/// The embedded resource is required package content; failure to locate it is a packaging error.
/// </remarks>
internal static class AppSurfaceWebPushClientAsset
{
    /// <summary>Gets the fixed request path used by the Tag Helper and endpoint mapping.</summary>
    public const string Path = "/_appsurface/pwa/push-client.js";
    private static readonly byte[] Content = Load();

    /// <summary>Gets the first 16 lowercase hexadecimal characters of the embedded asset SHA-256 hash.</summary>
    /// <remarks>The version is suitable for cache busting; it is not a package or API version.</remarks>
    public static string Version { get; } = Convert.ToHexString(SHA256.HashData(Content))[..16].ToLowerInvariant();

    /// <summary>Writes the JavaScript response, or headers only for a HEAD request.</summary>
    /// <param name="context">The active request context. Request cancellation stops body transmission.</param>
    /// <remarks>
    /// A matching <c>v</c> query value enables immutable caching. Missing or stale versions use <c>no-cache</c>.
    /// The response always applies JavaScript content type and <c>nosniff</c> headers.
    /// </remarks>
    public static async Task WriteAsync(HttpContext context)
    {
        context.Response.ContentType = "text/javascript; charset=utf-8";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.CacheControl = string.Equals(context.Request.Query["v"], Version, StringComparison.Ordinal)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        context.Response.ContentLength = Content.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(Content, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static byte[] Load()
    {
        const string suffix = ".Assets.pwa-push-client.js";
        var assembly = typeof(AppSurfaceWebPushClientAsset).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
