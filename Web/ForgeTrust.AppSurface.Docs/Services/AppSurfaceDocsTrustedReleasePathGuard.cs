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
    internal static StringComparer PhysicalPathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static StringComparison PhysicalPathComparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    internal static string NormalizePhysicalPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static string ResolveConfiguredRoot(string contentRootPath, string? configuredRootPath, string catalogDirectory)
    {
        var rootPath = string.IsNullOrWhiteSpace(configuredRootPath)
            ? catalogDirectory
            : ResolveContentRootRelativePath(contentRootPath, configuredRootPath.Trim());
        return NormalizePhysicalPath(rootPath);
    }

    internal static string ResolveContentRootRelativePath(string contentRootPath, string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }

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

            var candidatePath = NormalizePhysicalPath(Path.Combine(trustedReleaseRootPath, trimmed));
            if (!IsSameOrDescendant(trustedReleaseRootPath, candidatePath))
            {
                publicIssue = "Published release tree path must stay inside the trusted release root.";
                internalDetail = $"ExactTreePath '{trimmed}' resolves outside TrustedReleaseRootPath '{trustedReleaseRootPath}'.";
                return false;
            }

            exactTreePath = candidatePath;
            return true;
        }
        catch (Exception ex) when (IsPathMetadataException(ex))
        {
            publicIssue = "Published release tree path is invalid.";
            internalDetail = $"ExactTreePath '{trimmed}' is invalid: {ex.Message}";
            return false;
        }
    }

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

            physicalFilePath = NormalizePhysicalPath(Path.Combine(exactTreeRootPath, relativeFilePath));
            if (!IsSameOrDescendant(exactTreeRootPath, physicalFilePath))
            {
                denialReason = "candidate path resolves outside the published tree root.";
                return false;
            }

            return TryValidateNoReparseSegments(exactTreeRootPath, physicalFilePath, expectLeafFile: true, out denialReason);
        }
        catch (Exception ex) when (IsPathMetadataException(ex))
        {
            denialReason = $"candidate path could not be inspected: {ex.Message}";
            return false;
        }
    }

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

            current = Path.Combine(current, segment);
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

    internal static bool IsSameOrDescendant(string trustedRootPath, string candidatePath)
    {
        var root = NormalizePhysicalPath(trustedRootPath);
        var candidate = NormalizePhysicalPath(candidatePath);
        if (string.Equals(root, candidate, PhysicalPathComparison))
        {
            return true;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, PhysicalPathComparison);
    }

    internal static bool IsPathMetadataException(Exception ex)
    {
        return ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or SecurityException
            or PathTooLongException
            or NotSupportedException;
    }

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

    private static bool IsLinkOrReparsePoint(FileSystemInfo info)
    {
        return (info.Attributes & FileAttributes.ReparsePoint) != 0
               || !string.IsNullOrEmpty(info.LinkTarget);
    }

    private static bool ContainsParentTraversal(string path)
    {
        return path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal));
    }
}
