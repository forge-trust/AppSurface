using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Writes the AppSurface Docs release archive manifest for a completed static export.
/// </summary>
/// <remarks>
/// The manifest is emitted after export materialization so it records final on-disk bytes, including rewritten HTML,
/// redirect artifacts, binary assets, and hidden control files. The manifest itself is excluded from its file list so
/// callers can pin the manifest digest in trusted host configuration without creating a self-referential payload.
/// </remarks>
internal static class ReleaseArchiveManifestWriter
{
    internal const string FileName = ".appsurface-docs-release-manifest.json";
    internal const string Schema = "appsurface-docs-release-manifest-v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Writes the release archive manifest beneath the export output directory.
    /// </summary>
    /// <param name="outputPath">Export output directory.</param>
    /// <param name="cancellationToken">Token observed while reading and writing files.</param>
    /// <returns>A summary containing the manifest path and digest operators should pin in the version catalog.</returns>
    internal static async Task<ReleaseArchiveManifestSummary> WriteAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        ExportOutputPathGuards.EnsureOutputRootReady(
            outputPath,
            "release archive output root",
            route: null,
            "create-directory");
        var fullOutputPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath));
        var manifestPath = Path.Join(fullOutputPath, FileName);
        var entries = new List<ReleaseArchiveManifestFile>();

        foreach (var filePath in EnumerateArchiveFiles(fullOutputPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportOutputPathGuards.ValidateArchiveEntryPath(fullOutputPath, filePath, "archive-enumerate");

            var relativePath = NormalizeRelativePath(fullOutputPath, filePath);
            if (string.Equals(relativePath, FileName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsSafeArchiveManifestPath(relativePath))
            {
                throw new ExportValidationException(
                    [
                        new ExportDiagnostic(
                            "ASDOCSARCHIVE005",
                            $"Release archive manifest cannot include unsafe path '{relativePath}'. Remove the file or export to a clean output directory before pinning releaseManifestSha256.",
                            "/" + relativePath)
                    ]);
            }

            ExportOutputPathGuards.ValidateArchiveEntryPath(fullOutputPath, filePath, "archive-hash");
            var fileInfo = new FileInfo(filePath);
            entries.Add(
                new ReleaseArchiveManifestFile(
                    relativePath,
                    fileInfo.Length,
                    ResolveContentType(relativePath),
                    "sha256",
                    await ComputeFileSha256Async(fullOutputPath, filePath, cancellationToken)));
        }

        var document = new ReleaseArchiveManifestDocument(
            Schema,
            entries
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .ToArray());

        var payload = JsonSerializer.Serialize(document, SerializerOptions) + "\n";
        await ExportAuthArtifactAuditor.WriteTextArtifactAsync(
            fullOutputPath,
            manifestPath,
            "release archive manifest",
            route: "/" + FileName,
            payload,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        var manifestDigest = await ComputeFileSha256Async(fullOutputPath, manifestPath, cancellationToken);

        return new ReleaseArchiveManifestSummary(manifestPath, Schema, document.Files.Count, manifestDigest);
    }

    private static IEnumerable<string> EnumerateArchiveFiles(string outputPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(outputPath))
        {
            return [];
        }

        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(outputPath);

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryPath = directories.Pop();
            ExportOutputPathGuards.ValidateArchiveEntryPath(outputPath, directoryPath, "archive-enumerate");

            foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath)
                         .OrderBy(path => NormalizeRelativePath(outputPath, path), StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExportOutputPathGuards.ValidateArchiveEntryPath(outputPath, entryPath, "archive-enumerate");
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entryPath);
                    continue;
                }

                files.Add(entryPath);
            }
        }

        return files.OrderBy(path => NormalizeRelativePath(outputPath, path), StringComparer.Ordinal);
    }

    private static string NormalizeRelativePath(string outputPath, string filePath)
    {
        return Path.GetRelativePath(outputPath, filePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static bool IsSafeArchiveManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || path.Contains('?', StringComparison.Ordinal)
            || path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(segment)
                || segment == "."
                || segment == "..")
            {
                return false;
            }

            if (segment.StartsWith(".", StringComparison.Ordinal)
                && !string.Equals(path, ".appsurface-docs-route-manifest.json", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string> ComputeFileSha256Async(
        string outputPath,
        string filePath,
        CancellationToken cancellationToken)
    {
        ExportOutputPathGuards.ValidateArchiveEntryPath(outputPath, filePath, "archive-hash");
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string? ResolveContentType(string relativePath)
    {
        return Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".eot" => "application/vnd.ms-fontobject",
            _ => null
        };
    }

    private sealed record ReleaseArchiveManifestDocument(
        string Schema,
        IReadOnlyList<ReleaseArchiveManifestFile> Files);

    private sealed record ReleaseArchiveManifestFile(
        string Path,
        long Length,
        string? ContentType,
        string HashAlgorithm,
        string Sha256);
}

/// <summary>
/// Summarizes a release archive manifest emitted by static export.
/// </summary>
/// <param name="ManifestPath">Absolute path to the written manifest.</param>
/// <param name="Schema">Manifest schema identifier.</param>
/// <param name="FileCount">Number of archive files covered by the manifest.</param>
/// <param name="Sha256">Lowercase hex SHA-256 digest of the manifest bytes.</param>
public sealed record ReleaseArchiveManifestSummary(
    string ManifestPath,
    string Schema,
    int FileCount,
    string Sha256);
