using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Web.Push;

internal static class AppSurfaceWebPushClientAsset
{
    public const string Path = "/_appsurface/pwa/push-client.js";
    private static readonly byte[] Content = Load();

    public static string Version { get; } = Convert.ToHexString(SHA256.HashData(Content))[..16].ToLowerInvariant();

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
