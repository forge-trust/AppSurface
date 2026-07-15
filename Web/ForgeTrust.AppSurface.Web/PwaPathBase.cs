using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Resolves app-root-relative PWA paths beneath the current request path base.
/// </summary>
internal static class PwaPathBase
{
    /// <summary>
    /// Prepends <paramref name="pathBase"/> to the app-root-relative <paramref name="path"/>.
    /// </summary>
    /// <param name="pathBase">The current request path base.</param>
    /// <param name="path">The app-root-relative path to resolve.</param>
    /// <returns>The path resolved beneath <paramref name="pathBase"/>.</returns>
    public static string Add(PathString pathBase, string path) =>
        pathBase.Add(new PathString(path)).Value ?? path;
}
