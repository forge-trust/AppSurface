namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

internal static class HarvestPathExclusions
{
    private static readonly string[] TestProjectSuffixes =
    [
        ".Tests",
        ".UnitTests",
        ".IntegrationTests",
        ".FunctionalTests",
        ".E2ETests",
        "-Tests",
        "_Tests"
    ];

    // Explicitly excluded regardless of hidden-directory allowlist behavior.
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        "bin",
        "obj",
        "Test",
        "Tests"
    };

    // Hidden directories to include even though dot-prefixed directories are excluded by default.
    private static readonly HashSet<string> AllowedHiddenDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    // File paths should be filtered by directory segments only so dot-prefixed files are still included.
    /// <summary>
    /// Determines whether a documentation file should be excluded from harvesting based on its path segments.
    /// </summary>
    /// <param name="filePath">The relative file path to check.</param>
    /// <returns><c>true</c> if the file should be excluded; otherwise, <c>false</c>.</returns>
    public static bool ShouldExcludeFilePath(string filePath)
        => ShouldExcludeFilePath(filePath, ExcludedDirectories, excludeTestProjectDirectories: true);

    /// <summary>
    /// Determines whether a file should be excluded based on shared hidden-directory rules and a caller-supplied
    /// set of explicitly excluded directory names.
    /// </summary>
    /// <param name="filePath">The relative file path to check.</param>
    /// <param name="excludedDirectories">
    /// Directory names that should always be excluded in addition to the shared hidden-directory behavior.
    /// </param>
    /// <param name="excludeTestProjectDirectories">
    /// When <c>true</c>, also excludes test-project directory segments by case-insensitive exact names
    /// (<c>Test</c>, <c>Tests</c>, <c>UnitTests</c>, <c>IntegrationTests</c>, <c>FunctionalTests</c>,
    /// <c>E2ETests</c>) and case-insensitive suffix patterns (<c>*.Tests</c>, <c>*.UnitTests</c>,
    /// <c>*.IntegrationTests</c>, <c>*.FunctionalTests</c>, <c>*.E2ETests</c>, <c>*-Tests</c>,
    /// and <c>*_Tests</c>).
    /// </param>
    /// <returns><c>true</c> if the file should be excluded; otherwise, <c>false</c>.</returns>
    internal static bool ShouldExcludeFilePath(
        string filePath,
        IReadOnlySet<string> excludedDirectories,
        bool excludeTestProjectDirectories = false)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(excludedDirectories);

        var segments = filePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length <= 1)
        {
            return false;
        }

        foreach (var directorySegment in segments[..^1])
        {
            if (excludedDirectories.Contains(directorySegment))
            {
                return true;
            }

            if (excludeTestProjectDirectories && IsTestProjectDirectorySegment(directorySegment))
            {
                return true;
            }

            if (directorySegment.StartsWith('.') && !AllowedHiddenDirectories.Contains(directorySegment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTestProjectDirectorySegment(string segment)
    {
        return segment.Equals("Test", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("Tests", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("UnitTests", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("IntegrationTests", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("FunctionalTests", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("E2ETests", StringComparison.OrdinalIgnoreCase)
               || TestProjectSuffixes.Any(suffix => segment.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
