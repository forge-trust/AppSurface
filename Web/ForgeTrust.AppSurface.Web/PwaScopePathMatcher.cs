namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Evaluates whether a resolved PWA start URL path falls within a resolved manifest scope path.
/// </summary>
/// <remarks>
/// The matcher centralizes the browser-style prefix contract used by both AppSurface Web startup validation and the
/// AppSurface CLI verifier. Browser service-worker and manifest scopes use raw URL-path prefix matching. Applications
/// that require a segment boundary should end a non-root scope in <c>/</c>.
/// </remarks>
internal static class PwaScopePathMatcher
{
    /// <summary>
    /// Returns a value indicating whether <paramref name="path"/> is within <paramref name="scope"/>.
    /// </summary>
    /// <param name="path">The resolved app-root-relative path portion of the PWA start URL.</param>
    /// <param name="scope">The resolved app-root-relative path portion of the PWA manifest scope.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> is covered by <paramref name="scope"/>.</returns>
    public static bool IsPathWithinScope(string path, string scope)
    {
        if (scope == "/")
        {
            return true;
        }

        return path.StartsWith(scope, StringComparison.Ordinal);
    }
}
