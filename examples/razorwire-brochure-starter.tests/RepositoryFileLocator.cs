namespace NorthstarBrochureStarter.Tests;

internal static class RepositoryFileLocator
{
    public static string Find(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length == 0 || segments.Any(IsUnsafeSegment))
        {
            throw new ArgumentException(
                "Repository-relative path segments must be non-empty, relative, and free of parent traversal.",
                nameof(segments));
        }

        var relativePath = string.Join(Path.DirectorySeparatorChar, segments);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}.");
    }

    private static bool IsUnsafeSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return true;
        }

        return IsRootedOrAbsoluteLooking(segment) || ContainsParentTraversalSegment(segment);
    }

    private static bool IsRootedOrAbsoluteLooking(string segment)
    {
        return Path.IsPathRooted(segment)
            || (segment.Length >= 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':')
            || segment.StartsWith('\\')
            || segment.StartsWith('/');
    }

    private static bool ContainsParentTraversalSegment(string segment)
    {
        return segment
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, "..", StringComparison.Ordinal));
    }
}
