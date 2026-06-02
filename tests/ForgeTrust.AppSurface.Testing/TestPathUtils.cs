namespace ForgeTrust.AppSurface.Testing;

/// <summary>
/// Provides path helpers for tests that need deterministic containment under a known root.
/// </summary>
/// <remarks>
/// These helpers make test path intent explicit instead of relying on <see cref="Path.Combine(string, string)" />,
/// whose rooted later segments can discard earlier arguments. They normalize syntactic separators and full paths, but
/// they do not resolve symlink targets; use them for test fixture construction, not as a production filesystem sandbox.
/// </remarks>
public static class TestPathUtils
{
    /// <summary>
    /// Resolves an absolute path that is the same as or a descendant of <paramref name="basePath" />.
    /// </summary>
    /// <param name="basePath">The non-empty root directory that bounds the returned path.</param>
    /// <param name="relativeSegments">
    /// One or more relative path segments. Each segment must be non-empty after separator trimming, non-null, not
    /// rooted, and must not contain a parent traversal segment.
    /// </param>
    /// <returns>A full path under <paramref name="basePath" /> using the current platform's path normalization.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="basePath" /> is blank, when a relative segment is invalid, or when the resolved path
    /// escapes <paramref name="basePath" /> after normalization.
    /// </exception>
    /// <remarks>
    /// Containment is checked by <see cref="IsSameOrDescendant" /> after <see cref="RelativePath" /> validates and trims
    /// segment separators. Comparison is case-insensitive on Windows and case-sensitive elsewhere.
    /// </remarks>
    public static string PathUnder(string basePath, params string[] relativeSegments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        var fullBasePath = Path.GetFullPath(basePath);
        var candidatePath = Path.GetFullPath(Path.Join(fullBasePath, RelativePath(relativeSegments)));
        if (!IsSameOrDescendant(fullBasePath, candidatePath))
        {
            throw new ArgumentException("Relative path segments must stay under the base path.", nameof(relativeSegments));
        }

        return candidatePath;
    }

    /// <summary>
    /// Builds a platform-relative path from validated path segments.
    /// </summary>
    /// <param name="segments">Relative path segments to normalize and join.</param>
    /// <returns>The segments joined with <see cref="Path.DirectorySeparatorChar" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="segments" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when no segments are supplied, a segment is <see langword="null" />, blank, rooted, absolute-looking on
    /// Windows, contains parent traversal, or trims to empty after removing directory separators.
    /// </exception>
    /// <remarks>
    /// This helper is safe relative formatting only. It intentionally rejects rooted, Windows absolute-looking, and
    /// parent traversal segments so callers cannot accidentally make a relative path that resets or escapes a later
    /// base. Use <see cref="PathUnder(string, string[])" /> when constructing a full filesystem path under a base.
    /// </remarks>
    public static string RelativePath(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Length == 0)
        {
            throw new ArgumentException("At least one relative path segment is required.", nameof(segments));
        }

        var normalizedSegments = new string[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index] ?? throw new ArgumentException("Relative path segments cannot be null.", nameof(segments));
            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new ArgumentException("Relative path segments cannot be empty.", nameof(segments));
            }

            if (IsRootedOrAbsoluteLooking(segment))
            {
                throw new ArgumentException("Relative path segments cannot be rooted.", nameof(segments));
            }

            var normalizedSegment = segment.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalizedSegment))
            {
                throw new ArgumentException("Relative path segments cannot be empty.", nameof(segments));
            }

            if (ContainsParentTraversalSegment(normalizedSegment))
            {
                throw new ArgumentException("Relative path segments cannot contain parent traversal.", nameof(segments));
            }

            normalizedSegments[index] = normalizedSegment;
        }

        return string.Join(Path.DirectorySeparatorChar, normalizedSegments);
    }

    /// <summary>
    /// Finds the repository root by walking upward from a start path until the solution file is found.
    /// </summary>
    /// <param name="startPath">Directory or file path to begin searching from.</param>
    /// <returns>The repository root directory.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="startPath" /> is blank.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the solution file cannot be found in any ancestor.</exception>
    public static string FindRepoRoot(string startPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);

        var current = ResolveStartDirectory(startPath);
        while (current != null)
        {
            if (File.Exists(PathUnder(current.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    /// <summary>
    /// Returns whether <paramref name="candidatePath" /> is equal to or nested below <paramref name="basePath" />.
    /// </summary>
    /// <param name="basePath">Normalized full root path.</param>
    /// <param name="candidatePath">Normalized full path to test.</param>
    /// <returns><see langword="true" /> when the candidate is the root itself or has the root as a directory prefix.</returns>
    /// <remarks>
    /// Trailing directory separators are ignored for equality. The comparison follows filesystem casing expectations:
    /// ordinal-ignore-case on Windows and ordinal on other platforms.
    /// </remarks>
    private static bool IsSameOrDescendant(string basePath, string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
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

    private static DirectoryInfo ResolveStartDirectory(string startPath)
    {
        if (File.Exists(startPath))
        {
            return new FileInfo(startPath).Directory
                ?? throw new InvalidOperationException($"Could not resolve parent directory for '{startPath}'.");
        }

        if (Directory.Exists(startPath))
        {
            return new DirectoryInfo(startPath);
        }

        return new DirectoryInfo(startPath);
    }

    private static bool IsRootedOrAbsoluteLooking(string segment)
    {
        if (Path.IsPathRooted(segment))
        {
            return true;
        }

        return (segment.Length >= 2
                && char.IsAsciiLetter(segment[0])
                && segment[1] == ':')
            || segment.StartsWith(@"\", StringComparison.Ordinal)
            || segment.StartsWith("/", StringComparison.Ordinal)
            || segment.StartsWith(@"\\", StringComparison.Ordinal)
            || segment.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool ContainsParentTraversalSegment(string segment)
    {
        return segment
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, "..", StringComparison.Ordinal));
    }
}
