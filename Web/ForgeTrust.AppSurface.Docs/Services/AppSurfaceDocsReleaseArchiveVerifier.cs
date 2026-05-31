using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Verifies catalog-pinned AppSurface Docs release archive manifests.
/// </summary>
/// <remarks>
/// The verifier treats the version catalog as trusted host configuration. It proves that the local exact-version tree
/// matches the digest pinned by that catalog; it does not prove who built the archive or replace future signed
/// attestation support.
/// </remarks>
internal static class AppSurfaceDocsReleaseArchiveVerifier
{
    internal const string FileName = ".appsurface-docs-release-manifest.json";
    internal const string Schema = "appsurface-docs-release-manifest-v1";

    private const string RouteManifestFileName = AppSurfaceDocsFrozenRouteManifest.FileName;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Attempts to verify an exact-version tree against the manifest digest pinned in the version catalog.
    /// </summary>
    /// <param name="exactTreePath">Exact-version export root.</param>
    /// <param name="expectedManifestSha256">Catalog-pinned manifest digest.</param>
    /// <param name="archive">Verified archive metadata when verification succeeds.</param>
    /// <param name="failure">Verification failure when verification fails.</param>
    /// <returns><c>true</c> when every required archive integrity check passes.</returns>
    internal static bool TryVerify(
        string exactTreePath,
        string expectedManifestSha256,
        out AppSurfaceDocsVerifiedReleaseArchive? archive,
        out AppSurfaceDocsArchiveVerificationFailure? failure)
    {
        return TryVerify(
            exactTreePath,
            expectedManifestSha256,
            AppSurfaceDocsReleaseArchiveFileSystem.Physical,
            out archive,
            out failure);
    }

    /// <summary>
    /// Attempts to verify an exact-version tree using the supplied filesystem adapter.
    /// </summary>
    /// <param name="exactTreePath">Exact-version export root.</param>
    /// <param name="expectedManifestSha256">Catalog-pinned manifest digest.</param>
    /// <param name="fileSystem">Filesystem adapter used for verification reads.</param>
    /// <param name="archive">Verified archive metadata when verification succeeds.</param>
    /// <param name="failure">Verification failure when verification fails.</param>
    /// <returns><c>true</c> when every required archive integrity check passes.</returns>
    internal static bool TryVerify(
        string exactTreePath,
        string expectedManifestSha256,
        AppSurfaceDocsReleaseArchiveFileSystem fileSystem,
        out AppSurfaceDocsVerifiedReleaseArchive? archive,
        out AppSurfaceDocsArchiveVerificationFailure? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactTreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedManifestSha256);
        ArgumentNullException.ThrowIfNull(fileSystem);

        archive = null;
        failure = null;

        if (!TryNormalizeSha256(expectedManifestSha256, out var normalizedExpectedDigest))
        {
            failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                "ASDOCSARCHIVE002",
                "Release manifest digest is invalid.",
                "Set releaseManifestSha256 to the lowercase or uppercase 64-character SHA-256 hex digest printed by export.");
            return false;
        }

        var manifestFileName = Path.GetFileName(FileName);
        var manifestPath = Path.Join(exactTreePath, manifestFileName);
        if (!fileSystem.FileExists(manifestPath))
        {
            failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                "ASDOCSARCHIVE001",
                "Release manifest is missing.",
                $"Re-export the release tree or remove the catalog pin until {FileName} exists.");
            return false;
        }

        byte[] manifestBytes;
        string actualManifestDigest;
        try
        {
            manifestBytes = fileSystem.ReadAllBytes(manifestPath);
            actualManifestDigest = ComputeBytesSha256(manifestBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                "ASDOCSARCHIVE001",
                "Release manifest could not be read.",
                $"Ensure {FileName} is readable. Detail: {ex.Message}");
            return false;
        }

        if (!string.Equals(actualManifestDigest, normalizedExpectedDigest, StringComparison.Ordinal))
        {
            failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                "ASDOCSARCHIVE002",
                "Release manifest digest does not match the catalog pin.",
                "Copy the digest printed by the matching export into releaseManifestSha256, or restore the original manifest.");
            return false;
        }

        ReleaseArchiveManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ReleaseArchiveManifestDocument>(manifestBytes, SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                "ASDOCSARCHIVE003",
                "Release manifest payload is unreadable.",
                $"Re-export the release tree so {FileName} is valid JSON. Detail: {ex.Message}");
            return false;
        }

        if (document is null || !string.Equals(document.Schema, Schema, StringComparison.Ordinal))
        {
            failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                "ASDOCSARCHIVE003",
                "Release manifest schema is unsupported.",
                $"Re-export with a tool that writes {Schema}.");
            return false;
        }

        var files = new Dictionary<string, AppSurfaceDocsReleaseArchiveFile>(StringComparer.Ordinal);
        var verifiedFrozenRouteManifest = AppSurfaceDocsFrozenRouteManifest.Empty;
        foreach (var entry in document.Files ?? [])
        {
            if (!TryValidateEntryShape(entry, out var validatedEntry, out failure))
            {
                return false;
            }

            if (!files.TryAdd(validatedEntry.Path, validatedEntry))
            {
                failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                    "ASDOCSARCHIVE004",
                    "Release manifest contains a duplicate path.",
                    $"Remove the duplicate entry for '{validatedEntry.Path}' and re-export.");
                return false;
            }

            var filePath = Path.Join(exactTreePath, validatedEntry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!fileSystem.FileExists(filePath))
            {
                failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                    "ASDOCSARCHIVE006",
                    "Release manifest lists a file that is missing.",
                    $"Restore '{validatedEntry.Path}' or re-export the release tree.",
                    validatedEntry.Path);
                return false;
            }

            var actualLength = fileSystem.GetLength(filePath);
            if (actualLength != validatedEntry.Length)
            {
                failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                    "ASDOCSARCHIVE007",
                    "Release archive file length does not match the manifest.",
                    $"Restore '{validatedEntry.Path}' from the original export or re-export the release tree.",
                    validatedEntry.Path);
                return false;
            }

            string actualDigest;
            byte[]? fileBytes = null;
            try
            {
                if (string.Equals(validatedEntry.Path, RouteManifestFileName, StringComparison.Ordinal))
                {
                    fileBytes = fileSystem.ReadAllBytes(filePath);
                    actualDigest = ComputeBytesSha256(fileBytes);
                }
                else
                {
                    actualDigest = fileSystem.ComputeSha256(filePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                    "ASDOCSARCHIVE008",
                    "Release archive file could not be read for digest verification.",
                    $"Ensure '{validatedEntry.Path}' is readable. Detail: {ex.Message}",
                    validatedEntry.Path);
                return false;
            }

            if (!string.Equals(actualDigest, validatedEntry.Sha256, StringComparison.Ordinal))
            {
                failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                    "ASDOCSARCHIVE008",
                    "Release archive file digest does not match the manifest.",
                    $"Restore '{validatedEntry.Path}' from the original export or re-export the release tree.",
                    validatedEntry.Path);
                return false;
            }

            if (fileBytes is not null
                && !AppSurfaceDocsFrozenRouteManifest.TryLoadVerified(fileBytes, out verifiedFrozenRouteManifest, out var routeManifestIssue))
            {
                failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                    "ASDOCSARCHIVE003",
                    "Release archive route manifest is malformed.",
                    $"Re-export the release tree so {RouteManifestFileName} is valid. Detail: {routeManifestIssue}",
                    RouteManifestFileName);
                return false;
            }
        }

        if (fileSystem.FileExists(Path.Join(exactTreePath, Path.GetFileName(RouteManifestFileName)))
            && !files.ContainsKey(RouteManifestFileName))
        {
            failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                "ASDOCSARCHIVE009",
                "Release archive route manifest is not listed in the release manifest.",
                $"Re-export the release tree so {RouteManifestFileName} is covered.",
                RouteManifestFileName);
            return false;
        }

        IEnumerable<string> archiveFilePaths;
        try
        {
            archiveFilePaths = fileSystem.EnumerateFiles(exactTreePath).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                "ASDOCSARCHIVE009",
                "Release archive files could not be enumerated.",
                $"Ensure the exact release tree is readable before verification. Detail: {ex.Message}");
            return false;
        }

        foreach (var relativePath in archiveFilePaths.Select(filePath => NormalizeRelativePath(exactTreePath, filePath)))
        {
            if (string.Equals(relativePath, FileName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!AppSurfaceDocsPublishedTreeHandler.IsHandlerServeableFilePath(relativePath, allowSvg: true))
            {
                continue;
            }

            if (!files.ContainsKey(relativePath))
            {
                failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                    "ASDOCSARCHIVE009",
                    "Release archive contains a serveable file that is not listed in the release manifest.",
                    $"Re-export the release tree so '{relativePath}' is covered, or remove the file.",
                    relativePath);
                return false;
            }
        }

        archive = new AppSurfaceDocsVerifiedReleaseArchive(files, verifiedFrozenRouteManifest);
        return true;
    }

    /// <summary>
    /// Recomputes an already-verified file digest from an <see cref="IFileInfo"/> before serving active content.
    /// </summary>
    /// <param name="fileInfo">Resolved file to check.</param>
    /// <param name="expectedFile">Manifest entry to compare against.</param>
    /// <returns><c>true</c> when length and SHA-256 still match the manifest entry.</returns>
    internal static bool FileMatches(IFileInfo fileInfo, AppSurfaceDocsReleaseArchiveFile expectedFile)
    {
        ArgumentNullException.ThrowIfNull(fileInfo);
        ArgumentNullException.ThrowIfNull(expectedFile);

        if (!fileInfo.Exists || fileInfo.Length != expectedFile.Length)
        {
            return false;
        }

        try
        {
            using var stream = fileInfo.CreateReadStream();
            var digest = SHA256.HashData(stream);
            return string.Equals(Convert.ToHexString(digest).ToLowerInvariant(), expectedFile.Sha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryValidateEntryShape(
        ReleaseArchiveManifestFile? entry,
        out AppSurfaceDocsReleaseArchiveFile validatedEntry,
        out AppSurfaceDocsArchiveVerificationFailure? failure)
    {
        validatedEntry = null!;
        failure = null;

        if (entry is null
            || string.IsNullOrWhiteSpace(entry.Path)
            || entry.Length < 0
            || !string.Equals(entry.HashAlgorithm, "sha256", StringComparison.OrdinalIgnoreCase)
            || !TryNormalizeSha256(entry.Sha256, out var normalizedSha256))
        {
            failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                "ASDOCSARCHIVE003",
                "Release manifest contains an invalid file entry.",
                "Re-export the release tree with a supported AppSurface Docs exporter.");
            return false;
        }

        var trimmedPath = entry.Path.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPath)
            || !IsSafeManifestPath(trimmedPath))
        {
            failure = AppSurfaceDocsArchiveVerificationFailure.Create(
                "ASDOCSARCHIVE005",
                "Release manifest contains an unsafe path.",
                $"Remove or re-export the unsafe path '{entry.Path}'.");
            return false;
        }

        var normalizedPath = NormalizeManifestPath(trimmedPath);
        validatedEntry = new AppSurfaceDocsReleaseArchiveFile(
            normalizedPath,
            entry.Length,
            string.IsNullOrWhiteSpace(entry.ContentType) ? null : entry.ContentType.Trim(),
            normalizedSha256);
        return true;
    }

    private static bool IsSafeManifestPath(string path)
    {
        if (path.StartsWith("/", StringComparison.Ordinal)
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
                && !string.Equals(path, RouteManifestFileName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeManifestPath(string path)
    {
        return path.Trim();
    }

    private static string NormalizeRelativePath(string rootPath, string filePath)
    {
        return Path.GetRelativePath(rootPath, filePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length != 64 || candidate.Any(static ch => !Uri.IsHexDigit(ch)))
        {
            return false;
        }

        normalized = candidate.ToLowerInvariant();
        return true;
    }

    internal static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var digest = SHA256.HashData(stream);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string ComputeBytesSha256(byte[] bytes)
    {
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private sealed record ReleaseArchiveManifestDocument(
        string Schema,
        IReadOnlyList<ReleaseArchiveManifestFile>? Files);

    private sealed record ReleaseArchiveManifestFile(
        string Path,
        long Length,
        string? ContentType,
        string HashAlgorithm,
        string Sha256);
}

/// <summary>
/// Filesystem adapter used by release archive verification.
/// </summary>
/// <remarks>
/// The production adapter reads the physical release tree. Tests use custom adapters to exercise portable failure
/// branches such as unreadable files without depending on platform-specific chmod behavior.
/// </remarks>
internal abstract class AppSurfaceDocsReleaseArchiveFileSystem
{
    /// <summary>
    /// Gets the physical filesystem adapter used by runtime verification.
    /// </summary>
    internal static AppSurfaceDocsReleaseArchiveFileSystem Physical { get; } = new PhysicalReleaseArchiveFileSystem();

    /// <summary>
    /// Returns whether the path exists as a file.
    /// </summary>
    internal abstract bool FileExists(string path);

    /// <summary>
    /// Reads all bytes from a file.
    /// </summary>
    internal abstract byte[] ReadAllBytes(string path);

    /// <summary>
    /// Returns the current file length in bytes.
    /// </summary>
    internal abstract long GetLength(string path);

    /// <summary>
    /// Computes a lowercase SHA-256 digest for a file.
    /// </summary>
    internal abstract string ComputeSha256(string path);

    /// <summary>
    /// Enumerates files under an exact release tree.
    /// </summary>
    internal abstract IEnumerable<string> EnumerateFiles(string rootPath);

    private sealed class PhysicalReleaseArchiveFileSystem : AppSurfaceDocsReleaseArchiveFileSystem
    {
        internal override bool FileExists(string path)
        {
            return File.Exists(path);
        }

        internal override byte[] ReadAllBytes(string path)
        {
            return File.ReadAllBytes(path);
        }

        internal override long GetLength(string path)
        {
            return new FileInfo(path).Length;
        }

        internal override string ComputeSha256(string path)
        {
            return AppSurfaceDocsReleaseArchiveVerifier.ComputeFileSha256(path);
        }

        internal override IEnumerable<string> EnumerateFiles(string rootPath)
        {
            return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories);
        }
    }
}

/// <summary>
/// Describes the archive-integrity state resolved for a published AppSurface Docs version.
/// </summary>
public enum AppSurfaceDocsReleaseArchiveVerificationState
{
    /// <summary>
    /// The version cannot be mounted because required archive shape or integrity checks failed.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The exact tree is shape-valid but has no catalog-pinned release manifest digest, so active archive SVG is denied.
    /// </summary>
    AvailableUnverifiedLegacy = 1,

    /// <summary>
    /// The exact tree matched the catalog-pinned release manifest digest and all verified file digests.
    /// </summary>
    AvailableVerified = 2
}

/// <summary>
/// Immutable file metadata from a verified release archive manifest.
/// </summary>
/// <param name="Path">Archive-root-relative file path using slash separators.</param>
/// <param name="Length">Expected byte length.</param>
/// <param name="ContentType">Content type captured by export, when known.</param>
/// <param name="Sha256">Expected lowercase SHA-256 digest.</param>
public sealed record AppSurfaceDocsReleaseArchiveFile(
    string Path,
    long Length,
    string? ContentType,
    string Sha256);

/// <summary>
/// Verified release archive metadata used by mounted published-tree handlers.
/// </summary>
public sealed class AppSurfaceDocsVerifiedReleaseArchive
{
    private readonly IReadOnlyDictionary<string, AppSurfaceDocsReleaseArchiveFile> _filesByPath;

    internal AppSurfaceDocsVerifiedReleaseArchive(
        IReadOnlyDictionary<string, AppSurfaceDocsReleaseArchiveFile> filesByPath,
        AppSurfaceDocsFrozenRouteManifest frozenRouteManifest)
    {
        _filesByPath = new Dictionary<string, AppSurfaceDocsReleaseArchiveFile>(
            filesByPath ?? throw new ArgumentNullException(nameof(filesByPath)),
            StringComparer.Ordinal);
        FrozenRouteManifest = frozenRouteManifest ?? throw new ArgumentNullException(nameof(frozenRouteManifest));
    }

    /// <summary>
    /// Gets the number of files covered by the verified release manifest.
    /// </summary>
    public int FileCount => _filesByPath.Count;

    /// <summary>
    /// Gets the route manifest parsed from verified release archive bytes.
    /// </summary>
    internal AppSurfaceDocsFrozenRouteManifest FrozenRouteManifest { get; }

    /// <summary>
    /// Attempts to resolve verified metadata for a path the published-tree handler is about to serve.
    /// </summary>
    /// <param name="relativePath">Archive-root-relative path using either platform or slash separators.</param>
    /// <param name="file">Verified file metadata when present.</param>
    /// <returns><c>true</c> when the path is covered by the release manifest.</returns>
    public bool TryGetFile(string relativePath, out AppSurfaceDocsReleaseArchiveFile file)
    {
        var normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        return _filesByPath.TryGetValue(normalized, out file!);
    }
}

/// <summary>
/// Stable archive verification diagnostic surfaced through logs and sanitized availability messages.
/// </summary>
/// <param name="Code">Stable diagnostic code.</param>
/// <param name="PublicMessage">Sanitized public message.</param>
/// <param name="Detail">Operator-facing detail suitable for structured logs.</param>
/// <param name="Path">Archive-root-relative path associated with the failure, when applicable.</param>
public sealed record AppSurfaceDocsArchiveVerificationFailure(
    string Code,
    string PublicMessage,
    string Detail,
    string? Path)
{
    internal static AppSurfaceDocsArchiveVerificationFailure Create(
        string code,
        string publicMessage,
        string detail,
        string? path = null)
    {
        return new AppSurfaceDocsArchiveVerificationFailure(code, publicMessage, detail, path);
    }
}
