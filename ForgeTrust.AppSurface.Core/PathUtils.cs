using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// Provides utility methods for operations on file and directory paths.
/// </summary>
public static partial class PathUtils
{
    /// <summary>
    /// Resolves an absolute path that is the same as or a descendant of <paramref name="basePath"/>.
    /// </summary>
    /// <param name="basePath">The non-empty directory that bounds the returned path.</param>
    /// <param name="relativeSegments">
    /// One or more non-empty relative path segments. Rooted values and parent traversal are rejected.
    /// </param>
    /// <returns>A normalized full path under <paramref name="basePath"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="relativeSegments"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the base path or a relative segment is invalid, or when normalization escapes the base path.
    /// </exception>
    /// <remarks>
    /// This helper enforces lexical containment and does not resolve symbolic-link targets. Use a filesystem posture
    /// check as well when callers must reject links or aliases.
    /// </remarks>
    public static string PathUnder(string basePath, params string[] relativeSegments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(relativeSegments);

        if (relativeSegments.Length == 0)
        {
            throw new ArgumentException("At least one relative path segment is required.", nameof(relativeSegments));
        }

        var normalizedSegments = new string[relativeSegments.Length];
        for (var index = 0; index < relativeSegments.Length; index++)
        {
            var segment = relativeSegments[index] ??
                throw new ArgumentException("Relative path segments cannot be null.", nameof(relativeSegments));
            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new ArgumentException("Relative path segments cannot be empty.", nameof(relativeSegments));
            }

            if (IsRootedOrAbsoluteLooking(segment))
            {
                throw new ArgumentException("Relative path segments cannot be rooted.", nameof(relativeSegments));
            }

            var normalizedSegment = segment.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalizedSegment))
            {
                throw new ArgumentException("Relative path segments cannot be empty.", nameof(relativeSegments));
            }

            if (ContainsParentTraversalSegment(normalizedSegment))
            {
                throw new ArgumentException("Relative path segments cannot contain parent traversal.", nameof(relativeSegments));
            }

            normalizedSegments[index] = normalizedSegment;
        }

        var fullBasePath = Path.GetFullPath(basePath);
        var relativePath = string.Join(Path.DirectorySeparatorChar, normalizedSegments);
        var candidatePath = Path.GetFullPath(relativePath, fullBasePath);
        if (!IsSameOrDescendant(fullBasePath, candidatePath))
        {
            throw new ArgumentException("Relative path segments must stay under the base path.", nameof(relativeSegments));
        }

        return candidatePath;
    }

    /// <summary>
    /// Locates the nearest ancestor directory (starting at <paramref name="startPath"/>) that contains a `.git` directory or file, effectively identifying the repository root.
    /// </summary>
    /// <param name="startPath">The path from which to begin searching upward for a repository root; may refer to a file or directory.</param>
    /// <returns>The full path of the nearest ancestor directory containing a `.git` directory or file, or the original <paramref name="startPath"/> if none is found.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="startPath"/> is null, empty, or consists only of whitespace.</exception>
    public static string FindRepositoryRoot(string startPath)
    {
        return FindRepositoryRootCore(startPath, logger: null);
    }

    /// <summary>
    /// Locates the nearest ancestor directory (starting at <paramref name="startPath"/>) that contains a `.git` directory or file, effectively identifying the repository root.
    /// </summary>
    /// <param name="startPath">The path from which to begin searching upward for a repository root; may refer to a file or directory.</param>
    /// <param name="logger">
    /// Logger for diagnostic warnings when repository-root discovery has to recover from a path that does not exist.
    /// </param>
    /// <returns>The full path of the nearest ancestor directory containing a `.git` directory or file, or the original <paramref name="startPath"/> if none is found.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="startPath"/> is null, empty, or consists only of whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is null.</exception>
    public static string FindRepositoryRoot(string startPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return FindRepositoryRootCore(startPath, logger);
    }

    private static string FindRepositoryRootCore(string startPath, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            throw new ArgumentException("Start path cannot be null or whitespace.", nameof(startPath));
        }

        var fileExists = File.Exists(startPath);
        DirectoryInfo? current = fileExists
            ? new DirectoryInfo(GetExistingFileDirectory(startPath))
            : new DirectoryInfo(startPath);
        var fallbackFromMissingPath = !fileExists && !current.Exists;

        while (current is { Exists: false })
        {
            current = current.Parent;
        }

        if (fallbackFromMissingPath)
        {
            if (logger != null && current != null)
            {
                LogMissingPathFallback(logger, startPath, current.FullName);
            }
        }

        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git"))
                || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return startPath;
    }

    /// <summary>
    /// Resolves the containing directory for an existing file path used by repository-root discovery.
    /// </summary>
    /// <remarks>
    /// <see cref="FindRepositoryRootCore"/> calls <c>GetExistingFileDirectory</c> only after <see cref="File.Exists(string?)"/>
    /// has accepted <paramref name="startPath"/> as an existing file. The method normalizes the value with
    /// <see cref="Path.GetFullPath(string)"/> so relative file paths are canonicalized before returning the directory
    /// portion from <see cref="Path.GetDirectoryName(string?)"/>. If normalization cannot produce a containing
    /// directory, <see cref="ArgumentException.ThrowIfNullOrEmpty(string?, string?)"/> throws to surface the broken
    /// existing-file invariant instead of silently probing from an unrelated fallback path.
    /// </remarks>
    /// <param name="startPath">A non-empty path already known to reference an existing file.</param>
    /// <returns>The normalized containing directory for <paramref name="startPath"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the normalized file path does not yield a containing directory.
    /// </exception>
    private static string GetExistingFileDirectory(string startPath)
    {
        var directoryPath = Path.GetDirectoryName(Path.GetFullPath(startPath));
        ArgumentException.ThrowIfNullOrEmpty(directoryPath, nameof(startPath));

        return directoryPath;
    }

    private static bool IsRootedOrAbsoluteLooking(string segment)
    {
        if (Path.IsPathRooted(segment))
        {
            return true;
        }

        return (segment.Length >= 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':')
            || segment[0] is '\\' or '/';
    }

    private static bool ContainsParentTraversalSegment(string segment) =>
        segment
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static part => string.Equals(part, "..", StringComparison.Ordinal));

    private static bool IsSameOrDescendant(string basePath, string candidatePath)
    {
        var comparison = GetFilesystemPathComparison();
        if (string.Equals(
            Path.TrimEndingDirectorySeparator(candidatePath),
            Path.TrimEndingDirectorySeparator(basePath),
            comparison))
        {
            return true;
        }

        var basePrefix = Path.EndsInDirectorySeparator(basePath)
            ? basePath
            : basePath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(basePrefix, comparison);
    }

    [ExcludeFromCodeCoverage(Justification = "Runtime OS probe; Windows and Unix CI exercise opposite path-comparison branches.")]
    private static StringComparison GetFilesystemPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Repository-root search started from missing path {StartPath}; continuing from nearest existing ancestor {FallbackPath}.")]
    private static partial void LogMissingPathFallback(ILogger logger, string startPath, string fallbackPath);

}
