using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

internal enum ContextPlatform
{
    LinuxArm64,
    LinuxAmd64
}

internal sealed record ContextSourceRoot(string RootPath, ImmutableArray<ContextSourceFile> Files, ImmutableArray<ContextSourceDirectory> Directories);
internal sealed record ContextSourceFile(string RelativePath, string Sha256, long Length, UnixFileMode Mode);
internal sealed record ContextSourceDirectory(string RelativePath, UnixFileMode Mode);
internal sealed record ContextOciLayout(string Name, string RootPath, string ExpectedManifest, string ExpectedSha256);
internal sealed record ContextDeb(string Name, string Path, string Sha256);
internal sealed record ContextReplacement(string Destination, string OriginalSha256, string ReplacementPrivatePath, string ReplacementSha256);
internal sealed record ContextThemeInput(string ImagePath, string SourcePath, string Sha256, UnixFileMode Mode);
internal sealed record ContextHostInput(string ImagePath, string SourcePath, string Sha256, UnixFileMode Mode);
internal sealed record ContextMaterializationInput(ContextSourceRoot Source, ContextOciLayout Runtime, ContextOciLayout Frontend, ImmutableArray<ContextDeb> CapturedDebs, ImmutableArray<ContextReplacement> Replacements, ArchiveTransformSpec? Archive, ImmutableArray<ContextThemeInput> Themes, ContextHostInput? Entrypoint, ContextPlatform Platform, string KeycloakVersion = "26.7.2", string? ArchiveDestination = null, bool ThemeOnly = false, string? ExpectedRepairedCliSha256 = null);
internal sealed record ContextMaterializationResult(string ContextPath, string ContextDigest, string ManifestDigest, int FileCount, long VerifiedBytes, string? RepairedCliSha256, string Entrypoint, ContextPlatform Platform, ImmutableDictionary<string, string> ExpectedFinalFiles, ImmutableArray<ContextSourceDirectory> FinalDirectories);
internal sealed record ContextMaterializationLimits(long MaxFileBytes = 1024L * 1024 * 1024, long MaxAggregateBytes = 1024L * 1024 * 1024, int MaxFiles = 65536, TimeSpan Deadline = default)
{
    internal TimeSpan EffectiveDeadline => Deadline == default ? TimeSpan.FromSeconds(300) : Deadline;
}

internal static class ContextMaterialization
{
    private const string RootPrefix = "rootfs/opt/keycloak/";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false
    };
    internal static async Task<ContextMaterializationResult> MaterializeAsync(ContextMaterializationInput input, string absentDestination, CancellationToken cancellationToken = default, ContextMaterializationLimits? limits = null)
    {
        limits ??= new ContextMaterializationLimits();
        ValidateInput(input, limits);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.EffectiveDeadline);
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(absentDestination);
        Require(!Path.Exists(destination), "Context destination must be absent.");
        RequireSafeParent(destination);
        var stage = destination + ".owned-" + Guid.NewGuid().ToString("N");
        var verified = 0L;
        var files = 0;
        string? repaired = null;
        try
        {
            Directory.CreateDirectory(stage);
            foreach (var directory in input.Source.Directories.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var target = Rooted(stage, RootPrefix + directory.RelativePath);
                Directory.CreateDirectory(target);
                SetMode(target, directory.Mode);
            }

            foreach (var file in input.Source.Files.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
            {
                var source = Rooted(input.Source.RootPath, file.RelativePath);
                var target = Rooted(stage, RootPrefix + file.RelativePath);
                verified += await CopyVerifiedAsync(source, target, file.Sha256, file.Length, file.Mode, limits, verified, token).ConfigureAwait(false);
                files++;
            }

            SetMode(Rooted(stage, "rootfs/opt/keycloak"), (UnixFileMode)493);
            foreach (var replacement in input.Replacements.OrderBy(x => x.Destination, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var target = ContextRooted(stage, replacement.Destination);
                Require(HashFile(target) == replacement.OriginalSha256, "Replacement original digest mismatch.");
                var next = target + ".replacement";
                var copied = await CopyVerifiedAsync(replacement.ReplacementPrivatePath, next, replacement.ReplacementSha256, -1, FileMode644, limits, verified, token).ConfigureAwait(false);
                verified += copied;
                File.Move(next, target, true);
            }

            if (input.Archive is not null)
            {
                var archive = input.Archive;
                var source = ContextRooted(stage, input.ArchiveDestination ?? "/opt/keycloak/bin/client/keycloak-admin-cli-26.7.2.jar");
                var remaining = limits.MaxAggregateBytes - verified;
                Require(remaining > 0, "Aggregate budget exceeded.");
                var transformed = archive with
                {
                    SourcePath = source,
                    OutputPath = source + ".archive-repaired",
                    Bounds = archive.Bounds with
                    {
                        MaxOutputBytes = Math.Min(archive.Bounds.MaxOutputBytes, remaining)
                    }
                };
                var result = await ArchiveTransformer.TransformAsync(transformed, token).ConfigureAwait(false);
                verified = checked(verified + new FileInfo(transformed.OutputPath).Length);
                File.Move(transformed.OutputPath, source, true);
                repaired = result.OutputSha256;
                Require(input.ExpectedRepairedCliSha256 is null || repaired == input.ExpectedRepairedCliSha256, "Repaired archive differs from its declared output digest.");
            }

            foreach (var theme in input.Themes.OrderBy(x => x.ImagePath, StringComparer.Ordinal))
                verified += await CopyVerifiedAsync(theme.SourcePath, ContextRooted(stage, theme.ImagePath), theme.Sha256, -1, theme.Mode, limits, verified, token).ConfigureAwait(false);
            var entrypoint = input.Entrypoint;
            var entrypointPath = entrypoint?.ImagePath ?? "/opt/keycloak/bin/kc.sh";
            if (entrypoint is not null)
            {
                var entrypointTarget = ContextRooted(stage, entrypointPath);
                var entrypointStage = entrypointTarget + ".entrypoint";
                verified += await CopyVerifiedAsync(entrypoint.SourcePath, entrypointStage, entrypoint.Sha256, -1, entrypoint.Mode, limits, verified, token).ConfigureAwait(false);
                File.Move(entrypointStage, entrypointTarget, true);
            }

            foreach (var deb in input.CapturedDebs.OrderBy(x => x.Name, StringComparer.Ordinal))
                verified += await CopyVerifiedAsync(deb.Path, Rooted(stage, "debs/" + deb.Name), deb.Sha256, -1, FileMode644, limits, verified, token).ConfigureAwait(false);
            verified += await CopyLayoutAsync(input.Runtime, stage, limits, verified, token).ConfigureAwait(false);
            verified += await CopyLayoutAsync(input.Frontend, stage, limits, verified, token).ConfigureAwait(false);
            await WriteFixedFilesAsync(stage, input, entrypointPath, token).ConfigureAwait(false);
            var manifest = await BuildManifestAsync(stage, input, repaired, verified, files, entrypointPath, token).ConfigureAwait(false);
            var finalFiles = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).ToArray();
            Require(finalFiles.Length < limits.MaxFiles, "Context file count exceeds bound.");
            var finalBytes = finalFiles.Aggregate(0L, (sum, path) => checked(sum + new FileInfo(path).Length));
            Require(manifest.LongLength <= limits.MaxAggregateBytes - finalBytes, "Final context budget exceeded.");
            TemporaryDiskBudget.Account(manifest.LongLength);
            await File.WriteAllBytesAsync(Path.Combine(stage, "context-manifest.json"), manifest, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
            return new ContextMaterializationResult(destination, HashFile(Path.Combine(destination, "context-manifest.json")), HashFile(Path.Combine(destination, "context-manifest.json")), files, verified, repaired, entrypointPath, input.Platform, FinalFiles(destination), FinalDirectories(destination));
        }
        finally
        {
            TryDelete(stage);
        }
    }

    internal static string Revalidate(string contextPath, string expectedManifestSha256, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(contextPath);
        RequireNoLinks(root);
        var manifest = Path.Combine(root, "context-manifest.json");
        Require(new FileInfo(manifest).Length <= 16L * 1024 * 1024, "Context manifest budget exceeded.");
        Require(HashFile(manifest) == expectedManifestSha256, "Context manifest changed.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var expectedFiles = new Dictionary<string, (string Sha, long Length, int Mode)>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.GetProperty("files").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = item.GetProperty("path").GetString()!;
            Require(SafeRelative(relative) && expectedFiles.TryAdd(relative, (item.GetProperty("sha256").GetString()!, item.GetProperty("length").GetInt64(), item.GetProperty("mode").GetInt32())), "Invalid or duplicate context file.");
        }

        var expectedDirectories = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.GetProperty("directories").EnumerateArray())
        {
            var relative = item.GetProperty("path").GetString()!;
            Require(SafeRelative(relative) && expectedDirectories.TryAdd(relative, item.GetProperty("mode").GetInt32()), "Invalid or duplicate context directory.");
        }

        var inventory = EnumerateSafeTree(root).ToArray();
        var actualFiles = inventory.Where(File.Exists).Where(x => !Path.GetRelativePath(root, x).Equals("context-manifest.json", StringComparison.Ordinal)).ToDictionary(x => Path.GetRelativePath(root, x).Replace('\\', '/'), StringComparer.Ordinal);
        var actualDirectories = inventory.Where(Directory.Exists).ToDictionary(x => Path.GetRelativePath(root, x).Replace('\\', '/'), StringComparer.Ordinal);
        Require(actualFiles.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedFiles.Keys), "Context file inventory changed.");
        Require(actualDirectories.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedDirectories.Keys), "Context directory inventory changed.");
        foreach (var pair in expectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = actualFiles[pair.Key];
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && new FileInfo(path).Length == pair.Value.Length && (int)FileModeOf(path) == pair.Value.Mode && HashFile(path) == pair.Value.Sha, "Context file changed.");
        }

        foreach (var pair in expectedDirectories)
        {
            var path = actualDirectories[pair.Key];
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && (int)FileModeOf(path) == pair.Value, "Context directory changed.");
        }

        return expectedManifestSha256;
    }

    private static async Task<byte[]> BuildManifestAsync(string stage, ContextMaterializationInput input, string? repaired, long verified, int copied, string entrypoint, CancellationToken token)
    {
        var entries = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).Select(x => new { path = Path.GetRelativePath(stage, x).Replace('\\', '/'), sha256 = HashFile(x), length = new FileInfo(x).Length, mode = (int)FileModeOf(x), owner = "1000:0" }).ToArray();
        var dirs = Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).Select(x => new { path = Path.GetRelativePath(stage, x).Replace('\\', '/'), mode = (int)FileModeOf(x) }).ToArray();
        var value = new
        {
            schema = 1,
            platform = input.Platform.ToString(),
            keycloakVersion = input.KeycloakVersion,
            entrypoint,
            verifiedBytes = verified,
            sourceFiles = copied,
            repairedCliSha256 = repaired,
            files = entries,
            directories = dirs
        };
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        return JsonSerializer.SerializeToUtf8Bytes(value, Json);
    }

    private static async Task WriteFixedFilesAsync(string stage, ContextMaterializationInput input, string entrypoint, CancellationToken token)
    {
        var dockerfile = input.ThemeOnly ? $"# syntax=appsurface-frontend\nFROM appsurface-runtime\nCOPY --chown=1000:0 rootfs/opt/keycloak/ /opt/keycloak/\n" : $"# syntax=appsurface-frontend\nFROM appsurface-runtime\nCOPY debs/ /tmp/issue887-debs/\nRUN dpkg -i /tmp/issue887-debs/*.deb && dpkg --purge curl libcurl4t64 && rm -f /usr/bin/pebble && userdel ubuntu && useradd --uid 1000 --gid 0 --home-dir /opt/keycloak --shell /usr/sbin/nologin keycloak && rm -rf /tmp/issue887-debs\nCOPY --chown=1000:0 rootfs/opt/keycloak/ /opt/keycloak/\nENV KC_RUN_IN_CONTAINER=true\nEXPOSE 8080 8443 9000\nLABEL org.opencontainers.image.version=\"{input.KeycloakVersion}\"\nUSER 1000:0\nENTRYPOINT {JsonSerializer.Serialize(new[] { entrypoint })}\n";
        TemporaryDiskBudget.Account(Encoding.UTF8.GetByteCount(dockerfile) + 30);
        await File.WriteAllTextAsync(Path.Combine(stage, "Dockerfile"), dockerfile, new UTF8Encoding(false), token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(stage, ".dockerignore"), "context-manifest.json\ninputs/\n", new UTF8Encoding(false), token).ConfigureAwait(false);
    }

    private static async Task<long> CopyLayoutAsync(ContextOciLayout layout, string stage, ContextMaterializationLimits limits, long verified, CancellationToken token)
    {
        var destination = Rooted(stage, "inputs/oci/" + layout.Name);
        var copied = 0L;
        foreach (var file in Directory.EnumerateFiles(layout.RootPath, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(layout.RootPath, file).Replace('\\', '/');
            var bytes = await CopyVerifiedAsync(file, Rooted(destination, relative), HashFile(file), -1, FileModeOf(file), limits, verified + copied, token).ConfigureAwait(false);
            copied += bytes;
        }

        var index = Path.Combine(destination, "index.json");
        Require(File.Exists(index), "OCI index missing.");
        Require(File.ReadAllText(index).Contains(layout.ExpectedManifest, StringComparison.Ordinal), "OCI manifest identity mismatch.");
        Require(HashTree(destination) == layout.ExpectedSha256, "OCI layout digest mismatch.");
        return copied;
    }

    private static void ValidateInput(ContextMaterializationInput input, ContextMaterializationLimits limits)
    {
        Require(limits.MaxFileBytes is > 0 and <= 1024L * 1024 * 1024 && limits.MaxAggregateBytes is > 0 and <= 1024L * 1024 * 1024 && limits.MaxFiles is > 0 and <= 65536 && limits.EffectiveDeadline > TimeSpan.Zero && limits.EffectiveDeadline <= TimeSpan.FromSeconds(300), "Context bounds are unsupported.");
        Require(input.Platform is ContextPlatform.LinuxArm64 or ContextPlatform.LinuxAmd64, "Unsupported platform.");
        Require(input.KeycloakVersion == "26.7.2", "Unsupported Keycloak version.");
        Require(input.Source.Files.Length <= limits.MaxFiles && input.Source.Files.Length > 0, "Source file count exceeds bound.");
        Require(input.Source.RootPath.Length > 0, "Source root is required.");
        if (input.Entrypoint is not null)
            Require(input.Entrypoint.ImagePath.StartsWith("/opt/keycloak/bin/", StringComparison.Ordinal) && SafeContextPath(input.Entrypoint.ImagePath), "Entrypoint is not approved.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in input.Source.Files)
        {
            Require(names.Add(file.RelativePath) && SafeRelative(file.RelativePath) && IsSha(file.Sha256) && file.Length >= 0 && file.Length <= limits.MaxFileBytes, "Invalid source file.");
        }

        foreach (var replacement in input.Replacements)
        {
            Require(SafeContextPath(replacement.Destination) && IsSha(replacement.OriginalSha256) && IsSha(replacement.ReplacementSha256), "Invalid replacement.");
        }

        foreach (var deb in input.CapturedDebs)
        {
            Require(SafeRelative(deb.Name) && !deb.Name.Contains('/'), "Invalid deb name.");
        }
    }

    private static async Task<long> CopyVerifiedAsync(string source, string destination, string expected, long expectedLength, UnixFileMode mode, ContextMaterializationLimits limits, long aggregate, CancellationToken token)
    {
        Require(!Path.IsPathRooted(source) || File.Exists(source), "Input file missing.");
        Require((File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0, "Linked input unsupported.");
        Require(!File.Exists(destination), "Destination collision.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (expectedLength >= 0)
            Require(input.Length == expectedLength, "Input length mismatch.");
        Require(input.Length <= limits.MaxFileBytes, "File budget exceeded.");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long count = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            count = checked(count + read);
            aggregate = checked(aggregate + read);
            Require(aggregate <= limits.MaxAggregateBytes, "Aggregate budget exceeded.");
            hash.AppendData(buffer, 0, read);
            TemporaryDiskBudget.Account(read);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }

        Require(expectedLength < 0 || count == expectedLength, "Input length changed.");
        Require(Convert.ToHexStringLower(hash.GetHashAndReset()) == expected, "Input digest mismatch.");
        await output.FlushAsync(token).ConfigureAwait(false);
        SetMode(destination, mode);
        return count;
    }

    private static bool SafeRelative(string value) => !string.IsNullOrEmpty(value) && !Path.IsPathRooted(value) && !value.Contains('\\') && value.Split('/').All(x => x.Length > 0 && x is not "." and not "..") && !value.Any(char.IsControl);
    private static bool SafeContextPath(string value) => value.StartsWith("/opt/keycloak/", StringComparison.Ordinal) && SafeRelative(value[1..]);
    private static string Rooted(string root, string relative)
    {
        var path = relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        Require(SafeRelative(path.Replace(Path.DirectorySeparatorChar, '/')), "Unsafe context path.");
        return Path.Combine(Path.GetFullPath(root), path);
    }

    private static string ContextRooted(string stage, string imagePath) => Rooted(stage, "rootfs/" + imagePath.TrimStart('/'));
    private static void RequireSafeParent(string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        RequireNoLinks(destination);
        Require(Directory.Exists(parent), "Destination parent must already exist.");
    }

    private static void RequireNoLinks(string path)
    {
        for (var node = new FileInfo(Path.GetFullPath(path)); node is not null; node = node.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            Require(node.LinkTarget is null && (!Path.Exists(node.FullName) || (File.GetAttributes(node.FullName) & FileAttributes.ReparsePoint) == 0), "Context path contains a link.");
    }

    private static IEnumerable<string> EnumerateSafeTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RequireNoLinks(entry);
                yield return entry;
                if (Directory.Exists(entry))
                    pending.Push(entry);
            }
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }

    private static UnixFileMode FileModeOf(string path) => OperatingSystem.IsWindows() ? UnixFileMode.UserRead | UnixFileMode.UserWrite : File.GetUnixFileMode(path);
    private static readonly UnixFileMode FileMode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static string ComputeLayoutDigest(string path)
    {
        var entries = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).Select(x => Path.GetRelativePath(path, x).Replace('\\', '/') + "\0" + HashFile(x));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    private static string HashTree(string path) => ComputeLayoutDigest(path);
    private static ImmutableDictionary<string, string> FinalFiles(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToImmutableDictionary(x => Path.GetRelativePath(path, x).Replace('\\', '/'), HashFile, StringComparer.Ordinal);
    private static ImmutableArray<ContextSourceDirectory> FinalDirectories(string path) => Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).Select(x => new ContextSourceDirectory(Path.GetRelativePath(path, x).Replace('\\', '/'), FileModeOf(x))).ToImmutableArray();
    private static bool IsSha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    /// <summary>Deletes an owned staging directory, tolerating only expected filesystem cleanup failures.</summary>
    internal static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
