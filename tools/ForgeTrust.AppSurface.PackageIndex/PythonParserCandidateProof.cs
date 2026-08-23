using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Performs a bounded, static inspection of an explicitly evaluated Python parser package candidate.
/// </summary>
/// <remarks>
/// This workflow deliberately never restores, builds, loads, or executes candidate package content. A local feed and
/// a child process are not a security boundary for untrusted NuGet build assets, managed assemblies, analyzers, or
/// native libraries. A future candidate that passes this static gate needs a separately approved, sandboxed execution
/// design before runtime initialization can become acceptance evidence.
/// </remarks>
internal sealed class PythonParserCandidateProofWorkflow
{
    internal const string CandidatePackageId = "TreeSitter.DotNet";
    internal const string CandidatePackageVersion = "1.3.0";
    internal const long MaximumCompressedPackageBytes = 5L * 1024 * 1024;
    internal const long MaximumArchiveBytesToInspect = 64L * 1024 * 1024;
    internal const int MaximumArchiveEntryCount = 1_024;
    internal const long MaximumUncompressedArchiveBytes = 1L * 1024 * 1024 * 1024;
    internal const long MaximumNuspecBytes = 512L * 1024;

    private static readonly IReadOnlyList<string> RequiredRuntimeIdentifiers =
    [
        "linux-arm",
        "linux-arm64",
        "linux-x64",
        "linux-x86",
        "osx-arm64",
        "osx-x64",
        "win-arm64",
        "win-x64",
        "win-x86"
    ];

    /// <summary>
    /// Inspects the supplied archive and writes deterministic static-gate evidence.
    /// </summary>
    /// <param name="request">Repository, candidate archive, report, and size-budget inputs.</param>
    /// <param name="cancellationToken">Cancellation token propagated to file operations.</param>
    /// <returns>A machine-readable record of archive, native-asset, and provenance evidence.</returns>
    /// <exception cref="PackageIndexException">Thrown when the request has unsafe or incomplete paths.</exception>
    internal async Task<PythonParserCandidateProofReport> RunAsync(
        PythonParserCandidateProofRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var archive = await InspectArchiveAsync(request, cancellationToken);
        var rejectionReasons = new List<string>();

        if (!string.IsNullOrWhiteSpace(archive.InspectionFailure))
        {
            rejectionReasons.Add("archive_inspection_failed");
        }
        else
        {
            if (archive.CompressedArchiveBytes > request.MaximumCompressedPackageBytes)
            {
                rejectionReasons.Add("compressed_archive_exceeds_budget");
            }

            if (!string.Equals(archive.Metadata.PackageId, CandidatePackageId, StringComparison.Ordinal))
            {
                rejectionReasons.Add("package_id_mismatch");
            }

            if (!string.Equals(archive.Metadata.PackageVersion, CandidatePackageVersion, StringComparison.Ordinal))
            {
                rejectionReasons.Add("package_version_mismatch");
            }

            if (archive.MissingRuntimeIdentifiers.Count != 0)
            {
                rejectionReasons.Add("native_runtime_identifier_missing");
            }

            if (archive.UnexpectedRuntimeIdentifiers.Count != 0)
            {
                rejectionReasons.Add("native_runtime_identifier_unexpected");
            }

            if (!archive.HasCompleteProvenanceMetadata)
            {
                rejectionReasons.Add("provenance_metadata_incomplete");
            }
        }

        var report = new PythonParserCandidateProofReport(
            archive,
            rejectionReasons.Distinct(StringComparer.Ordinal).OrderBy(reason => reason, StringComparer.Ordinal).ToArray());
        await WriteReportAsync(report, request.ReportPath, cancellationToken);
        return report;
    }

    private static async Task<PythonParserCandidateArchiveEvidence> InspectArchiveAsync(
        PythonParserCandidateProofRequest request,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(request.CandidatePackagePath);
        if (fileInfo.Length > MaximumArchiveBytesToInspect)
        {
            return CreateUninspectableArchiveEvidence(
                fileInfo,
                string.Empty,
                0,
                0,
                $"Compressed archive exceeds the {MaximumArchiveBytesToInspect}-byte static inspection limit.");
        }

        await using var packageStream = File.OpenRead(request.CandidatePackagePath);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken));

        try
        {
            using var archive = ZipFile.OpenRead(request.CandidatePackagePath);
            if (archive.Entries.Count > MaximumArchiveEntryCount)
            {
                return CreateUninspectableArchiveEvidence(
                    fileInfo,
                    sha256,
                    archive.Entries.Count,
                    0,
                    $"Archive contains more than the {MaximumArchiveEntryCount}-entry static inspection limit.");
            }

            var uncompressedArchiveBytes = GetBoundedUncompressedArchiveBytes(archive);
            if (uncompressedArchiveBytes is null)
            {
                return CreateUninspectableArchiveEvidence(
                    fileInfo,
                    sha256,
                    archive.Entries.Count,
                    0,
                    $"Archive exceeds the {MaximumUncompressedArchiveBytes}-byte uncompressed static inspection limit.");
            }

            var metadata = ReadNuspecMetadata(archive);
            var nativeAssets = archive.Entries
                .Select(entry => new { Entry = entry, RuntimeIdentifier = GetNativeRuntimeIdentifier(entry.FullName) })
                .Where(candidate => candidate.RuntimeIdentifier is not null)
                .GroupBy(candidate => candidate.RuntimeIdentifier!, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new PythonParserNativeRuntimeEvidence(
                    group.Key,
                    group.Select(candidate => candidate.Entry.FullName).OrderBy(path => path, StringComparer.Ordinal).ToArray()))
                .ToArray();
            var actualRuntimeIdentifiers = nativeAssets.Select(asset => asset.RuntimeIdentifier).ToHashSet(StringComparer.Ordinal);
            var missingRuntimeIdentifiers = RequiredRuntimeIdentifiers.Where(rid => !actualRuntimeIdentifiers.Contains(rid)).ToArray();
            var unexpectedRuntimeIdentifiers = actualRuntimeIdentifiers
                .Where(rid => !RequiredRuntimeIdentifiers.Contains(rid, StringComparer.Ordinal))
                .OrderBy(rid => rid, StringComparer.Ordinal)
                .ToArray();
            var licenseAndNoticePaths = archive.Entries
                .Select(entry => entry.FullName)
                .Where(IsLicenseOrNoticePath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            return new PythonParserCandidateArchiveEvidence(
                fileInfo.Name,
                sha256,
                fileInfo.Length,
                archive.Entries.Count,
                uncompressedArchiveBytes.Value,
                metadata,
                nativeAssets,
                missingRuntimeIdentifiers,
                unexpectedRuntimeIdentifiers,
                licenseAndNoticePaths,
                new PythonParserCandidateProvenanceReviewEvidence(
                    "metadata_recorded_not_accepted",
                    "Archive metadata and license/notice paths are recorded for a separate human redistribution review; this candidate proof does not approve a dependency."),
                null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or SecurityException or XmlException or PackageIndexException)
        {
            return CreateUninspectableArchiveEvidence(fileInfo, sha256, 0, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static long? GetBoundedUncompressedArchiveBytes(ZipArchive archive)
    {
        var total = 0L;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > MaximumUncompressedArchiveBytes - total)
            {
                return null;
            }

            total += entry.Length;
        }

        return total;
    }

    private static PythonParserCandidateArchiveEvidence CreateUninspectableArchiveEvidence(
        FileInfo fileInfo,
        string sha256,
        int archiveEntryCount,
        long uncompressedArchiveBytes,
        string inspectionFailure) =>
        new(
            fileInfo.Name,
            sha256,
            fileInfo.Length,
            archiveEntryCount,
            uncompressedArchiveBytes,
            new PythonParserCandidateNuspecMetadata(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty),
            [],
            [],
            [],
            [],
            new PythonParserCandidateProvenanceReviewEvidence(
                "not_evaluated",
                "Archive inspection did not complete, so this candidate cannot receive provenance approval."),
            inspectionFailure);

    private static PythonParserCandidateNuspecMetadata ReadNuspecMetadata(ZipArchive archive)
    {
        var nuspecEntries = archive.Entries
            .Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.Contains('/', StringComparison.Ordinal))
            .ToArray();
        if (nuspecEntries.Length != 1)
        {
            throw new PackageIndexException($"Python parser candidate archive must contain exactly one root .nuspec file, found {nuspecEntries.Length}.");
        }

        if (nuspecEntries[0].Length > MaximumNuspecBytes)
        {
            throw new PackageIndexException($"Python parser candidate .nuspec exceeds the {MaximumNuspecBytes}-byte static inspection limit.");
        }

        using var stream = nuspecEntries[0].Open();
        var document = XDocument.Load(stream, LoadOptions.None);
        var metadata = document.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata");
        if (metadata is null)
        {
            throw new PackageIndexException("Python parser candidate .nuspec is missing metadata.");
        }

        var repository = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "repository");
        return new PythonParserCandidateNuspecMetadata(
            GetElementValue(metadata, "id"),
            GetElementValue(metadata, "version"),
            GetElementValue(metadata, "license"),
            repository?.Attribute("type")?.Value ?? string.Empty,
            repository?.Attribute("url")?.Value ?? string.Empty,
            repository?.Attribute("commit")?.Value ?? string.Empty);
    }

    private static string GetElementValue(XElement element, string name) =>
        element.Elements().SingleOrDefault(child => child.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

    private static string? GetNativeRuntimeIdentifier(string entryPath)
    {
        var segments = entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4
            && string.Equals(segments[0], "runtimes", StringComparison.Ordinal)
            && string.Equals(segments[2], "native", StringComparison.Ordinal)
            ? segments[1]
            : null;
    }

    private static bool IsLicenseOrNoticePath(string entryPath)
    {
        var fileName = Path.GetFileName(entryPath);
        return fileName.StartsWith("license", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("notice", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("copying", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("third-party", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteReportAsync(
        PythonParserCandidateProofReport report,
        string reportPath,
        CancellationToken cancellationToken)
    {
        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            throw new PackageIndexException($"Python parser candidate report path '{reportPath}' does not have a directory.");
        }

        Directory.CreateDirectory(reportDirectory);
        await using var stream = File.Create(reportPath);
        await JsonSerializer.SerializeAsync(
            stream,
            report,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
            cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(Environment.NewLine), cancellationToken);
    }

    private static void ValidateRequest(PythonParserCandidateProofRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidatePackagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReportPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaximumCompressedPackageBytes);
        if (!Directory.Exists(request.RepositoryRoot))
        {
            throw new PackageIndexException($"Python parser candidate proof repository root '{request.RepositoryRoot}' does not exist.");
        }

        if (!File.Exists(request.CandidatePackagePath))
        {
            throw new PackageIndexException($"Python parser candidate package '{request.CandidatePackagePath}' does not exist.");
        }

        if (!string.Equals(Path.GetExtension(request.CandidatePackagePath), ".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIndexException("Python parser candidate package must be a .nupkg file.");
        }

        var repositoryRoot = Path.GetFullPath(request.RepositoryRoot);
        var artifactsDirectory = Path.Join(repositoryRoot, "artifacts");
        var reportPath = Path.GetFullPath(request.ReportPath);
        if (!IsPathWithin(artifactsDirectory, reportPath))
        {
            throw new PackageIndexException("Python parser candidate report path must be within the repository artifacts directory.");
        }

        RejectExistingReportLinks(artifactsDirectory, reportPath);
    }

    private static void RejectExistingReportLinks(string artifactsDirectory, string reportPath)
    {
        var currentDirectory = Path.GetFullPath(artifactsDirectory);
        ThrowIfLink(new DirectoryInfo(currentDirectory));
        var reportDirectory = Path.GetDirectoryName(reportPath) ?? throw new PackageIndexException("Python parser candidate report path must have a directory.");
        var relativeDirectory = Path.GetRelativePath(currentDirectory, reportDirectory);
        foreach (var segment in relativeDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment) || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            currentDirectory = Path.Join(currentDirectory, segment);
            ThrowIfLink(new DirectoryInfo(currentDirectory));
        }

        ThrowIfLink(new FileInfo(reportPath));
    }

    private static void ThrowIfLink(FileSystemInfo fileSystemInfo)
    {
        if (fileSystemInfo.LinkTarget is not null)
        {
            throw new PackageIndexException("Python parser candidate report path must not traverse or overwrite symbolic links.");
        }
    }

    private static bool IsPathWithin(string parentDirectory, string childPath)
    {
        var normalizedParent = Path.GetFullPath(parentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedChild = Path.GetFullPath(childPath);
        var prefix = normalizedParent + Path.DirectorySeparatorChar;
        return normalizedChild.StartsWith(prefix, PackageIndexGenerator.RepositoryPathComparison);
    }
}

/// <summary>
/// Inputs for the static Python parser candidate proof.
/// </summary>
internal sealed record PythonParserCandidateProofRequest(
    string RepositoryRoot,
    string CandidatePackagePath,
    string ReportPath,
    long MaximumCompressedPackageBytes = PythonParserCandidateProofWorkflow.MaximumCompressedPackageBytes);

/// <summary>
/// Archive and provenance evidence for one parser candidate.
/// </summary>
internal sealed record PythonParserCandidateProofReport(
    PythonParserCandidateArchiveEvidence Archive,
    IReadOnlyList<string> RejectionReasons)
{
    /// <summary>
    /// Gets whether the automated static checks have no rejection reason.
    /// A true value only permits further review; it is not legal or runtime-execution approval.
    /// </summary>
    public bool IsEligibleForFurtherReview => RejectionReasons.Count == 0;
}

/// <summary>
/// Immutable static inspection result for the supplied candidate archive.
/// </summary>
internal sealed record PythonParserCandidateArchiveEvidence(
    string PackageFileName,
    string Sha256,
    long CompressedArchiveBytes,
    int ArchiveEntryCount,
    long UncompressedArchiveBytes,
    PythonParserCandidateNuspecMetadata Metadata,
    IReadOnlyList<PythonParserNativeRuntimeEvidence> NativeRuntimeAssets,
    IReadOnlyList<string> MissingRuntimeIdentifiers,
    IReadOnlyList<string> UnexpectedRuntimeIdentifiers,
    IReadOnlyList<string> LicenseAndNoticePaths,
    PythonParserCandidateProvenanceReviewEvidence ProvenanceReview,
    string? InspectionFailure)
{
    /// <summary>
    /// Gets whether the archive declares the minimum license and source provenance fields for later human review.
    /// </summary>
    public bool HasCompleteProvenanceMetadata =>
        !string.IsNullOrWhiteSpace(Metadata.LicenseExpression)
        && !string.IsNullOrWhiteSpace(Metadata.RepositoryType)
        && !string.IsNullOrWhiteSpace(Metadata.RepositoryUrl)
        && !string.IsNullOrWhiteSpace(Metadata.RepositoryCommit);
}

/// <summary>
/// NuGet metadata retained as provenance evidence without treating it as legal approval.
/// </summary>
internal sealed record PythonParserCandidateNuspecMetadata(
    string PackageId,
    string PackageVersion,
    string LicenseExpression,
    string RepositoryType,
    string RepositoryUrl,
    string RepositoryCommit);

/// <summary>
/// Explicit boundary between machine-recorded provenance facts and human redistribution approval.
/// </summary>
internal sealed record PythonParserCandidateProvenanceReviewEvidence(string Status, string Explanation);

/// <summary>
/// Full native asset inventory declared for one runtime identifier.
/// </summary>
internal sealed record PythonParserNativeRuntimeEvidence(
    string RuntimeIdentifier,
    IReadOnlyList<string> NativeAssetPaths)
{
    /// <summary>
    /// Gets the number of enumerated native asset paths.
    /// </summary>
    public int NativeFileCount => NativeAssetPaths.Count;
}
