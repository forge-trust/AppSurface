namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Safely recreates isolated package-consumer proof workspaces.
/// </summary>
internal static class PackageProofWorkDirectory
{
    /// <summary>
    /// Requires two package-consumer proof workspaces to be disjoint before either is recreated.
    /// </summary>
    /// <param name="firstDirectory">First proof workspace.</param>
    /// <param name="secondDirectory">Second proof workspace.</param>
    /// <exception cref="PackageIndexException">Thrown when either workspace is the other workspace or one contains the other.</exception>
    internal static void RequireDisjoint(string firstDirectory, string secondDirectory)
    {
        var normalizedFirstDirectory = NormalizeDirectoryForSafetyComparison(firstDirectory);
        var normalizedSecondDirectory = NormalizeDirectoryForSafetyComparison(secondDirectory);
        if (IsParentOrSame(normalizedFirstDirectory, normalizedSecondDirectory)
            || IsParentOrSame(normalizedSecondDirectory, normalizedFirstDirectory))
        {
            throw new PackageIndexException(
                $"Package consumer proof work directories '{normalizedFirstDirectory}' and '{normalizedSecondDirectory}' must not overlap.");
        }
    }

    /// <summary>
    /// Deletes and recreates an isolated package-consumer proof workspace after rejecting unsafe deletion targets.
    /// </summary>
    /// <param name="workDirectory">Workspace that may be recursively deleted and recreated.</param>
    /// <param name="repositoryRoot">Repository root that must not be deleted or contained by the work directory.</param>
    /// <param name="artifactsDirectory">Package artifact directory that must not be deleted or contained by the work directory.</param>
    /// <exception cref="PackageIndexException">
    /// Thrown when <paramref name="workDirectory" /> is a filesystem root, the repository root, the artifact directory,
    /// the user's home directory, or a parent of the repository or artifact directory.
    /// </exception>
    /// <remarks>
    /// All compared paths are normalized and trailing directory separators are trimmed before comparison. This prevents
    /// bypasses such as passing the repository root with a trailing slash before the recursive delete runs.
    /// </remarks>
    internal static void Prepare(string workDirectory, string repositoryRoot, string artifactsDirectory)
    {
        var normalizedWorkDirectory = NormalizeDirectoryForSafetyComparison(workDirectory);
        var normalizedRepositoryRoot = NormalizeDirectoryForSafetyComparison(repositoryRoot);
        var normalizedArtifactsDirectory = NormalizeDirectoryForSafetyComparison(artifactsDirectory);
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var invalidTargets = new List<string>
        {
            Path.GetPathRoot(normalizedWorkDirectory) ?? normalizedWorkDirectory,
            normalizedRepositoryRoot,
            normalizedArtifactsDirectory
        };
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            invalidTargets.Add(NormalizeDirectoryForSafetyComparison(homeDirectory));
        }

        if (invalidTargets.Any(target => string.Equals(normalizedWorkDirectory, target, PackageIndexGenerator.RepositoryPathComparison)))
        {
            throw new PackageIndexException($"Package consumer proof work directory '{normalizedWorkDirectory}' is not a safe deletion target.");
        }

        if (IsParentOrSame(normalizedWorkDirectory, normalizedRepositoryRoot)
            || IsParentOrSame(normalizedWorkDirectory, normalizedArtifactsDirectory))
        {
            throw new PackageIndexException($"Package consumer proof work directory '{normalizedWorkDirectory}' must not contain the repository root or package artifact directory.");
        }

        if (Directory.Exists(normalizedWorkDirectory))
        {
            Directory.Delete(normalizedWorkDirectory, recursive: true);
        }

        Directory.CreateDirectory(normalizedWorkDirectory);
    }

    private static string NormalizeDirectoryForSafetyComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        while (fullPath.Length > root.Length
            && (fullPath.EndsWith(Path.DirectorySeparatorChar)
                || fullPath.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            fullPath = fullPath[..^1];
        }

        return fullPath;
    }

    private static bool IsParentOrSame(string possibleParent, string child)
    {
        var parent = possibleParent.EndsWith(Path.DirectorySeparatorChar)
            ? possibleParent
            : possibleParent + Path.DirectorySeparatorChar;
        return string.Equals(possibleParent, child, PackageIndexGenerator.RepositoryPathComparison)
            || child.StartsWith(parent, PackageIndexGenerator.RepositoryPathComparison);
    }
}
