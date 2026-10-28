using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace EvidenceHost.CompletedIsolation;

/// <summary>Bounded source and node custody for the single-use completed-run workspace.</summary>
/// <remarks>All path and provider data is treated as untrusted; this class issues no product authority.</remarks>
internal static class OwnerCustody
{
    private const int MaxCaptureBytes = 1024 * 1024;
    private const int MaxGitOutputBytes = 16 * 1024 * 1024;
    private const int MaxNodes = 131072;
    private const long MaxBytes = 8L * 1024 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, Snapshot> Snapshots = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal sealed record FileFact(string Path, string Sha256, string GitBlob, string Mode);
    private sealed class Snapshot
    {
        internal required string ValidatorHead { get; init; }
        internal required string ValidatorTree { get; init; }
        internal required string SourceHead { get; init; }
        internal required string SourceTree { get; init; }
        internal required string ValidatorCaptureSha { get; init; }
        internal required string SourceCaptureSha { get; init; }
        internal required byte[] ValidatorCapture { get; init; }
        internal required byte[] SourceCapture { get; init; }
        internal required byte[] Event { get; init; }
        internal required byte[] HostCanaryPath { get; init; }
        internal required Dictionary<string, FileFact> ValidatorGitFiles { get; init; }
        internal required Dictionary<string, FileFact> SourceGitFiles { get; init; }
        internal required Dictionary<string, FileFact> NativeGitFiles { get; init; }
        internal required Dictionary<string, FileFact> ValidatorFiles { get; init; }
        internal required Dictionary<string, FileFact> SourceFiles { get; init; }
        internal required Dictionary<string, NodeFact> Roots { get; init; }
        internal required string[] RootPaths { get; init; }
        internal Dictionary<string, FileFact> StageFiles { get; set; } = new(StringComparer.Ordinal);
        internal FileFact? Preparation { get; set; }
        internal FileFact? Replay { get; set; }
        internal required SafeFileHandle[] RootHandles { get; init; }
        internal Dictionary<string, SafeFileHandle> EnrolledHandles { get; } = new(StringComparer.Ordinal);
    }

    internal static async Task InitializeAsync(OwnerLayout layout, byte[] sourceCapture,
        byte[] validatorCapture, byte[] eventBytes, CancellationToken token)
    {
        Require(sourceCapture.Length is > 0 and <= MaxCaptureBytes
            && validatorCapture.Length is > 0 and <= MaxCaptureBytes
            && eventBytes.Length is > 0 and <= 128 * 1024, "custody-input-bounds");
        var source = ParseCapture(sourceCapture);
        var validator = ParseCapture(validatorCapture);
        var validatorRoot = layout.Host(OwnerPaths.Validator);
        var sourceRoot = layout.Host(OwnerPaths.Source);
        var nativeRoot = layout.Host(OwnerPaths.Native);
        await VerifyCheckoutAsync(validatorRoot, validator, validator.Source, token, layout.Uid).ConfigureAwait(false);
        await VerifyCheckoutAsync(sourceRoot, source, source.Source, token, layout.Uid).ConfigureAwait(false);
        await VerifyCheckoutAsync(nativeRoot, source, source.Source, token, layout.Uid).ConfigureAwait(false);
        var validatorGitFiles = SnapshotGitMetadata(validatorRoot);
        var sourceGitFiles = SnapshotGitMetadata(sourceRoot);
        var nativeGitFiles = SnapshotGitMetadata(nativeRoot);

        var roots = new[]
        {
            layout.RootFileSystem,
            layout.Host(OwnerPaths.Validator), layout.Host(OwnerPaths.Source), layout.Host(OwnerPaths.Native),
            layout.Host(OwnerPaths.Stage), layout.Host(OwnerPaths.Input), layout.Host(OwnerPaths.Handoff),
            layout.Host(OwnerPaths.Output), layout.Host(OwnerPaths.Report),
        };
        var handles = new SafeFileHandle[roots.Length];
        var facts = new Dictionary<string, NodeFact>(StringComparer.Ordinal);
        Dictionary<string, SafeFileHandle>? inputPins = null;
        Snapshot? snapshot = null;
        try
        {
            for (var index = 0; index < roots.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var path = roots[index];
                var handle = OpenDirectory(path);
                handles[index] = handle;
                var node = Stat(handle);
                Require(node.Type == DirectoryType, "custody-root-type");
                Require(SameNode(StatPath(path), node with { Path = path }), "custody-root-name");
                facts.Add(path, node);
            }
            inputPins = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
            try
            {
                foreach (var path in new[]
                {
                    OwnerPaths.ValidatorCapture, OwnerPaths.SourceCapture, OwnerPaths.Event,
                    OwnerPaths.Input + "/immutable-canary", OwnerPaths.Input + "/host-canary-path",
                })
                {
                    var hostPath = layout.Host(path);
                    inputPins.Add(hostPath, OpenPinnedFile(hostPath));
                }
            }
            catch
            {
                foreach (var handle in inputPins.Values) handle.Dispose();
                throw;
            }
            snapshot = new Snapshot
            {
                ValidatorHead = validator.Source,
                ValidatorTree = validator.Tree,
                SourceHead = source.Source,
                SourceTree = source.Tree,
                ValidatorCaptureSha = Sha256(validatorCapture),
                SourceCaptureSha = Sha256(sourceCapture),
                ValidatorCapture = validatorCapture.ToArray(),
                SourceCapture = sourceCapture.ToArray(),
                Event = eventBytes.ToArray(),
                HostCanaryPath = Encoding.UTF8.GetBytes(Path.Combine(layout.HostRoot, "credential-canary") + "\n"),
                ValidatorGitFiles = validatorGitFiles,
                SourceGitFiles = sourceGitFiles,
                NativeGitFiles = nativeGitFiles,
                ValidatorFiles = validator.Files,
                SourceFiles = source.Files,
                Roots = facts,
                RootPaths = roots,
                RootHandles = handles,
            };
            foreach (var pair in inputPins) snapshot.EnrolledHandles.Add(pair.Key, pair.Value);
            Require(Snapshots.TryAdd(layout.HostRoot, snapshot), "custody-layout-duplicate");
            await VerifyCustodyAsync(layout, token).ConfigureAwait(false);
        }
        catch
        {
            if (Snapshots.TryRemove(layout.HostRoot, out var added))
                foreach (var handle in added.EnrolledHandles.Values) handle.Dispose();
            else if (inputPins is not null && snapshot is null)
                foreach (var handle in inputPins.Values) handle.Dispose();
            foreach (var handle in handles) handle?.Dispose();
            throw;
        }
    }

    internal static async Task VerifyCustodyAsync(OwnerLayout layout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = Get(layout);
        VerifyPinnedRoots(state);
        VerifyInputFiles(layout, state);
        await VerifyTrackedTreeAsync(layout.Host(OwnerPaths.Validator), state.ValidatorFiles,
            state.ValidatorHead, state.ValidatorTree, state.ValidatorCapture, layout.Uid, allowIgnored: false, token).ConfigureAwait(false);
        await VerifyTrackedTreeAsync(layout.Host(OwnerPaths.Source), state.SourceFiles,
            state.SourceHead, state.SourceTree, state.SourceCapture, layout.Uid, allowIgnored: true, token).ConfigureAwait(false);
        await VerifyTrackedTreeAsync(layout.Host(OwnerPaths.Native), state.SourceFiles,
            state.SourceHead, state.SourceTree, state.SourceCapture, layout.Uid, allowIgnored: true, token).ConfigureAwait(false);
        VerifyGitMetadata(layout.Host(OwnerPaths.Validator), state.ValidatorGitFiles);
        VerifyGitMetadata(layout.Host(OwnerPaths.Source), state.SourceGitFiles);
        VerifyGitMetadata(layout.Host(OwnerPaths.Native), state.NativeGitFiles);
        VerifyStage(layout, state);
        VerifyHandoff(layout, state);
        VerifyFilesystemLimits(layout);
        VerifyPinnedRoots(state);
    }

    /// <summary>Enrolls the exact post-prepare stage archives and prep wire after the caller joined prepare.</summary>
    internal static async Task EnrollPreparedAsync(OwnerLayout layout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = Get(layout);
        VerifyPinnedRoots(state);
        var stage = layout.Host(OwnerPaths.Stage);
        var files = ReadDirectoryFiles(stage, layout.Uid, 384, int.MaxValue, 37);
        Require(files.Count is > 0 and <= 37, "stage-membership");
        var preparation = ReadFileFact(layout.Host(OwnerPaths.Preparation), layout.Uid, 384, 16 * 1024 * 1024);
        var pins = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
        try
        {
            foreach (var name in files.Keys)
                pins.Add(layout.Host(OwnerPaths.Stage + "/" + name), OpenPinnedFile(layout.Host(OwnerPaths.Stage + "/" + name)));
            pins.Add(layout.Host(OwnerPaths.Preparation), OpenPinnedFile(layout.Host(OwnerPaths.Preparation)));
        }
        catch
        {
            foreach (var handle in pins.Values) handle.Dispose();
            throw;
        }
        state.StageFiles = files;
        state.Preparation = preparation;
        foreach (var pair in pins) state.EnrolledHandles.Add(pair.Key, pair.Value);
        VerifyInputFiles(layout, state);
        await VerifyTrackedTreeAsync(layout.Host(OwnerPaths.Validator), state.ValidatorFiles,
            state.ValidatorHead, state.ValidatorTree, state.ValidatorCapture, layout.Uid, allowIgnored: false, token).ConfigureAwait(false);
        await VerifyTrackedTreeAsync(layout.Host(OwnerPaths.Source), state.SourceFiles,
            state.SourceHead, state.SourceTree, state.SourceCapture, layout.Uid, allowIgnored: true, token).ConfigureAwait(false);
        await VerifyTrackedTreeAsync(layout.Host(OwnerPaths.Native), state.SourceFiles,
            state.SourceHead, state.SourceTree, state.SourceCapture, layout.Uid, allowIgnored: true, token).ConfigureAwait(false);
        VerifyGitMetadata(layout.Host(OwnerPaths.Validator), state.ValidatorGitFiles);
        VerifyGitMetadata(layout.Host(OwnerPaths.Source), state.SourceGitFiles);
        VerifyGitMetadata(layout.Host(OwnerPaths.Native), state.NativeGitFiles);
        VerifyPinnedRoots(state);
    }

    /// <summary>Enrolls the one bounded replay wire after replay physically settled.</summary>
    internal static Task EnrollReplayAsync(OwnerLayout layout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = Get(layout);
        Require(state.Preparation is not null, "replay-before-prepare-enrollment");
        var replayPath = layout.Host(OwnerPaths.ReplayResult);
        state.Replay = ReadFileFact(replayPath, layout.Uid, 384, 16 * 1024 * 1024);
        state.EnrolledHandles.Add(replayPath, OpenPinnedFile(replayPath));
        VerifyPinnedRoots(state);
        return Task.CompletedTask;
    }

    /// <summary>Closes retained root descriptors only after the caller proves phase settlement.</summary>
    internal static void ReleaseAsyncState(OwnerLayout layout)
    {
        if (!Snapshots.TryRemove(layout.HostRoot, out var state)) return;
        foreach (var handle in state.RootHandles) handle.Dispose();
        foreach (var handle in state.EnrolledHandles.Values) handle.Dispose();
    }

    internal static byte[] ReadBoundedInput(string path, int maximum)
    {
        Require(Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path, "input-path");
        var before = StatPath(path);
        Require(before.Type == RegularType && before.Links == 1 && before.Uid == EffectiveUid
            && before.Size is > 0 && before.Size <= (ulong)maximum, "input-shape");
        using var stream = OpenReadNoFollow(path);
        var opened = Stat(stream.SafeFileHandle);
        Require(SameNode(opened, before), "input-substitution");
        using var output = new MemoryStream(checked((int)before.Size));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var amount = stream.Read(buffer, 0, buffer.Length);
            if (amount == 0) break;
        Require((ulong)(output.Length + amount) <= before.Size && output.Length + amount <= maximum, "input-growth");
            output.Write(buffer, 0, amount);
        }
        Require(SameNode(Stat(stream.SafeFileHandle), before) && SameNode(StatPath(path), before)
            && output.Length == (long)before.Size, "input-final-substitution");
        return output.ToArray();
    }

    internal static Task<byte[]> ReadBoundedInputAsync(string path, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(ReadBoundedInput(path, maximum));
    }

    internal static void VerifyExt4Image(string rootFileSystem)
    {
        var stat = StatFs(rootFileSystem);
        Require(stat.Type == Ext4Magic && stat.BlockSize > 0 && stat.Blocks > 0
            && checked((ulong)stat.BlockSize * stat.Blocks) <= (ulong)MaxBytes
            && stat.Files is > 0 and <= MaxNodes, "ext4-quota");
    }

    internal static void VerifyRootHost(string hostRoot)
    {
        Require(Path.GetFullPath(hostRoot) == hostRoot
            && hostRoot.StartsWith("/var/lib/appsurface-completed-", StringComparison.Ordinal), "host-root-path");
        var info = StatPath(hostRoot);
        Require(info.Type == DirectoryType && info.Uid == 0 && info.Gid == 0 && info.Mode == 0x1c0,
            "host-root-metadata"); // root:root 0700.
        var marker = ReadRootFile(Path.Combine(hostRoot, "owner.marker"), 0, 384, 256);
        Require(StrictUtf8.GetString(marker).StartsWith("issue779-completed-owner-v1\n", StringComparison.Ordinal),
            "host-root-marker");
    }

    internal static bool ProcessUsesId(uint id, CancellationToken token)
    {
        foreach (var path in Directory.EnumerateDirectories("/proc"))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!uint.TryParse(name, out _)) continue;
            var procRoot = Path.Combine("/proc", name);
            var before = ReadProcStartTime(procRoot);
            if (before is null) continue; // The process exited during enumeration.
            var raw = ReadProcFile(Path.Combine(procRoot, "status"), 64 * 1024);
            if (raw is null) continue;
            var after = ReadProcStartTime(procRoot);
            if (after is null) continue; // It exited after the status snapshot.
            Require(before == after, "proc-identity-race");
            if (ContainsId(StrictUtf8.GetString(raw), id)) return true;
        }
        return false;
    }

    private static bool ContainsId(string status, uint id)
    {
        var seenUid = false;
        var seenGid = false;
        var seenGroups = false;
        var found = false;
        foreach (var line in status.Split('\n'))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                Require(!seenUid, "proc-uid-format");
                found |= MatchesNumericFields(line[4..], id, exactCount: 4);
                seenUid = true;
            }
            else if (line.StartsWith("Gid:", StringComparison.Ordinal))
            {
                Require(!seenGid, "proc-gid-format");
                found |= MatchesNumericFields(line[4..], id, exactCount: 4);
                seenGid = true;
            }
            else if (line.StartsWith("Groups:", StringComparison.Ordinal))
            {
                Require(!seenGroups, "proc-groups-format");
                found |= MatchesNumericFields(line[7..], id, exactCount: null);
                seenGroups = true;
            }
        }
        Require(seenUid && seenGid && seenGroups, "proc-credential-fields");
        return found;
    }

    private static bool MatchesNumericFields(string fields, uint id, int? exactCount)
    {
        var values = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Require((exactCount is null || values.Length == exactCount.Value)
            && values.All(static value => uint.TryParse(value, out _)), "proc-numeric-field");
        return values.Any(value => uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture) == id);
    }

    private static string? ReadProcStartTime(string procRoot)
    {
        var raw = ReadProcFile(Path.Combine(procRoot, "stat"), 4096);
        if (raw is null) return null;
        var text = StrictUtf8.GetString(raw).TrimEnd('\n');
        var close = text.LastIndexOf(')');
        Require(close >= 0 && close + 2 < text.Length, "proc-stat-format");
        var fields = text[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // /proc/PID/stat's first token after comm is field 3 (state); starttime is field 22.
        Require(fields.Length > 19 && ulong.TryParse(fields[19], out _), "proc-starttime-format");
        return fields[19];
    }

    private static byte[]? ReadProcFile(string path, int maximum)
    {
        var fd = Open(path, 0 | 0x20000 | 0x80000 | 0x800, 0); // O_RDONLY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK.
        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3) return null; // ENOENT/ESRCH are process-exit races only.
            throw new IsolationException("proc-file-open");
        }
        using var stream = new FileStream(new SafeFileHandle((IntPtr)fd, true), FileAccess.Read, 4096, false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            Require(output.Length + count <= maximum, "proc-file-bound");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static async Task VerifyCheckoutAsync(string root, Capture capture, string expectedHead,
        CancellationToken token, uint? expectedUid = null)
    {
        Require(Path.IsPathFullyQualified(root) && Path.GetFullPath(root) == root
            && Directory.Exists(root), "source-root");
        var head = await Git(root, ["rev-parse", "HEAD"], token).ConfigureAwait(false);
        var tree = await Git(root, ["rev-parse", "HEAD^{tree}"], token).ConfigureAwait(false);
        Require(head == expectedHead && head == capture.Source && tree == capture.Tree, "source-revision");
        var tracked = await GitBytes(root, ["ls-files", "--stage", "-z"], token, MaxGitOutputBytes)
            .ConfigureAwait(false);
        var parsed = ParseIndex(tracked);
        Require(parsed.Count == capture.Files.Count
            && parsed.Keys.Order(StringComparer.Ordinal).SequenceEqual(capture.Files.Keys.Order(StringComparer.Ordinal)),
            "source-tracked-set");
        foreach (var pair in capture.Files)
        {
            token.ThrowIfCancellationRequested();
            var row = pair.Value;
            var index = parsed[pair.Key];
            Require(index.Blob == row.GitBlob && index.Mode == row.Mode, "source-index-binding");
            var item = ReadTrackedFile(root, pair.Key, expectedUid);
            Require(item.Sha256 == row.Sha256 && item.GitBlob == row.GitBlob && item.Mode == row.Mode,
                "source-file-binding");
        }
        var status = await Git(root, ["status", "--porcelain=v1", "--untracked-files=all"], token)
            .ConfigureAwait(false);
        Require(status.Length == 0, "source-not-clean");
    }

    internal static Capture ParseCapture(byte[] raw)
    {
        Require(raw.Length is > 0 and <= MaxCaptureBytes, "capture-size");
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        Require(root.ValueKind == JsonValueKind.Object && ExactProperties(root, "schema", "source", "tree", "count", "files"), "capture-fields");
        var schema = root.GetProperty("schema").GetString();
        var source = root.GetProperty("source").GetString();
        var tree = root.GetProperty("tree").GetString();
        var count = root.GetProperty("count");
        var files = root.GetProperty("files");
        Require(schema == "issue779-checkpoint2-source-v1" && IsHash40(source) && IsHash40(tree)
            && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var fileCount)
            && fileCount is > 0 and <= MaxNodes && files.ValueKind == JsonValueKind.Object
            && files.EnumerateObject().Count() == fileCount, "capture-identity");
        var rows = new Dictionary<string, FileFact>(StringComparer.Ordinal);
        foreach (var file in files.EnumerateObject())
        {
            Require(SafeRelative(file.Name) && file.Value.ValueKind == JsonValueKind.Object
                && ExactProperties(file.Value, "sha256", "mode", "git_blob"), "capture-row");
            var sha = file.Value.GetProperty("sha256").GetString();
            var mode = file.Value.GetProperty("mode").GetString();
            var blob = file.Value.GetProperty("git_blob").GetString();
            Require(IsHash64(sha) && IsHash40(blob) && mode is "0644" or "0755", "capture-file");
            rows.Add(file.Name, new FileFact(file.Name, sha!, blob!, mode!));
        }
        return new Capture(source!, tree!, rows);
    }

    internal sealed record Capture(string Source, string Tree, Dictionary<string, FileFact> Files);

    private static void VerifyInputFiles(OwnerLayout layout, Snapshot state)
    {
        foreach (var (name, expected, mode) in new[]
        {
            (OwnerPaths.ValidatorCapture, state.ValidatorCapture, 384),
            (OwnerPaths.SourceCapture, state.SourceCapture, 384),
            (OwnerPaths.Event, state.Event, 384),
            (OwnerPaths.Input + "/immutable-canary", Encoding.UTF8.GetBytes("immutable\n"), 384),
        })
        {
            var path = layout.Host(name);
            VerifyPinnedFile(state, path);
            var raw = ReadRootFile(path, layout.Uid, (uint)mode, MaxCaptureBytes);
            Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), SHA256.HashData(expected)), "input-immutable");
        }
        var hostCanaryPath = layout.Host(OwnerPaths.Input + "/host-canary-path");
        VerifyPinnedFile(state, hostCanaryPath);
        var hostCanary = ReadRootFile(hostCanaryPath, layout.Uid, 384, 4096);
        Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(hostCanary), SHA256.HashData(state.HostCanaryPath)),
            "host-canary-path-changed");
    }

    private static async Task VerifyTrackedTreeAsync(string root, Dictionary<string, FileFact> files, string head,
        string tree, byte[] capture, uint expectedUid, bool allowIgnored, CancellationToken token)
    {
        var parsed = ParseCapture(capture);
        Require(parsed.Source == head && parsed.Tree == tree && parsed.Files.Count == files.Count, "tracked-capture-binding");
        var identity = StatPath(root);
        Require(identity.Type == DirectoryType, "tracked-root-type");
        foreach (var pair in files)
        {
            token.ThrowIfCancellationRequested();
            var actual = ReadTrackedFile(root, pair.Key, expectedUid);
            Require(actual.Sha256 == pair.Value.Sha256 && actual.GitBlob == pair.Value.GitBlob
                && actual.Mode == pair.Value.Mode, "tracked-file-changed");
        }
        var gitHead = await Git(root, ["rev-parse", "HEAD"], token).ConfigureAwait(false);
        var gitTree = await Git(root, ["rev-parse", "HEAD^{tree}"], token).ConfigureAwait(false);
        Require(gitHead == head && gitTree == tree, "tracked-git-head");
        var status = await Git(root, ["status", "--porcelain=v1", "--untracked-files=all"], token).ConfigureAwait(false);
        Require(status.Length == 0, "tracked-git-dirty");
        if (!allowIgnored)
        {
            var ignored = await Git(root, ["status", "--porcelain=v1", "--untracked-files=all", "--ignored"], token).ConfigureAwait(false);
            Require(ignored.Length == 0, "validator-ignored-files");
        }
    }

    internal static void SealGitMetadata(string repository)
    {
        var config = Path.Combine(repository, ".git", "config");
        var original = StatPath(config);
        Require(original.Type == RegularType && original.Uid == 0 && original.Links == 1, "git-config-shape");
        var minimal = Encoding.UTF8.GetBytes("[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n");
        using (var stream = new FileStream(config, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(minimal);
            stream.Flush(true);
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(repository, ".git"), "*", SearchOption.AllDirectories)
            .Prepend(Path.Combine(repository, ".git")))
        {
            Require(!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "git-metadata-link");
            var node = StatPath(path);
            var mode = node.Type == DirectoryType || (node.Mode & 0x40) != 0 ? 0x1edu : 0x1a4u;
            OwnerWorkspace.SetOwner(path, 0, 0);
            OwnerWorkspace.SetMode(path, mode);
        }
    }

    private static Dictionary<string, FileFact> SnapshotGitMetadata(string repository)
    {
        var result = new Dictionary<string, FileFact>(StringComparer.Ordinal);
        var gitRoot = Path.Combine(repository, ".git");
        Require(Directory.Exists(gitRoot), "git-metadata-missing");
        foreach (var path in Directory.EnumerateFileSystemEntries(gitRoot, "*", SearchOption.AllDirectories).Prepend(gitRoot))
        {
            Require(!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "git-metadata-link");
            var stat = StatPath(path);
            Require(stat.Uid == 0 && stat.Gid == 0 && (stat.Mode & 0x12) == 0, "git-metadata-owner");
            if (Directory.Exists(path))
            {
                Require(stat.Type == DirectoryType && stat.Mode == 0x1ed, "git-metadata-directory");
                continue;
            }
            var relative = Path.GetRelativePath(repository, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith(".git/objects/", StringComparison.Ordinal)) continue;
            Require(stat.Type == RegularType && stat.Links == 1 && stat.Size <= 8 * 1024 * 1024
                && (stat.Mode is 0x1a4 or 0x1ed), "git-metadata-file");
            var fact = ReadFileFact(path, stat.Uid, stat.Mode, 8 * 1024 * 1024);
            result.Add(relative, fact with { Path = relative });
        }
        Require(result.Count is > 0 and <= 16384, "git-metadata-count");
        return result;
    }

    private static void VerifyGitMetadata(string repository, Dictionary<string, FileFact> expected)
    {
        var actual = SnapshotGitMetadata(repository);
        Require(actual.Count == expected.Count && actual.Keys.Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Keys.Order(StringComparer.Ordinal)), "git-metadata-membership");
        foreach (var pair in expected)
            Require(actual.TryGetValue(pair.Key, out var fact) && fact == pair.Value, "git-metadata-changed");
    }

    private static void VerifyStage(OwnerLayout layout, Snapshot state)
    {
        var stage = layout.Host(OwnerPaths.Stage);
        if (state.StageFiles.Count == 0)
        {
            Require(Directory.EnumerateFileSystemEntries(stage).Count() == 0, "stage-not-empty");
            return;
        }
        var actual = ReadDirectoryFiles(stage, layout.Uid, 384, int.MaxValue, 37);
        Require(actual.Count == state.StageFiles.Count, "stage-count");
        foreach (var pair in state.StageFiles)
        {
            var fullPath = Path.Combine(stage, pair.Key);
            Require(actual.TryGetValue(pair.Key, out var row) && row == pair.Value, "stage-changed");
            VerifyPinnedFile(state, fullPath);
        }
    }

    private static void VerifyHandoff(OwnerLayout layout, Snapshot state)
    {
        if (state.Preparation is not null)
        {
            Require(ReadFileFact(layout.Host(OwnerPaths.Preparation), layout.Uid, 384, 16 * 1024 * 1024) == state.Preparation,
                "preparation-changed");
            VerifyPinnedFile(state, layout.Host(OwnerPaths.Preparation));
        }
        var replayPath = layout.Host(OwnerPaths.ReplayResult);
        if (state.Replay is not null)
        {
            Require(ReadFileFact(replayPath, layout.Uid, 384, 16 * 1024 * 1024) == state.Replay, "replay-changed");
            VerifyPinnedFile(state, replayPath);
        }
        else if (File.Exists(replayPath))
            Require(ReadFileFact(replayPath, layout.Uid, 384, 16 * 1024 * 1024).Path == OwnerPaths.ReplayResult,
                "unexpected-replay");
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (state.Preparation is not null) allowed.Add(OwnerPaths.Preparation);
        if (File.Exists(replayPath)) allowed.Add(OwnerPaths.ReplayResult);
        foreach (var path in Directory.EnumerateFileSystemEntries(layout.Host(OwnerPaths.Handoff)))
            Require(allowed.Contains(OwnerPaths.Handoff + "/" + Path.GetFileName(path)), "handoff-unexpected-node");
    }

    private static void VerifyPinnedRoots(Snapshot state)
    {
        for (var index = 0; index < state.RootHandles.Length; index++)
        {
            var path = state.RootPaths[index];
            var opened = Stat(handle: state.RootHandles[index]) with { Path = path };
            Require(opened.Type == DirectoryType, "root-fd-type");
            Require(SameNode(StatPath(path), opened), "root-name-changed");
        }
    }

    private static SafeFileHandle OpenPinnedFile(string path)
    {
        var before = StatPath(path);
        Require(before.Type == RegularType && before.Links == 1, "pin-file-shape");
        var fd = Open(path, 0x200000 | 0x20000 | 0x80000, 0); // O_PATH|O_NOFOLLOW|O_CLOEXEC.
        if (fd < 0) throw new IsolationException("pin-file-open");
        var handle = new SafeFileHandle((IntPtr)fd, true);
        try
        {
            Require(SameNode(Stat(handle), before), "pin-file-substitution");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void VerifyPinnedFile(Snapshot state, string path)
    {
        Require(state.EnrolledHandles.TryGetValue(path, out var handle), "pin-file-missing");
        var pinned = Stat(handle!) with { Path = path };
        Require(SameNode(StatPath(path), pinned), "pin-file-name-changed");
    }

    private static void VerifyFilesystemLimits(OwnerLayout layout)
    {
        VerifyExt4Image(layout.RootFileSystem);
        var count = 0;
        long bytes = 0;
        Walk(layout.RootFileSystem);
        Require(count <= MaxNodes && bytes <= MaxBytes, "filesystem-usage");
        void Walk(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                count++;
                Require(count <= MaxNodes, "filesystem-nodes");
                var node = StatPath(path);
                Require(node.Type is DirectoryType or RegularType, "filesystem-special-node");
                if (node.Type == DirectoryType) Walk(path);
                else
                {
                    bytes = checked(bytes + (long)node.Size);
                    Require(bytes <= MaxBytes, "filesystem-bytes");
                }
            }
        }
    }

    private static Dictionary<string, FileFact> ReadDirectoryFiles(string path, uint uid, uint mode,
        int maximumFileBytes, int maximumNodes)
    {
        var result = new Dictionary<string, FileFact>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            Require(result.Count < maximumNodes, "directory-node-limit");
            var name = Path.GetFileName(entry);
            Require(SafeLeaf(name), "directory-name");
            result.Add(name, ReadFileFact(entry, uid, mode, maximumFileBytes));
        }
        return result;
    }

    private static FileFact ReadFileFact(string path, uint uid, uint mode, int maximum)
    {
        var before = StatPath(path);
        Require(before.Type == RegularType && before.Links == 1 && before.Uid == uid
            && before.Mode == mode && before.Size is > 0 && before.Size <= (ulong)maximum, "custody-file-shape");
        using var stream = OpenReadNoFollow(path);
        Require(SameNode(Stat(stream.SafeFileHandle), before), "custody-file-substitution");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var git = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        git.AppendData(Encoding.ASCII.GetBytes("blob " + before.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"));
        var buffer = new byte[64 * 1024];
        ulong total = 0;
        while (true)
        {
            var amount = stream.Read(buffer, 0, buffer.Length);
            if (amount == 0) break;
            total += (uint)amount;
            Require(total <= before.Size, "custody-file-growth");
            sha.AppendData(buffer, 0, amount);
            git.AppendData(buffer, 0, amount);
        }
        Require(total == before.Size && SameNode(Stat(stream.SafeFileHandle), before) && SameNode(StatPath(path), before),
            "custody-file-changed");
        return new FileFact(Path.GetFileName(path), Convert.ToHexStringLower(sha.GetHashAndReset()),
            Convert.ToHexStringLower(git.GetHashAndReset()), ModeText(before.Mode));
    }

    internal static byte[] ReadRootFile(string path, uint uid, uint? mode, int maximum)
    {
        var before = StatPath(path);
        Require(before.Type == RegularType && before.Links == 1 && before.Uid == uid
            && (!mode.HasValue || before.Mode == mode.Value) && before.Size <= (ulong)maximum, "root-file-shape");
        using var stream = OpenReadNoFollow(path);
        Require(SameNode(Stat(stream.SafeFileHandle), before), "root-file-substitution");
        using var output = new MemoryStream(checked((int)before.Size));
        var buffer = new byte[8192];
        while (true)
        {
            var amount = stream.Read(buffer, 0, buffer.Length);
            if (amount == 0) break;
            Require(output.Length + amount <= maximum, "root-file-bound");
            output.Write(buffer, 0, amount);
        }
        Require(SameNode(Stat(stream.SafeFileHandle), before) && SameNode(StatPath(path), before) && output.Length == (long)before.Size,
            "root-file-changed");
        return output.ToArray();
    }

    private static (string Blob, string Mode)[] _unused = [];
    private static Dictionary<string, (string Blob, string Mode)> ParseIndex(byte[] raw)
    {
        var result = new Dictionary<string, (string Blob, string Mode)>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < raw.Length)
        {
            var nul = Array.IndexOf(raw, (byte)0, offset);
            Require(nul > offset, "git-index-format");
            var row = raw.AsSpan(offset, nul - offset);
            var tab = row.IndexOf((byte)'\t');
            Require(tab > 0, "git-index-format");
            var header = Encoding.ASCII.GetString(row[..tab]);
            var fields = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Require(fields.Length == 3 && fields[2] == "0" && fields[0] is "100644" or "100755"
                && IsHash40(fields[1]), "git-index-entry");
            var name = StrictUtf8.GetString(row[(tab + 1)..]);
            Require(SafeRelative(name), "git-index-path");
            result.Add(name, (fields[1], fields[0]));
            offset = nul + 1;
        }
        return result;
    }

    private static FileFact ReadTrackedFile(string root, string relative, uint? expectedUid = null)
    {
        Require(SafeRelative(relative), "tracked-path");
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        var pathFact = StatPath(path);
        var mode = pathFact.Mode switch { 0x1a4 => "0644", 0x1ed => "0755", _ => "" };
        Require(mode.Length != 0, "tracked-mode");
        var fact = ReadFileFact(path, expectedUid ?? EffectiveUid, pathFact.Mode, int.MaxValue);
        return fact with { Path = relative, Mode = mode };
    }

    private static async Task<string> Git(string root, string[] args, CancellationToken token)
    {
        var raw = await GitBytes(root, args, token, 4096).ConfigureAwait(false);
        try { return StrictUtf8.GetString(raw).Trim(); }
        catch (DecoderFallbackException) { throw new IsolationException("git-output-encoding"); }
    }

    private static async Task<byte[]> GitBytes(string root, string[] args, CancellationToken token, int maximum)
    {
        var result = await OwnerWorkspace.RunFixedAsync("/usr/bin/git", ["--no-optional-locks", "-c", "core.fsmonitor=false",
            "-c", "core.hooksPath=/dev/null", "-c", "safe.directory=" + root, "-C", root, .. args],
            maximum, maximum, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        Require(result.ExitCode == 0, "git-command");
        return result.StandardOutput;
    }

    private static string RunGitSync(string root, string[] args)
    {
        return Git(root, args, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(35)).GetAwaiter().GetResult();
    }

    private static bool ExactProperties(JsonElement value, params string[] names) =>
        value.EnumerateObject().Count() == names.Length
        && value.EnumerateObject().Select(static property => property.Name).Distinct(StringComparer.Ordinal).Count() == names.Length
        && value.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal)
            .SetEquals(names);

    private static bool SafeRelative(string path) => path.Length is > 0 and <= 4095
        && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':')
        && !path.Any(char.IsControl)
        && path.Split('/').All(static part => part.Length > 0 && part is not ("." or ".." or ".git"));

    private static bool SafeLeaf(string path) => path.Length is > 0 and <= 255 && path is not ("." or "..")
        && !path.Any(char.IsControl) && path.IndexOfAny(['/', '\\']) < 0;
    private static bool IsHash40(string? value) => value is { Length: 40 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsHash64(string? value) => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Sha256(byte[] raw) => Convert.ToHexStringLower(SHA256.HashData(raw));
    private static string ModeText(uint mode) => (mode & 0x1ff) switch { 0x1a4 => "0644", 0x1ed => "0755", _ => "" };
    private static void Require(bool condition, string category) { if (!condition) throw new IsolationException(category); }
    private static Snapshot Get(OwnerLayout layout) => Snapshots.TryGetValue(layout.HostRoot, out var state)
        ? state : throw new IsolationException("custody-uninitialized");

    internal const uint RegularType = 0x8000;
    internal const uint DirectoryType = 0x4000;
    private const long Ext4Magic = 0xEF53;
    internal static uint EffectiveUid => GetEuid();
    private static SafeFileHandle OpenDirectory(string path)
    {
        var fd = Open(path, 0x200000 | 0x10000 | 0x20000 | 0x80000, 0); // O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC.
        return fd < 0 ? throw new IsolationException("directory-open") : new SafeFileHandle((IntPtr)fd, true);
    }
    private static FileStream OpenReadNoFollow(string path)
    {
        var fd = Open(path, 0 | 0x20000 | 0x80000 | 0x800, 0); // O_RDONLY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK.
        return fd < 0 ? throw new IsolationException("file-open")
            : new FileStream(new SafeFileHandle((IntPtr)fd, true), FileAccess.Read, 65536, false);
    }
    private static NodeFact Stat(SafeFileHandle handle)
    {
        if (FStat(handle, out var value) != 0) throw new IsolationException("fstat");
        return ToFact(string.Empty, value);
    }
    internal static NodeFact StatPath(string path)
    {
        if (LStat(path, out var value) != 0) throw new IsolationException("lstat");
        return ToFact(path, value);
    }
    private static NodeFact ToFact(string path, LinuxStat value) => new(path, value.Device, value.Inode,
        value.Mode & 0xf000, value.Mode & 0xfff, value.Uid, value.Gid, value.Links, checked((ulong)Math.Max(0, value.Size)));
    private static StatFsFact StatFs(string path)
    {
        if (FStatFsPath(path, out var value) != 0) throw new IsolationException("statfs");
        return new StatFsFact(value.Type, value.BlockSize, value.Blocks, value.Files);
    }
    internal readonly record struct StatFsFact(long Type, long BlockSize, ulong Blocks, ulong Files);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        internal ulong Device, Inode, Links;
        internal uint Mode, Uid, Gid;
        private int Padding;
        private ulong DeviceType;
        internal long Size, BlockSize, Blocks;
        private Timespec Accessed, Modified, Changed;
        private long Reserved0, Reserved1, Reserved2;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Timespec { private long Seconds, Nanoseconds; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatFs
    {
        internal long Type, BlockSize;
        internal ulong Blocks, FreeBlocks, AvailableBlocks, Files, FreeFiles;
        private int FileSystemId0, FileSystemId1;
        internal long NameLength, FragmentSize, Flags;
        private long Spare0, Spare1, Spare2, Spare3;
    }
    internal readonly record struct NodeFact(string Path, ulong Device, ulong Inode, uint Type, uint Mode,
        uint Uid, uint Gid, ulong Links, ulong Size);
    private static bool SameNode(NodeFact left, NodeFact right) => left.Device == right.Device && left.Inode == right.Inode
        && left.Type == right.Type && left.Mode == right.Mode && left.Uid == right.Uid && left.Gid == right.Gid
        && (left.Type == DirectoryType || left.Links == right.Links && left.Size == right.Size);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int FStat(SafeFileHandle fd, out LinuxStat value);
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)] private static extern int LStat(string path, out LinuxStat value);
    [DllImport("libc", EntryPoint = "statfs", SetLastError = true)] private static extern int FStatFsPath(string path, out LinuxStatFs value);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEuid();
}
