namespace ForgeTrust.AppSurface.Testing;

/// <summary>Resolves the dotnet host used by test subprocesses.</summary>
/// <remarks>
/// Resolution prefers a fully qualified <c>DOTNET_HOST_PATH</c>, then a host beneath
/// <c>DOTNET_ROOT</c>, then the first existing host candidate in <c>PATH</c>.
/// The explicit host and root candidates are returned without checking file existence,
/// preserving the test subprocess helpers' existing selection behavior.
/// </remarks>
public static class TestDotnetHost
{
    /// <summary>Resolves the dotnet host using the current process environment.</summary>
    /// <returns>The selected host path, or the first existing host found in <c>PATH</c>.</returns>
    /// <exception cref="InvalidOperationException">No host candidate exists in <c>PATH</c>.</exception>
    public static string Resolve() => Resolve(
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
        Environment.GetEnvironmentVariable("DOTNET_ROOT"),
        Environment.GetEnvironmentVariable("PATH"),
        Environment.CurrentDirectory,
        OperatingSystem.IsWindows());

    /// <summary>Resolves a dotnet host from explicit environment values and a current directory.</summary>
    /// <param name="dotnetHostPath">The <c>DOTNET_HOST_PATH</c> value, if set.</param>
    /// <param name="dotnetRoot">The <c>DOTNET_ROOT</c> value, if set.</param>
    /// <param name="path">The <c>PATH</c> value, if set. Empty entries refer to <paramref name="currentDirectory"/>.</param>
    /// <param name="currentDirectory">The absolute current directory used to resolve relative path entries.</param>
    /// <param name="isWindows">Whether to search for <c>dotnet.exe</c> instead of <c>dotnet</c>.</param>
    /// <returns>The selected host path, or the first existing host found in <paramref name="path"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="currentDirectory"/> is not fully qualified.</exception>
    /// <exception cref="InvalidOperationException">No host candidate exists in <paramref name="path"/>.</exception>
    internal static string Resolve(
        string? dotnetHostPath,
        string? dotnetRoot,
        string? path,
        string currentDirectory,
        bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        if (!Path.IsPathFullyQualified(currentDirectory))
        {
            throw new ArgumentException("The current directory must be fully qualified.", nameof(currentDirectory));
        }

        if (!string.IsNullOrWhiteSpace(dotnetHostPath) && Path.IsPathFullyQualified(dotnetHostPath))
        {
            return dotnetHostPath;
        }

        var executable = isWindows ? "dotnet.exe" : "dotnet";
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            // Environment paths may be rooted or contain parent segments; resolve their OS semantics before containment.
            var root = Path.GetFullPath(dotnetRoot, currentDirectory);
            return TestPathUtils.PathUnder(root, executable);
        }

        return (path ?? string.Empty)
            .Split(Path.PathSeparator)
            .Select(entry =>
            {
                // PATH entries may be rooted or traverse parents, so they are not relative containment segments.
                var root = string.IsNullOrEmpty(entry)
                    ? currentDirectory
                    : Path.GetFullPath(entry, currentDirectory);
                return TestPathUtils.PathUnder(root, executable);
            })
            .First(File.Exists);
    }
}
