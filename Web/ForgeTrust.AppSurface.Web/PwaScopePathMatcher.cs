namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Evaluates whether a resolved PWA start URL path falls within a resolved manifest scope path.
/// </summary>
/// <remarks>
/// The matcher centralizes the browser-style prefix contract used by both AppSurface Web startup validation and the
/// AppSurface CLI verifier. A scope ending in <c>/</c> is a directory prefix and does not include the same path without
/// that slash; a scope without a trailing slash includes the exact path and child paths separated by <c>/</c>.
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

        if (scope.EndsWith("/", StringComparison.Ordinal))
        {
            return path.StartsWith(scope, StringComparison.Ordinal);
        }

        return string.Equals(path, scope, StringComparison.Ordinal)
            || path.StartsWith(scope + "/", StringComparison.Ordinal);
    }
}
