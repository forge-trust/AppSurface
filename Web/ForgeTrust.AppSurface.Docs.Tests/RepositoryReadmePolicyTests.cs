using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed partial class RepositoryReadmePolicyTests
{
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
    public void StandaloneHarvestPolicy_ShouldUseConfiguredDogfoodBoundary()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        using var provider = CreatePolicyProvider(repoRoot);
        var policy = provider.GetRequiredService<AppSurfaceDocsHarvestPathPolicy>();

        Assert.True(policy.ShouldIncludeFilePath("README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldIncludeFilePath("LICENSE", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/generated/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/TestResults/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldIncludeFilePath("unpublished/scratch/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/AppSurfaceDocsOptions.cs", AppSurfaceDocsHarvestSourceKind.CSharp));
        Assert.False(policy.ShouldIncludeFilePath("examples/web-app/Program.cs", AppSurfaceDocsHarvestSourceKind.CSharp));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Web.Tests/Fixture.cs", AppSurfaceDocsHarvestSourceKind.CSharp));
    }

    [Fact]
    public void PublicPackageReadmes_ShouldLinkToV01ReleasePreview()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var entries = ReadPackageManifestEntries(repoRoot);
        var requiredReadmes = entries
            .Where(entry => string.Equals(entry.Classification, "public", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(entry.PublishDecision, "publish", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(entry.StartHerePath))
            .Select(entry => entry.StartHerePath!)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nonRequiredReadmes = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.StartHerePath)
                            && !requiredReadmes.Contains(entry.StartHerePath, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.StartHerePath!)
            .ToArray();

        Assert.Equal(12, requiredReadmes.Length);
        Assert.Contains("Web/ForgeTrust.AppSurface.Docs/README.md", nonRequiredReadmes);
        Assert.Contains("Web/ForgeTrust.RazorWire.Cli/README.md", nonRequiredReadmes);

        foreach (var readmePath in requiredReadmes)
        {
            var fullReadmePath = Path.Combine(repoRoot, readmePath.Replace('/', Path.DirectorySeparatorChar));
            var content = File.ReadAllText(fullReadmePath);

            Assert.Contains("## Release Guidance", content, StringComparison.Ordinal);

            var match = ReleasePreviewLinkRegex().Match(content);
            Assert.True(match.Success, $"{readmePath} must link to the v0.1 release preview.");

            var resolvedTarget = Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(fullReadmePath)!,
                    match.Groups["href"].Value.Replace('/', Path.DirectorySeparatorChar)));
            var expectedTarget = Path.GetFullPath(Path.Combine(repoRoot, "releases", "v0.1-preview.md"));

            Assert.Equal(expectedTarget, resolvedTarget);
        }
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
        using var provider = CreatePolicyProvider(repoRoot);
        var policy = provider.GetRequiredService<AppSurfaceDocsHarvestPathPolicy>();

        return Directory
            .EnumerateFiles(repoRoot, "README.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .Where(path => policy.ShouldIncludeFilePath(path, AppSurfaceDocsHarvestSourceKind.Markdown))
            .ToArray();
    }

    private static ServiceProvider CreatePolicyProvider(string repoRoot)
    {
        var standaloneConfigRelativePath = Path.Join("Web", "ForgeTrust.AppSurface.Docs.Standalone", "appsettings.json");
        var standaloneConfigPath = Path.Join(repoRoot, standaloneConfigRelativePath);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(
                standaloneConfigPath,
                optional: false,
                reloadOnChange: false)
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddAppSurfaceDocs();

        return services.BuildServiceProvider();
    }

    private static bool StartsWithYamlFrontMatter(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimStart('\uFEFF')
            .StartsWith("---\n", StringComparison.Ordinal);
    }

    private static IReadOnlyList<PackageManifestReadmeEntry> ReadPackageManifestEntries(string repoRoot)
    {
        var entries = new List<PackageManifestReadmeEntry>();
        PackageManifestReadmeEntry? current = null;
        var manifestPath = Path.Combine(repoRoot, "packages", "package-index.yml");

        foreach (var rawLine in File.ReadLines(manifestPath))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("- project:", StringComparison.Ordinal) || line.StartsWith("  - project:", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    entries.Add(current);
                }

                current = new PackageManifestReadmeEntry(
                    ReadYamlScalar(line, "project:"),
                    string.Empty,
                    string.Empty,
                    null);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.TrimStart().StartsWith("classification:", StringComparison.Ordinal))
            {
                current = current with { Classification = ReadYamlScalar(line, "classification:") };
            }
            else if (line.TrimStart().StartsWith("publish_decision:", StringComparison.Ordinal))
            {
                current = current with { PublishDecision = ReadYamlScalar(line, "publish_decision:") };
            }
            else if (line.TrimStart().StartsWith("start_here_path:", StringComparison.Ordinal))
            {
                current = current with { StartHerePath = ReadYamlScalar(line, "start_here_path:") };
            }
        }

        if (current is not null)
        {
            entries.Add(current);
        }

        return entries;
    }

    private static string ReadYamlScalar(string line, string key)
    {
        var value = line[(line.IndexOf(key, StringComparison.Ordinal) + key.Length)..].Trim();
        return value.Trim('"', '\'');
    }

    [GeneratedRegex(@"\[v0\.1 release preview\]\((?<href>[^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleasePreviewLinkRegex();

    private sealed record PackageManifestReadmeEntry(
        string Project,
        string Classification,
        string PublishDecision,
        string? StartHerePath);
}
