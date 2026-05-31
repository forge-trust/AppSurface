namespace ForgeTrust.RazorWire.Cli.Tests;

internal static class TestPathUtils
{
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
