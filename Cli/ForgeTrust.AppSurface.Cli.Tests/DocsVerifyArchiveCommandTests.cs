using System.Security.Cryptography;
using System.Text.Json;
using CliFx;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Trait("Category", "Unit")]
public sealed class DocsVerifyArchiveCommandTests : IDisposable
{
    private readonly string _tempDirectory;

    public DocsVerifyArchiveCommandTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), "appsurface-docs-verify-archive-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Execute_ShouldPass_WhenCatalogPinsValidReleaseArchive()
    {
        var tree = CreateExactTree("verified");
        var manifestDigest = WriteReleaseManifest(tree);
        var catalogPath = WriteCatalog(tree, manifestDigest);
        var command = CreateCommand(catalogPath, "1.2.3");

        command.Execute();
    }

    [Fact]
    public void Execute_ShouldFail_WhenCatalogEntryIsLegacyUnverified()
    {
        var tree = CreateExactTree("legacy");
        var catalogPath = WriteCatalog(tree, releaseManifestSha256: null);
        var command = CreateCommand(catalogPath, "1.2.3");

        var exception = Assert.Throws<CommandException>(command.Execute);

        Assert.Contains("AvailableUnverifiedLegacy", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static DocsVerifyArchiveCommand CreateCommand(string catalogPath, string version)
    {
        return new DocsVerifyArchiveCommand(
            NullLogger<DocsVerifyArchiveCommand>.Instance,
            NullLoggerFactory.Instance)
        {
            CatalogPath = catalogPath,
            Version = version
        };
    }

    private string CreateExactTree(string name)
    {
        var root = Path.Combine(_tempDirectory, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<html>ok</html>");
        File.WriteAllText(Path.Combine(root, "search.html"), "<html>search</html>");
        File.WriteAllText(Path.Combine(root, "search-index.json"), "{\"documents\":[]}");
        File.WriteAllText(Path.Combine(root, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Combine(root, "search-client.js"), "window.__searchClientLoaded = true;");
        File.WriteAllText(Path.Combine(root, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");
        return root;
    }

    private string WriteCatalog(string tree, string? releaseManifestSha256)
    {
        var version = new Dictionary<string, object?>
        {
            ["version"] = "1.2.3",
            ["exactTreePath"] = Path.GetRelativePath(_tempDirectory, tree),
            ["releaseManifestSha256"] = releaseManifestSha256
        };
        if (releaseManifestSha256 is null)
        {
            version.Remove("releaseManifestSha256");
        }

        var path = Path.Combine(_tempDirectory, "catalog.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    recommendedVersion = "1.2.3",
                    versions = new[] { version }
                }));
        return path;
    }

    private static string WriteReleaseManifest(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ".appsurface-docs-release-manifest.json", StringComparison.Ordinal))
            .Select(
                path => new
                {
                    path = Path.GetRelativePath(root, path)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/'),
                    length = new FileInfo(path).Length,
                    contentType = (string?)null,
                    hashAlgorithm = "sha256",
                    sha256 = ComputeFileSha256(path)
                })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        var manifestPath = Path.Combine(root, ".appsurface-docs-release-manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new { schema = "appsurface-docs-release-manifest-v1", files },
                new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return ComputeFileSha256(manifestPath);
    }

    private static string ComputeFileSha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
}
