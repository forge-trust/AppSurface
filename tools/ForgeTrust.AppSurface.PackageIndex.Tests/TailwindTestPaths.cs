namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Provides a portable temporary root for Tailwind evidence tests.</summary>
internal static class TailwindTestPaths
{
    /// <summary>Gets the process temp root, resolving macOS system directory aliases used by path-security tests.</summary>
    internal static string TemporaryRoot
    {
        get
        {
            var path = Path.GetTempPath();
            return OperatingSystem.IsMacOS()
                && (path.StartsWith("/var/", StringComparison.Ordinal)
                    || path.StartsWith("/tmp/", StringComparison.Ordinal))
                ? "/private" + path
                : path;
        }
    }
}
