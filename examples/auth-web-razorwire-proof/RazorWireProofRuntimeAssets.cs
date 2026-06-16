using System.Reflection;
using ForgeTrust.RazorWire;
using Microsoft.AspNetCore.StaticFiles;

namespace AuthWebRazorWireProofExample;

/// <summary>
/// Sample-local fallback for RazorWire runtime assets when the proof runs from project references.
/// </summary>
internal static class RazorWireProofRuntimeAssets
{
    private const string StaticAssetBasePath = "/_content/ForgeTrust.RazorWire";
    private const string EmbeddedAssetResourcePrefix = "RazorWireEmbeddedAssets/";
    private static readonly Assembly RazorWireAssembly = typeof(RazorWireWebModule).Assembly;
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    public static void MapRazorWireProofRuntimeAssets(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        MapEmbeddedAsset(endpoints, "razorwire/razorwire.js");
        MapEmbeddedAsset(endpoints, "razorwire/razorwire.islands.js");
        MapEmbeddedAsset(endpoints, "razorwire/page-navigation.js");
        MapEmbeddedAsset(endpoints, "razorwire/section-copy.js");
    }

    private static void MapEmbeddedAsset(IEndpointRouteBuilder endpoints, string webRootSubPath)
    {
        endpoints.MapMethods(
            $"{StaticAssetBasePath}/{webRootSubPath}",
            [HttpMethods.Get, HttpMethods.Head],
            async context =>
            {
                if (!await TryWriteEmbeddedAssetAsync(context, webRootSubPath))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                }
            });
    }

    private static async Task<bool> TryWriteEmbeddedAssetAsync(HttpContext context, string webRootSubPath)
    {
        var resourceName = EmbeddedAssetResourcePrefix + webRootSubPath.Replace('\\', '/').TrimStart('/');
        await using var stream = RazorWireAssembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ResolveContentType(webRootSubPath);
        context.Response.ContentLength = stream.Length;
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return true;
        }

        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        return true;
    }

    private static string ResolveContentType(string relativePath)
    {
        return ContentTypeProvider.TryGetContentType(relativePath, out var contentType)
            ? contentType
            : "application/octet-stream";
    }
}
