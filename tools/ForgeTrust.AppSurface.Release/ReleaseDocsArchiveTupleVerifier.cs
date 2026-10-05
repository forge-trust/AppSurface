using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Verifies that a selected Docs archive, publication plan, and SHA-256 sidecar describe the same release bytes.
/// </summary>
/// <remarks>
/// Callers must select these files from a trusted private artifact handoff. The verifier rejects observed links and
/// reparse points, bounds every input, and hashes the bytes actually read. Its path checks are defense in depth; they
/// do not provide race-proof descriptor-relative traversal or establish artifact provenance.
/// </remarks>
internal static class ReleaseDocsArchiveTupleVerifier
{
    internal const int MaximumPlanBytes = 64 * 1024;
    internal const int MaximumSidecarBytes = 256;
    internal const long MaximumArchiveBytes = 1024L * 1024 * 1024;

    private const string PlanSchema = "appsurface-docs-publication-plan-v1";
    private const string DocumentationPath = "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication";

    /// <summary>
    /// Recomputes the selected archive SHA-256 and checks it against the publication plan and sibling digest ledger.
    /// </summary>
    /// <param name="expectedVersion">Canonical SemVer version expected by the trusted caller.</param>
    /// <param name="archivePath">Host-selected archive file with the expected versioned asset name.</param>
    /// <param name="planPath">Host-selected publication-plan JSON file.</param>
    /// <param name="sidecarPath">Host-selected SHA-256 sidecar with the expected versioned asset name.</param>
    /// <param name="cancellationToken">Cancellation token checked during every read.</param>
    /// <returns>The verified tuple identity and byte count. No input paths are included.</returns>
    /// <exception cref="ReleaseToolException">An input is unsafe, malformed, inconsistent, or outside its byte bound.</exception>
    internal static async Task<ReleaseDocsArchiveTupleVerification> VerifyAsync(
        SemVer expectedVersion,
        string archivePath,
        string planPath,
        string sidecarPath,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = expectedVersion.ToString();
            if (!SemVer.TryParse(version, out var parsedVersion) || parsedVersion != expectedVersion)
            {
                throw Invalid("The expected release version is not canonical SemVer.");
            }

            var assetName = $"appsurface-docs-v{version}.tar.gz";
            var fullArchivePath = GetFullPath(archivePath);
            var fullPlanPath = GetFullPath(planPath);
            var fullSidecarPath = GetFullPath(sidecarPath);
            if (!string.Equals(Path.GetFileName(fullArchivePath), assetName, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(fullSidecarPath), assetName + ".sha256", StringComparison.Ordinal))
            {
                throw Invalid("The selected archive or sidecar does not use the expected versioned filename.");
            }

            var planBytes = await ReadBoundedFileAsync(fullPlanPath, MaximumPlanBytes, cancellationToken).ConfigureAwait(false);
            PublicationPlanTuple plan;
            try
            {
                plan = ReadPlanTuple(planBytes);
            }
            catch (JsonException)
            {
                throw Invalid("The publication plan is malformed or does not use the supported schema.");
            }
            if (!string.Equals(plan.Version, version, StringComparison.Ordinal)
                || !string.Equals(plan.Tag, expectedVersion.TagName, StringComparison.Ordinal)
                || !string.Equals(plan.ArchiveAssetName, assetName, StringComparison.Ordinal)
                || !string.Equals(GetPortableFileName(plan.ArchivePath), assetName, StringComparison.Ordinal)
                || !string.Equals(GetPortableFileName(plan.Sha256Path), assetName + ".sha256", StringComparison.Ordinal)
                || !IsCanonicalSha256(plan.ArchiveSha256))
            {
                throw Invalid("The publication plan does not bind the expected version, tag, archive name, and canonical digest.");
            }

            var sidecarBytes = await ReadBoundedFileAsync(fullSidecarPath, MaximumSidecarBytes, cancellationToken).ConfigureAwait(false);
            if (!SidecarMatches(sidecarBytes, plan.ArchiveSha256, assetName))
            {
                throw Invalid("The SHA-256 sidecar does not match the publication plan and expected archive name.");
            }

            var (archiveSha256, archiveLength) = await HashArchiveAsync(fullArchivePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(archiveSha256, plan.ArchiveSha256, StringComparison.Ordinal))
            {
                throw Invalid("The selected archive bytes do not match the publication plan and SHA-256 sidecar.");
            }

            return new ReleaseDocsArchiveTupleVerification(version, expectedVersion.TagName, assetName, archiveSha256, archiveLength);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ReleaseToolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or JsonException
            or DecoderFallbackException
            or OverflowException)
        {
            throw Invalid("A selected tuple input could not be read safely.");
        }
    }

    private static string GetFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid("A required tuple input path is missing.");
        }

        var candidate = ReleaseProjectionOutputWriter.NormalizePlatformPath(path);
        return Path.GetFullPath(candidate);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        EnsureObservedOrdinaryPath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var startingLength = stream.Length;
        if (startingLength <= 0 || startingLength > maximumBytes)
        {
            throw Invalid("A selected plan or sidecar is empty or exceeds its byte limit.");
        }

        var bytes = new byte[checked((int)startingLength)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw Invalid("A selected plan or sidecar changed while it was being read.");
            }

            offset += read;
        }

        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0 || stream.Length != startingLength)
        {
            throw Invalid("A selected plan or sidecar changed while it was being read.");
        }

        EnsureObservedOrdinaryPath(path);
        return bytes;
    }

    private static async Task<(string Sha256, long Length)> HashArchiveAsync(string path, CancellationToken cancellationToken)
    {
        EnsureObservedOrdinaryPath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var startingLength = stream.Length;
        if (startingLength <= 0 || startingLength > MaximumArchiveBytes)
        {
            throw Invalid("The selected Docs archive is empty or exceeds its byte limit.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long totalBytes = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalBytes = checked(totalBytes + read);
            if (totalBytes > MaximumArchiveBytes)
            {
                throw Invalid("The selected Docs archive exceeds its byte limit.");
            }

            hash.AppendData(buffer, 0, read);
        }

        if (totalBytes != startingLength || stream.Length != startingLength)
        {
            throw Invalid("The selected Docs archive changed while it was being hashed.");
        }

        EnsureObservedOrdinaryPath(path);
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), totalBytes);
    }

    private static void EnsureObservedOrdinaryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException();
        var segments = Path.GetRelativePath(root, fullPath)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            throw Invalid("A selected tuple input is not an ordinary file path.");
        }

        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Join(current, segments[index]);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Device) != 0
                || (index < segments.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                || (index == segments.Length - 1 && (attributes & FileAttributes.Directory) != 0))
            {
                throw Invalid("A selected tuple input contains a link or is not an ordinary file.");
            }
        }
    }

    private static PublicationPlanTuple ReadPlanTuple(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetUniqueString(root, "schema", out var schema)
            || !TryGetUniqueString(root, "version", out var version)
            || !TryGetUniqueString(root, "tag", out var tag)
            || !TryGetUniqueString(root, "archiveAssetName", out var archiveAssetName)
            || !TryGetUniqueString(root, "archivePath", out var archivePath)
            || !TryGetUniqueString(root, "archiveSha256", out var archiveSha256)
            || !TryGetUniqueString(root, "sha256Path", out var sha256Path)
            || !string.Equals(schema, PlanSchema, StringComparison.Ordinal))
        {
            throw Invalid("The publication plan is malformed or does not use the supported schema.");
        }

        return new PublicationPlanTuple(version!, tag!, archiveAssetName!, archivePath!, archiveSha256!, sha256Path!);
    }

    private static bool TryGetUniqueString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found || property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.Value.GetString();
            found = value is not null;
        }

        return found;
    }

    private static string GetPortableFileName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex < 0 ? normalized : normalized[(separatorIndex + 1)..];
    }

    private static bool SidecarMatches(byte[] bytes, string expectedSha256, string archiveAssetName)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);
        return string.Equals(text, $"{expectedSha256}  {archiveAssetName}\n", StringComparison.Ordinal)
            || string.Equals(text, $"{expectedSha256}  {archiveAssetName}\r\n", StringComparison.Ordinal);
    }

    private static bool IsCanonicalSha256(string value) =>
        value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ReleaseToolException Invalid(string cause) => new(ReleaseDiagnostic.Error(
        "release-docs-archive-tuple-invalid",
        "Docs archive tuple verification failed.",
        cause,
        "Select the archive, publication plan, and generated .sha256 sidecar for the same release version, then rerun verification.",
        DocumentationPath));

    private sealed record PublicationPlanTuple(
        string Version,
        string Tag,
        string ArchiveAssetName,
        string ArchivePath,
        string ArchiveSha256,
        string Sha256Path);
}

/// <summary>Verified identity of one host-selected Docs archive tuple.</summary>
/// <param name="Version">Canonical release version.</param>
/// <param name="Tag">Expected annotated release tag.</param>
/// <param name="ArchiveAssetName">Expected versioned GitHub Release asset name.</param>
/// <param name="ArchiveSha256">SHA-256 recomputed from the selected archive bytes.</param>
/// <param name="ArchiveLengthBytes">Number of archive bytes included in the digest.</param>
internal sealed record ReleaseDocsArchiveTupleVerification(
    string Version,
    string Tag,
    string ArchiveAssetName,
    string ArchiveSha256,
    long ArchiveLengthBytes);
