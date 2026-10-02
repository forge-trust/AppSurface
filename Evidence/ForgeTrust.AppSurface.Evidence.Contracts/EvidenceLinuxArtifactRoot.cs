using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Closed host operations retained when exclusive artifact allocation fails.</summary>
internal enum EvidenceLinuxArtifactAllocationOperation
{
    None, ValidateArguments, CheckPlatform, OpenFilesystemRoot, OpenParent, CheckParentIdentity,
    CheckParentName, CreateSlot, OpenSlot, InspectSlotIdentity, CheckSlotPolicy, CheckSlotName,
    RecheckParentName, RetainDescriptors, Completed,
}

/// <summary>Identifies a Linux filesystem object by device, inode, owner and group.</summary>
/// <param name="DeviceMajor">Linux device major number.</param>
/// <param name="DeviceMinor">Linux device minor number.</param>
/// <param name="Inode">Filesystem inode number.</param>
/// <param name="Uid">Owning user ID.</param>
/// <param name="Gid">Owning group ID.</param>
/// <remarks>The identity is a comparison fact, not an authorization token. Obtain expected values through a protected channel.</remarks>
internal readonly record struct EvidenceLinuxArtifactIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode, uint Uid, uint Gid)
{
    /// <summary>Returns a stable, bounded representation suitable for protected policy configuration.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"linux:{DeviceMajor:x8}:{DeviceMinor:x8}:{Inode:x16}:{Uid:x8}:{Gid:x8}");
}

/// <summary>Owns a freshly allocated Linux artifact directory and retained descriptors for files written into it.</summary>
/// <remarks>
/// Allocation requires Linux <c>openat2</c> with <c>RESOLVE_BENEATH</c>, <c>RESOLVE_NO_SYMLINKS</c>,
/// <c>RESOLVE_NO_MAGICLINKS</c> and <c>RESOLVE_NO_XDEV</c>. There is no path-based fallback. Callers must
/// allocate only after protected policy admission, activate only after inspecting <see cref="Identity"/>,
/// join all writers/verifiers, and then dispose exactly once. This internal provisional mechanism does not
/// itself establish consumer acceptance or make untrusted code safe to run in-process.
/// </remarks>
internal sealed class EvidenceLinuxArtifactRoot : IAsyncDisposable
{
    private const ulong ResolveNoXdev = 0x01;
    private const ulong ResolveNoMagiclinks = 0x02;
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;
    private const ulong ParentResolveFlags = ResolveBeneath | ResolveNoSymlinks | ResolveNoMagiclinks;
    private const ulong DescendantResolveFlags = ParentResolveFlags | ResolveNoXdev;
    private const ulong O_RDONLY = 0;
    private const ulong O_WRONLY = 1;
    private const ulong O_RDWR = 2;
    private const ulong O_CREAT = 0x40;
    private const ulong O_EXCL = 0x80;
    private const ulong O_CLOEXEC = 0x80000;
    private const ulong O_DIRECTORY = 0x10000;
    private const ulong O_NOFOLLOW = 0x20000;
    private const ulong O_PATH = 0x200000;
    private const int AtFdcwd = -100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x7ff;
    private const uint DirectoryMode = 0x1c0;
    private const uint ArtifactFileMode = 0x180;
    private const uint PermissionMask = 0xfff;
    private const int MaximumPathBytes = 4096;
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const long MaximumTotalBytes = 256L * 1024 * 1024;

    private readonly string _parentPath;
    private readonly string _slotName;
    private readonly uint _uid;
    private readonly uint _gid;
    private readonly SafeFileHandle _slash;
    private readonly SafeFileHandle _parent;
    private readonly SafeFileHandle _root;
    private readonly Dictionary<string, RetainedFile> _files = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private long _totalBytes;
    private bool _disposed;

    private EvidenceLinuxArtifactRoot(string parentPath, string slotName, uint uid, uint gid, SafeFileHandle slash, SafeFileHandle parent, SafeFileHandle root, EvidenceLinuxArtifactIdentity identity)
    {
        _parentPath = parentPath;
        _slotName = slotName;
        _uid = uid;
        _gid = gid;
        _slash = slash;
        _parent = parent;
        _root = root;
        Identity = identity;
    }

    /// <summary>Gets the actual retained root identity for the second admission/activation step.</summary>
    internal EvidenceLinuxArtifactIdentity Identity { get; }

    /// <summary>
    /// Resolves and validates an already protected parent, then exclusively creates a fresh child directory.
    /// </summary>
    /// <param name="parentPath">Absolute protected parent path, resolved without following any symlink.</param>
    /// <param name="expectedParentIdentity">Device, inode, UID and GID admitted for the parent.</param>
    /// <param name="slotName">One fresh single-component child name; separators, dot names and control characters are rejected.</param>
    /// <param name="expectedUid">Required owner for parent, root, subdirectories and artifact files.</param>
    /// <param name="expectedGid">Required group for parent, root, subdirectories and artifact files.</param>
    /// <returns>A root retaining the parent, root and filesystem-root handles.</returns>
    /// <exception cref="PlatformNotSupportedException">The OS, architecture, syscall or required resolution flags are unavailable.</exception>
    /// <exception cref="IOException">The parent identity/policy is wrong, the slot collides, or safe allocation fails.</exception>
    /// <remarks>The parent and allocated root must have exact mode <c>0700</c>. The parent is re-resolved before and after creation.</remarks>
    internal static EvidenceLinuxArtifactRoot Allocate(string parentPath, EvidenceLinuxArtifactIdentity expectedParentIdentity, string slotName, uint expectedUid, uint expectedGid)
        => Allocate(parentPath, expectedParentIdentity, slotName, expectedUid, expectedGid, out _);

    /// <summary>Allocates with the same guards and exceptions, retaining the last attempted closed operation.</summary>
    /// <param name="parentPath">Absolute protected parent path.</param>
    /// <param name="expectedParentIdentity">Protected parent identity.</param>
    /// <param name="slotName">Fresh single-component slot.</param>
    /// <param name="expectedUid">Exact required owner.</param>
    /// <param name="expectedGid">Exact required group.</param>
    /// <param name="operation">Last attempted operation on failure; Completed on success. Diagnostic only, never authority.</param>
    /// <returns>The same retained root as the overload without diagnostics.</returns>
    internal static EvidenceLinuxArtifactRoot Allocate(string parentPath, EvidenceLinuxArtifactIdentity expectedParentIdentity,
        string slotName, uint expectedUid, uint expectedGid, out EvidenceLinuxArtifactAllocationOperation operation)
    {
        operation = EvidenceLinuxArtifactAllocationOperation.ValidateArguments;
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        ValidateComponent(slotName, nameof(slotName));
        if (!Path.IsPathFullyQualified(parentPath) || Encoding.UTF8.GetByteCount(parentPath) > MaximumPathBytes)
            throw new ArgumentException("An absolute bounded parent path is required.", nameof(parentPath));
        operation = EvidenceLinuxArtifactAllocationOperation.CheckPlatform;
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Linux openat2 artifact storage is unavailable on this platform.");

        SafeFileHandle? slash = null;
        SafeFileHandle? parent = null;
        SafeFileHandle? root = null;
        try
        {
            operation = EvidenceLinuxArtifactAllocationOperation.OpenFilesystemRoot;
            slash = OpenAt2(AtFdcwd, "/", O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0, 0);
            operation = EvidenceLinuxArtifactAllocationOperation.OpenParent;
            parent = OpenAt2(Fd(slash), ToRootRelative(parentPath), O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0, ParentResolveFlags);
            operation = EvidenceLinuxArtifactAllocationOperation.CheckParentIdentity;
            RequireIdentity(parent, expectedParentIdentity, expectedUid, expectedGid, DirectoryMode, "Protected parent identity or policy changed.");
            operation = EvidenceLinuxArtifactAllocationOperation.CheckParentName;
            RequireNamedIdentity(slash, ToRootRelative(parentPath), parent, DirectoryMode, ParentResolveFlags, "Protected parent path no longer names its retained directory.");
            operation = EvidenceLinuxArtifactAllocationOperation.CreateSlot;
            if (MkdirAt(Fd(parent), slotName, DirectoryMode) != 0)
                throw IoError("Could not exclusively create the artifact root.");

            operation = EvidenceLinuxArtifactAllocationOperation.OpenSlot;
            root = OpenAt2(Fd(parent), slotName, O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0, DescendantResolveFlags);
            operation = EvidenceLinuxArtifactAllocationOperation.InspectSlotIdentity;
            var identity = IdentityOf(root);
            operation = EvidenceLinuxArtifactAllocationOperation.CheckSlotPolicy;
            if (identity.Uid != expectedUid || identity.Gid != expectedGid || ModeOf(root) != DirectoryMode)
                throw new IOException("Allocated artifact root does not match the required owner and mode.");
            operation = EvidenceLinuxArtifactAllocationOperation.CheckSlotName;
            RequireNamedIdentity(parent, slotName, root, DirectoryMode, DescendantResolveFlags, "Artifact root name changed during allocation.");
            operation = EvidenceLinuxArtifactAllocationOperation.RecheckParentName;
            RequireNamedIdentity(slash, ToRootRelative(parentPath), parent, DirectoryMode, ParentResolveFlags, "Protected parent changed during allocation.");
            operation = EvidenceLinuxArtifactAllocationOperation.RetainDescriptors;
            var result = new EvidenceLinuxArtifactRoot(parentPath, slotName, expectedUid, expectedGid, slash, parent, root, identity);
            slash = parent = root = null;
            operation = EvidenceLinuxArtifactAllocationOperation.Completed;
            return result;
        }
        catch
        {
            root?.Dispose();
            parent?.Dispose();
            slash?.Dispose();
            throw;
        }
    }

    /// <summary>Inspects an existing directory through the no-symlink, no-mount-crossing Linux resolver.</summary>
    /// <param name="directoryPath">Absolute directory path whose identity is obtained for protected configuration or tests.</param>
    /// <returns>The device, inode, UID and GID currently bound to the opened directory.</returns>
    /// <remarks>This reports identity only; it does not validate ownership policy or grant allocation authority.</remarks>
    internal static EvidenceLinuxArtifactIdentity InspectDirectoryIdentity(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Path.IsPathFullyQualified(directoryPath) || Encoding.UTF8.GetByteCount(directoryPath) > MaximumPathBytes)
            throw new ArgumentException("An absolute bounded directory path is required.", nameof(directoryPath));
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Linux openat2 artifact storage is unavailable on this platform.");
        using var slash = OpenAt2(AtFdcwd, "/", O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0, 0);
        using var directory = OpenAt2(Fd(slash), ToRootRelative(directoryPath), O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0, ParentResolveFlags);
        var info = StatFd(directory);
        if (!IsDirectory(info)) throw new IOException("The selected object is not a directory.");
        return IdentityOf(info);
    }

    /// <summary>Creates and writes an artifact through a newly exclusive retained descriptor.</summary>
    /// <param name="relativePath">A bounded relative path with safe components; every directory is created exclusively.</param>
    /// <param name="contents">Bytes to write, limited to 256 MiB per file and 256 MiB total.</param>
    /// <param name="cancellationToken">Cancellation observed between bounded write operations.</param>
    /// <remarks>Files use exact mode <c>0600</c>. A path can be written only once per root; collisions fail closed.</remarks>
    internal async ValueTask WriteAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken)
    {
        ValidateRelativePath(relativePath);
        if (contents.Length > MaximumFileBytes) throw new InvalidDataException("Artifact exceeds the per-file byte limit.");
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRootBinding();
            if (_totalBytes > MaximumTotalBytes - contents.Length)
                throw new InvalidDataException("Artifact storage limit exceeded.");
            var (parentFd, leaf) = OpenParent(relativePath, create: true, cancellationToken);
            SafeFileHandle? file = null;
            FileStream? stream = null;
            try
            {
                file = OpenAt2(parentFd, leaf, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, ArtifactFileMode, DescendantResolveFlags);
                var bound = IdentityOf(file);
                var fileInfo = StatFd(file);
                if (!IsRegular(fileInfo) || fileInfo.Uid != _uid || fileInfo.Gid != _gid || (fileInfo.Mode & PermissionMask) != ArtifactFileMode || fileInfo.Nlink != 1)
                    throw new IOException("Artifact file owner or mode is unsafe.");
                stream = new FileStream(file, FileAccess.ReadWrite, 4096, isAsync: false);
                file = null;
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                var handle = stream.SafeFileHandle;
                _ = RequireFileBinding(relativePath, handle, bound, contents.Length);
                _files.Add(relativePath, new RetainedFile(handle, bound, contents.Length, stream));
                _totalBytes += contents.Length;
                stream = null;
            }
            catch
            {
                file?.Dispose();
                stream?.Dispose();
                throw;
            }
            finally
            {
                if (parentFd != Fd(_root)) CloseFd(parentFd);
            }
        }
        finally { _sync.Release(); }
    }

    /// <summary>Streams the retained file through SHA-256 and checks name, link count, size and identity before and after.</summary>
    /// <param name="relativePath">The exact previously written artifact path.</param>
    /// <param name="expectedLength">Expected byte count, no greater than 256 MiB.</param>
    /// <param name="expectedSha256">Expected 64-character hexadecimal SHA-256 digest, case-insensitive.</param>
    /// <param name="cancellationToken">Cancellation observed during bounded reads.</param>
    /// <exception cref="IOException">A replacement, unlink, hard link, metadata drift, length mismatch or digest mismatch is detected.</exception>
    internal async ValueTask VerifyAsync(string relativePath, long expectedLength, string expectedSha256, CancellationToken cancellationToken)
    {
        ValidateRelativePath(relativePath);
        if (expectedLength is < 0 or > MaximumFileBytes) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        if (expectedSha256 is null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A 64-character SHA-256 digest is required.", nameof(expectedSha256));
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureRootBinding();
            if (!_files.TryGetValue(relativePath, out var retained)) throw new IOException("Artifact was not created by this root.");
            if (retained.Length != expectedLength) throw new IOException("Artifact length does not match the expected value.");
            var before = RequireFileBinding(relativePath, retained.Handle, retained.Identity, expectedLength);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                long offset = 0;
                while (offset < expectedLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(buffer.Length, expectedLength - offset);
                    var read = await RandomAccess.ReadAsync(retained.Handle, buffer.AsMemory(0, count), offset, cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new IOException("Artifact ended before its expected length.");
                    hash.AppendData(buffer, 0, read);
                    offset += read;
                }
                var extra = new byte[1];
                if (await RandomAccess.ReadAsync(retained.Handle, extra, expectedLength, cancellationToken).ConfigureAwait(false) != 0)
                    throw new IOException("Artifact exceeds its expected length.");
                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedSha256.ToUpperInvariant())))
                    throw new IOException("Artifact SHA-256 does not match.");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            var after = RequireFileBinding(relativePath, retained.Handle, retained.Identity, expectedLength);
            if (before.ChangeSeconds != after.ChangeSeconds || before.ChangeNanoseconds != after.ChangeNanoseconds
                || before.ModifySeconds != after.ModifySeconds || before.ModifyNanoseconds != after.ModifyNanoseconds)
                throw new IOException("Artifact contents changed while they were being verified.");
        }
        finally { _sync.Release(); }
    }

    /// <summary>Closes all retained handles after callers have joined every writer and verifier.</summary>
    /// <remarks>Disposal is single-use and must not race active callers; a second disposal is harmless.</remarks>
    public async ValueTask DisposeAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var file in _files.Values) file.Stream.Dispose();
            _files.Clear();
            _root.Dispose();
            _parent.Dispose();
            _slash.Dispose();
        }
        finally { _sync.Release(); }
    }

    private (int ParentFd, string Leaf) OpenParent(string relativePath, bool create, CancellationToken cancellationToken)
    {
        var components = relativePath.Split('/');
        var current = Fd(_root);
        try
        {
            for (var i = 0; i < components.Length - 1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (create && MkdirAt(current, components[i], DirectoryMode) != 0 && Marshal.GetLastPInvokeError() != 17)
                    throw IoError("Could not create an artifact subdirectory.");
                using var next = OpenAt2(current, components[i], O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0, DescendantResolveFlags);
                var directoryInfo = StatFd(next);
                if (!IsDirectory(directoryInfo) || directoryInfo.Uid != _uid || directoryInfo.Gid != _gid || (directoryInfo.Mode & PermissionMask) != DirectoryMode)
                    throw new IOException("Artifact subdirectory owner or mode is unsafe.");
                if (current != Fd(_root)) CloseFd(current);
                current = Fd(next);
                next.SetHandleAsInvalid();
            }

            return (current, components[^1]);
        }
        catch
        {
            // Opening a later component may fail after ownership has moved to an intermediate
            // descriptor. The root belongs to this instance; every intermediate belongs here.
            if (current != Fd(_root)) CloseFd(current);
            throw;
        }
    }

    private void EnsureRootBinding()
    {
        RequireIdentity(_parent, null, _uid, _gid, DirectoryMode, "Protected parent owner or mode changed.");
        RequireNamedIdentity(_slash, ToRootRelative(_parentPath), _parent, DirectoryMode, ParentResolveFlags, "Protected parent path was substituted.");
        RequireIdentity(_root, Identity, _uid, _gid, DirectoryMode, "Artifact root identity or policy changed.");
        RequireNamedIdentity(_parent, _slotName, _root, DirectoryMode, DescendantResolveFlags, "Artifact root name was replaced or unlinked.");
    }

    private Statx RequireFileBinding(string path, SafeFileHandle handle, EvidenceLinuxArtifactIdentity identity, long length)
    {
        var info = StatFd(handle);
        if (!Matches(info, identity) || !IsRegular(info) || info.Nlink != 1 || info.Size != (ulong)length || info.Uid != _uid || info.Gid != _gid || (info.Mode & PermissionMask) != ArtifactFileMode)
            throw new IOException("Retained artifact identity, link count, owner, mode or size changed.");
        var (parentFd, leaf) = OpenParent(path, create: false, CancellationToken.None);
        try
        {
            using var named = OpenAt2(parentFd, leaf, O_PATH | O_CLOEXEC, 0, DescendantResolveFlags);
            if (!Matches(StatFd(named), identity)) throw new IOException("Artifact name no longer identifies its retained file.");
        }
        finally { if (parentFd != Fd(_root)) CloseFd(parentFd); }
        return info;
    }

    private static void RequireNamedIdentity(SafeFileHandle parent, string name, SafeFileHandle expected, uint mode, ulong resolveFlags, string message)
    {
        using var named = OpenAt2(Fd(parent), name, O_PATH | O_DIRECTORY | O_CLOEXEC, 0, resolveFlags);
        var actual = StatFd(named);
        if (!Matches(actual, IdentityOf(expected)) || !IsDirectory(actual) || (actual.Mode & PermissionMask) != mode) throw new IOException(message);
    }

    private static void RequireIdentity(SafeFileHandle handle, EvidenceLinuxArtifactIdentity? expected, uint uid, uint gid, uint mode, string message)
    {
        var info = StatFd(handle);
        var actual = IdentityOf(info);
        if (!IsDirectory(info) || actual.Uid != uid || actual.Gid != gid || (info.Mode & PermissionMask) != mode || (expected is { } e && actual != e))
            throw new IOException(message);
    }

    private static uint ModeOf(SafeFileHandle handle) => (uint)(StatFd(handle).Mode & PermissionMask);
    private static bool IsDirectory(Statx info) => (info.Mode & 0xf000) == 0x4000;
    private static bool IsRegular(Statx info) => (info.Mode & 0xf000) == 0x8000;
    private static bool Matches(Statx info, EvidenceLinuxArtifactIdentity identity) => IdentityOf(info) == identity;
    private static EvidenceLinuxArtifactIdentity IdentityOf(SafeFileHandle handle) => IdentityOf(StatFd(handle));
    private static EvidenceLinuxArtifactIdentity IdentityOf(Statx info) => new(info.DeviceMajor, info.DeviceMinor, info.Inode, info.Uid, info.Gid);

    private static SafeFileHandle OpenAt2(int dirFd, string path, ulong flags, uint mode, ulong resolve)
    {
        var how = new OpenHow { Flags = flags, Mode = mode, Resolve = resolve };
        var fd = SyscallOpenAt2(437, dirFd, path, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 1 or 22 or 38 or 95) throw new PlatformNotSupportedException("Required Linux openat2 resolution is unsupported or blocked.", new System.ComponentModel.Win32Exception(error));
            throw new IOException("Safe descriptor-relative filesystem operation failed.", new System.ComponentModel.Win32Exception(error));
        }
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static Statx StatFd(SafeFileHandle handle)
    {
        if (StatxCall(Fd(handle), string.Empty, AtEmptyPath, StatxBasicStats, out var info) != 0)
            throw IoError("Could not inspect a retained Linux filesystem handle.");
        if ((info.Mask & StatxBasicStats) != StatxBasicStats)
            throw new PlatformNotSupportedException("Filesystem identity or change-time fields are unavailable.");
        return info;
    }

    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    private static void CloseFd(int fd) { if (fd >= 0) Close(fd); }

    private static string ToRootRelative(string path)
    {
        var trimmed = path.TrimStart('/');
        if (trimmed.Length == 0 || trimmed.Split('/').Any(x => x is "" or "." or "..")) throw new ArgumentException("Protected parent path contains an unsafe component.", nameof(path));
        return trimmed;
    }

    private static void ValidateRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Encoding.UTF8.GetByteCount(path) > MaximumPathBytes || Path.IsPathFullyQualified(path)) throw new ArgumentException("Artifact path must be bounded and relative.", nameof(path));
        foreach (var component in path.Split('/')) ValidateComponent(component, nameof(path));
    }

    private static void ValidateComponent(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." || value.Length > 255 || value.Any(c => c < 0x20 || c == 0x7f || c is '/' or '\\' or ':' or '\0'))
            throw new ArgumentException("Path contains an unsafe component.", parameterName);
    }

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(EvidenceLinuxArtifactRoot)); }
    private static IOException IoError(string message) => new(message, new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)] private struct OpenHow { internal ulong Flags; internal ulong Mode; internal ulong Resolve; }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] internal uint Mask; [FieldOffset(4)] internal uint BlockSize; [FieldOffset(16)] internal uint Nlink;
        [FieldOffset(20)] internal uint Uid; [FieldOffset(24)] internal uint Gid; [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode; [FieldOffset(40)] internal ulong Size; [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
        [FieldOffset(96)] private StatxTimestamp ChangeTime;
        [FieldOffset(112)] private StatxTimestamp ModifyTime;
        internal long ChangeSeconds => ChangeTime.Seconds;
        internal uint ChangeNanoseconds => ChangeTime.Nanoseconds;
        internal long ModifySeconds => ModifyTime.Seconds;
        internal uint ModifyNanoseconds => ModifyTime.Nanoseconds;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StatxTimestamp { internal long Seconds; internal uint Nanoseconds; private int _reserved; }
    private sealed record RetainedFile(SafeFileHandle Handle, EvidenceLinuxArtifactIdentity Identity, long Length, FileStream Stream);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long SyscallOpenAt2(long number, int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref OpenHow how, nuint size);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int StatxCall(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx buffer);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int MkdirAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int fd);
}
