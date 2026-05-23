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
        WriteFrozenManifest(
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
        using var provider = new PhysicalFileProvider(_tempDirectory, ExclusionFilters.None);

        var manifest = AppSurfaceDocsFrozenRouteManifest.Load(
            provider,
            NullLogger<AppSurfaceDocsFrozenRouteManifestTests>.Instance,
            _tempDirectory);

        Assert.False(manifest.TryResolveAlias("README.md", out _));
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
}
