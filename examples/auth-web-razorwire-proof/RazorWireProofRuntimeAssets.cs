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

    /// <summary>
    /// Maps the RazorWire browser runtime assets needed by this project-reference proof app.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder for the proof host.</param>
    /// <remarks>
    /// The sample maps GET and HEAD endpoints under <c>/_content/ForgeTrust.RazorWire</c> for the embedded
    /// RazorWire JavaScript files. This fallback is needed only for the sample's project-reference run path;
    /// packaged/static-web-asset hosting should serve RazorWire assets through the normal ASP.NET Core
    /// static asset pipeline.
    /// </remarks>
    public static void MapRazorWireProofRuntimeAssets(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        MapEmbeddedAsset(endpoints, "razorwire/razorwire.js");
        MapEmbeddedAsset(endpoints, "razorwire/razorwire.islands.js");
        MapEmbeddedAsset(endpoints, "razorwire/page-navigation.js");
        MapEmbeddedAsset(endpoints, "razorwire/section-copy.js");
        MapEmbeddedAsset(endpoints, "razorwire/form-interactions.js");
    }

    /// <summary>
    /// Maps one embedded RazorWire asset to its static-web-asset-style URL.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder for the proof host.</param>
    /// <param name="webRootSubPath">
    /// Slash-separated asset path below <c>/_content/ForgeTrust.RazorWire</c> and below the
    /// <c>RazorWireEmbeddedAssets/</c> manifest-resource prefix.
    /// </param>
    /// <remarks>
    /// Requests are limited to the hard-coded paths supplied by <see cref="MapRazorWireProofRuntimeAssets"/>,
    /// so arbitrary request paths are not converted into resource names.
    /// </remarks>
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

    /// <summary>
    /// Writes an embedded asset response if the mapped resource exists.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="webRootSubPath">The mapped asset path below the RazorWire static asset base path.</param>
    /// <returns>
    /// <see langword="true"/> when the resource was found and response headers were written; otherwise
    /// <see langword="false"/> so the caller can return 404.
    /// </returns>
    /// <remarks>
    /// Resource names are resolved by appending the normalized asset path to
    /// <c>RazorWireEmbeddedAssets/</c>. HEAD requests return status, content type, and content length
    /// without copying the response body.
    /// </remarks>
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

    /// <summary>
    /// Resolves the HTTP content type for a mapped asset path.
    /// </summary>
    /// <param name="relativePath">The mapped asset path.</param>
    /// <returns>
    /// The provider-detected content type, or <c>application/octet-stream</c> when the extension is unknown.
    /// </returns>
    private static string ResolveContentType(string relativePath)
    {
        return ContentTypeProvider.TryGetContentType(relativePath, out var contentType)
            ? contentType
            : "application/octet-stream";
    }
}
