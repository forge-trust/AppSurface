namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Resolves the shared Tailwind standalone CLI download cache used by source-tree runtime builds.
/// </summary>
internal static class TailwindDownloadCache
{
    /// <summary>
    /// Gets the default shared cache root for the current user.
    /// </summary>
    /// <param name="getEnvironmentVariable">Optional environment lookup used by tests.</param>
    /// <returns>
    /// A user-level cache directory when one can be derived from the environment; otherwise <c>null</c>.
    /// </returns>
    /// <remarks>
    /// Source-tree runtime projects download Tailwind executables before they are packed into runtime packages.
    /// Keeping that cache under a user-level location avoids one full copy per Git worktree while still allowing
    /// callers to override the root through MSBuild's <c>TailwindDownloadCacheRoot</c> property.
    /// </remarks>
    public static string? GetDefaultRoot(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        var xdgCacheHome = getEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgCacheHome))
        {
            return Path.Join(xdgCacheHome, "forgetrust", "appsurface", "tailwind");
        }

        var localAppData = getEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Join(localAppData, "ForgeTrust", "AppSurface", "Tailwind");
        }

        var home = getEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Join(home, ".cache", "forgetrust", "appsurface", "tailwind");
        }

        var userProfile = getEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Join(userProfile, ".cache", "forgetrust", "appsurface", "tailwind");
        }

        return null;
    }

    /// <summary>
    /// Gets the cached runtime binary path for a Tailwind version and host runtime identifier.
    /// </summary>
    /// <param name="cacheRoot">The configured or default cache root.</param>
    /// <param name="tailwindVersion">The Tailwind standalone CLI version.</param>
    /// <param name="rid">The Tailwind runtime identifier.</param>
    /// <param name="runtimeBinaryName">The RID-specific standalone binary file name.</param>
    /// <returns>The expected cache path for the binary.</returns>
    public static string GetRuntimeBinaryPath(
        string cacheRoot,
        string tailwindVersion,
        string rid,
        string runtimeBinaryName)
    {
        return Path.Join(cacheRoot, $"tailwind-{tailwindVersion}", rid, runtimeBinaryName);
    }
}
