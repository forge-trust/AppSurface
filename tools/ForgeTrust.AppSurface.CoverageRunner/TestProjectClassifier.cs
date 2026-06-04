namespace ForgeTrust.AppSurface.CoverageRunner;

/// <summary>
/// Classifies test projects into coverage groups and scheduler safety classes.
/// </summary>
internal static class TestProjectClassifier
{
    /// <summary>
    /// Gets the coverage group for a repository-relative test project path.
    /// </summary>
    /// <param name="projectPath">Repository-relative project path.</param>
    /// <returns>The coverage group name.</returns>
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
    /// <param name="projectPath">Project path.</param>
    /// <param name="projectContents">Project file contents.</param>
    /// <returns><c>true</c> when the project should run exclusively.</returns>
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
    /// <param name="projectPath">Project path.</param>
    /// <returns>File-system-safe project slug.</returns>
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
