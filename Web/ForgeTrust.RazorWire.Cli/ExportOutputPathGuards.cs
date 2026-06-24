using System.Text;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Validates exporter-owned artifact paths before the CLI creates directories, opens files, or reads release archive
/// entries.
/// </summary>
/// <remarks>
/// The guard enforces the physical output-root boundary for generated artifacts. It rejects existing symlink, junction,
/// or other reparse-point segments before the exporter follows them, while preserving ordinary non-empty output
/// directories that contain regular files and directories only.
/// </remarks>
internal static class ExportOutputPathGuards
{
    internal const string DiagnosticCode = "RWEXPORT009";
    internal const string OutputRootReparse = "output-root-reparse";
    internal const string ArtifactParentReparse = "artifact-parent-reparse";
    internal const string ArtifactTargetReparse = "artifact-target-reparse";
    internal const string ArchiveEntryReparse = "archive-entry-reparse";
    internal const string ArtifactOutsideRoot = "artifact-outside-root";
    internal const string DocsAnchor = "#generated-export-artifact-boundary";

    /// <summary>
    /// Validates the output root and its existing ancestor segments without changing the filesystem.
    /// </summary>
    /// <param name="outputPath">Export output root.</param>
    /// <param name="artifactKind">Generated artifact surface being prepared.</param>
    /// <param name="route">Route context when one exists.</param>
    /// <param name="operation">Filesystem operation being guarded.</param>
    internal static void ValidateOutputRootPath(
        string outputPath,
        string artifactKind,
        string? route,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        RejectExistingOutputRootSegments(NormalizePath(outputPath), artifactKind, route, ".", operation);
    }

    /// <summary>
    /// Ensures the output root can be used without following an existing reparse point.
    /// </summary>
    /// <param name="outputPath">Export output root.</param>
    /// <param name="artifactKind">Generated artifact surface being prepared.</param>
    /// <param name="route">Route context when one exists.</param>
    /// <param name="operation">Filesystem operation being guarded.</param>
    internal static void EnsureOutputRootReady(
        string outputPath,
        string artifactKind,
        string? route,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var outputRoot = NormalizePath(outputPath);
        ValidateOutputRootPath(outputRoot, artifactKind, route, operation);
        Directory.CreateDirectory(outputRoot);
        ValidateOutputRootPath(outputRoot, artifactKind, route, operation);
    }

    /// <summary>
    /// Validates a generated artifact file path without changing the filesystem.
    /// </summary>
    /// <param name="outputPath">Export output root.</param>
    /// <param name="artifactPath">Artifact file path to validate.</param>
    /// <param name="artifactKind">Human-readable artifact kind for diagnostics.</param>
    /// <param name="route">Route context when one exists.</param>
    /// <param name="operation">Filesystem operation being guarded.</param>
    internal static void ValidateWritableArtifactPath(
        string outputPath,
        string artifactPath,
        string artifactKind,
        string? route,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);

        var outputRoot = NormalizePath(outputPath);
        var fullArtifactPath = NormalizePath(artifactPath);
        var relativePath = NormalizeRelativePath(outputRoot, fullArtifactPath);
        if (!IsPathUnderRoot(fullArtifactPath, outputRoot, GetPathComparison()))
        {
            throw CreateException(
                ArtifactOutsideRoot,
                artifactKind,
                route,
                relativePath,
                fullArtifactPath,
                operation);
        }

        RejectExistingOutputRootSegments(outputRoot, artifactKind, route, relativePath, operation);

        var parentPath = Path.GetDirectoryName(fullArtifactPath);
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            if (!IsPathUnderRoot(parentPath, outputRoot, GetPathComparison()))
            {
                throw CreateException(
                    ArtifactOutsideRoot,
                    artifactKind,
                    route,
                    relativePath,
                    parentPath,
                    operation);
            }

            RejectExistingReparseSegments(
                outputRoot,
                parentPath,
                ArtifactParentReparse,
                artifactKind,
                route,
                relativePath,
                operation);
        }

        if (TryGetExistingFileSystemInfo(fullArtifactPath, out var targetInfo) && IsReparseOrLink(targetInfo))
        {
            throw CreateException(
                ArtifactTargetReparse,
                artifactKind,
                route,
                relativePath,
                fullArtifactPath,
                operation);
        }
    }

    /// <summary>
    /// Creates the artifact parent directory only after validating all existing physical path segments.
    /// </summary>
    /// <param name="outputPath">Export output root.</param>
    /// <param name="artifactPath">Artifact file path whose parent should exist.</param>
    /// <param name="artifactKind">Human-readable artifact kind for diagnostics.</param>
    /// <param name="route">Route context when one exists.</param>
    internal static void EnsureWritableArtifactParent(
        string outputPath,
        string artifactPath,
        string artifactKind,
        string? route)
    {
        EnsureOutputRootReady(outputPath, artifactKind, route, "create-directory");
        ValidateWritableArtifactPath(outputPath, artifactPath, artifactKind, route, "create-directory");
        var parentPath = Path.GetDirectoryName(NormalizePath(artifactPath));
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            Directory.CreateDirectory(parentPath);
        }

        ValidateWritableArtifactPath(outputPath, artifactPath, artifactKind, route, "create-directory");
    }

    /// <summary>
    /// Writes a generated text artifact after guarding its parent creation and final open/write operation.
    /// </summary>
    /// <param name="outputPath">Export output root.</param>
    /// <param name="artifactPath">Artifact file path to write.</param>
    /// <param name="artifactKind">Human-readable artifact kind for diagnostics.</param>
    /// <param name="route">Route context when one exists.</param>
    /// <param name="contents">Text payload to write.</param>
    /// <param name="encoding">Encoding to use when writing the payload.</param>
    /// <param name="cancellationToken">Token observed while writing the file.</param>
    internal static async Task WriteTextArtifactAsync(
        string outputPath,
        string artifactPath,
        string artifactKind,
        string? route,
        string contents,
        Encoding? encoding,
        CancellationToken cancellationToken)
    {
        EnsureWritableArtifactParent(outputPath, artifactPath, artifactKind, route);
        ValidateWritableArtifactPath(outputPath, artifactPath, artifactKind, route, "open-write");
        if (encoding is null)
        {
            await File.WriteAllTextAsync(artifactPath, contents, cancellationToken);
            return;
        }

        await File.WriteAllTextAsync(artifactPath, contents, encoding, cancellationToken);
    }

    /// <summary>
    /// Opens a generated file for writing after guarding its parent creation and final open operation.
    /// </summary>
    /// <param name="outputPath">Export output root.</param>
    /// <param name="artifactPath">Artifact file path to open.</param>
    /// <param name="artifactKind">Human-readable artifact kind for diagnostics.</param>
    /// <param name="route">Route context when one exists.</param>
    /// <returns>A write-only stream positioned at the start of the generated artifact.</returns>
    internal static FileStream OpenWritableArtifactStream(
        string outputPath,
        string artifactPath,
        string artifactKind,
        string? route)
    {
        EnsureWritableArtifactParent(outputPath, artifactPath, artifactKind, route);
        ValidateWritableArtifactPath(outputPath, artifactPath, artifactKind, route, "open-write");
        return new FileStream(
            artifactPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
    }

    /// <summary>
    /// Validates an existing release archive entry before traversal, metadata reads, hashing, or manifest inclusion.
    /// </summary>
    /// <param name="outputPath">Export output root.</param>
    /// <param name="entryPath">Existing archive entry path.</param>
    /// <param name="operation">Filesystem operation being guarded.</param>
    internal static void ValidateArchiveEntryPath(
        string outputPath,
        string entryPath,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);

        var outputRoot = NormalizePath(outputPath);
        var fullEntryPath = NormalizePath(entryPath);
        var relativePath = NormalizeRelativePath(outputRoot, fullEntryPath);
        if (!IsPathUnderRoot(fullEntryPath, outputRoot, GetPathComparison()))
        {
            throw CreateException(
                ArtifactOutsideRoot,
                "release archive entry",
                route: null,
                relativePath,
                fullEntryPath,
                operation);
        }

        if (TryGetExistingFileSystemInfo(fullEntryPath, out var info) && IsReparseOrLink(info))
        {
            throw CreateException(
                ArchiveEntryReparse,
                "release archive entry",
                route: null,
                relativePath,
                fullEntryPath,
                operation);
        }
    }

    internal static bool IsPathUnderRoot(string path, string root, StringComparison comparison)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(root);
        if (string.Equals(normalizedPath, normalizedRoot, comparison))
        {
            return true;
        }

        var rootPrefix = EnsureTrailingDirectorySeparator(normalizedRoot, Path.DirectorySeparatorChar);
        if (normalizedPath.StartsWith(rootPrefix, comparison))
        {
            return true;
        }

        return Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar
               && normalizedPath.StartsWith(
                   EnsureTrailingDirectorySeparator(normalizedRoot, Path.AltDirectorySeparatorChar),
                   comparison);
    }

    internal static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private static void RejectExistingOutputRootSegments(
        string outputRoot,
        string artifactKind,
        string? route,
        string relativePath,
        string operation)
    {
        var root = Path.GetPathRoot(outputRoot);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var cursor = Path.TrimEndingDirectorySeparator(root);
        if (string.IsNullOrWhiteSpace(cursor))
        {
            cursor = root;
        }

        var relative = Path.GetRelativePath(cursor, outputRoot);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
        {
            ValidateOutputRootSegment(cursor, artifactKind, route, relativePath, operation);
            return;
        }

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Join(cursor, segment);
            if (!TryGetExistingFileSystemInfo(cursor, out var info))
            {
                break;
            }

            if (IsAllowedPlatformRootAlias(cursor, info))
            {
                continue;
            }

            if (IsReparseOrLink(info))
            {
                throw CreateException(OutputRootReparse, artifactKind, route, relativePath, cursor, operation);
            }
        }
    }

    private static void ValidateOutputRootSegment(
        string cursor,
        string artifactKind,
        string? route,
        string relativePath,
        string operation)
    {
        if (!TryGetExistingFileSystemInfo(cursor, out var info)
            || IsAllowedPlatformRootAlias(cursor, info)
            || !IsReparseOrLink(info))
        {
            return;
        }

        throw CreateException(OutputRootReparse, artifactKind, route, relativePath, cursor, operation);
    }

    private static void RejectExistingReparseSegments(
        string outputRoot,
        string path,
        string reason,
        string artifactKind,
        string? route,
        string relativePath,
        string operation)
    {
        var fullPath = NormalizePath(path);
        var root = NormalizePath(outputRoot);
        if (TryGetExistingFileSystemInfo(root, out var rootInfo) && IsReparseOrLink(rootInfo))
        {
            throw CreateException(OutputRootReparse, artifactKind, route, relativePath, root, operation);
        }

        var relative = Path.GetRelativePath(root, fullPath);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
        {
            return;
        }

        var cursor = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            cursor = Path.Join(cursor, segment);
            if (!TryGetExistingFileSystemInfo(cursor, out var info))
            {
                break;
            }

            if (IsReparseOrLink(info))
            {
                throw CreateException(reason, artifactKind, route, relativePath, cursor, operation);
            }
        }
    }

    private static bool TryGetExistingFileSystemInfo(string path, out FileSystemInfo info)
    {
        var fileInfo = new FileInfo(path);
        if (TryIsReparseOrLink(fileInfo))
        {
            info = fileInfo;
            return true;
        }

        var directoryInfo = new DirectoryInfo(path);
        if (TryIsReparseOrLink(directoryInfo))
        {
            info = directoryInfo;
            return true;
        }

        if (File.Exists(path))
        {
            info = fileInfo;
            return true;
        }

        if (Directory.Exists(path))
        {
            info = directoryInfo;
            return true;
        }

        info = null!;
        return false;
    }

    private static bool TryIsReparseOrLink(FileSystemInfo info)
    {
        try
        {
            return IsReparseOrLink(info);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsAllowedPlatformRootAlias(string path, FileSystemInfo info)
    {
        if (!OperatingSystem.IsMacOS() || !IsReparseOrLink(info))
        {
            return false;
        }

        var normalized = NormalizePath(path);
        var linkTarget = info.LinkTarget;
        if (string.IsNullOrWhiteSpace(linkTarget))
        {
            return false;
        }

        return (string.Equals(normalized, "/var", StringComparison.Ordinal) && IsLinkTarget(linkTarget, "private/var"))
               || (string.Equals(normalized, "/tmp", StringComparison.Ordinal) && IsLinkTarget(linkTarget, "private/tmp"));
    }

    private static bool IsLinkTarget(string linkTarget, string expected)
    {
        return string.Equals(linkTarget.TrimStart('/'), expected, StringComparison.Ordinal);
    }

    private static bool IsReparseOrLink(FileSystemInfo info)
    {
        return !string.IsNullOrEmpty(info.LinkTarget)
               || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static ExportValidationException CreateException(
        string reason,
        string artifactKind,
        string? route,
        string relativePath,
        string unsafeSegment,
        string operation)
    {
        var effectiveRoute = string.IsNullOrWhiteSpace(route) || !route.TrimStart().StartsWith("/", StringComparison.Ordinal)
            ? "/"
            : route.Trim();
        var routeDetail = string.IsNullOrWhiteSpace(route) ? "n/a" : effectiveRoute;
        var message =
            $"[{reason}] Generated export artifact boundary blocked a filesystem operation. "
            + $"Cause: {DescribeReason(reason)} "
            + "Fix: export to a regular directory tree, remove the symlink/junction/reparse point, or choose an output path inside the export root. "
            + $"Artifact kind: {artifactKind}. "
            + $"Route: {routeDetail}. "
            + $"Output-relative path: {relativePath}. "
            + $"Unsafe segment: {FormatUnsafeSegment(unsafeSegment)}. "
            + $"Operation: {operation}. "
            + $"Docs: {DocsAnchor}.";
        return new ExportValidationException([new ExportDiagnostic(DiagnosticCode, message, effectiveRoute)]);
    }

    private static string DescribeReason(string reason)
    {
        return reason switch
        {
            OutputRootReparse => "the output root or one of its existing segments is a symlink, junction, or reparse point.",
            ArtifactParentReparse => "an existing artifact parent segment is a symlink, junction, or reparse point.",
            ArtifactTargetReparse => "the artifact target already exists as a symlink, junction, or reparse point.",
            ArchiveEntryReparse => "a release archive entry is a symlink, junction, or reparse point.",
            ArtifactOutsideRoot => "the resolved artifact path escapes the configured output root.",
            _ => "the resolved artifact path violates the export output boundary."
        };
    }

    private static string FormatUnsafeSegment(string unsafeSegment)
    {
        return string.IsNullOrWhiteSpace(unsafeSegment)
            ? "n/a"
            : Path.GetFileName(unsafeSegment.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : unsafeSegment;
    }

    private static string NormalizeRelativePath(string outputRoot, string path)
    {
        var relativePath = Path.GetRelativePath(outputRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath;
    }

    private static string NormalizePath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string EnsureTrailingDirectorySeparator(string path, char separator)
    {
        return Path.EndsInDirectorySeparator(path)
            ? path
            : path + separator;
    }
}
