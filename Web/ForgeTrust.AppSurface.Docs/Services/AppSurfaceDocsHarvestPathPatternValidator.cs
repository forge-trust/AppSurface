using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Validates and normalizes repository-relative harvest path patterns and candidate paths.
/// </summary>
/// <remarks>
/// The validator is stateless and thread-safe. Callers should normalize or validate before matching: backslashes are
/// converted to <c>/</c>, configured patterns are trimmed, and rooted, URI-like, drive-rooted, control-character,
/// query, fragment, and parent-directory paths are rejected. <see cref="NormalizeSlashes"/> only changes separators;
/// it does not perform validation by itself.
/// </remarks>
internal static partial class AppSurfaceDocsHarvestPathPatternValidator
{
    /// <summary>
    /// Returns whether a configured glob pattern is safe to evaluate as a repository-relative path policy rule.
    /// </summary>
    /// <param name="pattern">The configured glob pattern. Null, empty, and whitespace-only values are invalid.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="pattern"/> is repository-relative after trimming and slash
    /// normalization; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Patterns starting with <c>/</c>, <c>./</c>, or <c>//</c>, containing <c>://</c>, drive roots, control
    /// characters, <c>?</c>, <c>#</c>, or a <c>..</c> segment are invalid. Validation does not prove that a glob can
    /// match an existing file; it only rejects unsafe path forms before matcher construction.
    /// </remarks>
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

    /// <summary>
    /// Attempts to normalize a candidate file or directory path before path-policy evaluation.
    /// </summary>
    /// <param name="path">The candidate repository-relative path to normalize.</param>
    /// <param name="normalizedPath">
    /// Receives the slash-normalized candidate path. When validation fails, this value contains the normalized input
    /// that failed validation so callers can log the inspected value.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="path"/> is a non-empty repository-relative path that is safe for
    /// policy evaluation; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This method assumes callers have already chosen whether the path represents a file or directory. It trims no
    /// whitespace, so caller-provided spaces remain part of the candidate path; unsafe rooted, URI-like,
    /// drive-rooted, query, fragment, control-character, and parent-directory forms are rejected.
    /// </remarks>
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

    /// <summary>
    /// Converts Windows path separators in <paramref name="value"/> to repository-style slash separators.
    /// </summary>
    /// <param name="value">The path or pattern value whose separators should be normalized.</param>
    /// <returns><paramref name="value"/> with each <c>\</c> character replaced by <c>/</c>.</returns>
    /// <remarks>
    /// This helper does not trim, validate, collapse duplicate separators, or reject unsafe segments. Use one of the
    /// validation methods when accepting configuration or candidate paths from outside the policy implementation.
    /// </remarks>
    public static string NormalizeSlashes(string value)
    {
        return value.Replace('\\', '/');
    }

    /// <summary>
    /// Generates the expression used to reject Windows drive-rooted patterns after slash normalization.
    /// </summary>
    /// <returns>A culture-invariant regular expression that matches values such as <c>C:/repo</c>.</returns>
    [GeneratedRegex(@"^[A-Za-z]:/", RegexOptions.CultureInvariant)]
    private static partial Regex DriveRootedPatternRegex();
}
