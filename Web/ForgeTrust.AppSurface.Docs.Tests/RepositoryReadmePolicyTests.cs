using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RepositoryReadmePolicyTests
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
        var policy = provider.GetRequiredService<RazorDocsHarvestPathPolicy>();

        Assert.True(policy.ShouldIncludeFilePath("README.md", RazorDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldIncludeFilePath("LICENSE", RazorDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/README.md", RazorDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/generated/README.md", RazorDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/TestResults/README.md", RazorDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldIncludeFilePath("unpublished/scratch/README.md", RazorDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/RazorDocsOptions.cs", RazorDocsHarvestSourceKind.CSharp));
        Assert.False(policy.ShouldIncludeFilePath("examples/web-app/Program.cs", RazorDocsHarvestSourceKind.CSharp));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Web.Tests/Fixture.cs", RazorDocsHarvestSourceKind.CSharp));
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
        var policy = provider.GetRequiredService<RazorDocsHarvestPathPolicy>();

        return Directory
            .EnumerateFiles(repoRoot, "README.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .Where(path => policy.ShouldIncludeFilePath(path, RazorDocsHarvestSourceKind.Markdown))
            .ToArray();
    }

    private static ServiceProvider CreatePolicyProvider(string repoRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(repoRoot, "Web", "ForgeTrust.AppSurface.Docs.Standalone", "appsettings.json"),
                optional: false,
                reloadOnChange: false)
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddRazorDocs();

        return services.BuildServiceProvider();
    }

    private static bool StartsWithYamlFrontMatter(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimStart('\uFEFF')
            .StartsWith("---\n", StringComparison.Ordinal);
    }
}
