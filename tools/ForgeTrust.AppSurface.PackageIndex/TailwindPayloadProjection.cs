using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Deterministic v1 projection of package build/runtime assets and files selected by the fixed assets graph.</summary>
internal static class TailwindPayloadProjection
{
    private static readonly string[] ProtectedRoots = ["build", "buildTransitive", "buildMultiTargeting", "lib", "ref", "analyzers", "tools", "tasks", "runtimes", "contentFiles"];
    private static readonly HashSet<string> FileGroups = new(StringComparer.Ordinal)
        { "compile", "runtime", "native", "resource", "build", "buildMultiTargeting", "contentFiles", "runtimeTargets" };
    private static readonly HashSet<string> MetadataGroups = new(StringComparer.Ordinal)
        { "type", "framework", "dependencies", "frameworkAssemblies", "frameworkReferences", "compileOnly" };

    internal static IReadOnlyDictionary<string, string> Verify(
        string archivePath,
        string restoredPackageDirectory,
        JsonElement targetNode,
        CancellationToken cancellationToken)
    {
        var selected = ReadSelectedAssetPaths(targetNode);
        var archiveFiles = ReadArchiveFileHashes(archivePath, selected, cancellationToken);
        var restoredFiles = ReadRestoredFileHashes(restoredPackageDirectory, selected, cancellationToken);
        if (archiveFiles.Count != restoredFiles.Count)
            throw new PackageIndexException($"Expanded first-party package payload file count differs from archive ({archiveFiles.Count} expected, {restoredFiles.Count} restored).");
        foreach (var (path, expectedHash) in archiveFiles)
        {
            if (!restoredFiles.TryGetValue(path, out var actualHash))
                throw new PackageIndexException($"Expanded first-party package payload is missing '{path}'.");
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                throw new PackageIndexException($"Expanded first-party package payload bytes changed at '{path}'.");
        }
        return archiveFiles;
    }

    private static HashSet<string> ReadSelectedAssetPaths(JsonElement targetNode)
    {
        if (targetNode.ValueKind != JsonValueKind.Object) throw new PackageIndexException("First-party assets target node must be an object.");
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in targetNode.EnumerateObject())
        {
            if (MetadataGroups.Contains(property.Name))
            {
                ValidateMetadata(property.Name, property.Value);
                continue;
            }
            if (!FileGroups.Contains(property.Name)) throw new PackageIndexException($"Unsupported NuGet assets group '{property.Name}' in first-party target node.");
            if (property.Value.ValueKind != JsonValueKind.Object) throw new PackageIndexException($"NuGet assets group '{property.Name}' must be a path-keyed object.");
            foreach (var file in property.Value.EnumerateObject())
            {
                var normalized = TailwindProofSubjectService.NormalizeArchivePath(file.Name);
                if (property.Name == "runtimeTargets")
                {
                    if (file.Value.ValueKind != JsonValueKind.Object
                        || !file.Value.TryGetProperty("assetType", out var type)
                        || type.ValueKind != JsonValueKind.String
                        || type.GetString() is not ("runtime" or "native" or "resources")
                        || !file.Value.TryGetProperty("rid", out var rid)
                        || rid.ValueKind != JsonValueKind.String)
                        throw new PackageIndexException($"Malformed runtimeTargets metadata for '{file.Name}'.");
                }
                else if (file.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.String)
                    throw new PackageIndexException($"Malformed path metadata for '{property.Name}/{file.Name}'.");
                if (paths.TryGetValue(normalized, out var originalPath)
                    && !string.Equals(originalPath, normalized, StringComparison.Ordinal))
                    throw new PackageIndexException($"Case-colliding asset paths '{originalPath}' and '{normalized}'.");
                paths.TryAdd(normalized, normalized);
            }
        }
        return paths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateMetadata(string name, JsonElement value)
    {
        if (name is "type" or "framework")
        {
            if (value.ValueKind != JsonValueKind.String) throw new PackageIndexException($"Target node metadata '{name}' must be a string.");
        }
        else if (name == "dependencies")
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.String))
                throw new PackageIndexException("Target node dependency metadata must map package IDs to version strings.");
        }
        else if (name is "frameworkAssemblies" or "frameworkReferences")
        {
            if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new PackageIndexException($"Target node metadata '{name}' must be an array of strings.");
        }
        else if (name == "compileOnly" && value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw new PackageIndexException("Target node metadata 'compileOnly' must be boolean.");
    }

    private static Dictionary<string, string> ReadArchiveFileHashes(string archivePath, HashSet<string> selected, CancellationToken token)
    {
        var info = new FileInfo(archivePath);
        if (info.Length > 1024L * 1024 * 1024) throw new PackageIndexException("First-party package archive exceeds the 1 GiB limit.");
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 100_000) throw new PackageIndexException("First-party package archive exceeds the 100,000-entry limit.");
        var allNames = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var raw = isDirectory ? entry.FullName[..^1] : entry.FullName;
            var path = TailwindProofSubjectService.NormalizeArchivePath(raw);
            if (!allNames.TryAdd(path, isDirectory)) throw new PackageIndexException($"Duplicate or case-colliding package ZIP path '{entry.FullName}'.");
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!isDirectory && type == 0xA000) throw new PackageIndexException($"First-party archive contains symlink '{path}'.");
            if (isDirectory) continue;
            var protectedPath = IsProtected(path) || selected.Contains(path);
            if (!protectedPath) continue;
            using var stream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long actual = 0;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                actual = checked(actual + read);
                expanded = checked(expanded + read);
                if (expanded > 4L * 1024 * 1024 * 1024 || actual > entry.Length) throw new PackageIndexException("Protected package payload exceeded its declared or aggregate expanded-size limit.");
                hash.AppendData(buffer, 0, read);
            }
            if (actual != entry.Length) throw new PackageIndexException($"Protected ZIP entry '{path}' expanded to an unexpected length.");
            result.Add(path, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        foreach (var path in allNames.Keys)
            foreach (var ancestor in GetAncestors(path))
                if (allNames.TryGetValue(ancestor, out var isDirectory) && !isDirectory)
                    throw new PackageIndexException($"ZIP file/directory collision at '{ancestor}'.");
        foreach (var selectedPath in selected)
            if (!result.ContainsKey(selectedPath)) throw new PackageIndexException($"Assets graph references missing archive entry '{selectedPath}'.");
        return result;
    }

    private static Dictionary<string, string> ReadRestoredFileHashes(string packageDirectory, HashSet<string> selected, CancellationToken token)
    {
        var root = Path.GetFullPath(packageDirectory);
        RequireDirectory(root);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in selected)
        {
            token.ThrowIfCancellationRequested();
            var actual = FindCaseInsensitivePath(root, path);
            if (actual is null) throw new PackageIndexException($"Restored package is missing assets-graph path '{path}'.");
            found[path] = HashFile(actual, token);
        }
        foreach (var protectedRoot in ProtectedRoots)
        {
            var directory = FindCaseInsensitivePath(root, protectedRoot);
            if (directory is null || !Directory.Exists(directory)) continue;
            WalkProtectedDirectory(root, directory, found, observed, token);
        }
        return found;
    }

    private static void WalkProtectedDirectory(string root, string directory, Dictionary<string, string> result,
        HashSet<string> observed, CancellationToken token)
    {
        RequireNotLink(directory);
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            RequireNotLink(child);
            var relative = Path.GetRelativePath(root, child).Replace('\\', '/');
            var normalized = TailwindProofSubjectService.NormalizeArchivePath(relative);
            if (!observed.Add(normalized))
                throw new PackageIndexException($"Restored protected payload contains a case-colliding path '{normalized}'.");
            if (Directory.Exists(child)) WalkProtectedDirectory(root, child, result, observed, token);
            else if (!result.ContainsKey(normalized)) result[normalized] = HashFile(child, token);
        }
    }

    private static string? FindCaseInsensitivePath(string root, string relative)
    {
        var current = root;
        foreach (var component in relative.Split('/'))
        {
            if (!Directory.Exists(current)) return null;
            var matches = Directory.EnumerateFileSystemEntries(current)
                .Where(path => string.Equals(Path.GetFileName(path), component, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length > 1) throw new PackageIndexException($"Restored package contains ambiguous case-insensitive path component '{component}'.");
            if (matches.Length == 0) return null;
            RequireNotLink(matches[0]);
            current = matches[0];
        }
        return current;
    }

    private static void RequireDirectory(string directory)
    {
        if (!Directory.Exists(directory)) throw new PackageIndexException($"Restored package directory '{directory}' does not exist.");
        RequireNotLink(directory);
    }

    private static void RequireNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new PackageIndexException($"Protected restored payload contains link/reparse point '{path}'.");
    }

    private static string HashFile(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total = checked(total + read);
            if (total > 4L * 1024 * 1024 * 1024) throw new PackageIndexException("Restored protected payload exceeds the 4 GiB size limit.");
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsProtected(string path) => ProtectedRoots.Any(root => path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    private static IEnumerable<string> GetAncestors(string path)
    {
        var slash = path.IndexOf('/');
        while (slash >= 0) { yield return path[..slash]; slash = path.IndexOf('/', slash + 1); }
    }
}
