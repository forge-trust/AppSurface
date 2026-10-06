using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Sampled Linux metadata. Constructing this value grants no file or deployment ownership.</summary>
internal readonly record struct LinuxProtectedMetadata(uint DeviceMajor, uint DeviceMinor, ulong Inode,
    uint Uid, uint Gid, ushort Mode, uint LinkCount, ulong Length,
    long ChangeSeconds, uint ChangeNanoseconds, long ModifySeconds, uint ModifyNanoseconds)
{
    /// <summary>Rejects unsafe sampled ownership, type, permissions or file bounds.</summary>
    internal void Require(bool directory, bool sharedRead, long maximumBytes)
    {
        var type = Mode & 0xf000;
        if (Uid != 0 || Inode == 0 || (Mode & 0x0c12) != 0 || (directory ? type != 0x4000 : type != 0x8000)
            || (directory ? (Mode & 0x140) != 0x140 : (Mode & 0x100) == 0)
            || (sharedRead && (directory ? (Mode & 0x5) != 0x5 : (Mode & 0x4) == 0))
            || (!directory && (LinkCount != 1 || maximumBytes < 1 || Length is 0 || Length > (ulong)maximumBytes)))
            throw LinuxProtectedDeployment.InvalidDeployment();
    }

    /// <summary>Checks a protected ancestor that needs search access, not directory inventory read access.</summary>
    internal void RequireAncestor(bool sharedTraversal)
    {
        if (Uid != 0 || Inode == 0 || (Mode & 0xf000) != 0x4000 || (Mode & 0x0c12) != 0
            || (Mode & 0x40) == 0 || (sharedTraversal && (Mode & 0x1) == 0))
            throw LinuxProtectedDeployment.InvalidDeployment();
    }

    /// <summary>Compares ancestor identity and access policy, ignoring unrelated child-list churn.</summary>
    internal bool SameAncestorAs(LinuxProtectedMetadata expected) =>
        DeviceMajor == expected.DeviceMajor && DeviceMinor == expected.DeviceMinor && Inode == expected.Inode
        && Uid == expected.Uid && Gid == expected.Gid && Mode == expected.Mode;

    internal static LinuxProtectedMetadata From(Statx value) => new(value.DeviceMajor, value.DeviceMinor,
        value.Inode, value.Uid, value.Gid, value.Mode, value.Nlink, value.Size,
        value.ChangeSeconds, value.ChangeNanoseconds, value.ModifySeconds, value.ModifyNanoseconds);
}

/// <summary>Original monotonic input-work deadline data; it grants no runtime authority.</summary>
internal readonly record struct LinuxInputDeadline(long StartedAt, TimeSpan Allowance)
{
    /// <summary>Checks cancellation and the original remainder before/after each native input operation.</summary>
    internal void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var elapsed = TimeProvider.System.GetElapsedTime(StartedAt);
        if (Allowance <= TimeSpan.Zero || Allowance > TimeSpan.FromHours(1) || elapsed < TimeSpan.Zero || elapsed >= Allowance)
            throw LinuxProtectedDeployment.InvalidDeployment();
    }
}

/// <summary>Bounded Linux getdents64 decoding; directory entry data is never filesystem authority.</summary>
internal static class LinuxDirectoryData
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Decodes one actual kernel block, rejecting malformed, duplicate or unsafe names.</summary>
    internal static IReadOnlyList<string> Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length > 32 * 1024) throw LinuxProtectedDeployment.InvalidDeployment();
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var offset = 0;
            while (offset < block.Length)
            {
                if (block.Length - offset < 20) throw LinuxProtectedDeployment.InvalidDeployment();
                var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 16)..]);
                if (recordLength < 24 || recordLength % 8 != 0 || recordLength > block.Length - offset)
                    throw LinuxProtectedDeployment.InvalidDeployment();
                var bytes = block.Slice(offset + 19, recordLength - 19);
                var end = bytes.IndexOf((byte)0);
                if (end < 1 || end > 255) throw LinuxProtectedDeployment.InvalidDeployment();
                var name = Utf8.GetString(bytes[..end]);
                if (name is not "." and not "..")
                {
                    if (name.Any(static c => char.IsControl(c) || c is '/' or '\\' or ':') || !names.Add(name))
                        throw LinuxProtectedDeployment.InvalidDeployment();
                }
                offset += recordLength;
            }
            return Array.AsReadOnly(names.Order(StringComparer.Ordinal).ToArray());
        }
        catch (DecoderFallbackException) { throw LinuxProtectedDeployment.InvalidDeployment(); }
    }
}

/// <summary>One actually opened node and its retained parent/name binding.</summary>
/// <remarks>Only native open factories construct nodes. Callers join users before disposing the owning collection.</remarks>
internal sealed class LinuxProtectedNode
{
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong FileFlags = 0x80000 | 0x800; // O_NONBLOCK prevents FIFO/device open stalls before type inspection.
    private const ulong ParentResolution = 0x02 | 0x04 | 0x08;
    private const ulong ChildResolution = ParentResolution | 0x01;
    private readonly SafeFileHandle _handle;
    private readonly LinuxProtectedNode? _parent;
    private readonly string _name;
    private readonly bool _directory;
    private readonly bool _sharedRead;
    private readonly bool _allowMount;
    private readonly bool _ancestor;
    private readonly LinuxInputDeadline? _deadline;
    private readonly long _maximumBytes;

    private LinuxProtectedNode(SafeFileHandle handle, LinuxProtectedNode? parent, string name,
        bool directory, bool sharedRead, long maximumBytes, bool allowMount, bool ancestor, LinuxInputDeadline? deadline)
    {
        _handle = handle; _parent = parent; _name = name; _directory = directory;
        _sharedRead = sharedRead; _maximumBytes = maximumBytes; _allowMount = allowMount; _ancestor = ancestor; _deadline = deadline;
        Metadata = LinuxProtectedMetadata.From(StatFd(handle));
        deadline?.Check(default);
        RequireMetadata(Metadata);
    }

    /// <summary>Gets the initial actual retained metadata, for later exact comparisons.</summary>
    internal LinuxProtectedMetadata Metadata { get; }

    /// <summary>Opens an absolute path component by component and retains all ancestors.</summary>
    internal static LinuxProtectedNode OpenAbsolute(string path, bool directory, bool sharedRead,
        long maximumBytes, List<LinuxProtectedNode> owned, CancellationToken token, LinuxInputDeadline? deadline = null)
    {
        LinuxProtectedDeployment.ValidateAbsolute(path);
        LinuxProtectedDeployment.RequirePlatform();
        Check(token, deadline);
        var current = Open(null, "/", true, sharedRead, 0, allowMount: true, ancestor: true, deadline);
        owned.Add(current);
        var parts = path[1..].Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            Check(token, deadline);
            current.Recheck();
            var next = Open(current, parts[i], i < parts.Length - 1 || directory,
                sharedRead, maximumBytes, allowMount: i < parts.Length - 1 || directory, ancestor: i < parts.Length - 1, deadline);
            owned.Add(next);
            current = next;
        }
        Check(token, deadline);
        return current;
    }

    /// <summary>Opens a child of a retained deployment directory without crossing mounts or following links.</summary>
    internal LinuxProtectedNode OpenChild(string name, bool directory, CancellationToken token)
    {
        Check(token, _deadline); Recheck();
        var node = Open(this, name, directory, true, LinuxProtectedDeployment.MaximumFileBytes, allowMount: false, ancestor: false, _deadline);
        try { Check(token, _deadline); return node; }
        catch { node.Close(); throw; }
    }

    private static LinuxProtectedNode Open(LinuxProtectedNode? parent, string name, bool directory,
        bool sharedRead, long maximumBytes, bool allowMount, bool ancestor, LinuxInputDeadline? deadline)
    {
        deadline?.Check(default);
        var handle = OpenAt2(parent is null ? -100 : Fd(parent._handle), name,
            directory ? DirectoryFlags : FileFlags, 0, parent is null ? 0 : allowMount ? ParentResolution : ChildResolution);
        try { return new(handle, parent, name, directory, sharedRead, maximumBytes, allowMount, ancestor, deadline); }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Checks the retained object and its current directory name, not a managed pathname lookup.</summary>
    internal void Recheck()
    {
        _deadline?.Check(default);
        var current = LinuxProtectedMetadata.From(StatFd(_handle));
        _deadline?.Check(default);
        RequireMetadata(current);
        if (!MatchesMetadata(current)) throw LinuxProtectedDeployment.InvalidDeployment();
        if (_parent is null) return;
        using var named = OpenAt2(Fd(_parent._handle), _name, 0x200000 | 0x80000,
            0, _allowMount ? ParentResolution : ChildResolution);
        if (!MatchesMetadata(LinuxProtectedMetadata.From(StatFd(named)))) throw LinuxProtectedDeployment.InvalidDeployment();
        _deadline?.Check(default);
    }

    /// <summary>Enumerates the retained directory through getdents64, inspecting child type separately through statx.</summary>
    internal IReadOnlyList<string> ReadNames(CancellationToken token)
    {
        if (!_directory) throw LinuxProtectedDeployment.InvalidDeployment();
        Check(token, _deadline); Recheck();
        using var fresh = OpenAt2(Fd(_handle), ".", DirectoryFlags, 0, ChildResolution);
        var buffer = new byte[32 * 1024];
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            Check(token, _deadline);
            var length = ReadDirectory(fresh, buffer);
            if (length == 0) break;
            foreach (var name in LinuxDirectoryData.Parse(buffer.AsSpan(0, length)))
                if (names.Count >= LinuxProtectedDeployment.MaximumNodes || !names.Add(name))
                    throw LinuxProtectedDeployment.InvalidDeployment();
        }
        Check(token, _deadline); Recheck();
        return Array.AsReadOnly(names.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Classifies an actual link-safe child, without opening special files for data access.</summary>
    internal bool IsChildDirectory(string name)
    {
        _deadline?.Check(default);
        using var probe = OpenAt2(Fd(_handle), name, 0x200000 | 0x80000, 0, ChildResolution);
        var metadata = LinuxProtectedMetadata.From(StatFd(probe));
        var directory = (metadata.Mode & 0xf000) == 0x4000;
        metadata.Require(directory, true, LinuxProtectedDeployment.MaximumFileBytes);
        return directory;
    }

    /// <summary>Hashes exact retained bytes and samples identity/time/name before and after the read.</summary>
    internal string Hash(CancellationToken token, LinuxInputDeadline? deadline = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Read(token, memory => hash.AppendData(memory.Span), deadline);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Reads one small protected request, never the full deployment into managed memory.</summary>
    internal byte[] ReadRequest(CancellationToken token) => ReadBounded(64 * 1024, token);

    /// <summary>Reads one retained regular file under a positive counted limit, with original deadline/name checks.</summary>
    /// <remarks>The limit is comparison data; the already opened node and its retained parent chain select the file.</remarks>
    internal byte[] ReadBounded(long maximumBytes, CancellationToken token, LinuxInputDeadline? deadline = null)
    {
        if (_directory || maximumBytes <= 0 || maximumBytes > EvidenceCanonicalJson.MaximumInputBytes
            || Metadata.Length > (ulong)maximumBytes) throw LinuxProtectedDeployment.InvalidDeployment();
        var bytes = new byte[checked((int)Metadata.Length)];
        var offset = 0;
        Read(token, memory => { memory.Span.CopyTo(bytes.AsSpan(offset)); offset += memory.Length; }, deadline);
        return bytes;
    }

    private void Read(CancellationToken token, Action<ReadOnlyMemory<byte>> receive, LinuxInputDeadline? deadline = null)
    {
        if (_directory) throw LinuxProtectedDeployment.InvalidDeployment();
        Check(token, deadline ?? _deadline); Recheck();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long offset = 0;
            while ((ulong)offset < Metadata.Length)
            {
                Check(token, deadline ?? _deadline);
                var count = (int)Math.Min((ulong)buffer.Length, Metadata.Length - (ulong)offset);
                var read = RandomAccess.Read(_handle, buffer.AsSpan(0, count), offset);
                if (read == 0) throw LinuxProtectedDeployment.InvalidDeployment();
                receive(buffer.AsMemory(0, read)); offset += read;
            }
            Check(token, deadline ?? _deadline);
            if (RandomAccess.Read(_handle, buffer.AsSpan(0, 1), offset) != 0) throw LinuxProtectedDeployment.InvalidDeployment();
            Check(token, deadline ?? _deadline); Recheck();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private void RequireMetadata(LinuxProtectedMetadata metadata)
    {
        if (_ancestor) metadata.RequireAncestor(_sharedRead);
        else metadata.Require(_directory, _sharedRead, _maximumBytes);
    }
    private bool MatchesMetadata(LinuxProtectedMetadata metadata) =>
        _ancestor ? metadata.SameAncestorAs(Metadata) : metadata == Metadata;
    private static void Check(CancellationToken token, LinuxInputDeadline? deadline)
    {
        token.ThrowIfCancellationRequested(); deadline?.Check(token);
    }

    /// <summary>Closes this descriptor only after the collection's users have joined.</summary>
    internal void Close() => _handle.Dispose();
}

/// <summary>A retained, fully inventoried protected runtime/tool tree. It is not admission.</summary>
internal sealed class LinuxProtectedDeployment : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const int MaximumNodes = 8192;
    internal const long MaximumFileBytes = 256L * 1024 * 1024;
    private const long MaximumTotalBytes = 1024L * 1024 * 1024;
    private readonly List<LinuxProtectedNode> _owned;
    private readonly IReadOnlyDictionary<string, (LinuxProtectedNode Node, string Hash)> _files;
    private int _closed;

    private LinuxProtectedDeployment(List<LinuxProtectedNode> owned,
        Dictionary<string, (LinuxProtectedNode Node, string Hash)> files)
    {
        _owned = owned; _files = new ReadOnlyDictionary<string, (LinuxProtectedNode, string)>(files);
    }

    /// <summary>Inventories every file below a root-owned read/searchable, untrusted-unwritable tree.</summary>
    /// <remarks>No caller file list can omit dependencies or probing candidates. Empty files are rejected.</remarks>
    internal static LinuxProtectedDeployment Open(string root, CancellationToken token, LinuxInputDeadline? deadline = null)
    {
        var owned = new List<LinuxProtectedNode>();
        try
        {
            var directory = LinuxProtectedNode.OpenAbsolute(root, true, true, 0, owned, token, deadline);
            var files = new Dictionary<string, (LinuxProtectedNode Node, string Hash)>(StringComparer.Ordinal);
            long bytes = 0;
            Walk(directory, string.Empty, 0);
            if (files.Count == 0) throw InvalidDeployment();
            foreach (var node in owned) { token.ThrowIfCancellationRequested(); node.Recheck(); }
            return new(owned, files);

            void Walk(LinuxProtectedNode parent, string prefix, int depth)
            {
                if (depth > 32) throw InvalidDeployment();
                foreach (var name in parent.ReadNames(token))
                {
                    if (owned.Count >= MaximumNodes) throw InvalidDeployment();
                    var relative = prefix + name;
                    if (Encoding.UTF8.GetByteCount(relative) > 4096) throw InvalidDeployment();
                    var isDirectory = parent.IsChildDirectory(name);
                    var child = parent.OpenChild(name, isDirectory, token);
                    owned.Add(child);
                    if (isDirectory) Walk(child, relative + "/", depth + 1);
                    else
                    {
                        if (child.Metadata.Length > (ulong)(MaximumTotalBytes - bytes)) throw InvalidDeployment();
                        bytes += checked((long)child.Metadata.Length);
                        files.Add(relative, (child, child.Hash(token)));
                    }
                }
            }
        }
        catch { Close(owned); throw; }
    }

    /// <summary>Returns the retained digest only for an inventoried exact relative file.</summary>
    internal string RequireFile(string relativePath, bool executable = false)
    {
        ThrowIfClosed();
        if (!_files.TryGetValue(relativePath, out var file) || (executable && (file.Node.Metadata.Mode & 0x49) != 0x49))
            throw InvalidDeployment();
        return file.Hash;
    }

    /// <summary>Copies counted bytes from an already inventoried file and verifies their original measured digest.</summary>
    /// <remarks>This exposes no descriptor, pathname-opening callback or execution capability.</remarks>
    internal byte[] ReadFile(string relativePath, CancellationToken token, LinuxInputDeadline deadline)
    {
        ThrowIfClosed();
        if (!_files.TryGetValue(relativePath, out var file)) throw InvalidDeployment();
        var bytes = file.Node.ReadBounded(EvidenceCanonicalJson.MaximumInputBytes, token, deadline);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Hash) throw InvalidDeployment();
        deadline.Check(token);
        file.Node.Recheck();
        ThrowIfClosed();
        deadline.Check(token);
        return bytes;
    }

    /// <summary>Rechecks all ancestor/name metadata and every retained file's exact bytes before use.</summary>
    internal void Recheck(CancellationToken token)
    {
        ThrowIfClosed();
        foreach (var node in _owned) { token.ThrowIfCancellationRequested(); node.Recheck(); }
        foreach (var file in _files.Values)
            if (file.Node.Hash(token) != file.Hash) throw InvalidDeployment();
        foreach (var node in _owned) { token.ThrowIfCancellationRequested(); node.Recheck(); }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Closes retained descriptors after all readers/start operations have joined.</summary>
    public void Dispose() { if (Interlocked.Exchange(ref _closed, 1) == 0) Close(_owned); }
    private void ThrowIfClosed() { if (Volatile.Read(ref _closed) != 0) throw InvalidDeployment(); }
    internal static void Close(List<LinuxProtectedNode> nodes) { for (var i = nodes.Count - 1; i >= 0; i--) nodes[i].Close(); }

    /// <summary>Requires actual root Linux x64 before invoking the native handle operations.</summary>
    internal static void RequirePlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || GetEffectiveUid() != 0)
            throw InvalidDeployment();
    }

    /// <summary>Validates canonical absolute Linux syntax only; does not inspect or authorize the path.</summary>
    internal static void ValidateAbsolute(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/' || StrictUtf8.GetByteCount(path) > 4096
                || path.Any(static c => char.IsControl(c) || c is '\\' or ':')
                || path[1..].Split('/').Any(static part => part is "" or "." or ".." || StrictUtf8.GetByteCount(part) > 255))
                throw InvalidDeployment();
        }
        catch (EncoderFallbackException) { throw InvalidDeployment(); }
    }

    /// <summary>Creates a fixed protected-input error without private filenames or native details.</summary>
    internal static EvidenceAdmissionException InvalidDeployment() =>
        new("ASEVD402", "The protected deployment or launch input is unavailable or changed.");

    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
}
