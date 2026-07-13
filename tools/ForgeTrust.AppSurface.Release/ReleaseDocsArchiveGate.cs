using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Verifies stable release evidence against a staged AppSurface Docs version catalog and exact archive tree.
/// </summary>
/// <remarks>
/// Runtime docs catalog loading is deliberately lenient so one bad version cannot break unrelated docs. Stable release
/// publishing is the opposite boundary: the selected release version must be present, public, catalog-pinned, and byte
/// verified before the release tool allows the stable GitHub Release path to continue.
/// </remarks>
internal static class ReleaseDocsArchiveGate
{
    internal const string VerifiedState = "availableVerified";

    private const string DocsCatalogFallbackPath = "dist/docs/versions.json";
    private const string ReleaseManifestFileName = ".appsurface-docs-release-manifest.json";
    private const string ReleaseManifestSchema = "appsurface-docs-release-manifest-v1";
    private const string RouteManifestFileName = ".appsurface-docs-route-manifest.json";
    private const string RouteManifestSchema = "appsurface-docs-route-manifest-v1";

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly PhysicalFileSystemInspector DefaultFileSystemInspector = new();
    private static readonly AsyncLocal<IFileSystemInspector?> FileSystemInspectorOverride = new();

    /// <summary>
    /// Validates the stable release docs archive contract using command-supplied catalog inputs.
    /// </summary>
    internal static async Task<ReleaseDocsArchiveGateResult> ValidateStableAsync(
        ReleaseWorkspace workspace,
        ReleaseOptions options,
        ReleaseEvidenceBundle bundle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bundle);

        var docsPath = options.Command switch
        {
            "publish" => "tools/ForgeTrust.AppSurface.Release/README.md#publish",
            _ => "tools/ForgeTrust.AppSurface.Release/README.md#check"
        };

        var catalogPath = ResolveCatalogPath(workspace, options);
        if (catalogPath is null || !File.Exists(catalogPath))
        {
            return ReleaseDocsArchiveGateResult.Error(ReleaseDiagnostic.Error(
                "release-docs-catalog-input-missing",
                "Stable release docs catalog input is missing.",
                options.Command == "check"
                    ? $"No `--docs-catalog` value was supplied and `{DocsCatalogFallbackPath}` does not exist for local review fallback."
                    : "Stable publish must receive `--docs-catalog` from the staged docs release artifact.",
                "Run AppSurface Docs export, stage the exact docs artifact, then pass its `versions.json` with `--docs-catalog` and the archive root with `--docs-trusted-release-root`.",
                docsPath));
        }

        var trustedReleaseRootPath = options.DocsTrustedReleaseRootPath
            ?? Path.GetDirectoryName(catalogPath)
            ?? workspace.RepositoryRoot;
        var trustedReleaseRoot = NormalizePhysicalPath(trustedReleaseRootPath);
        if (!TryValidateOrdinaryDirectory(
            trustedReleaseRoot,
            out var trustedRootIssue,
            out var trustedRootDetail))
        {
            return ReleaseDocsArchiveGateResult.Error(ReleaseDiagnostic.Error(
                "release-docs-archive-verification-failed",
                "Stable release docs trusted root is unavailable.",
                trustedRootDetail ?? trustedRootIssue ?? "The trusted release root could not be inspected.",
                "Point `--docs-trusted-release-root` at the ordinary directory that contains the catalog exact trees.",
                docsPath));
        }

        ReleaseDocsCatalogEntry catalogEntry;
        try
        {
            catalogEntry = await ReadSelectedCatalogEntryAsync(catalogPath, options.Version.ToString(), cancellationToken);
        }
        catch (ReleaseToolException ex)
        {
            return ReleaseDocsArchiveGateResult.Error(ex.Diagnostic);
        }

        if (!CatalogMatchesEvidence(bundle.DocsArchive, catalogEntry))
        {
            return ReleaseDocsArchiveGateResult.Error(ReleaseDiagnostic.Error(
                "release-evidence-catalog-entry-mismatch",
                "Release evidence docs archive fields do not match the checked catalog entry.",
                $"Evidence records exactTreePath `{bundle.DocsArchive.ExactTreePath ?? "missing"}` and releaseManifestSha256 `{bundle.DocsArchive.ReleaseManifestSha256 ?? "missing"}`, but catalog `{DisplayPath(catalogPath)}` records exactTreePath `{catalogEntry.ExactTreePath}` and releaseManifestSha256 `{catalogEntry.ReleaseManifestSha256}`.",
                "Regenerate release evidence from the same staged docs catalog entry that publish will verify.",
                docsPath));
        }

        if (!TryResolveExactTreePath(
            trustedReleaseRoot,
            catalogEntry.ExactTreePath,
            out var exactTreePath,
            out var exactTreeIssue))
        {
            return ReleaseDocsArchiveGateResult.Error(ReleaseDiagnostic.Error(
                "release-evidence-docs-exacttreepath-unsafe",
                "Stable release docs catalog exactTreePath is unsafe.",
                exactTreeIssue ?? "The catalog exactTreePath could not be resolved under the trusted root.",
                "Use a trusted-release-root-relative exactTreePath with no rooted, parent, or hidden path segments.",
                docsPath));
        }

        if (!TryValidateOrdinaryDirectory(exactTreePath!, out _, out var exactTreeDetail))
        {
            return ReleaseDocsArchiveGateResult.Error(ReleaseDiagnostic.Error(
                "release-docs-archive-verification-failed",
                "Stable release docs exact tree is unavailable.",
                exactTreeDetail ?? $"Exact tree `{DisplayPath(exactTreePath!)}` does not exist or is not an ordinary directory.",
                "Restore the staged docs exact tree under the trusted root before publishing the stable release.",
                docsPath));
        }

        if (!TryValidateNoReparseSegments(trustedReleaseRoot, exactTreePath!, out var reparseDetail))
        {
            return ReleaseDocsArchiveGateResult.Error(ReleaseDiagnostic.Error(
                "release-docs-archive-verification-failed",
                "Stable release docs exact tree is unsafe.",
                reparseDetail ?? "The exact tree path contains a symlink, junction, or reparse point.",
                "Stage the docs archive in ordinary directories under the trusted release root.",
                docsPath));
        }

        if (!TryVerifyArchive(
            exactTreePath!,
            catalogEntry.ReleaseManifestSha256,
            out var fileCount,
            out var verificationIssue))
        {
            return ReleaseDocsArchiveGateResult.Error(ReleaseDiagnostic.Error(
                "release-docs-archive-verification-failed",
                "Stable release docs archive verification failed.",
                verificationIssue ?? "The archive manifest did not match the staged exact tree.",
                "Re-export the docs archive or restore the exact tree that produced the catalog-pinned releaseManifestSha256.",
                docsPath));
        }

        var proof = new ReleaseDocsArchiveVerificationProof(
            VerifiedState,
            catalogPath,
            trustedReleaseRoot,
            catalogEntry.ExactTreePath,
            catalogEntry.ReleaseManifestSha256,
            exactTreePath!,
            fileCount);
        return new ReleaseDocsArchiveGateResult(proof, []);
    }

    private static string? ResolveCatalogPath(ReleaseWorkspace workspace, ReleaseOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DocsCatalogPath))
        {
            return options.DocsCatalogPath;
        }

        if (!string.Equals(options.Command, "check", StringComparison.Ordinal))
        {
            return null;
        }

        var fallback = workspace.PathFor(DocsCatalogFallbackPath);
        return File.Exists(fallback) ? fallback : null;
    }

    private static async Task<ReleaseDocsCatalogEntry> ReadSelectedCatalogEntryAsync(
        string catalogPath,
        string version,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            await using var stream = File.OpenRead(catalogPath);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-docs-catalog-version-unavailable",
                "Stable release docs catalog could not be read.",
                ex.Message,
                "Pass a readable AppSurface Docs versions.json produced from the staged docs release artifact.",
                "tools/ForgeTrust.AppSurface.Release/README.md#stable-docs-evidence"));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("versions", out var versionsElement)
                || versionsElement.ValueKind != JsonValueKind.Array)
            {
                throw CatalogUnavailable(version, "The catalog root must be an object with a `versions` array.");
            }

            ReleaseDocsCatalogEntry? selected = null;
            foreach (var entryElement in versionsElement.EnumerateArray())
            {
                if (entryElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryReadTrimmedString(entryElement, "version", out var entryVersion, out _)
                    || !string.Equals(entryVersion, version, StringComparison.Ordinal))
                {
                    continue;
                }

                if (selected is not null)
                {
                    throw CatalogUnavailable(version, "The selected version appears more than once in the catalog.");
                }

                selected = ReadSelectedEntry(entryElement, version);
            }

            return selected ?? throw CatalogUnavailable(version, "The selected version is not present in the catalog.");
        }
    }

    /// <summary>
    /// Validates that a physical directory candidate stays below an ordinary trusted root without crossing reparse segments.
    /// </summary>
    /// <param name="rootPath">Trusted exact-tree root or trusted release root to contain <paramref name="candidatePath"/>.</param>
    /// <param name="candidatePath">Physical directory path to validate after canonicalization.</param>
    /// <param name="detail">Receives diagnostic detail when the candidate escapes the root or crosses a symlink, junction, or reparse point.</param>
    /// <returns><see langword="true"/> when the candidate equals or descends from the root and every directory segment is ordinary.</returns>
    /// <remarks>
    /// This test seam validates directory ancestors. File leaves still need a separate <see cref="FileInfo"/> check before hashing or reading
    /// bytes so a manifest entry cannot point at a symlinked file inside an otherwise ordinary directory.
    /// </remarks>
    internal static bool TryValidateNoReparseSegments(string rootPath, string candidatePath, out string? detail)
    {
        detail = null;
        var root = NormalizePhysicalPath(rootPath);
        var candidate = NormalizePhysicalPath(candidatePath);
        if (!candidate.Equals(root, PhysicalPathComparison)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, PhysicalPathComparison))
        {
            detail = $"Exact tree `{DisplayPath(candidate)}` is outside trusted root `{DisplayPath(root)}`.";
            return false;
        }

        var relativePath = Path.GetRelativePath(root, candidate);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            return true;
        }

        var current = root;
        foreach (var segment in relativePath
                     .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            var info = new DirectoryInfo(current);
            if (!FileSystemInspector.DirectoryExists(info))
            {
                detail = $"Directory segment `{DisplayPath(current)}` does not exist.";
                return false;
            }

            FileAttributes attributes;
            try
            {
                attributes = FileSystemInspector.GetDirectoryAttributes(info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                detail = $"Directory segment `{DisplayPath(current)}` could not be inspected: {ex.Message}";
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                detail = $"Directory segment `{DisplayPath(current)}` is a symlink, junction, or reparse point.";
                return false;
            }
        }

        return true;
    }

    private static ReleaseDocsCatalogEntry ReadSelectedEntry(JsonElement entryElement, string version)
    {
        if (!TryReadVisibility(entryElement, out var isHidden, out var visibilityIssue))
        {
            throw CatalogUnavailable(version, visibilityIssue!);
        }

        if (isHidden)
        {
            throw CatalogUnavailable(version, "The selected version is hidden and cannot be published as stable docs evidence.");
        }

        if (!TryReadTrimmedString(entryElement, "exactTreePath", out var exactTreePath, out var exactTreeIssue)
            || string.IsNullOrWhiteSpace(exactTreePath))
        {
            throw CatalogUnavailable(version, exactTreeIssue ?? "The selected version is missing exactTreePath.");
        }

        if (!TryReadTrimmedString(entryElement, "releaseManifestSha256", out var digest, out var digestIssue)
            || !TryNormalizeSha256(digest, out var normalizedDigest))
        {
            throw CatalogUnavailable(version, digestIssue ?? "The selected version is missing a valid releaseManifestSha256 pin.");
        }

        return new ReleaseDocsCatalogEntry(exactTreePath!, normalizedDigest);
    }

    private static bool TryReadVisibility(JsonElement element, out bool isHidden, out string? issue)
    {
        isHidden = false;
        issue = null;
        if (!element.TryGetProperty("visibility", out var visibilityElement)
            || visibilityElement.ValueKind == JsonValueKind.Null)
        {
            issue = "The selected version must declare public visibility.";
            return false;
        }

        if (visibilityElement.ValueKind == JsonValueKind.String)
        {
            var value = visibilityElement.GetString()!.Trim();
            if (string.Equals(value, "public", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "hidden", StringComparison.OrdinalIgnoreCase))
            {
                isHidden = true;
                return true;
            }
        }
        else if (visibilityElement.ValueKind == JsonValueKind.Number
            && visibilityElement.TryGetInt32(out var numericVisibility))
        {
            if (numericVisibility == 0)
            {
                return true;
            }

            if (numericVisibility == 1)
            {
                isHidden = true;
                return true;
            }
        }

        issue = "The selected version has an invalid visibility value.";
        return false;
    }

    private static bool TryReadTrimmedString(JsonElement element, string propertyName, out string? value, out string? issue)
    {
        value = null;
        issue = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            issue = $"The selected version has a non-string `{propertyName}` value.";
            return false;
        }

        value = property.GetString()!.Trim();
        return true;
    }

    private static ReleaseToolException CatalogUnavailable(string version, string cause)
    {
        return new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-docs-catalog-version-unavailable",
            $"Stable release docs catalog version '{version}' is unavailable.",
            cause,
            "Regenerate or repair the staged AppSurface Docs version catalog so the selected stable version is public, unique, and catalog-pinned.",
            "tools/ForgeTrust.AppSurface.Release/README.md#stable-docs-evidence"));
    }

    private static bool CatalogMatchesEvidence(ReleaseEvidenceDocsArchive docsArchive, ReleaseDocsCatalogEntry catalogEntry)
    {
        return string.Equals(docsArchive.ExactTreePath, catalogEntry.ExactTreePath, StringComparison.Ordinal)
            && (string.Equals(docsArchive.ReleaseManifestSha256, catalogEntry.ReleaseManifestSha256, StringComparison.Ordinal)
                || string.Equals(docsArchive.ReleaseManifestSha256, ReleaseEvidence.DocsArchiveGeneratedDigest, StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolves a catalog <c>exactTreePath</c> into a physical path beneath the trusted release root.
    /// </summary>
    /// <param name="trustedReleaseRoot">Canonical trusted release root that contains staged exact-tree archives.</param>
    /// <param name="exactTreePath">Catalog-authored exact-tree path. It must be relative and must avoid parent or hidden segments.</param>
    /// <param name="physicalExactTreePath">Receives the canonical physical exact-tree path when resolution succeeds; otherwise <see langword="null"/> unless containment fails after normalization.</param>
    /// <param name="issue">Receives a maintainer-facing reason when the catalog path is empty, rooted, unsafe, escaping, or invalid.</param>
    /// <returns><see langword="true"/> when the authored path can be safely resolved under <paramref name="trustedReleaseRoot"/>.</returns>
    /// <remarks>
    /// The trusted root is supplied by release operators or defaults from the catalog directory. Callers should validate the returned directory
    /// exists and has no reparse segments before reading archive content.
    /// </remarks>
    internal static bool TryResolveExactTreePath(
        string trustedReleaseRoot,
        string exactTreePath,
        out string? physicalExactTreePath,
        out string? issue)
    {
        physicalExactTreePath = null;
        issue = null;
        var trimmed = exactTreePath.Trim();
        try
        {
            if (Path.IsPathRooted(trimmed))
            {
                issue = $"Catalog exactTreePath `{exactTreePath}` is rooted.";
                return false;
            }

            var segments = trimmed.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0
                || segments.Any(segment => segment == ".." || (segment.StartsWith(".", StringComparison.Ordinal) && segment != ".")))
            {
                issue = $"Catalog exactTreePath `{exactTreePath}` contains an empty, hidden, or parent segment.";
                return false;
            }

            physicalExactTreePath = NormalizePhysicalPath(Path.Join(trustedReleaseRoot, trimmed));
            if (!physicalExactTreePath.Equals(trustedReleaseRoot, PhysicalPathComparison)
                && !physicalExactTreePath.StartsWith(trustedReleaseRoot + Path.DirectorySeparatorChar, PhysicalPathComparison))
            {
                issue = $"Catalog exactTreePath `{exactTreePath}` escapes the trusted release root.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            issue = $"Catalog exactTreePath `{exactTreePath}` is invalid: {ex.Message}";
            return false;
        }
    }

    private static bool TryValidateOrdinaryDirectory(string directoryPath, out string? publicIssue, out string? detail)
    {
        publicIssue = null;
        detail = null;
        try
        {
            var info = new DirectoryInfo(directoryPath);
            if (!FileSystemInspector.DirectoryExists(info))
            {
                publicIssue = "Directory is missing.";
                detail = File.Exists(directoryPath)
                    ? $"Path `{DisplayPath(directoryPath)}` is a file, not a directory."
                    : $"Directory `{DisplayPath(directoryPath)}` does not exist.";
                return false;
            }

            if ((FileSystemInspector.GetDirectoryAttributes(info) & FileAttributes.ReparsePoint) != 0)
            {
                publicIssue = "Directory is unsafe.";
                detail = $"Directory `{DisplayPath(directoryPath)}` is a symlink, junction, or reparse point.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            publicIssue = "Directory could not be inspected.";
            detail = ex.Message;
            return false;
        }
    }

    private static bool TryVerifyArchive(
        string exactTreePath,
        string expectedManifestSha256,
        out int fileCount,
        out string? issue)
    {
        fileCount = 0;
        issue = null;
        var manifestPath = Path.Join(exactTreePath, ReleaseManifestFileName);
        if (!TryValidateOrdinaryFile(exactTreePath, manifestPath, ReleaseManifestFileName, out _, out issue))
        {
            return false;
        }

        byte[] manifestBytes;
        string actualManifestDigest;
        try
        {
            manifestBytes = File.ReadAllBytes(manifestPath);
            actualManifestDigest = ComputeBytesSha256(manifestBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            issue = $"Release manifest `{ReleaseManifestFileName}` could not be read: {ex.Message}";
            return false;
        }

        if (!string.Equals(actualManifestDigest, expectedManifestSha256, StringComparison.Ordinal))
        {
            issue = "Release manifest digest does not match the catalog-pinned releaseManifestSha256.";
            return false;
        }

        ReleaseArchiveManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ReleaseArchiveManifestDocument>(manifestBytes, ManifestSerializerOptions);
        }
        catch (JsonException ex)
        {
            issue = $"Release manifest payload is unreadable: {ex.Message}";
            return false;
        }

        if (document is null || !string.Equals(document.Schema, ReleaseManifestSchema, StringComparison.Ordinal))
        {
            issue = $"Release manifest schema must be `{ReleaseManifestSchema}`.";
            return false;
        }

        var files = new Dictionary<string, ReleaseArchiveManifestFile>(StringComparer.Ordinal);
        foreach (var entry in document.Files ?? [])
        {
            if (!TryValidateManifestEntry(entry, out var normalizedEntry, out issue))
            {
                return false;
            }

            if (!files.TryAdd(normalizedEntry.Path, normalizedEntry))
            {
                issue = $"Release manifest contains duplicate path `{normalizedEntry.Path}`.";
                return false;
            }

            var physicalFile = Path.Join(exactTreePath, normalizedEntry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!TryValidateOrdinaryFile(exactTreePath, physicalFile, normalizedEntry.Path, out var info, out issue))
            {
                return false;
            }

            if (info.Length != normalizedEntry.Length)
            {
                issue = $"Release manifest file `{normalizedEntry.Path}` has a different byte length.";
                return false;
            }

            string digest;
            byte[]? fileBytes = null;
            try
            {
                if (string.Equals(normalizedEntry.Path, RouteManifestFileName, StringComparison.Ordinal))
                {
                    fileBytes = File.ReadAllBytes(physicalFile);
                    digest = ComputeBytesSha256(fileBytes);
                }
                else
                {
                    digest = ComputeFileSha256(physicalFile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                issue = $"Release manifest file `{normalizedEntry.Path}` could not be read: {ex.Message}";
                return false;
            }

            if (!string.Equals(digest, normalizedEntry.Sha256, StringComparison.Ordinal))
            {
                issue = $"Release manifest file `{normalizedEntry.Path}` has a different SHA-256 digest.";
                return false;
            }

            if (fileBytes is not null && !TryValidateRouteManifest(fileBytes, out issue))
            {
                return false;
            }
        }

        if (!TryEnumerateOrdinaryServeableFiles(exactTreePath, out var serveableFiles, out issue))
        {
            return false;
        }

        var physicalManifestPaths = CreatePhysicalManifestPathSet(
            files.Keys,
            ResolvePhysicalPathComparer(exactTreePath, Directory.Exists, Directory.EnumerateFileSystemEntries));
        var unmanifestedServeableFile = serveableFiles
            .Where(serveableFile => !physicalManifestPaths.Contains(serveableFile))
            .FirstOrDefault();
        if (unmanifestedServeableFile is not null)
        {
            issue = $"Release archive contains serveable file `{unmanifestedServeableFile}` that is not listed in the release manifest.";
            return false;
        }

        fileCount = files.Count;
        return true;
    }

    /// <summary>
    /// Creates the physical-path coverage set for a release manifest under the filesystem's casing rules.
    /// </summary>
    /// <param name="manifestPaths">Logical paths recorded by the release manifest.</param>
    /// <param name="pathComparer">Comparer matching the physical filesystem's case behavior.</param>
    /// <returns>A set used to match paths returned by physical archive enumeration.</returns>
    internal static IReadOnlySet<string> CreatePhysicalManifestPathSet(
        IEnumerable<string> manifestPaths,
        StringComparer pathComparer)
    {
        ArgumentNullException.ThrowIfNull(manifestPaths);
        ArgumentNullException.ThrowIfNull(pathComparer);
        return new HashSet<string>(manifestPaths, pathComparer);
    }

    /// <summary>
    /// Resolves physical filesystem casing behavior without writing probe files into an immutable archive.
    /// </summary>
    /// <param name="rootPath">Existing exact release tree.</param>
    /// <param name="directoryExists">Directory existence operation used for the read-only case-variant probe.</param>
    /// <param name="enumerateFileSystemEntries">Filesystem enumeration used to reject ambiguous case-variant siblings.</param>
    /// <returns>An ordinal comparer matching the archive root's case behavior.</returns>
    internal static StringComparer ResolvePhysicalPathComparer(
        string rootPath,
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateFileSystemEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(enumerateFileSystemEntries);
        var probePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        while (!string.IsNullOrEmpty(probePath))
        {
            var segment = Path.GetFileName(probePath);
            var parentPath = Path.GetDirectoryName(probePath);
            var letterIndex = -1;
            for (var index = segment.Length - 1; index >= 0; index--)
            {
                if (char.IsAsciiLetter(segment[index]))
                {
                    letterIndex = index;
                    break;
                }
            }

            if (letterIndex < 0 || string.IsNullOrEmpty(parentPath))
            {
                probePath = parentPath ?? string.Empty;
                continue;
            }

            int matchingEntryCount;
            try
            {
                matchingEntryCount = enumerateFileSystemEntries(parentPath)
                    .Select(Path.GetFileName)
                    .Where(entryName => string.Equals(entryName, segment, StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .Count();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return StringComparer.Ordinal;
            }
            if (matchingEntryCount != 1)
            {
                return StringComparer.Ordinal;
            }

            var alternateSegmentCharacters = segment.ToCharArray();
            alternateSegmentCharacters[letterIndex] = char.IsAsciiLetterLower(alternateSegmentCharacters[letterIndex])
                ? char.ToUpperInvariant(alternateSegmentCharacters[letterIndex])
                : char.ToLowerInvariant(alternateSegmentCharacters[letterIndex]);
            var alternateRootPath = Path.Combine(parentPath, new string(alternateSegmentCharacters));
            return directoryExists(alternateRootPath)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }

        return StringComparer.Ordinal;
    }

    private static bool TryEnumerateOrdinaryServeableFiles(
        string exactTreePath,
        out IReadOnlyList<string> serveableFiles,
        out string? issue)
    {
        var files = new List<string>();
        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(exactTreePath));
        serveableFiles = files;
        issue = null;

        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = FileSystemInspector.EnumerateFileSystemInfos(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                issue = $"Release archive directory `{DisplayPath(directory.FullName)}` could not be inspected: {ex.Message}";
                return false;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = FileSystemInspector.GetFileSystemInfoAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    issue = $"Release archive entry `{DisplayPath(entry.FullName)}` could not be inspected: {ex.Message}";
                    return false;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    issue = $"Release archive entry `{DisplayPath(entry.FullName)}` is a symlink or reparse point.";
                    return false;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(new DirectoryInfo(entry.FullName));
                    continue;
                }

                var relativePath = Path.GetRelativePath(exactTreePath, entry.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                if (!string.Equals(relativePath, ReleaseManifestFileName, StringComparison.Ordinal)
                    && IsHandlerServeablePath(relativePath))
                {
                    files.Add(relativePath);
                }
            }
        }

        return true;
    }

    private static bool TryValidateOrdinaryFile(
        string rootPath,
        string filePath,
        string displayPath,
        out FileInfo fileInfo,
        out string? issue)
    {
        fileInfo = null!;
        issue = null;
        try
        {
            var normalizedFilePath = NormalizePhysicalPath(filePath);
            var directoryPath = Path.GetDirectoryName(normalizedFilePath)!;

            if (!TryValidateNoReparseSegments(rootPath, directoryPath, out var detail))
            {
                issue = detail;
                return false;
            }

            fileInfo = new FileInfo(normalizedFilePath);
            if (!FileSystemInspector.FileExists(fileInfo))
            {
                issue = string.Equals(displayPath, ReleaseManifestFileName, StringComparison.Ordinal)
                    ? $"Release manifest `{ReleaseManifestFileName}` is missing."
                    : $"Release manifest lists missing file `{displayPath}`.";
                return false;
            }

            if ((FileSystemInspector.GetFileAttributes(fileInfo) & FileAttributes.ReparsePoint) != 0)
            {
                issue = string.Equals(displayPath, ReleaseManifestFileName, StringComparison.Ordinal)
                    ? $"Release manifest `{ReleaseManifestFileName}` is a symlink or reparse point."
                    : $"Release manifest file `{displayPath}` is a symlink or reparse point.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            issue = $"Release manifest file `{displayPath}` could not be inspected: {ex.Message}";
            return false;
        }
    }

    private static bool TryValidateManifestEntry(
        ReleaseArchiveManifestFile? entry,
        out ReleaseArchiveManifestFile normalizedEntry,
        out string? issue)
    {
        normalizedEntry = null!;
        issue = null;
        if (entry is null
            || string.IsNullOrWhiteSpace(entry.Path)
            || entry.Length < 0
            || !string.Equals(entry.HashAlgorithm, "sha256", StringComparison.OrdinalIgnoreCase)
            || !TryNormalizeSha256(entry.Sha256, out var normalizedDigest))
        {
            issue = "Release manifest contains an invalid file entry.";
            return false;
        }

        var path = entry.Path.Trim();
        if (path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || path.Contains('?', StringComparison.Ordinal)
            || path.Contains("//", StringComparison.Ordinal))
        {
            issue = $"Release manifest contains unsafe file path `{entry.Path}`.";
            return false;
        }

        if (entry.ContentType is not null && string.IsNullOrWhiteSpace(entry.ContentType))
        {
            issue = $"Release manifest file `{entry.Path}` has an invalid contentType value.";
            return false;
        }

        var hasUnsafeSegment = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => string.IsNullOrWhiteSpace(segment)
                              || segment == "."
                              || segment == ".."
                              || (segment.StartsWith(".", StringComparison.Ordinal)
                                  && !string.Equals(path, RouteManifestFileName, StringComparison.Ordinal)))
            .Any();
        if (hasUnsafeSegment)
        {
            issue = $"Release manifest contains unsafe file path `{entry.Path}`.";
            return false;
        }

        normalizedEntry = entry with { Path = path, Sha256 = normalizedDigest };
        return true;
    }

    private static bool TryValidateRouteManifest(byte[] bytes, out string? issue)
    {
        issue = null;
        try
        {
            var document = JsonSerializer.Deserialize<RouteManifestDocument>(bytes, ManifestSerializerOptions);
            if (document is null || !string.Equals(document.Schema, RouteManifestSchema, StringComparison.Ordinal))
            {
                issue = $"Release archive route manifest `{RouteManifestFileName}` is malformed.";
                return false;
            }

            ValidateRouteManifestDocument(document);
            return true;
        }
        catch (JsonException ex)
        {
            issue = $"Release archive route manifest `{RouteManifestFileName}` is malformed: {ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            issue = $"Release archive route manifest `{RouteManifestFileName}` is invalid: {ex.Message}";
            return false;
        }
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

    private static bool IsHandlerServeablePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".woff", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".eot", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateRouteManifestDocument(RouteManifestDocument document)
    {
        var canonicalRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Entries ?? [])
        {
            if (entry.CanonicalRoutePath is null)
            {
                throw new InvalidOperationException("Frozen AppSurface Docs route manifest entries require canonicalRoutePath values.");
            }

            var canonicalRoutePath = NormalizeRoutePath(entry.CanonicalRoutePath);
            var canonicalRoutePathPart = GetRoutePathPart(canonicalRoutePath);
            if (!IsSafeRoutePath(canonicalRoutePath))
            {
                throw new InvalidOperationException($"Frozen AppSurface Docs route manifest contains unsafe canonical route '{canonicalRoutePath}'.");
            }

            if (!canonicalRoutes.Add(canonicalRoutePathPart))
            {
                throw new InvalidOperationException($"Frozen AppSurface Docs route manifest contains duplicate canonical route '{canonicalRoutePath}'.");
            }
        }

        var canonicalRouteByAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Entries ?? [])
        {
            var canonicalRoutePath = NormalizeRoutePath(entry.CanonicalRoutePath);
            var canonicalRoutePathPart = GetRoutePathPart(canonicalRoutePath);
            foreach (var aliasRoutePath in (entry.RecoveryAliases ?? [])
                         .Concat(entry.DeclaredAliases ?? [])
                         .Select(NormalizeRoutePath))
            {
                if (string.IsNullOrWhiteSpace(aliasRoutePath))
                {
                    continue;
                }

                if (!IsSafeRoutePath(aliasRoutePath))
                {
                    throw new InvalidOperationException($"Frozen AppSurface Docs route manifest alias '{aliasRoutePath}' is unsafe.");
                }

                var aliasRoutePathPart = GetRoutePathPart(aliasRoutePath);
                if (string.Equals(aliasRoutePathPart, canonicalRoutePathPart, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Frozen AppSurface Docs route manifest alias '{aliasRoutePath}' matches its canonical route.");
                }

                if (canonicalRoutes.Contains(aliasRoutePathPart))
                {
                    throw new InvalidOperationException($"Frozen AppSurface Docs route manifest alias '{aliasRoutePath}' collides with a canonical route.");
                }

                if (canonicalRouteByAlias.TryGetValue(aliasRoutePath, out var existingCanonicalRoute))
                {
                    var issue = string.Equals(existingCanonicalRoute, canonicalRoutePath, StringComparison.OrdinalIgnoreCase)
                        ? "is duplicated"
                        : "points at multiple canonical routes";
                    throw new InvalidOperationException($"Frozen AppSurface Docs route manifest alias '{aliasRoutePath}' {issue}.");
                }

                canonicalRouteByAlias[aliasRoutePath] = canonicalRoutePath;
            }
        }
    }

    private static string NormalizeRoutePath(string? routePath)
    {
        if (string.IsNullOrWhiteSpace(routePath))
        {
            return string.Empty;
        }

        return routePath.Trim().TrimStart('/');
    }

    private static bool IsSafeRoutePath(string? routePath)
    {
        var normalizedRoutePath = NormalizeRoutePath(routePath);
        var pathPart = GetRoutePathPart(normalizedRoutePath);

        if (pathPart.Length == 0)
        {
            return true;
        }

        if (pathPart.Contains('\\', StringComparison.Ordinal)
            || pathPart.Contains('?', StringComparison.Ordinal)
            || pathPart.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        return pathPart.Split('/')
            .All(segment => !string.IsNullOrWhiteSpace(segment)
                            && !segment.StartsWith(".", StringComparison.Ordinal));
    }

    private static string GetRoutePathPart(string routePath)
    {
        var fragmentIndex = routePath.IndexOf('#', StringComparison.Ordinal);
        return fragmentIndex < 0
            ? routePath
            : routePath[..fragmentIndex];
    }

    private static string NormalizePhysicalPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var digest = SHA256.HashData(stream);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string ComputeBytesSha256(byte[] bytes)
    {
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string DisplayPath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static IFileSystemInspector FileSystemInspector => FileSystemInspectorOverride.Value ?? DefaultFileSystemInspector;

    /// <summary>
    /// Overrides release docs archive filesystem inspection for the current async test flow.
    /// </summary>
    /// <param name="inspector">Inspector used to read filesystem metadata until the returned scope is disposed.</param>
    /// <returns>A disposable scope that restores the previous inspector.</returns>
    /// <remarks>
    /// Production code uses the default physical inspector. Tests use this seam to force deterministic metadata and enumeration failures that
    /// operating systems otherwise expose only through race-prone permission or reparse-point behavior.
    /// </remarks>
    internal static IDisposable UseFileSystemInspectorForTesting(IFileSystemInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        var previous = FileSystemInspectorOverride.Value;
        FileSystemInspectorOverride.Value = inspector;
        return new FileSystemInspectorScope(previous);
    }

    private static StringComparison PhysicalPathComparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Reads filesystem metadata used by stable docs archive validation.
    /// </summary>
    /// <remarks>
    /// The interface is intentionally narrow and internal: callers should not use it to virtualize archive bytes, only metadata operations whose
    /// real filesystem failures are difficult to trigger deterministically in tests.
    /// </remarks>
    internal interface IFileSystemInspector
    {
        /// <summary>
        /// Returns whether the directory currently exists.
        /// </summary>
        bool DirectoryExists(DirectoryInfo directory);

        /// <summary>
        /// Reads directory attributes, throwing the same filesystem exceptions as <see cref="FileSystemInfo.Attributes"/>.
        /// </summary>
        FileAttributes GetDirectoryAttributes(DirectoryInfo directory);

        /// <summary>
        /// Enumerates immediate entries in a directory.
        /// </summary>
        FileSystemInfo[] EnumerateFileSystemInfos(DirectoryInfo directory);

        /// <summary>
        /// Reads attributes for a file or directory entry discovered during archive traversal.
        /// </summary>
        FileAttributes GetFileSystemInfoAttributes(FileSystemInfo entry);

        /// <summary>
        /// Returns whether the file currently exists.
        /// </summary>
        bool FileExists(FileInfo file);

        /// <summary>
        /// Reads file attributes, throwing the same filesystem exceptions as <see cref="FileSystemInfo.Attributes"/>.
        /// </summary>
        FileAttributes GetFileAttributes(FileInfo file);
    }

    private sealed class PhysicalFileSystemInspector : IFileSystemInspector
    {
        public bool DirectoryExists(DirectoryInfo directory)
        {
            return directory.Exists;
        }

        public FileAttributes GetDirectoryAttributes(DirectoryInfo directory)
        {
            return directory.Attributes;
        }

        public FileSystemInfo[] EnumerateFileSystemInfos(DirectoryInfo directory)
        {
            return directory.EnumerateFileSystemInfos().ToArray();
        }

        public FileAttributes GetFileSystemInfoAttributes(FileSystemInfo entry)
        {
            return entry.Attributes;
        }

        public bool FileExists(FileInfo file)
        {
            return file.Exists;
        }

        public FileAttributes GetFileAttributes(FileInfo file)
        {
            return file.Attributes;
        }
    }

    private sealed class FileSystemInspectorScope(IFileSystemInspector? previous) : IDisposable
    {
        public void Dispose()
        {
            FileSystemInspectorOverride.Value = previous;
        }
    }

    private sealed record ReleaseDocsCatalogEntry(string ExactTreePath, string ReleaseManifestSha256);

    private sealed record ReleaseArchiveManifestDocument(
        string Schema,
        IReadOnlyList<ReleaseArchiveManifestFile>? Files);

    private sealed record ReleaseArchiveManifestFile(
        string Path,
        long Length,
        string? ContentType,
        string HashAlgorithm,
        string Sha256);

    private sealed record RouteManifestDocument(
        string? Schema,
        IReadOnlyList<RouteManifestEntry>? Entries);

    private sealed record RouteManifestEntry(
        string? CanonicalRoutePath,
        IReadOnlyList<string>? RecoveryAliases,
        IReadOnlyList<string>? DeclaredAliases);
}

/// <summary>
/// Result of stable docs archive verification.
/// </summary>
/// <param name="Proof">Verification proof when the catalog entry, exact tree, manifest, and serveable files matched; otherwise <see langword="null"/>.</param>
/// <param name="Diagnostics">Blocking diagnostics explaining why stable docs archive verification could not produce proof.</param>
/// <remarks>
/// Successful results have a non-null proof and no diagnostics. Failure results keep proof null so check and publish callers cannot accidentally
/// treat a partially inspected archive as verified.
/// </remarks>
internal sealed record ReleaseDocsArchiveGateResult(
    ReleaseDocsArchiveVerificationProof? Proof,
    IReadOnlyList<ReleaseDiagnostic> Diagnostics)
{
    internal static ReleaseDocsArchiveGateResult Error(ReleaseDiagnostic diagnostic)
    {
        return new ReleaseDocsArchiveGateResult(null, [diagnostic]);
    }
}

/// <summary>
/// Immutable proof that the stable release docs catalog entry and staged exact tree were verified.
/// </summary>
/// <param name="State">Verification state written into release reports. Successful verification uses <see cref="ReleaseDocsArchiveGate.VerifiedState"/>.</param>
/// <param name="CatalogPath">Physical path to the staged AppSurface Docs <c>versions.json</c> that supplied the selected catalog entry.</param>
/// <param name="TrustedReleaseRootPath">Canonical trusted release root used to resolve the catalog exact tree path.</param>
/// <param name="CatalogExactTreePath">Catalog-authored exact tree path reviewed by maintainers.</param>
/// <param name="CatalogReleaseManifestSha256">Catalog-pinned release manifest digest that matched the staged manifest bytes.</param>
/// <param name="PhysicalExactTreePath">Canonical physical exact tree path that was inspected for ordinary directories and verified content.</param>
/// <param name="VerifiedFileCount">Number of release manifest entries that were byte-verified.</param>
/// <remarks>
/// The proof intentionally carries both authored catalog values and resolved physical paths. Maintainers should review the authored values for
/// release identity and the physical values for staging provenance.
/// </remarks>
internal sealed record ReleaseDocsArchiveVerificationProof(
    string State,
    string CatalogPath,
    string TrustedReleaseRootPath,
    string CatalogExactTreePath,
    string CatalogReleaseManifestSha256,
    string PhysicalExactTreePath,
    int VerifiedFileCount);
