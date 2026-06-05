using System.Diagnostics.CodeAnalysis;

namespace ForgeTrust.RazorWire.Cli;

internal static class ExportDeploymentExtras
{
    internal const string DiagnosticCode = "RWEXPORT007";
    internal const string RouteFallback = "/";

    private static readonly HashSet<string> ReservedPublishPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/_redirects",
        "/_headers",
        "/.appsurface-docs-route-manifest.json",
        "/.appsurface-docs-release-manifest.json"
    };

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    internal static ExportDeploymentExtra CreateRegisteredExtra(string sourcePath, string publishPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw CreateException(
                "source-outside-root",
                $"Publish-root deployment extra source '{sourcePath}' must be an absolute resolved file path for programmatic registration. Fix: pass a fully qualified source path.",
                publishPath);
        }

        var normalizedSource = Path.GetFullPath(sourcePath);
        ValidateRegularSourceFile(normalizedSource, sourceRoot: null, manifestPath: null, entryIndex: null, publishPath);
        return new ExportDeploymentExtra(normalizedSource, NormalizePublishPath(publishPath, manifestPath: null, entryIndex: null));
    }

    internal static ExportDeploymentExtra CreateManifestExtra(
        string manifestPath,
        int entryIndex,
        string manifestDirectory,
        string source,
        string publishPath)
    {
        if (Path.IsPathFullyQualified(source) || Path.IsPathRooted(source))
        {
            throw CreateException(
                "source-outside-root",
                FormatMessage(
                    manifestPath,
                    entryIndex,
                    $"Source '{source}' must be relative to the manifest directory. Fix: use a relative source path such as 'CNAME'."),
                publishPath);
        }

        var resolvedSource = Path.GetFullPath(Path.Combine(manifestDirectory, source));
        ValidateRegularSourceFile(resolvedSource, manifestDirectory, manifestPath, entryIndex, publishPath);
        return new ExportDeploymentExtra(resolvedSource, NormalizePublishPath(publishPath, manifestPath, entryIndex));
    }

    internal static string NormalizePublishPath(string publishPath, string? manifestPath, int? entryIndex)
    {
        if (!TryNormalizePublishPath(publishPath, out var normalized, out var message))
        {
            throw CreateException(
                "target-invalid",
                FormatMessage(manifestPath, entryIndex, message ?? "Publish-root deployment extra path is invalid."),
                RouteFallback);
        }

        if (ReservedPublishPaths.Contains(normalized))
        {
            throw CreateException(
                "target-reserved",
                FormatMessage(
                    manifestPath,
                    entryIndex,
                    $"Publish-root deployment extra target '{normalized}' is reserved by the exporter. Fix: use the exporter-owned redirect/header/archive surface instead of copying this provider or archive file."),
                normalized);
        }

        return normalized;
    }

    internal static bool TryNormalizePublishPath(
        string? publishPath,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? message)
    {
        normalized = null;
        message = null;

        if (string.IsNullOrWhiteSpace(publishPath))
        {
            message = "Publish-root deployment extra publishPath is required. Fix: set publishPath to a root-relative file path such as '/CNAME'.";
            return false;
        }

        var candidate = publishPath.Trim();
        if (!candidate.StartsWith("/", StringComparison.Ordinal)
            || candidate.StartsWith("//", StringComparison.Ordinal))
        {
            message = $"Publish-root deployment extra publishPath '{publishPath}' must start with one '/' and must not be protocol-relative. Fix: use a publish-root path such as '/CNAME'.";
            return false;
        }

        if (candidate.Length == 1 || candidate.EndsWith("/", StringComparison.Ordinal))
        {
            message = $"Publish-root deployment extra publishPath '{publishPath}' must name a file, not the publish root or a directory. Fix: use a file path such as '/CNAME'.";
            return false;
        }

        if (candidate.Contains('\\', StringComparison.Ordinal)
            || candidate.Contains('?', StringComparison.Ordinal)
            || candidate.Contains('#', StringComparison.Ordinal)
            || candidate.Contains(':', StringComparison.Ordinal)
            || candidate.Contains("//", StringComparison.Ordinal)
            || candidate.Any(char.IsControl))
        {
            message = $"Publish-root deployment extra publishPath '{publishPath}' contains unsupported URL or filesystem characters. Fix: use a clean publish-root file path such as '/.well-known/security.txt'.";
            return false;
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                message = $"Publish-root deployment extra publishPath '{publishPath}' must not contain traversal segments. Fix: remove '.' or '..' path segments.";
                return false;
            }

            string unescaped;
            try
            {
                unescaped = Uri.UnescapeDataString(segment);
            }
            catch (UriFormatException ex)
            {
                message = $"Publish-root deployment extra publishPath '{publishPath}' contains invalid percent encoding: {ex.Message}";
                return false;
            }

            if (unescaped is "." or ".."
                || unescaped.Contains('/', StringComparison.Ordinal)
                || unescaped.Contains('\\', StringComparison.Ordinal)
                || unescaped.Any(char.IsControl))
            {
                message = $"Publish-root deployment extra publishPath '{publishPath}' contains encoded traversal or separators. Fix: use literal safe path segments.";
                return false;
            }

            var deviceName = unescaped.Split('.', 2)[0];
            if (WindowsReservedNames.Contains(deviceName))
            {
                message = $"Publish-root deployment extra publishPath '{publishPath}' uses reserved device name '{deviceName}'. Fix: choose a different file name.";
                return false;
            }
        }

        normalized = "/" + string.Join('/', segments);
        return true;
    }

    internal static string MapPublishPathToFilePath(string outputPath, string publishPath)
    {
        var outputRoot = Path.GetFullPath(outputPath);
        var relativeSegments = publishPath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var candidate = outputRoot;
        foreach (var segment in relativeSegments)
        {
            candidate = Path.Join(candidate, segment);
        }

        var fullPath = Path.GetFullPath(candidate);
        if (!IsPathUnderRoot(fullPath, outputRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateException(
                "target-invalid",
                $"Publish-root deployment extra target '{publishPath}' escaped the output root. Fix: use a publish-root file path without traversal.",
                publishPath);
        }

        return fullPath;
    }

    internal static void ValidateTargetParentPath(string outputPath, string targetPath, string publishPath)
    {
        var outputRoot = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }

        if (!IsPathUnderRoot(parent, outputRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateException(
                "target-invalid",
                $"Publish-root deployment extra target '{publishPath}' escaped the output root. Fix: use a publish-root file path without traversal.",
                publishPath);
        }

        RejectExistingReparseSegments(outputRoot, parent, "target-parent-symlink", publishPath);
    }

    internal static ExportValidationException CreateException(string reason, string message, string? route)
    {
        return new ExportValidationException([CreateDiagnostic(reason, message, route)]);
    }

    internal static ExportDiagnostic CreateDiagnostic(string reason, string message, string? route)
    {
        var effectiveRoute = string.IsNullOrWhiteSpace(route) || !route.TrimStart().StartsWith("/", StringComparison.Ordinal)
            ? RouteFallback
            : route.Trim();
        return new ExportDiagnostic(DiagnosticCode, $"[{reason}] {message}", effectiveRoute);
    }

    internal static string FormatMessage(string? manifestPath, int? entryIndex, string message)
    {
        if (manifestPath is null)
        {
            return message;
        }

        var entry = entryIndex is null ? string.Empty : $" entry {entryIndex.Value}";
        return $"Manifest '{manifestPath}'{entry}: {message}";
    }

    private static void ValidateRegularSourceFile(
        string sourcePath,
        string? sourceRoot,
        string? manifestPath,
        int? entryIndex,
        string? publishPath)
    {
        if (sourceRoot is not null && !IsPathUnderRoot(sourcePath, Path.GetFullPath(sourceRoot), GetPathComparison()))
        {
            throw CreateException(
                "source-outside-root",
                FormatMessage(
                    manifestPath,
                    entryIndex,
                    $"Source '{sourcePath}' must stay under the manifest directory. Fix: move the file beside the manifest or use a project-root manifest."),
                publishPath);
        }

        if (Directory.Exists(sourcePath))
        {
            throw CreateException(
                "source-directory",
                FormatMessage(
                    manifestPath,
                    entryIndex,
                    $"Source '{sourcePath}' is a directory. Fix: list explicit single files only."),
                publishPath);
        }

        if (!File.Exists(sourcePath))
        {
            throw CreateException(
                "source-missing",
                FormatMessage(
                    manifestPath,
                    entryIndex,
                    $"Source '{sourcePath}' does not exist. Fix: create the file or correct the source path."),
                publishPath);
        }

        if (sourceRoot is not null)
        {
            RejectExistingReparseSegments(sourceRoot, sourcePath, "source-symlink", publishPath, manifestPath, entryIndex);
        }

        var info = new FileInfo(sourcePath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || !string.IsNullOrEmpty(info.LinkTarget))
        {
            throw CreateException(
                "source-symlink",
                FormatMessage(
                    manifestPath,
                    entryIndex,
                    $"Source '{sourcePath}' is a symlink, junction, or reparse point. Fix: use a regular file."),
                publishPath);
        }

        if ((info.Attributes & FileAttributes.Directory) != 0)
        {
            throw CreateException(
                "source-directory",
                FormatMessage(
                    manifestPath,
                    entryIndex,
                    $"Source '{sourcePath}' is a directory. Fix: list explicit single files only."),
                publishPath);
        }
    }

    private static void RejectExistingReparseSegments(
        string? rootPath,
        string path,
        string reason,
        string? publishPath,
        string? manifestPath = null,
        int? entryIndex = null)
    {
        var fullPath = Path.GetFullPath(path);
        var root = rootPath is null ? Path.GetPathRoot(fullPath) : Path.GetFullPath(rootPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        if (File.Exists(root) || Directory.Exists(root))
        {
            FileSystemInfo rootInfo = File.Exists(root)
                ? new FileInfo(root)
                : new DirectoryInfo(root);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0 || !string.IsNullOrEmpty(rootInfo.LinkTarget))
            {
                throw CreateException(
                    reason,
                    FormatMessage(
                        manifestPath,
                        entryIndex,
                        $"Path segment '{root}' is a symlink, junction, or reparse point. Fix: use regular files and directories only."),
                    publishPath);
            }
        }

        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return;
        }

        var cursor = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Path.IsPathRooted(segment))
            {
                throw CreateException(
                    reason,
                    FormatMessage(
                        manifestPath,
                        entryIndex,
                        $"Path segment '{segment}' is rooted. Fix: use regular relative file and directory segments only."),
                    publishPath);
            }

            cursor = Path.Combine(cursor, segment);
            if (!File.Exists(cursor) && !Directory.Exists(cursor))
            {
                break;
            }

            FileSystemInfo info = File.Exists(cursor)
                ? new FileInfo(cursor)
                : new DirectoryInfo(cursor);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || !string.IsNullOrEmpty(info.LinkTarget))
            {
                throw CreateException(
                    reason,
                    FormatMessage(
                        manifestPath,
                        entryIndex,
                        $"Path segment '{cursor}' is a symlink, junction, or reparse point. Fix: use regular files and directories only."),
                    publishPath);
            }
        }
    }

    private static bool IsPathUnderRoot(string path, string root, StringComparison comparison)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedPath, normalizedRoot, comparison)
               || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)
               || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}
