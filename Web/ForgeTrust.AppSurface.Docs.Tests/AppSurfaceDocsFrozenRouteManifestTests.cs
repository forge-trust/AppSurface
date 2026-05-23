using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Unit")]
public sealed class AppSurfaceDocsFrozenRouteManifestTests : IDisposable
{
    private readonly string _tempDirectory;

    public AppSurfaceDocsFrozenRouteManifestTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), "appsurfacedocs-frozen-route-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task WriteAsync_ShouldRejectAliasesThatCollideWithCanonicalRoutes()
    {
        var manifest = new AppSurfaceDocsRouteManifest(
            [
                Entry(
                    sourcePath: "first.md",
                    canonicalRoutePath: "first",
                    aliases: ["second"]),
                Entry(
                    sourcePath: "second.md",
                    canonicalRoutePath: "second",
                    aliases: [])
            ],
            []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppSurfaceDocsFrozenRouteManifest.WriteAsync(_tempDirectory, manifest, CancellationToken.None));

        Assert.Contains("collides with a canonical route", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_ShouldAllowDocsRootCanonicalRoutes()
    {
        var manifest = new AppSurfaceDocsRouteManifest(
            [
                Entry(
                    sourcePath: "README.md",
                    canonicalRoutePath: string.Empty,
                    aliases: ["README.md", "README.md.html"])
            ],
            []);

        await AppSurfaceDocsFrozenRouteManifest.WriteAsync(_tempDirectory, manifest, CancellationToken.None);

        var frozenManifest = await File.ReadAllTextAsync(Path.Join(_tempDirectory, ".appsurface-docs-route-manifest.json"));
        Assert.Contains("\"canonicalRoutePath\": \"\"", frozenManifest);
        Assert.Contains("\"README.md\"", frozenManifest);
        Assert.Contains("\"README.md.html\"", frozenManifest);
    }

    [Fact]
    public void BuildManifestPath_ShouldRejectRootedManifestFileNames()
    {
        var rootedFileName = Path.GetFullPath("rooted-manifest.json");

        var exception = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsFrozenRouteManifest.BuildManifestPath(_tempDirectory, rootedFileName));

        Assert.Contains("filename must be relative", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ShouldResolveDocsRootCanonicalAliases()
    {
        WriteFrozenManifest(
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "README.md",
                  "canonicalRoutePath": "",
                  "recoveryAliases": [ "README.md", "README.md.html" ],
                  "declaredAliases": []
                }
              ]
            }
            """);
        using var provider = new PhysicalFileProvider(_tempDirectory, ExclusionFilters.None);

        var manifest = AppSurfaceDocsFrozenRouteManifest.Load(
            provider,
            NullLogger<AppSurfaceDocsFrozenRouteManifestTests>.Instance,
            _tempDirectory);

        Assert.True(manifest.TryResolveAlias("README.md", out var canonicalRoutePath));
        Assert.Equal(string.Empty, canonicalRoutePath);
    }

    [Fact]
    public void Load_ShouldDisableAliasRecovery_WhenCanonicalRoutePathIsMissing()
    {
        var manifest = LoadFrozenManifest(
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "README.md",
                  "recoveryAliases": [ "README.md" ],
                  "declaredAliases": []
                }
              ]
            }
            """);

        Assert.False(manifest.TryResolveAlias("README.md", out _));
    }

    [Fact]
    public void Load_ShouldIgnoreUnsafeCanonicalRoutes_AndContinueUsingValidEntries()
    {
        var manifest = LoadFrozenManifest(
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "unsafe.md",
                  "canonicalRoutePath": "../admin",
                  "recoveryAliases": [ "unsafe.md" ],
                  "declaredAliases": []
                },
                {
                  "sourcePath": "guide.md",
                  "canonicalRoutePath": "guide",
                  "recoveryAliases": [ "guide.md" ],
                  "declaredAliases": []
                }
              ]
            }
            """);

        Assert.False(manifest.TryResolveAlias("unsafe.md", out _));
        Assert.True(manifest.TryResolveAlias("guide.md", out var canonicalRoutePath));
        Assert.Equal("guide", canonicalRoutePath);
    }

    [Fact]
    public void Load_ShouldIgnoreUnsafeAliases_AndContinueUsingValidAliases()
    {
        var manifest = LoadFrozenManifest(
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "guide.md",
                  "canonicalRoutePath": "guide",
                  "recoveryAliases": [ "unsafe//alias", "guide.md" ],
                  "declaredAliases": [ "legacy\\guide" ]
                }
              ]
            }
            """);

        Assert.False(manifest.TryResolveAlias("unsafe//alias", out _));
        Assert.False(manifest.TryResolveAlias(@"legacy\guide", out _));
        Assert.True(manifest.TryResolveAlias("guide.md", out var canonicalRoutePath));
        Assert.Equal("guide", canonicalRoutePath);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{ "schema": "unknown", "entries": [] }""")]
    public void Load_ShouldReturnEmpty_WhenSchemaIsUnsupported(string json)
    {
        var manifest = LoadFrozenManifest(json);

        Assert.False(manifest.TryResolveAlias("README.md", out _));
    }

    [Fact]
    public void Load_ShouldReturnEmpty_WhenEntriesAreMissing()
    {
        var manifest = LoadFrozenManifest(
            """
            {
              "schema": "appsurface-docs-route-manifest-v1"
            }
            """);

        Assert.False(manifest.TryResolveAlias("README.md", out _));
    }

    [Fact]
    public void Load_ShouldIgnoreAliasThatMatchesItsCanonicalRoute()
    {
        var manifest = LoadFrozenManifest(
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "guide.md",
                  "canonicalRoutePath": "guide",
                  "recoveryAliases": [ "guide", "guide.md" ],
                  "declaredAliases": []
                }
              ]
            }
            """);

        Assert.False(manifest.TryResolveAlias("guide", out _));
        Assert.True(manifest.TryResolveAlias("guide.md", out var canonicalRoutePath));
        Assert.Equal("guide", canonicalRoutePath);
    }

    [Fact]
    public void Load_ShouldIgnoreAliasesThatCollideWithCanonicalRoutes()
    {
        var manifest = LoadFrozenManifest(
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "first.md",
                  "canonicalRoutePath": "first",
                  "recoveryAliases": [ "second", "first.md" ],
                  "declaredAliases": []
                },
                {
                  "sourcePath": "second.md",
                  "canonicalRoutePath": "second",
                  "recoveryAliases": [],
                  "declaredAliases": []
                }
              ]
            }
            """);

        Assert.False(manifest.TryResolveAlias("second", out _));
        Assert.True(manifest.TryResolveAlias("first.md", out var canonicalRoutePath));
        Assert.Equal("first", canonicalRoutePath);
    }

    [Fact]
    public void Load_ShouldAllowDuplicateCanonicalRoutesInLegacyManifests()
    {
        var manifest = LoadFrozenManifest(
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.md",
                  "canonicalRoutePath": "guide",
                  "recoveryAliases": [ "index.md" ],
                  "declaredAliases": []
                },
                {
                  "sourcePath": "guide.md",
                  "canonicalRoutePath": "guide",
                  "recoveryAliases": [ "guide.md" ],
                  "declaredAliases": []
                }
              ]
            }
            """);

        Assert.True(manifest.TryResolveAlias("index.md", out var firstCanonicalRoutePath));
        Assert.Equal("guide", firstCanonicalRoutePath);
        Assert.True(manifest.TryResolveAlias("guide.md", out var secondCanonicalRoutePath));
        Assert.Equal("guide", secondCanonicalRoutePath);
    }

    [Fact]
    public void TryResolveAlias_ShouldReturnFalseForBlankAliases()
    {
        Assert.False(AppSurfaceDocsFrozenRouteManifest.Empty.TryResolveAlias("   ", out _));
    }

    [Fact]
    public async Task WriteAsync_ShouldRejectAliasesThatPointAtMultipleCanonicalRoutes()
    {
        var manifest = new AppSurfaceDocsRouteManifest(
            [
                Entry(
                    sourcePath: "first.md",
                    canonicalRoutePath: "first",
                    aliases: ["legacy"]),
                Entry(
                    sourcePath: "second.md",
                    canonicalRoutePath: "second",
                    aliases: ["legacy"])
            ],
            []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppSurfaceDocsFrozenRouteManifest.WriteAsync(_tempDirectory, manifest, CancellationToken.None));

        Assert.Contains("multiple canonical routes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_ShouldRejectDuplicateCanonicalRoutes()
    {
        var manifest = new AppSurfaceDocsRouteManifest(
            [
                Entry(
                    sourcePath: "first.md",
                    canonicalRoutePath: "guide",
                    aliases: ["first"]),
                Entry(
                    sourcePath: "second.md",
                    canonicalRoutePath: "guide",
                    aliases: ["second"])
            ],
            []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppSurfaceDocsFrozenRouteManifest.WriteAsync(_tempDirectory, manifest, CancellationToken.None));

        Assert.Contains("duplicate canonical route", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("guide/../admin")]
    [InlineData(".hidden/guide")]
    [InlineData(@"guide\admin")]
    [InlineData("guide?next=admin")]
    public async Task WriteAsync_ShouldRejectUnsafeCanonicalRoutes(string canonicalRoutePath)
    {
        var manifest = new AppSurfaceDocsRouteManifest(
            [
                Entry(
                    sourcePath: "guide.md",
                    canonicalRoutePath: canonicalRoutePath,
                    aliases: ["guide.md"])
            ],
            []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppSurfaceDocsFrozenRouteManifest.WriteAsync(_tempDirectory, manifest, CancellationToken.None));

        Assert.Contains("unsafe canonical route", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("guide/../admin")]
    [InlineData(".hidden/guide")]
    [InlineData(@"guide\admin")]
    [InlineData("guide?next=admin")]
    public async Task WriteAsync_ShouldRejectUnsafeAliases(string aliasRoutePath)
    {
        var manifest = new AppSurfaceDocsRouteManifest(
            [
                Entry(
                    sourcePath: "guide.md",
                    canonicalRoutePath: "guide",
                    aliases: [aliasRoutePath])
            ],
            []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppSurfaceDocsFrozenRouteManifest.WriteAsync(_tempDirectory, manifest, CancellationToken.None));

        Assert.Contains("is unsafe", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_ShouldRejectAliasesThatMatchTheirCanonicalRoute()
    {
        var manifest = new AppSurfaceDocsRouteManifest(
            [
                Entry(
                    sourcePath: "packages/README.md",
                    canonicalRoutePath: "packages",
                    aliases: ["packages"])
            ],
            []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppSurfaceDocsFrozenRouteManifest.WriteAsync(_tempDirectory, manifest, CancellationToken.None));

        Assert.Contains("matches its canonical route", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_ShouldRejectDuplicateAliasesForOneCanonicalRoute()
    {
        var manifest = new AppSurfaceDocsRouteManifest(
            [
                Entry(
                    sourcePath: "packages/README.md",
                    canonicalRoutePath: "packages",
                    aliases: ["legacy/package", "legacy/package"])
            ],
            []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppSurfaceDocsFrozenRouteManifest.WriteAsync(_tempDirectory, manifest, CancellationToken.None));

        Assert.Contains("is duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static AppSurfaceDocsRouteManifestEntry Entry(
        string sourcePath,
        string canonicalRoutePath,
        IReadOnlyList<string> aliases)
    {
        return new AppSurfaceDocsRouteManifestEntry(
            sourcePath,
            canonicalRoutePath,
            "/docs/" + canonicalRoutePath,
            aliases
                .Select(alias => new AppSurfaceDocsRouteAlias(alias, "/docs/" + alias, AppSurfaceDocsRouteAliasKind.MarkdownSource))
                .ToArray(),
            [],
            SourcePathIsMarkdown: true);
    }

    private void WriteFrozenManifest(string json)
    {
        File.WriteAllText(Path.Join(_tempDirectory, AppSurfaceDocsFrozenRouteManifest.FileName), json);
    }

    private AppSurfaceDocsFrozenRouteManifest LoadFrozenManifest(string json)
    {
        WriteFrozenManifest(json);
        using var provider = new PhysicalFileProvider(_tempDirectory, ExclusionFilters.None);
        return AppSurfaceDocsFrozenRouteManifest.Load(
            provider,
            NullLogger<AppSurfaceDocsFrozenRouteManifestTests>.Instance,
            _tempDirectory);
    }
}
