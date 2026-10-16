using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Owns retained report descriptors and checks their original names, inventory and bytes.</summary>
/// <remarks>
/// This is file custody only: neither this object nor its sampled metadata establishes producer,
/// subject-group, output-pump or admission authority. A future private integration must join the
/// authentic producers before constructing it. There is no server integration in this primitive.
/// The root caller supplies the exact inventory and expected owner/group; no policy is selected here.
/// All operations and close are serialized. Any read/check failure is sticky until disposal.
/// </remarks>
internal sealed class LinuxRetainedReportSet : IDisposable
{
    internal const int MaximumFiles = 64;
    internal const long MaximumFileBytes = 1024 * 1024;
    internal const long MaximumAggregateBytes = 256L * 1024 * 1024;
    internal const int MaximumChunkBytes = 64 * 1024;
    private const int MaximumNodes = 4096;
    private const ulong PathFlags = 0x200000 | 0x80000;
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong FileFlags = 0x80000 | 0x800;
    private const ulong AncestorResolution = 0x02 | 0x04 | 0x08;
    private const ulong ChildResolution = AncestorResolution | 0x01;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _gate = new();
    private readonly List<Node> _nodes = [];
    private readonly Dictionary<string, FileReader> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<Node, string[]> _inventories = [];
    private readonly LinuxInputDeadline _deadline;
    private readonly uint _uid;
    private readonly uint _gid;
    private IReadOnlyList<ReportInfo> _reports = Array.AsReadOnly(Array.Empty<ReportInfo>());
    private bool _failed;
    private bool _closed;

    private LinuxRetainedReportSet(uint uid, uint gid, LinuxInputDeadline deadline)
    {
        _uid = uid;
        _gid = gid;
        _deadline = deadline;
    }

    /// <summary>Sampled report identity data; possession grants no filesystem or producer authority.</summary>
    internal readonly record struct ReportInfo(string RelativePath, long Length, string Sha256);

    /// <summary>Opens and retains the exact supplied tree on Linux x64 as root, using mandatory openat2.</summary>
    /// <remarks>
    /// Paths must already be normalized relative names, at most 4095 UTF-8 bytes, 32 components,
    /// and 255 bytes per component. Case aliases, duplicates and file/directory collisions reject.
    /// At most 64 regular single-link files of at most 1 MiB each are allowed, with a separate
    /// 256 MiB aggregate ceiling (the file-count ceiling currently gives a tighter total).
    /// Empty files and an empty inventory are permitted and prove no successful producer output.
    /// The caller supplies expectedRoot from its authentic root selection; the factory compares
    /// device/inode/owner/group/mode before inventory traversal and retains fresh full metadata.
    /// Expected metadata and the supplied inventory are comparisons, never uploaded permission.
    /// The explicit absolute ancestor chain may enter the selected root filesystem; every lookup
    /// beneath that root forbids mount crossings, symlinks and magic links, without fallback.
    /// Ancestors must be root-owned and untrusted-unwritable. Result nodes require the supplied
    /// owner/group, owner read/search access and no special bits or group/other write access.
    /// </remarks>
    internal static LinuxRetainedReportSet OpenActual(string resultRoot, LinuxProtectedMetadata expectedRoot,
        IReadOnlyList<string> reportPaths,
        uint expectedUid, uint expectedGid, LinuxInputDeadline deadline, CancellationToken token = default)
    {
        LinuxProtectedDeployment.RequirePlatform();
        LinuxProtectedDeployment.ValidateAbsolute(resultRoot);
        RequireMetadata(expectedRoot, true, expectedUid, expectedGid);
        var paths = ValidatePaths(reportPaths);
        var set = new LinuxRetainedReportSet(expectedUid, expectedGid, deadline);
        try
        {
            set.Check(token);
            var root = set.Add(null, "/", true, true, true, token);
            var parts = resultRoot[1..].Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                root = set.Add(root, parts[i], true, i != parts.Length - 1, true, token);
            }
            RequireRootIdentity(root.Expected, expectedRoot, expectedUid, expectedGid);
            set.OpenTree(root, "", paths, token);
            set.RecheckAll(token);
            long total = 0;
            var reports = new List<ReportInfo>(paths.Length);
            foreach (var path in paths)
            {
                var node = set._nodes.Single(n => n.RelativePath == path && !n.Directory);
                total = AddLength(total, node.Expected.Length);
                set.RecheckAll(token);
                var reader = FileReader.Capture(node.Handle, checked((long)node.Expected.Length), deadline,
                    () => set.RecheckChain(node, token), node.Handle, token);
                set._files.Add(path, reader);
                reports.Add(new(path, reader.Length, reader.Sha256));
                set.RecheckAll(token);
            }
            set._reports = Array.AsReadOnly(reports.ToArray());
            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    /// <summary>Returns immutable sampled lengths/digests after rechecking the retained exact tree.</summary>
    internal IReadOnlyList<ReportInfo> ListReports(CancellationToken token = default)
    {
        lock (_gate)
        {
            RequireOpen();
            try { RecheckAll(token); return _reports; }
            catch { _failed = true; throw; }
        }
    }

    /// <summary>Reads at the next exact offset only, returning at most 64 KiB and checking actual final EOF.</summary>
    /// <remarks>
    /// Offsets begin at zero independently per report. Replay, skipping, unknown paths, nonpositive
    /// chunk bounds, and reads after the final chunk reject and poison the set. An empty file yields
    /// one empty chunk. Bytes are returned only after all descriptor/name/inventory checks succeed.
    /// Cancellation or expiry also poisons the set; retrying cannot create a fresh deadline.
    /// </remarks>
    internal byte[] ReadChunk(string relativePath, long offset, int maximumBytes,
        CancellationToken token = default)
    {
        lock (_gate)
        {
            RequireOpen();
            try
            {
                RecheckAll(token);
                if (relativePath is null || !_files.TryGetValue(relativePath, out var reader)) throw Invalid();
                var bytes = reader.ReadChunk(offset, maximumBytes, token);
                RecheckAll(token);
                return bytes;
            }
            catch { _failed = true; throw; }
        }
    }

    /// <summary>Joins serialized reads and closes every retained descriptor, in reverse acquisition order.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            try
            {
                foreach (var file in _files.Values) file.Dispose();
            }
            finally
            {
                for (var i = _nodes.Count - 1; i >= 0; i--) _nodes[i].Handle.Dispose();
            }
        }
    }

    /// <summary>Validates and defensively copies inventory data; this performs no I/O or authorization.</summary>
    internal static string[] ValidatePaths(IReadOnlyList<string> paths)
    {
        if (paths is null) throw Invalid();
        var count = paths.Count;
        if (count < 0 || count > MaximumFiles) throw Invalid();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var path = paths[index];
                if (string.IsNullOrEmpty(path) || path != path.Trim() || path.StartsWith('/')
                    || StrictUtf8.GetByteCount(path) > 4095
                    || path.Any(static c => char.IsControl(c) || c is '\\' or ':')) throw Invalid();
                var parts = path.Split('/');
                if (parts.Length > 32 || parts.Any(static p => p is "" or "." or ".."
                    || StrictUtf8.GetByteCount(p) > 255) || !names.Add(path)) throw Invalid();
                result.Add(path);
            }
            if (paths.Count != count) throw Invalid();
        }
        catch (EncoderFallbackException) { throw Invalid(); }
        foreach (var path in result)
        {
            var prefix = path;
            while (prefix.LastIndexOf('/') is var slash && slash >= 0)
            {
                prefix = prefix[..slash];
                if (names.Contains(prefix)) throw Invalid();
            }
        }
        // Case aliases of directory components must not select two distinct Linux directories either.
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in result)
        {
            var slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                var prefix = path[..slash];
                if (directories.TryGetValue(prefix, out var original) && original != prefix) throw Invalid();
                directories[prefix] = prefix;
                slash = prefix.LastIndexOf('/');
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Checks per-file and aggregate size comparisons without issuing a retained handle.</summary>
    internal static long AddLength(long current, ulong length)
    {
        if (current < 0 || current > MaximumAggregateBytes || length > MaximumFileBytes
            || length > (ulong)(MaximumAggregateBytes - current)) throw Invalid();
        return current + (long)length;
    }

    /// <summary>Checks sampled type, identity, ownership and safe mode; data alone is never custody.</summary>
    internal static void RequireMetadata(LinuxProtectedMetadata value, bool directory, uint uid, uint gid)
    {
        if (value.Uid != uid || value.Gid != gid || value.Inode == 0
            || (value.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)
            || (value.Mode & 0x0e12) != 0 || (value.Mode & (directory ? 0x140 : 0x100))
                != (directory ? 0x140 : 0x100)
            || value.LinkCount == 0 || (!directory && (value.LinkCount != 1 || value.Length > MaximumFileBytes))
            || value.ChangeNanoseconds >= 1_000_000_000 || value.ModifyNanoseconds >= 1_000_000_000)
            throw Invalid();
    }

    /// <summary>Compares selected root identity; this pure guard cannot establish how selection occurred.</summary>
    internal static void RequireRootIdentity(LinuxProtectedMetadata actual, LinuxProtectedMetadata expected,
        uint uid, uint gid)
    {
        RequireMetadata(actual, true, uid, gid);
        RequireMetadata(expected, true, uid, gid);
        if (!actual.SameAncestorAs(expected)) throw Invalid();
    }

    private Node Add(Node? parent, string name, bool directory, bool ancestor, bool allowMount,
        CancellationToken token, string? relativePath = null)
    {
        Check(token);
        if (_nodes.Count >= MaximumNodes) throw Invalid();
        if (parent is not null) RecheckChain(parent, token);
        var resolution = parent is null ? 0UL : allowMount ? AncestorResolution : ChildResolution;
        using var probe = OpenAt2(parent is null ? -100 : Fd(parent.Handle), name, PathFlags, 0, resolution);
        var expected = LinuxProtectedMetadata.From(StatFd(probe));
        if (ancestor) expected.RequireAncestor(false);
        else RequireMetadata(expected, directory, _uid, _gid);
        var handle = OpenAt2(parent is null ? -100 : Fd(parent.Handle), name,
            directory ? DirectoryFlags : FileFlags, 0, resolution);
        try
        {
            if (LinuxProtectedMetadata.From(StatFd(handle)) != expected) throw Invalid();
            var node = new Node(handle, parent, name, directory, ancestor, allowMount, expected, relativePath);
            _nodes.Add(node);
            RecheckChain(node, token);
            return node;
        }
        catch { handle.Dispose(); throw; }
    }

    private void OpenTree(Node directory, string prefix, string[] paths, CancellationToken token)
    {
        var children = paths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p[prefix.Length..].Split('/')[0]).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        _inventories.Add(directory, children);
        RequireNames(directory, children, token);
        foreach (var name in children)
        {
            var path = prefix + name;
            var isDirectory = !paths.Contains(path, StringComparer.Ordinal);
            var child = Add(directory, name, isDirectory, false, false, token, path);
            if (isDirectory) OpenTree(child, path + "/", paths, token);
        }
        RequireNames(directory, children, token);
    }

    private void RequireNames(Node node, string[] expected, CancellationToken token)
    {
        RecheckChain(node, token);
        using var fresh = OpenAt2(Fd(node.Handle), ".", DirectoryFlags, 0, ChildResolution);
        if (LinuxProtectedMetadata.From(StatFd(fresh)) != node.Expected) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new byte[32 * 1024];
        var blocks = 0;
        while (true)
        {
            Check(token);
            if (++blocks > MaximumNodes) throw Invalid();
            var count = ReadDirectory(fresh, buffer);
            Check(token);
            if (count == 0) break;
            foreach (var name in LinuxDirectoryData.Parse(buffer.AsSpan(0, count)))
            {
                if (names.Count >= expected.Length || !names.Add(name)
                    || !expected.Contains(name, StringComparer.Ordinal)) throw Invalid();
            }
        }
        if (names.Count != expected.Length) throw Invalid();
        RecheckChain(node, token);
    }

    private void RecheckAll(CancellationToken token)
    {
        Check(token);
        foreach (var node in _nodes) RecheckNode(node, token);
        foreach (var (node, names) in _inventories) RequireNames(node, names, token);
        Check(token);
    }

    private void RecheckChain(Node node, CancellationToken token)
    {
        if (node.Parent is not null) RecheckChain(node.Parent, token);
        RecheckNode(node, token);
    }

    private void RecheckNode(Node node, CancellationToken token)
    {
        Check(token);
        var current = LinuxProtectedMetadata.From(StatFd(node.Handle));
        if (node.Ancestor)
        {
            current.RequireAncestor(false);
            if (!current.SameAncestorAs(node.Expected)) throw Invalid();
        }
        else
        {
            RequireMetadata(current, node.Directory, _uid, _gid);
            if (current != node.Expected) throw Invalid();
        }
        using var named = OpenAt2(node.Parent is null ? -100 : Fd(node.Parent.Handle), node.Name,
            PathFlags, 0, node.Parent is null ? 0UL : node.AllowMount ? AncestorResolution : ChildResolution);
        var namedMetadata = LinuxProtectedMetadata.From(StatFd(named));
        if (node.Ancestor ? !namedMetadata.SameAncestorAs(node.Expected) : namedMetadata != node.Expected)
            throw Invalid();
        Check(token);
    }

    private void Check(CancellationToken token) => _deadline.Check(token);

    private void RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_failed) throw Invalid();
    }

    private static IOException Invalid() => new("The retained report set is unavailable or changed.");

    private sealed record Node(SafeFileHandle Handle, Node? Parent, string Name, bool Directory,
        bool Ancestor, bool AllowMount, LinuxProtectedMetadata Expected, string? RelativePath);

    /// <summary>Shared monotonic read engine; it has no root, producer or admission capability.</summary>
    internal sealed class FileReader : IDisposable
    {
        private readonly object _gate = new();
        private readonly SafeFileHandle _handle;
        private readonly IDisposable _owner;
        private readonly Action _sample;
        private readonly LinuxInputDeadline _deadline;
        private long _next;
        private bool _finished;
        private bool _failed;
        private bool _closed;

        private FileReader(SafeFileHandle handle, long length, LinuxInputDeadline deadline,
            Action sample, IDisposable owner)
        {
            _handle = handle;
            Length = length;
            _deadline = deadline;
            _sample = sample;
            _owner = owner;
        }

        /// <summary>Gets the original sampled byte count; it is not evidence authority.</summary>
        internal long Length { get; }
        /// <summary>Gets the hash of the complete original descriptor bytes including checked actual EOF.</summary>
        internal string Sha256 { get; private set; } = "";

        internal static FileReader Capture(SafeFileHandle handle, long length, LinuxInputDeadline deadline,
            Action sample, IDisposable owner, CancellationToken token)
        {
            var reader = new FileReader(handle, length, deadline, sample, owner);
            try
            {
                if (length < 0 || length > MaximumFileBytes || handle.IsInvalid || handle.IsClosed) throw Invalid();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[MaximumChunkBytes];
                long offset = 0;
                reader.Sample(token);
                while (offset < length)
                {
                    reader.Sample(token);
                    var count = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                    if (count == 0) throw Invalid();
                    hash.AppendData(buffer.AsSpan(0, count));
                    offset += count;
                    reader.Sample(token);
                }
                if (RandomAccess.Read(handle, buffer.AsSpan(0, 1), length) != 0) throw Invalid();
                reader.Sample(token);
                reader.Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
                return reader;
            }
            catch { reader.Dispose(); throw; }
        }

        /// <summary>
        /// Owns an actual portable temporary-file stream for read-engine tests only. This seam checks
        /// stream length, not Linux name/inode custody, and cannot construct a retained report set.
        /// Ownership transfers immediately, including on failure; do not use it in protected integration.
        /// </summary>
        internal static FileReader CapturePortableFileForTesting(FileStream ownedFile, LinuxInputDeadline deadline,
            Action? sample = null)
        {
            ArgumentNullException.ThrowIfNull(ownedFile);
            try
            {
                var length = ownedFile.Length;
                return Capture(ownedFile.SafeFileHandle, length, deadline, () =>
                {
                    if (ownedFile.Length != length) throw Invalid();
                    sample?.Invoke();
                }, ownedFile, default);
            }
            catch { ownedFile.Dispose(); throw; }
        }

        /// <summary>Consumes one exact next offset under the original deadline; failures remain sticky.</summary>
        internal byte[] ReadChunk(long offset, int maximumBytes, CancellationToken token = default)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_failed) throw Invalid();
                try
                {
                    if (_finished || offset != _next || maximumBytes < 1 || maximumBytes > MaximumChunkBytes)
                        throw Invalid();
                    Sample(token);
                    var bytes = new byte[(int)Math.Min(maximumBytes, Length - offset)];
                    var count = 0;
                    while (count < bytes.Length)
                    {
                        Sample(token);
                        var read = RandomAccess.Read(_handle, bytes.AsSpan(count), offset + count);
                        if (read == 0) throw Invalid();
                        count += read;
                        Sample(token);
                    }
                    if (offset + count == Length)
                    {
                        Span<byte> extra = stackalloc byte[1];
                        if (RandomAccess.Read(_handle, extra, Length) != 0) throw Invalid();
                        _finished = true;
                    }
                    Sample(token);
                    _next += count;
                    return bytes;
                }
                catch { _failed = true; throw; }
            }
        }

        /// <summary>Waits for the serialized read to end and closes its owned handle exactly once.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                _owner.Dispose();
            }
        }

        private void Sample(CancellationToken token)
        {
            _deadline.Check(token);
            _sample();
            _deadline.Check(token);
        }
    }
}
