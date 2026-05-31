namespace ForgeTrust.AppSurface.Docs.Tests;

internal static class TestPathUtils
{
    public static string PathUnder(string basePath, params string[] relativeSegments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        return Path.Join(basePath, RelativePath(relativeSegments));
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

            normalizedSegments[index] = segment.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return string.Join(Path.DirectorySeparatorChar, normalizedSegments);
    }

    public static string FindRepoRoot(string startPath)
    {
        var current = new DirectoryInfo(startPath);
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
}
