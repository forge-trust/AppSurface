using System.Diagnostics;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Auth.Keycloak;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>Safe internal failure used by the supervised lease and recovery boundary.</summary>
internal sealed class QualifiedOwnershipFailure(string kind) : Exception(kind)
{
    internal string Kind { get; } = kind;
}

/// <summary>Recorded process incarnation; a PID is the same process only when its start time also matches.</summary>
internal sealed record QualifiedOwnedProcess(string Role, int Pid, long StartTimeUtcTicks);

/// <summary>Checks whether a recorded process incarnation has stopped; uncertain observations propagate as failures.</summary>
internal static class QualifiedProcessIdentity
{
    internal static bool Stopped(QualifiedOwnedProcess identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks;
        }
        catch (ArgumentException) { return true; }
        // ESRCH obtaining birth, access denial, and other incomplete inventory are unknown, never absence.
    }
}

/// <summary>Holds the Darwin ARM64 owner lock and performs anchor-checked active-record I/O.</summary>
/// <remarks>Root ancestors are trusted; extended ACL validation remains a separate gate.</remarks>
internal sealed class QualifiedLeaseDirectory : IDisposable
{
    private const int MaximumMarkerLength = 65_536;

    // Darwin fcntl.h, errno.h, stat.h, and stdio.h values for this ARM64 lease boundary.
    private const int OpenReadOnly = 0x0000;
    private const int OpenReadWrite = 0x0002;
    private const int OpenNonBlocking = 0x00000004;
    private const int OpenNoFollow = 0x00000100;
    private const int OpenDirectoryFlag = 0x00100000;
    private const int OpenCloseOnExec = 0x01000000;
    private const int OpenNoFollowAny = 0x20000000;
    private const int OpenAtFlags = OpenNoFollow | OpenCloseOnExec | OpenNonBlocking;
    private const int LockExclusive = 0x02;
    private const int LockNonBlocking = 0x04;
    private const int LockUnlock = 0x08;
    private const int NoSuchFile = 2;
    private const int FileExists = 17;
    private const int WouldBlock = 35;
    private const uint RenameExclusive = 0x00000004;
    private const int FileTypeMask = 0xf000;
    private const int DirectoryType = 0x4000;
    private const int RegularFileType = 0x8000;
    private const int PermissionModeMask = 0xfff;
    private const int PrivateDirectoryMode = (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    private const int PrivateFileMode = (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite);

    private readonly SafeFileHandle _root;
    private readonly SafeFileHandle _directory;
    private readonly SafeFileHandle _lock;
    private readonly FileId _directoryId;
    private readonly FileId _lockId;
    internal string Path { get; }
    internal readonly record struct FileId(int Device, ulong Inode, long BirthSeconds, long BirthNanoseconds);

    internal QualifiedLeaseDirectory(string root, string key)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new QualifiedOwnershipFailure("UnsupportedPlatform");
        try
        {
            root = System.IO.Path.GetFullPath(root);
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _root = OpenDirectory(root);
            ValidateDirectory(_root);
            Path = System.IO.Path.Combine(root, key);
            if (mkdirat(_root, key, PrivateDirectoryMode) != 0 && Marshal.GetLastPInvokeError() != FileExists)
                throw new QualifiedOwnershipFailure("UnsafeLeasePath");
            _directory = OpenAt(_root, key, OpenDirectoryFlag);
            ValidateDirectory(_directory);
            _directoryId = Id(_directory);
            try
            {
                using var created = NewPrivateFile(System.IO.Path.Combine(Path, "owner.lock"));
            }
            catch (IOException) when (File.Exists(System.IO.Path.Combine(Path, "owner.lock"))
                || new FileInfo(System.IO.Path.Combine(Path, "owner.lock")).LinkTarget is not null)
            { }
            _lock = OpenAt(_directory, "owner.lock", OpenReadWrite);
            ValidateFile(_lock);
            if (flock(_lock, LockExclusive | LockNonBlocking) != 0)
                throw new QualifiedOwnershipFailure(Marshal.GetLastPInvokeError() == WouldBlock ? "Contention" : "LockUnavailable");
            _lockId = Id(_lock);
            using var second = OpenAt(_directory, "owner.lock", OpenReadWrite);
            if (flock(second, LockExclusive | LockNonBlocking) == 0) throw new QualifiedOwnershipFailure("LockNotEnforced");
            if (Marshal.GetLastPInvokeError() != WouldBlock) throw new QualifiedOwnershipFailure("LockUnavailable");
        }
        catch
        {
            _lock?.Dispose(); _directory?.Dispose(); _root?.Dispose();
            throw;
        }
    }

    internal static void ValidatePrivateDirectory(string path) => ReadPrivateDirectoryId(path);

    /// <summary>Reads the incarnation of an owner-only directory through a no-follow handle.</summary>
    internal static FileId ReadPrivateDirectoryId(string path)
    {
        using var directory = OpenDirectory(path);
        return ValidateDirectory(directory);
    }

    /// <summary>Creates a run directory exclusively beneath the checked, private host root.</summary>
    /// <param name="root">The existing private run root with trusted ancestors.</param>
    /// <param name="ownerId">The new run's GUID N identifier, used as a single path component.</param>
    /// <returns>The new directory identity, or null if the name already exists and no directory was acquired.</returns>
    /// <exception cref="QualifiedOwnershipFailure">The directory could not be created or verified for other reasons.</exception>
    internal static FileId? TryCreatePrivateRunDirectory(string root, string ownerId)
    {
        if (!Guid.TryParseExact(ownerId, "N", out _)) throw new QualifiedOwnershipFailure("UnsafeRunDirectory");
        using var parent = OpenDirectory(root);
        ValidateDirectory(parent);
        if (mkdirat(parent, ownerId, PrivateDirectoryMode) != 0)
        {
            if (Marshal.GetLastPInvokeError() == FileExists) return null;
            throw new QualifiedOwnershipFailure("UnsafeRunDirectory");
        }
        using var directory = OpenAt(parent, ownerId, OpenDirectoryFlag);
        return ValidateDirectory(directory);
    }

    internal void VerifyAnchor()
    {
        using var path = OpenDirectory(Path);
        ValidateDirectory(path);
        if (Id(path) != _directoryId || Identity("owner.lock") != _lockId) throw new QualifiedOwnershipFailure("LeaseAnchorChanged");
    }
    internal bool Exists(string name)
    {
        var fd = openat(_directory, name, OpenAtFlags);
        if (fd >= 0) { using var file = new SafeFileHandle((IntPtr)fd, true); ValidateFile(file); return true; }
        if (Marshal.GetLastPInvokeError() == NoSuchFile) return false;
        throw new QualifiedOwnershipFailure("UnsafeMarker");
    }
    internal byte[] Read(string name)
    {
        using var handle = OpenAt(_directory, name, OpenReadOnly); ValidateFile(handle);
        var stat = Inspect(handle);
        ValidateMarkerLength(stat.Size);
        using var file = new FileStream(handle, FileAccess.Read); var bytes = new byte[(int)stat.Size]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1 || Id(handle) != ToId(stat)) throw new QualifiedOwnershipFailure("MarkerChanged");
        return bytes;
    }

    /// <summary>Rejects marker lengths outside the supported range before write or read allocation.</summary>
    internal static void ValidateMarkerLength(long length)
    {
        if (length is < 2 or > MaximumMarkerLength) throw new QualifiedOwnershipFailure("InvalidMarker");
    }
    internal FileId Identity(string name) { using var handle = OpenAt(_directory, name, OpenReadOnly); ValidateFile(handle); return Id(handle); }
    internal void WriteNew(string name, byte[] bytes) => Write(name, bytes, false);
    internal void Replace(string name, byte[] bytes) => Write(name, bytes, true);
    private void Write(string name, byte[] bytes, bool replace)
    {
        var temporary = ".staged-" + Guid.NewGuid().ToString("N");
        using (var file = NewPrivateFile(System.IO.Path.Combine(Path, temporary)))
        {
            file.Write(bytes); file.Flush(true);
        }
        if (!replace && Exists(name)) throw new QualifiedOwnershipFailure("QualifiedActiveOwner");
        var renamed = replace ? renameat(_directory, temporary, _directory, name)
            : renameatx_np(_directory, temporary, _directory, name, RenameExclusive);
        if (renamed != 0 || fsync(_directory) != 0)
            throw new QualifiedOwnershipFailure("MarkerPersistenceUncertain");
    }
    internal void Delete(string name)
    {
        if (unlinkat(_directory, name, 0) != 0 || fsync(_directory) != 0) throw new QualifiedOwnershipFailure("ClearUncertain");
    }
    /// <summary>Releases the held native lock before closing its descriptors.</summary>
    public void Dispose()
    {
        // A forked child can briefly retain the open file description before close-on-exec runs.
        // Explicitly unlock so a stopped owner can release the lease in that window.
        try
        {
            if (!_lock.IsClosed)
                _ = flock(_lock, LockUnlock);
        }
        finally
        {
            _lock.Dispose();
            _directory.Dispose();
            _root.Dispose();
        }
    }
    private static FileStream NewPrivateFile(string path)
    {
        if (!OperatingSystem.IsMacOS()) throw new QualifiedOwnershipFailure("UnsupportedPlatform");
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }
    private static SafeFileHandle OpenDirectory(string path)
    {
        var fd = open(path, OpenNoFollowAny | OpenDirectoryFlag | OpenCloseOnExec | OpenNonBlocking);
        if (fd < 0) throw new QualifiedOwnershipFailure("UnsafeLeasePath"); return new((IntPtr)fd, true);
    }
    private static SafeFileHandle OpenAt(SafeFileHandle parent, string name, int flags)
    {
        var fd = openat(parent, name, flags | OpenAtFlags);
        if (fd < 0) throw new QualifiedOwnershipFailure("UnsafeRecord"); return new((IntPtr)fd, true);
    }
    private static DarwinArm64FileMetadata Inspect(SafeFileHandle fd)
    {
        if (DarwinArm64FileMetadata.ReadNative(fd, out var metadata) != 0)
            throw new QualifiedOwnershipFailure("InventoryUnavailable");
        return metadata;
    }
    private static FileId ToId(DarwinArm64FileMetadata metadata) =>
        new(metadata.Device, metadata.Inode, metadata.BirthSeconds, metadata.BirthNanoseconds);
    private static FileId Id(SafeFileHandle fd) => ToId(Inspect(fd));
    private static FileId ValidateDirectory(SafeFileHandle fd)
    {
        var status = Inspect(fd);
        if ((status.Mode & FileTypeMask) != DirectoryType
            || (status.Mode & PermissionModeMask) != PrivateDirectoryMode || status.Owner != getuid())
            throw new QualifiedOwnershipFailure("UnsafeLeasePath");
        return ToId(status);
    }
    private static void ValidateFile(SafeFileHandle fd)
    {
        var status = Inspect(fd);
        if ((status.Mode & FileTypeMask) != RegularFileType
            || (status.Mode & PermissionModeMask) != PrivateFileMode || status.Owner != getuid() || status.Links != 1)
            throw new QualifiedOwnershipFailure("UnsafeRecord");
    }
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int openat(SafeFileHandle fd, string path, int flags);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int mkdirat(SafeFileHandle fd, string path, int mode);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int flock(SafeFileHandle fd, int operation);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int renameat(SafeFileHandle source, string name, SafeFileHandle target, string targetName);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int renameatx_np(SafeFileHandle source, string name, SafeFileHandle target, string targetName, uint flags);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int unlinkat(SafeFileHandle fd, string path, int flags);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int fsync(SafeFileHandle fd);
    [DllImport("libSystem.B.dylib")] private static extern uint getuid();
}
