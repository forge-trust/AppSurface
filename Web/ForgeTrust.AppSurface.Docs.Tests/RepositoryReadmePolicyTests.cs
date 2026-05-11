using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RepositoryReadmePolicyTests
{
    private static readonly HashSet<string> AuthoredReadmeExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "node_modules",
        "TestResults"
    };

    [Fact]
    public void AuthoredReadmes_ShouldNotStartWithYamlFrontMatter()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var violatingReadmes = EnumerateAuthoredReadmePaths(repoRoot)
            .Where(
                path => StartsWithYamlFrontMatter(File.ReadAllText(Path.Combine(repoRoot, path))))
            .ToArray();

        Assert.True(
            violatingReadmes.Length == 0,
            $"Authored README.md files must stay portable and avoid inline YAML front matter. Violations: {string.Join(", ", violatingReadmes)}");
    }

    [Fact]
    public void StartsWithYamlFrontMatter_ShouldReturnTrue_WhenContentStartsWithBomPrefixedYamlMarker()
    {
        Assert.True(
            StartsWithYamlFrontMatter(
                """
                ﻿---
                title: Portable Docs
                ---
                # Heading
                """));
    }

    [Fact]
    public void StartsWithYamlFrontMatter_ShouldReturnTrue_WhenContentStartsWithYamlMarkerUsingCrLf()
    {
        Assert.True(
            StartsWithYamlFrontMatter("---\r\ntitle: Portable Docs\r\n---\r\n# Heading\r\n"));
    }

    [Fact]
    public void StartsWithYamlFrontMatter_ShouldReturnFalse_WhenContentDoesNotStartWithYamlMarker()
    {
        Assert.False(
            StartsWithYamlFrontMatter(
                """
                ﻿# Heading
                ---
                not front matter
                """));
    }

    private static IReadOnlyList<string> EnumerateAuthoredReadmePaths(string repoRoot)
    {
        return Directory
            .EnumerateFiles(repoRoot, "README.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .Where(path => !HarvestPathExclusions.ShouldExcludeFilePath(path, AuthoredReadmeExcludedDirectories))
            .ToArray();
    }

    private static bool StartsWithYamlFrontMatter(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimStart('\uFEFF')
            .StartsWith("---\n", StringComparison.Ordinal);
    }
}
