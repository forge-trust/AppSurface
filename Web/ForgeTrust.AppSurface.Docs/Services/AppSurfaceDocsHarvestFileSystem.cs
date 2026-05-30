namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Provides file-system traversal helpers for source-backed AppSurface Docs harvesting.
/// </summary>
/// <remarks>
/// Built-in harvesters treat the configured repository root as the read boundary. Candidate traversal therefore skips
/// file and directory reparse points before a harvester can read content through a symlink, junction, or similar
/// filesystem indirection. The helper preserves the existing lazy depth-first traversal shape and lets callers provide
/// their own repository-relative directory-pruning policy.
/// </remarks>
internal static class AppSurfaceDocsHarvestFileSystem
{
    /// <summary>
    /// Lazily enumerates candidate files under a repository root while skipping reparse-point files and directories.
    /// </summary>
    /// <param name="rootPath">The absolute repository root to traverse.</param>
    /// <param name="searchPattern">The file-system search pattern applied to each visited directory.</param>
    /// <param name="shouldPruneDirectory">
    /// Callback that receives a forward-slash repository-relative directory path and returns whether the subtree should
    /// be skipped before its descendants are enumerated.
    /// </param>
    /// <param name="cancellationToken">A token observed before each directory expansion.</param>
    /// <returns>Absolute file paths that match <paramref name="searchPattern"/> and are not reparse points.</returns>
    /// <remarks>
    /// File-system enumeration and attribute exceptions intentionally flow to callers, matching the existing harvester
    /// behavior for unreadable trees. This keeps operational failures visible instead of silently shrinking the public
    /// documentation surface.
    /// </remarks>
    public static IEnumerable<string> EnumerateCandidateFiles(
        string rootPath,
        string searchPattern,
        Func<string, bool> shouldPruneDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(searchPattern);
        ArgumentNullException.ThrowIfNull(shouldPruneDirectory);

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDirectory = pendingDirectories.Pop();
            foreach (var file in Directory.EnumerateFiles(currentDirectory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePoint(file))
                {
                    continue;
                }

                yield return file;
            }

            foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
            {
                if (IsReparsePoint(directory))
                {
                    continue;
                }

                var relativeDirectory = Path.GetRelativePath(rootPath, directory).Replace('\\', '/');
                if (shouldPruneDirectory(relativeDirectory))
                {
                    continue;
                }

                pendingDirectories.Push(directory);
            }
        }
    }

    /// <summary>
    /// Returns whether <paramref name="filePath"/> exists and is not a file-system reparse point.
    /// </summary>
    /// <param name="filePath">The absolute file path to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when the file exists and can be read as a normal source file candidate; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Use this for explicit one-off source reads such as root <c>LICENSE</c> files and Markdown sidecars that do not
    /// flow through candidate traversal. Missing files return <see langword="false"/>. Attribute lookup exceptions still
    /// flow so callers do not accidentally hide filesystem failures.
    /// </remarks>
    public static bool IsNonReparsePointFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return File.Exists(filePath) && !IsReparsePoint(filePath);
    }

    private static bool IsReparsePoint(string path)
    {
        return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }
}
