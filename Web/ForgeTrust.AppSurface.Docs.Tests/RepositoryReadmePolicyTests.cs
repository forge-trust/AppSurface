using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed partial class RepositoryReadmePolicyTests
{
    [Fact]
    public void AuthoredReadmes_ShouldNotStartWithYamlFrontMatter()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var violatingReadmes = EnumerateAuthoredReadmePaths(repoRoot)
            .Where(
                path => StartsWithYamlFrontMatter(File.ReadAllText(TestPathUtils.PathUnder(repoRoot, path))))
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
        Assert.True(
            policy.ShouldIncludeFilePath(
                "Intelligence/ForgeTrust.AppSurface.Intelligence/README.md",
                AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/generated/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/TestResults/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldIncludeFilePath("unpublished/scratch/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Docs/AppSurfaceDocsOptions.cs", AppSurfaceDocsHarvestSourceKind.CSharp));
        Assert.False(policy.ShouldIncludeFilePath("examples/web-app/Program.cs", AppSurfaceDocsHarvestSourceKind.CSharp));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Web.Tests/Fixture.cs", AppSurfaceDocsHarvestSourceKind.CSharp));
        Assert.True(policy.ShouldIncludeFilePath("Web/ForgeTrust.RazorWire/assets/contracts/razorwire-public-contracts.js", AppSurfaceDocsHarvestSourceKind.JavaScript));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.js", AppSurfaceDocsHarvestSourceKind.JavaScript));
        Assert.False(policy.ShouldIncludeFilePath("Web/ForgeTrust.AppSurface.Web.Tests/fixture.js", AppSurfaceDocsHarvestSourceKind.JavaScript));
    }

    [Fact]
    public void PublicPackageStartHereReadmes_ShouldBeHarvestedByStandaloneDocs()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var entries = ReadPackageManifestEntries(repoRoot);
        var requiredReadmes = entries
            .Where(IsPublicPublishedStartHereEntry)
            .Select(entry => entry.StartHerePath!)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var provider = CreatePolicyProvider(repoRoot);
        var policy = provider.GetRequiredService<AppSurfaceDocsHarvestPathPolicy>();

        foreach (var readmePath in requiredReadmes)
        {
            var fullReadmePath = ResolveRepositoryRelativePath(
                repoRoot,
                readmePath,
                $"{readmePath} package chooser start_here_path");

            Assert.True(
                File.Exists(fullReadmePath),
                $"{readmePath} must exist because it is a public package start_here_path.");
            Assert.True(
                policy.ShouldIncludeFilePath(readmePath, AppSurfaceDocsHarvestSourceKind.Markdown),
                $"{readmePath} must be included by Web/ForgeTrust.AppSurface.Docs.Standalone/appsettings.json AppSurfaceDocs:Harvest:Paths:IncludeGlobs.");
        }
    }

    [Fact]
    public void PublicPackageReadmes_ShouldLinkToCurrentReleaseCandidate()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var entries = ReadPackageManifestEntries(repoRoot);
        var requiredReadmes = entries
            .Where(IsPublicPublishedStartHereEntry)
            .Select(entry => entry.StartHerePath!)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nonRequiredReadmes = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.StartHerePath)
                            && !requiredReadmes.Contains(entry.StartHerePath, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.StartHerePath!)
            .ToArray();

        Assert.Equal(21, requiredReadmes.Length);
        Assert.Contains("Web/ForgeTrust.AppSurface.Docs/README.md", nonRequiredReadmes);
        Assert.Contains("Web/ForgeTrust.RazorWire.Cli/README.md", nonRequiredReadmes);

        foreach (var readmePath in requiredReadmes)
        {
            var fullReadmePath = ResolveRepositoryRelativePath(repoRoot, readmePath, $"{readmePath} README path");
            var content = File.ReadAllText(fullReadmePath);

            Assert.Contains("## Release Guidance", content, StringComparison.Ordinal);

            var expectedTarget = Path.GetFullPath(Path.Join("releases", "v0.1.0-rc.4.md"), repoRoot);
            var linksToReleaseNote = MarkdownLinkRegex()
                .Matches(content)
                .Select(match => ResolveRelativeLinkTarget(
                    Path.GetDirectoryName(fullReadmePath)!,
                    match.Groups["href"].Value,
                    readmePath))
                .Any(target => string.Equals(target, expectedTarget, StringComparison.Ordinal));

            Assert.True(linksToReleaseNote, $"{readmePath} must link to the v0.1.0 RC 4 release note.");
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
        var standaloneConfigPath = TestPathUtils.PathUnder(repoRoot, standaloneConfigRelativePath);
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
        var manifestPath = Path.GetFullPath(Path.Join("packages", "package-index.yml"), repoRoot);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var manifest = deserializer.Deserialize<PackageReadmeManifest>(File.ReadAllText(manifestPath));

        return manifest.Packages;
    }

    private static bool IsPublicPublishedStartHereEntry(PackageManifestReadmeEntry entry)
    {
        return string.Equals(entry.Classification, "public", StringComparison.OrdinalIgnoreCase)
               && string.Equals(entry.PublishDecision, "publish", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(entry.StartHerePath);
    }

    private static string ResolveRepositoryRelativePath(string repoRoot, string repositoryRelativePath, string description)
    {
        try
        {
            return TestPathUtils.PathUnder(repoRoot, repositoryRelativePath);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"{description} must be repository-relative and stay under the repository root.",
                exception);
        }
    }

    private static string ResolveRelativeLinkTarget(string baseDirectory, string href, string readmePath)
    {
        var hrefPath = href.Replace('/', Path.DirectorySeparatorChar);
        Assert.False(Path.IsPathRooted(hrefPath), $"{readmePath} release guidance link must be relative.");

        return Path.GetFullPath(hrefPath, baseDirectory);
    }

    [GeneratedRegex(@"\[[^\]]+\]\((?<href>[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    private sealed class PackageReadmeManifest
    {
        public List<PackageManifestReadmeEntry> Packages { get; init; } = [];
    }

    private sealed class PackageManifestReadmeEntry
    {
        public string Project { get; init; } = string.Empty;

        public string Classification { get; init; } = string.Empty;

        public string PublishDecision { get; init; } = string.Empty;

        public string? StartHerePath { get; init; }
    }
}
