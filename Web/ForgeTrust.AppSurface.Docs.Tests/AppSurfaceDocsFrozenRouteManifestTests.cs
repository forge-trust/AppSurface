using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

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
}
