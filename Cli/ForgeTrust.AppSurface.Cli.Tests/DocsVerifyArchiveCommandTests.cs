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

    [Fact]
    public void Execute_ShouldFail_WhenCatalogPathIsMissing()
    {
        var command = CreateCommand(" ", "1.2.3");

        var exception = Assert.Throws<CommandException>(command.Execute);

        Assert.Contains("--catalog", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ShouldFail_WhenVersionIsMissing()
    {
        var command = CreateCommand("catalog.json", " ");

        var exception = Assert.Throws<CommandException>(command.Execute);

        Assert.Contains("--version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ShouldFail_WhenCatalogDoesNotContainVersion()
    {
        var tree = CreateExactTree("missing-version");
        var manifestDigest = WriteReleaseManifest(tree);
        var catalogPath = WriteCatalog(tree, manifestDigest);
        var command = CreateCommand(catalogPath, "9.9.9");

        var exception = Assert.Throws<CommandException>(command.Execute);

        Assert.Contains("could not find version '9.9.9'", exception.Message, StringComparison.Ordinal);
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
        var fullTempDirectory = Path.GetFullPath(_tempDirectory);
        var root = Path.GetFullPath(Path.Join(fullTempDirectory, name));
        if (!string.Equals(root, fullTempDirectory, StringComparison.Ordinal)
            && !root.StartsWith(fullTempDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Exact tree names must be relative to the test directory.", nameof(name));
        }

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Join(root, "index.html"), "<html>ok</html>");
        File.WriteAllText(Path.Join(root, "search.html"), "<html>search</html>");
        File.WriteAllText(Path.Join(root, "search-index.json"), "{\"documents\":[]}");
        File.WriteAllText(Path.Join(root, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Join(root, "search-client.js"), "window.__searchClientLoaded = true;");
        File.WriteAllText(Path.Join(root, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");
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

        var path = Path.Join(_tempDirectory, "catalog.json");
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
        var manifestPath = Path.Join(root, ".appsurface-docs-release-manifest.json");
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
