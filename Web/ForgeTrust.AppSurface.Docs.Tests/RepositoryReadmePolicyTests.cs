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
        Assert.True(
            policy.ShouldIncludeFilePath(
                "Config/ForgeTrust.AppSurface.Config.LocalSecrets/docs/local-secrets-without-a-remote-vault.md",
                AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(
            policy.ShouldIncludeFilePath(
                "Config/ForgeTrust.AppSurface.Config.LocalSecrets/docs/migrate-from-dotenv.md",
                AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(
            policy.ShouldIncludeFilePath(
                "Config/ForgeTrust.AppSurface.Config.LocalSecrets/docs/migrate-from-user-secrets.md",
                AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(
            policy.ShouldIncludeFilePath(
                "Config/ForgeTrust.AppSurface.Config.LocalSecrets/docs/move-to-future-remote-vault.md",
                AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(
            policy.ShouldIncludeFilePath(
                "Config/ForgeTrust.AppSurface.Config.LocalSecrets/docs/use-env-or-key-per-file-in-ci-and-containers.md",
                AppSurfaceDocsHarvestSourceKind.Markdown));
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
            .Where(IsPublicStartHereEntry)
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
    public void PublicPackageReadmes_ShouldLinkToStableReleaseSurfaces()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var entries = ReadPackageManifestEntries(repoRoot);
        var requiredReadmes = entries
            .Where(IsPublicStartHereEntry)
            .Select(entry => entry.StartHerePath!)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nonRequiredReadmes = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.StartHerePath)
                            && !requiredReadmes.Contains(entry.StartHerePath, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.StartHerePath!)
            .ToArray();

        Assert.Equal(31, requiredReadmes.Length);
        Assert.Contains(
            "Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md",
            requiredReadmes);
        Assert.Contains(
            "Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md",
            requiredReadmes);
        Assert.Contains(
            "Durable/ForgeTrust.AppSurface.Durable/README.md",
            requiredReadmes);
        Assert.Contains(
            "Durable/ForgeTrust.AppSurface.Durable.Provider/README.md",
            requiredReadmes);
        Assert.Contains(
            "Workers/ForgeTrust.AppSurface.Workers/README.md",
            requiredReadmes);
        Assert.Contains(
            "Workers/ForgeTrust.AppSurface.Workers.DurableTask/README.md",
            requiredReadmes);
        Assert.Contains(
            "Web/ForgeTrust.RazorWire.Auth.AspNetCore/README.md",
            requiredReadmes);
        Assert.Contains("Web/ForgeTrust.AppSurface.Docs/README.md", nonRequiredReadmes);
        Assert.Contains("Web/ForgeTrust.RazorWire.Cli/README.md", nonRequiredReadmes);

        foreach (var readmePath in requiredReadmes)
        {
            var fullReadmePath = ResolveRepositoryRelativePath(repoRoot, readmePath, $"{readmePath} README path");
            var content = File.ReadAllText(fullReadmePath);

            Assert.Contains("## Release Guidance", content, StringComparison.Ordinal);

            var linkTargets = MarkdownLinkRegex()
                .Matches(content)
                .Select(match => ResolveRelativeLinkTarget(
                    Path.GetDirectoryName(fullReadmePath)!,
                    match.Groups["href"].Value,
                    readmePath))
                .ToArray();

            var expectedPackageChooserTarget = Path.GetFullPath(Path.Join("packages", "README.md"), repoRoot);
            var expectedReleaseHubTarget = Path.GetFullPath(Path.Join("releases", "README.md"), repoRoot);
            var linksToPackageChooser = linkTargets.Any(target => string.Equals(target, expectedPackageChooserTarget, StringComparison.Ordinal));
            var linksToReleaseHub = linkTargets.Any(target => string.Equals(target, expectedReleaseHubTarget, StringComparison.Ordinal));

            Assert.True(linksToPackageChooser, $"{readmePath} must link to the stable package chooser.");
            Assert.True(linksToReleaseHub, $"{readmePath} must link to the release hub.");
        }
    }

    [Fact]
    public void AuthAdoptionLadder_ShouldBeHarvestedAndLinkedFromDiscoverySurfaces()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        const string ladderPath = "start-here/auth-adoption-ladder.md";
        var ladderFullPath = ResolveRepositoryRelativePath(repoRoot, ladderPath, "Auth adoption ladder path");
        using var provider = CreatePolicyProvider(repoRoot);
        var policy = provider.GetRequiredService<AppSurfaceDocsHarvestPathPolicy>();

        Assert.True(File.Exists(ladderFullPath), "The Auth adoption ladder must exist as an authored Start Here guide.");
        Assert.True(
            policy.ShouldIncludeFilePath(ladderPath, AppSurfaceDocsHarvestSourceKind.Markdown),
            "The Auth adoption ladder must be harvested by standalone AppSurface Docs.");
        var expectedLadderTarget = Path.GetFullPath(ladderFullPath);

        AssertMarkdownLinksTo(repoRoot, "README.md", expectedLadderTarget);
        Assert.Contains(
            ladderPath,
            File.ReadAllText(ResolveRepositoryRelativePath(repoRoot, "README.md.yml", "Root README metadata path")),
            StringComparison.Ordinal);
        Assert.Contains(
            ladderPath,
            File.ReadAllText(ResolveRepositoryRelativePath(repoRoot, "packages/package-index.yml", "Package index path")),
            StringComparison.Ordinal);
        AssertMarkdownLinksTo(repoRoot, "packages/README.md", expectedLadderTarget);

        var authReadmes = ReadPackageManifestEntries(repoRoot)
            .Where(IsPublishedAuthPackageEntry)
            .Select(entry => entry.StartHerePath!)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var readmePath in authReadmes)
        {
            var fullReadmePath = ResolveRepositoryRelativePath(repoRoot, readmePath, $"{readmePath} README path");
            var content = File.ReadAllText(fullReadmePath);
            var linkTargets = MarkdownLinkRegex()
                .Matches(content)
                .Select(match => ResolveRelativeLinkTarget(
                    Path.GetDirectoryName(fullReadmePath)!,
                    match.Groups["href"].Value,
                    readmePath))
                .ToArray();

            Assert.True(
                linkTargets.Any(target => string.Equals(target, expectedLadderTarget, StringComparison.Ordinal)),
                $"{readmePath} must link to the Auth adoption ladder.");
        }

        AssertMarkdownLinksTo(repoRoot, "examples/auth-web-razorwire-proof/README.md", expectedLadderTarget);
        AssertMarkdownLinksTo(repoRoot, "examples/auth-aspnetcore-dev-auth/README.md", expectedLadderTarget);
        AssertMarkdownLinksTo(repoRoot, "examples/auth-aspnetcore-oidc/README.md", expectedLadderTarget);
    }

    [Fact]
    public void AuthAdoptionLadder_ShouldMatchPublishedAuthPackagesAndPreserveHostOwnedBoundaries()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var ladder = File.ReadAllText(TestPathUtils.PathUnder(repoRoot, "start-here/auth-adoption-ladder.md"));
        var authPackageIds = ReadPackageManifestEntries(repoRoot)
            .Where(IsPublishedAuthPackageEntry)
            .Select(entry => Path.GetFileNameWithoutExtension(entry.Project))
            .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
            [
                "ForgeTrust.AppSurface.Auth",
                "ForgeTrust.AppSurface.Auth.Aspire.Keycloak",
                "ForgeTrust.AppSurface.Auth.AspNetCore",
                "ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth",
                "ForgeTrust.AppSurface.Auth.AspNetCore.Oidc",
                "ForgeTrust.AppSurface.Auth.Testing"
            ],
            authPackageIds);

        foreach (var packageId in authPackageIds)
        {
            Assert.Contains(packageId, ladder, StringComparison.Ordinal);
        }

        Assert.Contains("Host apps own identity providers, schemes/middleware, policies/enforcement, user stores, and production behavior.", ladder, StringComparison.Ordinal);
        Assert.Contains("ForgeTrust.RazorWire.Auth.AspNetCore", ladder, StringComparison.Ordinal);
        Assert.Contains("dotnet package add ForgeTrust.RazorWire.Auth.AspNetCore", ladder, StringComparison.Ordinal);
        Assert.Contains("UI may reflect host policy results, but endpoints still need ASP.NET Core/AppSurface enforcement.", ladder, StringComparison.Ordinal);
        Assert.Contains("Problem", ladder, StringComparison.Ordinal);
        Assert.Contains("Cause", ladder, StringComparison.Ordinal);
        Assert.Contains("Fix", ladder, StringComparison.Ordinal);
        Assert.Contains("Docs", ladder, StringComparison.Ordinal);

        Assert.DoesNotContain("dotnet package add ForgeTrust.AppSurface.Auth.Keycloak", ladder, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSurface secures your app", ladder, StringComparison.Ordinal);
        Assert.DoesNotContain("DevAuth login", ladder, StringComparison.Ordinal);
        Assert.DoesNotContain("RazorWire enforces policy", ladder, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthAdoptionDocs_ShouldNotTreatAuthTestingAsFutureOnly()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var publicAuthDocs = string.Join(
            Environment.NewLine,
            File.ReadAllText(TestPathUtils.PathUnder(repoRoot, "start-here/auth-adoption-ladder.md")),
            File.ReadAllText(TestPathUtils.PathUnder(repoRoot, "Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md")),
            File.ReadAllText(TestPathUtils.PathUnder(repoRoot, "Auth/ForgeTrust.AppSurface.Auth.Testing/README.md")),
            File.ReadAllText(TestPathUtils.PathUnder(repoRoot, "examples/auth-aspnetcore-dev-auth/README.md")),
            File.ReadAllText(TestPathUtils.PathUnder(repoRoot, "packages/package-index.yml")));

        Assert.Contains("ForgeTrust.AppSurface.Auth.Testing", publicAuthDocs, StringComparison.Ordinal);
        Assert.DoesNotContain("future test harness", publicAuthDocs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("once that package exists", publicAuthDocs, StringComparison.OrdinalIgnoreCase);
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

    private static bool IsPublicStartHereEntry(PackageManifestReadmeEntry entry)
    {
        return string.Equals(entry.Classification, "public", StringComparison.OrdinalIgnoreCase)
               && (string.Equals(entry.PublishDecision, "publish", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(entry.PublishDecision, "do_not_publish", StringComparison.OrdinalIgnoreCase))
               && !string.IsNullOrWhiteSpace(entry.StartHerePath);
    }

    private static bool IsPublishedAuthPackageEntry(PackageManifestReadmeEntry entry)
    {
        return IsPublicStartHereEntry(entry)
               && entry.Project.StartsWith("Auth/ForgeTrust.AppSurface.Auth", StringComparison.Ordinal);
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

    private static void AssertMarkdownLinksTo(string repoRoot, string markdownPath, string expectedTarget)
    {
        var fullMarkdownPath = ResolveRepositoryRelativePath(repoRoot, markdownPath, $"{markdownPath} markdown path");
        var content = File.ReadAllText(fullMarkdownPath);
        var linkTargets = MarkdownLinkRegex()
            .Matches(content)
            .Select(match => ResolveRelativeLinkTarget(
                Path.GetDirectoryName(fullMarkdownPath)!,
                match.Groups["href"].Value,
                markdownPath))
            .ToArray();

        Assert.True(
            linkTargets.Any(target => string.Equals(target, expectedTarget, StringComparison.Ordinal)),
            $"{markdownPath} must link to {Path.GetRelativePath(repoRoot, expectedTarget)}.");
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
