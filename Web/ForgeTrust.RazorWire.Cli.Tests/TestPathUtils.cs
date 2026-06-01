namespace ForgeTrust.RazorWire.Cli.Tests;

/// <summary>
/// Provides path helpers for tests that need deterministic containment under a known root.
/// </summary>
/// <remarks>
/// These helpers make test path intent explicit instead of relying on <see cref="Path.Combine(string, string)" />,
/// whose rooted later segments can discard earlier arguments. They normalize syntactic separators and full paths, but
/// they do not resolve symlink targets; use them for test fixture construction, not as a production filesystem sandbox.
/// </remarks>
internal static class TestPathUtils
{
    /// <summary>
    /// Resolves an absolute path that is the same as or a descendant of <paramref name="basePath" />.
    /// </summary>
    /// <param name="basePath">The non-empty root directory that bounds the returned path.</param>
    /// <param name="relativeSegments">
    /// One or more relative path segments. Each segment must be non-empty after separator trimming, non-null, and not rooted.
    /// </param>
    /// <returns>A full path under <paramref name="basePath" /> using the current platform's path normalization.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="basePath" /> is blank, when a relative segment is invalid, or when the resolved path
    /// escapes <paramref name="basePath" /> after normalization.
    /// </exception>
    /// <remarks>
    /// Containment is checked by <see cref="IsSameOrDescendant" /> after <see cref="RelativePath" /> trims segment
    /// separators. Comparison is case-insensitive on Windows and case-sensitive elsewhere.
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
    /// Thrown when no segments are supplied, a segment is <see langword="null" />, blank, rooted, or trims to empty after
    /// removing directory separators.
    /// </exception>
    /// <remarks>
    /// This helper trims leading and trailing platform directory separators from each segment. It intentionally rejects
    /// rooted segments before joining so callers cannot accidentally reset the path base.
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

            if (Path.IsPathRooted(segment))
            {
                throw new ArgumentException("Relative path segments cannot be rooted.", nameof(segments));
            }

            var normalizedSegment = segment.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalizedSegment))
            {
                throw new ArgumentException("Relative path segments cannot be empty.", nameof(segments));
            }

            normalizedSegments[index] = normalizedSegment;
        }

        return string.Join(Path.DirectorySeparatorChar, normalizedSegments);
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
}
