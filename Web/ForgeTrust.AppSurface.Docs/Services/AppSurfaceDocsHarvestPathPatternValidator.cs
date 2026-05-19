using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Docs.Services;

internal static partial class AppSurfaceDocsHarvestPathPatternValidator
{
    public static bool IsValidConfiguredGlobPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var normalized = NormalizeSlashes(pattern.Trim());
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("./", StringComparison.Ordinal)
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || normalized.Contains("://", StringComparison.Ordinal)
            || DriveRootedPatternRegex().IsMatch(normalized)
            || normalized.IndexOfAny(['?', '#']) >= 0
            || normalized.Any(char.IsControl))
        {
            return false;
        }

        return normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => !segment.Equals("..", StringComparison.Ordinal));
    }

    public static bool TryNormalizeCandidatePath(
        string path,
        out string normalizedPath)
    {
        normalizedPath = NormalizeSlashes(path);
        if (string.IsNullOrWhiteSpace(normalizedPath)
            || normalizedPath.StartsWith("/", StringComparison.Ordinal)
            || normalizedPath.StartsWith("./", StringComparison.Ordinal)
            || normalizedPath.StartsWith("//", StringComparison.Ordinal)
            || normalizedPath.Contains("://", StringComparison.Ordinal)
            || DriveRootedPatternRegex().IsMatch(normalizedPath)
            || normalizedPath.IndexOfAny(['?', '#']) >= 0
            || normalizedPath.Any(char.IsControl))
        {
            return false;
        }

        return normalizedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => !segment.Equals("..", StringComparison.Ordinal));
    }

    public static string NormalizeSlashes(string value)
    {
        return value.Replace('\\', '/');
    }

    [GeneratedRegex(@"^[A-Za-z]:/", RegexOptions.CultureInvariant)]
    private static partial Regex DriveRootedPatternRegex();
}
