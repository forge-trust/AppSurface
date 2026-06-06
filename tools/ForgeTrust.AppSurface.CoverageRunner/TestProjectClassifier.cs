namespace ForgeTrust.AppSurface.CoverageRunner;

/// <summary>
/// Classifies test projects into coverage groups and scheduler safety classes.
/// </summary>
/// <remarks>
/// Classification intentionally mirrors the historical shell script contract. Inputs are normalized
/// to forward slashes before prefix checks so Windows-style paths and solution-relative paths use
/// the same mapping rules.
/// </remarks>
internal static class TestProjectClassifier
{
    /// <summary>
    /// Gets the coverage group for a repository-relative test project path.
    /// </summary>
    /// <param name="projectPath">
    /// Repository-relative project path, normally from <c>dotnet sln list</c>. Backslashes are
    /// normalized before matching.
    /// </param>
    /// <returns>
    /// <c>core</c> for <c>Aspire/</c>, <c>Auth/</c>, <c>Caching/</c>, <c>Config/</c>,
    /// <c>Console/</c>, <c>Dependency/</c>, <c>Flow/</c>,
    /// <c>ForgeTrust.AppSurface.Core.Tests/</c>, or unmatched paths; <c>tools</c> for
    /// <c>Cli/</c> and <c>tools/</c>; <c>docs</c> for
    /// <c>Web/ForgeTrust.AppSurface.Docs.Tests/</c>; <c>integration</c> for
    /// <c>Web/ForgeTrust.RazorWire.IntegrationTests/</c>; <c>razorwire</c> for
    /// <c>Web/ForgeTrust.RazorWire.Tests/</c> and <c>Web/ForgeTrust.RazorWire.Cli.Tests/</c>;
    /// otherwise <c>web</c> for remaining <c>Web/</c> paths.
    /// </returns>
    /// <remarks>
    /// Prefix checks are order-sensitive: specialized <c>Web/</c> groups are evaluated before the
    /// broad <c>Web/</c> fallback, and unmatched paths deliberately fall back to <c>core</c> so new
    /// non-web projects remain covered by default.
    /// </remarks>
    public static string GetGroup(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var normalized = Normalize(projectPath);

        return normalized switch
        {
            _ when StartsWithAny(normalized, "Aspire/", "Auth/", "Caching/", "Config/", "Console/", "Dependency/", "Flow/", "ForgeTrust.AppSurface.Core.Tests/") => "core",
            _ when StartsWithAny(normalized, "Cli/", "tools/") => "tools",
            _ when normalized.StartsWith("Web/ForgeTrust.AppSurface.Docs.Tests/", StringComparison.Ordinal) => "docs",
            _ when normalized.StartsWith("Web/ForgeTrust.RazorWire.IntegrationTests/", StringComparison.Ordinal) => "integration",
            _ when StartsWithAny(normalized, "Web/ForgeTrust.RazorWire.Tests/", "Web/ForgeTrust.RazorWire.Cli.Tests/") => "razorwire",
            _ when normalized.StartsWith("Web/", StringComparison.Ordinal) => "web",
            _ => "core",
        };
    }

    /// <summary>
    /// Gets whether a project should run without overlapping other projects.
    /// </summary>
    /// <param name="projectPath">Project path, normalized before integration-test path checks.</param>
    /// <param name="projectContents">Project file contents used for package/reference heuristics.</param>
    /// <returns>
    /// <c>true</c> when the path ends with <c>.IntegrationTests.csproj</c>, contains
    /// <c>/IntegrationTests/</c>, or the project file text contains <c>Microsoft.Playwright</c> or
    /// <c>Playwright</c> case-insensitively.
    /// </returns>
    /// <remarks>
    /// The Playwright check is intentionally conservative for the first parallel rollout. It may
    /// serialize a project that only mentions Playwright indirectly, but that avoids browser/server
    /// contention until timing data proves a narrower rule is safe.
    /// </remarks>
    public static bool IsExclusive(string projectPath, string projectContents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(projectContents);

        var normalized = Normalize(projectPath);
        return normalized.EndsWith(".IntegrationTests.csproj", StringComparison.Ordinal)
            || normalized.Contains("/IntegrationTests/", StringComparison.Ordinal)
            || projectContents.Contains("Microsoft.Playwright", StringComparison.OrdinalIgnoreCase)
            || projectContents.Contains("Playwright", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates the stable slug used for project artifact directories.
    /// </summary>
    /// <param name="projectPath">Project path whose file name, without extension, becomes the slug source.</param>
    /// <returns>
    /// File-system-safe project slug preserving ASCII letters, digits, <c>_</c>, <c>.</c>, and
    /// <c>-</c>, while replacing every other character with <c>-</c>.
    /// </returns>
    /// <remarks>
    /// Slugs are intentionally readable and stable, not globally unique. Two project file names
    /// that differ only by replaced characters can collide, so solution project names should remain
    /// unique after sanitization.
    /// </remarks>
    public static string CreateSlug(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var fileName = Path.GetFileNameWithoutExtension(projectPath);
        return string.Concat(fileName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-'));
    }

    private static bool StartsWithAny(string value, params string[] prefixes)
    {
        return prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/');
    }
}
