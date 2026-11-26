using System.Diagnostics;
using System.Runtime.InteropServices;
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
            _root = Open(root, 0x20000000 | 0x100000); ValidateDirectory(_root);
            Path = System.IO.Path.Combine(root, key);
            if (mkdirat(_root, key, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17) throw new QualifiedOwnershipFailure("UnsafeLeasePath");
            _directory = OpenAt(_root, key, 0x100000); ValidateDirectory(_directory); _directoryId = Id(_directory);
            try
            {
                using var created = NewPrivateFile(System.IO.Path.Combine(Path, "owner.lock"));
            }
            catch (IOException) when (File.Exists(System.IO.Path.Combine(Path, "owner.lock"))
                || new FileInfo(System.IO.Path.Combine(Path, "owner.lock")).LinkTarget is not null)
            { }
            _lock = OpenAt(_directory, "owner.lock", 2); ValidateFile(_lock);
            if (flock(_lock, 2 | 4) != 0) throw new QualifiedOwnershipFailure(Marshal.GetLastPInvokeError() == 35 ? "Contention" : "LockUnavailable");
            _lockId = Id(_lock);
            using var second = OpenAt(_directory, "owner.lock", 2);
            if (flock(second, 2 | 4) == 0) throw new QualifiedOwnershipFailure("LockNotEnforced");
            if (Marshal.GetLastPInvokeError() != 35) throw new QualifiedOwnershipFailure("LockUnavailable");
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
        using var directory = Open(path, 0x20000000 | 0x100000);
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
        using var parent = Open(root, 0x20000000 | 0x100000);
        ValidateDirectory(parent);
        if (mkdirat(parent, ownerId, 0x1c0) != 0)
        {
            if (Marshal.GetLastPInvokeError() == 17) return null; // EEXIST: leave the existing path alone.
            throw new QualifiedOwnershipFailure("UnsafeRunDirectory");
        }
        using var directory = OpenAt(parent, ownerId, 0x100000);
        return ValidateDirectory(directory);
    }
    internal static byte[] ReadAbsoluteRecord(string path)
    {
        using var handle = Open(path, 0x20000000); ValidateFile(handle);
        var stat = Inspect(handle); if (stat.Size < 2 || stat.Size > 4096) throw new QualifiedOwnershipFailure("InvalidProcessRecord");
        using var file = new FileStream(handle, FileAccess.Read); var bytes = new byte[(int)stat.Size]; file.ReadExactly(bytes);
        return bytes;
    }

    internal void VerifyAnchor()
    {
        using var path = Open(Path, 0x20000000 | 0x100000); ValidateDirectory(path);
        if (Id(path) != _directoryId || Identity("owner.lock") != _lockId) throw new QualifiedOwnershipFailure("LeaseAnchorChanged");
    }
    internal bool Exists(string name)
    {
        var fd = openat(_directory, name, 0x100 | 0x1000000 | 4);
        if (fd >= 0) { using var file = new SafeFileHandle((IntPtr)fd, true); ValidateFile(file); return true; }
        if (Marshal.GetLastPInvokeError() == 2) return false;
        throw new QualifiedOwnershipFailure("UnsafeMarker");
    }
    internal byte[] Read(string name)
    {
        using var handle = OpenAt(_directory, name, 0); ValidateFile(handle);
        var stat = Inspect(handle); if (stat.Size < 2 || stat.Size > 65536) throw new QualifiedOwnershipFailure("InvalidMarker");
        using var file = new FileStream(handle, FileAccess.Read); var bytes = new byte[(int)stat.Size]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1 || Id(handle) != ToId(stat)) throw new QualifiedOwnershipFailure("MarkerChanged");
        return bytes;
    }
    internal FileId Identity(string name) { using var handle = OpenAt(_directory, name, 0); ValidateFile(handle); return Id(handle); }
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
            : renameatx_np(_directory, temporary, _directory, name, 4);
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
                _ = flock(_lock, 8); // Darwin LOCK_UN
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
    private static SafeFileHandle Open(string path, int flags)
    {
        var fd = open(path, flags | 0x1000000 | 4);
        if (fd < 0) throw new QualifiedOwnershipFailure("UnsafeLeasePath"); return new((IntPtr)fd, true);
    }
    private static SafeFileHandle OpenAt(SafeFileHandle parent, string name, int flags)
    {
        var fd = openat(parent, name, flags | 0x100 | 0x1000000 | 4);
        if (fd < 0) throw new QualifiedOwnershipFailure("UnsafeRecord"); return new((IntPtr)fd, true);
    }
    private static Stat Inspect(SafeFileHandle fd)
    { if (fstat(fd, out var s) != 0) throw new QualifiedOwnershipFailure("InventoryUnavailable"); return s; }
    private static FileId ToId(Stat s) => new(s.Device, s.Inode, s.BirthSeconds, s.BirthNanoseconds);
    private static FileId Id(SafeFileHandle fd) => ToId(Inspect(fd));
    private static FileId ValidateDirectory(SafeFileHandle fd)
    {
        var status = Inspect(fd);
        if ((status.Mode & 0xf000) != 0x4000 || (status.Mode & 0xfff) != 0x1c0 || status.Owner != getuid())
            throw new QualifiedOwnershipFailure("UnsafeLeasePath");
        return ToId(status);
    }
    private static void ValidateFile(SafeFileHandle fd)
    { var s = Inspect(fd); if ((s.Mode & 0xf000) != 0x8000 || (s.Mode & 0xfff) != 0x180 || s.Owner != getuid() || s.Links != 1) throw new QualifiedOwnershipFailure("UnsafeRecord"); }
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct Stat
    {
        [FieldOffset(0)] internal int Device; [FieldOffset(4)] internal ushort Mode; [FieldOffset(6)] internal ushort Links;
        [FieldOffset(8)] internal ulong Inode; [FieldOffset(16)] internal uint Owner;
        [FieldOffset(80)] internal long BirthSeconds; [FieldOffset(88)] internal long BirthNanoseconds; [FieldOffset(96)] internal long Size;
    }
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int openat(SafeFileHandle fd, string path, int flags);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int mkdirat(SafeFileHandle fd, string path, int mode);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int flock(SafeFileHandle fd, int operation);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int fstat(SafeFileHandle fd, out Stat stat);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int renameat(SafeFileHandle source, string name, SafeFileHandle target, string targetName);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int renameatx_np(SafeFileHandle source, string name, SafeFileHandle target, string targetName, uint flags);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int unlinkat(SafeFileHandle fd, string path, int flags);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int fsync(SafeFileHandle fd);
    [DllImport("libSystem.B.dylib")] private static extern uint getuid();
}
