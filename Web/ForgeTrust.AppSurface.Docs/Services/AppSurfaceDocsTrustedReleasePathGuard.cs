using System.Security;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Validates physical paths that belong to the AppSurface Docs trusted release store.
/// </summary>
/// <remarks>
/// The guard is intentionally filesystem-backed and fail-closed. AppSurface Docs uses it before mounting published
/// release trees and before reading request-time files from a mounted tree so catalog metadata cannot redirect a public
/// docs route outside the operator-owned release store.
/// </remarks>
internal static class AppSurfaceDocsTrustedReleasePathGuard
{
    /// <summary>
    /// Gets the comparer used for canonical physical paths on the current platform.
    /// </summary>
    internal static StringComparer PhysicalPathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Gets the comparison mode used when checking whether one canonical physical path contains another.
    /// </summary>
    internal static StringComparison PhysicalPathComparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Converts a filesystem path to a full physical path and removes a trailing directory separator.
    /// </summary>
    /// <param name="path">The path to canonicalize with <see cref="Path.GetFullPath(string)"/>.</param>
    /// <returns>The canonical physical path without a trailing directory separator.</returns>
    internal static string NormalizePhysicalPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>
    /// Resolves the trusted release root from configuration, defaulting to the catalog directory when unset.
    /// </summary>
    /// <param name="contentRootPath">The application content root used to anchor relative configured paths.</param>
    /// <param name="configuredRootPath">The optional operator-configured trusted release root.</param>
    /// <param name="catalogDirectory">The catalog directory used as the default trusted root.</param>
    /// <returns>The canonical trusted release root path.</returns>
    internal static string ResolveConfiguredRoot(string contentRootPath, string? configuredRootPath, string catalogDirectory)
    {
        var rootPath = string.IsNullOrWhiteSpace(configuredRootPath)
            ? catalogDirectory
            : ResolveContentRootRelativePath(contentRootPath, configuredRootPath.Trim());
        return NormalizePhysicalPath(rootPath);
    }

    /// <summary>
    /// Resolves an operator-configured path, preserving rooted paths and anchoring relative paths under the content root.
    /// </summary>
    /// <param name="contentRootPath">The application content root used for relative configured paths.</param>
    /// <param name="configuredPath">The configured path after caller-side trimming.</param>
    /// <returns>The full filesystem path represented by <paramref name="configuredPath"/>.</returns>
    internal static string ResolveContentRootRelativePath(string contentRootPath, string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(configuredPath, contentRootPath);
    }

    /// <summary>
    /// Resolves a catalog <c>exactTreePath</c> under the trusted release root after rejecting unsafe metadata.
    /// </summary>
    /// <param name="trustedReleaseRootPath">The canonical trusted release root that must contain the release tree.</param>
    /// <param name="configuredExactTreePath">The catalog value to resolve. It must be relative, non-empty, and free of hidden or parent-traversal segments.</param>
    /// <param name="exactTreePath">Receives the canonical physical release tree path when resolution succeeds.</param>
    /// <param name="publicIssue">Receives the user-safe availability message when resolution fails.</param>
    /// <param name="internalDetail">Receives diagnostic detail for logs and tests when resolution fails.</param>
    /// <returns><see langword="true"/> when the catalog path is safe to validate under the trusted root; otherwise, <see langword="false"/>.</returns>
    internal static bool TryResolveCatalogTreePath(
        string trustedReleaseRootPath,
        string? configuredExactTreePath,
        out string? exactTreePath,
        out string? publicIssue,
        out string? internalDetail)
    {
        exactTreePath = null;
        publicIssue = null;
        internalDetail = null;

        var trimmed = configuredExactTreePath?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            publicIssue = "Published release tree path is missing.";
            internalDetail = "ExactTreePath is missing.";
            return false;
        }

        try
        {
            if (Path.IsPathRooted(trimmed))
            {
                publicIssue = "Published release tree path must be relative to the trusted release root.";
                internalDetail = $"ExactTreePath '{trimmed}' is rooted. Set TrustedReleaseRootPath to the old parent directory and make exactTreePath relative.";
                return false;
            }

            if (ContainsParentTraversal(trimmed))
            {
                publicIssue = "Published release tree path must stay inside the trusted release root.";
                internalDetail = $"ExactTreePath '{trimmed}' contains parent-directory traversal.";
                return false;
            }

            if (ContainsHiddenSegment(trimmed))
            {
                publicIssue = "Published release tree path must not use hidden path segments.";
                internalDetail = $"ExactTreePath '{trimmed}' contains a hidden path segment.";
                return false;
            }

            exactTreePath = NormalizePhysicalPath(Path.Join(trustedReleaseRootPath, trimmed));
            return true;
        }
        catch (Exception ex) when (IsPathMetadataException(ex))
        {
            publicIssue = "Published release tree path is invalid.";
            internalDetail = $"ExactTreePath '{trimmed}' is invalid: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Validates that a physical directory exists and is not itself a symlink, junction, or reparse point.
    /// </summary>
    /// <param name="directoryPath">The physical directory path to inspect.</param>
    /// <param name="publicMissingIssue">The user-safe issue to report when the directory is missing.</param>
    /// <param name="publicUnsafeIssue">The user-safe issue to report when the directory is unsafe or cannot be inspected.</param>
    /// <param name="publicIssue">Receives the user-safe issue when validation fails.</param>
    /// <param name="internalDetail">Receives diagnostic detail for logs and tests when validation fails.</param>
    /// <returns><see langword="true"/> when the directory exists and is ordinary; otherwise, <see langword="false"/>.</returns>
    internal static bool TryValidateDirectory(
        string directoryPath,
        string publicMissingIssue,
        string publicUnsafeIssue,
        out string? publicIssue,
        out string? internalDetail)
    {
        publicIssue = null;
        internalDetail = null;

        try
        {
            var info = new DirectoryInfo(directoryPath);
            if (!info.Exists)
            {
                publicIssue = publicMissingIssue;
                internalDetail = $"Directory '{directoryPath}' does not exist.";
                return false;
            }

            if (IsLinkOrReparsePoint(info))
            {
                publicIssue = publicUnsafeIssue;
                internalDetail = $"Directory '{directoryPath}' is a symlink, junction, or reparse point.";
                return false;
            }
        }
        catch (Exception ex) when (IsPathMetadataException(ex))
        {
            publicIssue = publicUnsafeIssue;
            internalDetail = $"Directory '{directoryPath}' could not be inspected: {ex.Message}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves and validates a file candidate beneath an exact release tree root.
    /// </summary>
    /// <param name="exactTreeRootPath">The canonical release tree root that must contain the candidate file.</param>
    /// <param name="relativeFilePath">The relative file path requested from the release tree. Rooted paths and parent traversal are rejected.</param>
    /// <param name="physicalFilePath">Receives the canonical physical file path when the relative path can be resolved.</param>
    /// <param name="denialReason">Receives the internal reason when the candidate is unsafe, missing, or unreadable.</param>
    /// <returns><see langword="true"/> when the file candidate stays under the tree and has no reparse segments; otherwise, <see langword="false"/>.</returns>
    internal static bool TryValidateFileCandidate(
        string exactTreeRootPath,
        string relativeFilePath,
        out string physicalFilePath,
        out string? denialReason)
    {
        physicalFilePath = string.Empty;
        denialReason = null;

        try
        {
            if (string.IsNullOrWhiteSpace(relativeFilePath)
                || Path.IsPathRooted(relativeFilePath)
                || ContainsParentTraversal(relativeFilePath))
            {
                denialReason = "candidate path is not a safe relative path.";
                return false;
            }

            physicalFilePath = NormalizePhysicalPath(Path.Join(exactTreeRootPath, relativeFilePath));
            return TryValidateNoReparseSegments(exactTreeRootPath, physicalFilePath, expectLeafFile: true, out denialReason);
        }
        catch (Exception ex) when (IsPathMetadataException(ex))
        {
            denialReason = $"candidate path could not be inspected: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Walks a candidate path from its trusted root and rejects any symlink, junction, or reparse point segment.
    /// </summary>
    /// <param name="trustedRootPath">The physical root that must contain <paramref name="candidatePath"/>.</param>
    /// <param name="candidatePath">The physical path to validate.</param>
    /// <param name="expectLeafFile">Whether the candidate leaf is expected to be a file instead of a directory.</param>
    /// <param name="denialReason">Receives the internal reason when containment or segment validation fails.</param>
    /// <returns><see langword="true"/> when every segment from root to candidate is ordinary; otherwise, <see langword="false"/>.</returns>
    internal static bool TryValidateNoReparseSegments(
        string trustedRootPath,
        string candidatePath,
        bool expectLeafFile,
        out string? denialReason)
    {
        denialReason = null;
        var normalizedRoot = NormalizePhysicalPath(trustedRootPath);
        var normalizedCandidate = NormalizePhysicalPath(candidatePath);
        if (!IsSameOrDescendant(normalizedRoot, normalizedCandidate))
        {
            denialReason = "path resolves outside the trusted root.";
            return false;
        }

        if (!TryValidateSegment(normalizedRoot, expectFile: false, out denialReason))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            return true;
        }

        var current = normalizedRoot;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment) || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            current = Path.Join(current, segment);
            var isLeaf = string.Equals(
                NormalizePhysicalPath(current),
                normalizedCandidate,
                PhysicalPathComparison);
            var isFile = isLeaf && expectLeafFile;
            if (!TryValidateSegment(current, isFile, out denialReason))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether a canonical candidate path is equal to or physically beneath a trusted root path.
    /// </summary>
    /// <param name="trustedRootPath">The trusted root path to compare after canonicalization.</param>
    /// <param name="candidatePath">The candidate path to compare after canonicalization.</param>
    /// <returns><see langword="true"/> when the candidate equals the root or starts below it using platform path comparison semantics.</returns>
    internal static bool IsSameOrDescendant(string trustedRootPath, string candidatePath)
    {
        var root = NormalizePhysicalPath(trustedRootPath);
        var candidate = NormalizePhysicalPath(candidatePath);
        if (string.Equals(root, candidate, PhysicalPathComparison))
        {
            return true;
        }

        var filesystemRoot = Path.GetPathRoot(root);
        var rootWithSeparator = string.Equals(filesystemRoot, root, PhysicalPathComparison)
            || root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, PhysicalPathComparison);
    }

    /// <summary>
    /// Classifies exceptions that can occur while normalizing paths or reading filesystem metadata.
    /// </summary>
    /// <param name="ex">The exception thrown by path or metadata access.</param>
    /// <returns><see langword="true"/> when callers should convert the exception into a fail-closed validation denial.</returns>
    internal static bool IsPathMetadataException(Exception ex)
    {
        return ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or SecurityException
            or PathTooLongException
            or NotSupportedException;
    }

    /// <summary>
    /// Validates one physical path segment as an ordinary file or directory.
    /// </summary>
    /// <param name="path">The physical path segment to inspect.</param>
    /// <param name="expectFile">Whether the path is expected to be a file; otherwise a directory is expected.</param>
    /// <param name="denialReason">Receives the internal reason when the segment is missing, a reparse point, or unreadable.</param>
    /// <returns><see langword="true"/> when the segment exists and is not a link or reparse point; otherwise, <see langword="false"/>.</returns>
    private static bool TryValidateSegment(string path, bool expectFile, out string? denialReason)
    {
        denialReason = null;

        try
        {
            FileSystemInfo info = expectFile ? new FileInfo(path) : new DirectoryInfo(path);
            if (!info.Exists)
            {
                denialReason = $"path segment '{path}' does not exist.";
                return false;
            }

            if (IsLinkOrReparsePoint(info))
            {
                denialReason = $"path segment '{path}' is a symlink, junction, or reparse point.";
                return false;
            }
        }
        catch (Exception ex) when (IsPathMetadataException(ex))
        {
            denialReason = $"path segment '{path}' could not be inspected: {ex.Message}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Detects whether a filesystem entry is a symlink, junction, or other reparse point.
    /// </summary>
    /// <param name="info">The filesystem metadata entry to inspect.</param>
    /// <returns><see langword="true"/> when the entry has reparse attributes or a link target.</returns>
    private static bool IsLinkOrReparsePoint(FileSystemInfo info)
    {
        return (info.Attributes & FileAttributes.ReparsePoint) != 0
               || !string.IsNullOrEmpty(info.LinkTarget);
    }

    /// <summary>
    /// Checks catalog metadata for parent-directory traversal segments.
    /// </summary>
    /// <param name="path">The catalog path value to inspect.</param>
    /// <returns><see langword="true"/> when any path segment is exactly <c>..</c>.</returns>
    private static bool ContainsParentTraversal(string path)
    {
        return path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal));
    }

    /// <summary>
    /// Checks catalog metadata for hidden release tree segments while allowing a leading current-directory marker.
    /// </summary>
    /// <param name="path">The catalog path value to inspect.</param>
    /// <returns><see langword="true"/> when any non-current-directory segment starts with <c>.</c>.</returns>
    private static bool ContainsHiddenSegment(string path)
    {
        return path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.StartsWith(".", StringComparison.Ordinal)
                && !string.Equals(segment, ".", StringComparison.Ordinal));
    }
}
