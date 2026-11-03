using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Retained, atomically consumed root-only generation marker; only actual native acquisition constructs it.</summary>
/// <remarks>
/// The bootstrap creates armed with exactly the lowercase generation plus LF (33 bytes), mode 0600,
/// beneath a root:root 0700 generation directory. renameat2(RENAME_NOREPLACE) moves it to consumed.
/// Unsupported atomic rename has no delete/create fallback. Neither failure nor disposal recreates armed.
/// Generation cleanup belongs to the protected bootstrap after every pending start/unit has settled.
/// </remarks>
internal sealed partial class LinuxOwnerGuard : IDisposable
{
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong PathFlags = 0x200000 | 0x80000;
    private const ulong Resolution = 0x02 | 0x04 | 0x08;
    private readonly List<(SafeFileHandle Handle, LinuxProtectedMetadata Metadata, string Name)> _directories;
    private readonly SafeFileHandle _file;
    private readonly LinuxProtectedMetadata _metadata;
    private readonly byte[] _expected;
    private int _closed;

    private LinuxOwnerGuard(List<(SafeFileHandle, LinuxProtectedMetadata, string)> directories,
        SafeFileHandle file, LinuxProtectedMetadata metadata, byte[] expected)
    { _directories = directories; _file = file; _metadata = metadata; _expected = expected; }

    /// <summary>Opens protected ancestors, binds exact marker bytes and consumes it atomically before dependent starts.</summary>
    internal static LinuxOwnerGuard Consume(Guid id, CancellationToken token)
    {
        LinuxProtectedDeployment.RequirePlatform();
        if (id == Guid.Empty) throw LinuxOwnerFacts.Invalid();
        var directories = new List<(SafeFileHandle, LinuxProtectedMetadata, string)>();
        SafeFileHandle? file = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var root = OpenAt2(-100, "/", DirectoryFlags, 0, 0);
            directories.Add((root, default, "/")); // Own the FD before any metadata call can fail.
            directories[0] = (root, LinuxProtectedMetadata.From(StatFd(root)), "/");
            foreach (var name in new[] { "run", "appsurface-evidence-owners", id.ToString("N") })
            {
                token.ThrowIfCancellationRequested();
                RequireDirectories(directories);
                var parent = directories[^1].Item1;
                var next = OpenAt2(Fd(parent), name, DirectoryFlags, 0, Resolution);
                directories.Add((next, default, name));
                directories[^1] = (next, LinuxProtectedMetadata.From(StatFd(next)), name);
            }
            RequireDirectories(directories);
            RequireParent(directories[^1].Item2);
            var directory = Fd(directories[^1].Item1);
            file = OpenAt2(directory, "armed", 0x80000 | 0x800, 0, Resolution | 1);
            var before = LinuxProtectedMetadata.From(StatFd(file));
            RequireMarker(before);
            var expected = Encoding.ASCII.GetBytes(id.ToString("N") + "\n");
            ReadMarker(file, expected, token);
            using (var named = OpenAt2(directory, "armed", PathFlags, 0, Resolution | 1))
                if (LinuxProtectedMetadata.From(StatFd(named)) != before) throw LinuxOwnerFacts.Invalid();
            token.ThrowIfCancellationRequested();
            RequireDirectories(directories);
            if (RenameAt2(directory, "armed", directory, "consumed", 1) != 0) throw LinuxOwnerFacts.Invalid();
            var after = LinuxProtectedMetadata.From(StatFd(file));
            RequireMarker(after);
            if (!SameMarkerObject(before, after)) throw LinuxOwnerFacts.Invalid();
            var guard = new LinuxOwnerGuard(directories, file, after, expected);
            guard.Recheck(token);
            file = null; // Handles transfer only after the consumed name and original bytes bind.
            directories = [];
            return guard;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw LinuxOwnerFacts.Invalid(); }
        finally
        {
            file?.Dispose();
            foreach (var item in directories.AsEnumerable().Reverse()) item.Item1.Dispose();
        }
    }

    /// <summary>Checks consumed identity/bytes, protected ancestor bindings and continued absence of armed.</summary>
    internal void Recheck(CancellationToken token)
    {
        if (Volatile.Read(ref _closed) != 0) throw LinuxOwnerFacts.Invalid();
        token.ThrowIfCancellationRequested();
        RequireDirectories(_directories);
        RequireParent(_directories[^1].Metadata);
        var directory = Fd(_directories[^1].Handle);
        if (LinuxProtectedMetadata.From(StatFd(_file)) != _metadata) throw LinuxOwnerFacts.Invalid();
        using (var named = OpenAt2(directory, "consumed", PathFlags, 0, Resolution | 1))
            if (LinuxProtectedMetadata.From(StatFd(named)) != _metadata) throw LinuxOwnerFacts.Invalid();
        RequireArmedAbsent(directory);
        ReadMarker(_file, _expected, token);
        if (LinuxProtectedMetadata.From(StatFd(_file)) != _metadata) throw LinuxOwnerFacts.Invalid();
        RequireDirectories(_directories);
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Checks root-only marker metadata as data, without consuming or returning a native guard.</summary>
    internal static void RequireMarker(LinuxProtectedMetadata value)
    {
        if (value.Uid != 0 || value.Gid != 0 || value.Inode == 0 || value.Mode != 0x8180
            || value.LinkCount != 1 || value.Length != 33) throw LinuxOwnerFacts.Invalid();
    }

    /// <summary>Checks that atomic rename retained the same protected object, allowing rename's ctime update.</summary>
    internal static bool SameMarkerObject(LinuxProtectedMetadata before, LinuxProtectedMetadata after) =>
        before.DeviceMajor == after.DeviceMajor && before.DeviceMinor == after.DeviceMinor && before.Inode == after.Inode
        && before.Uid == after.Uid && before.Gid == after.Gid && before.Mode == after.Mode
        && before.LinkCount == after.LinkCount && before.Length == after.Length
        && before.ModifySeconds == after.ModifySeconds && before.ModifyNanoseconds == after.ModifyNanoseconds;

    private static void RequireParent(LinuxProtectedMetadata value)
    {
        if (value.Uid != 0 || value.Gid != 0 || value.Mode != 0x41c0 || value.Inode == 0) throw LinuxOwnerFacts.Invalid();
    }

    private static void RequireDirectories(List<(SafeFileHandle Handle, LinuxProtectedMetadata Metadata, string Name)> directories)
    {
        for (var i = 0; i < directories.Count; i++)
        {
            var item = directories[i];
            var current = LinuxProtectedMetadata.From(StatFd(item.Handle));
            current.RequireAncestor(false);
            if (!current.SameAncestorAs(item.Metadata)) throw LinuxOwnerFacts.Invalid();
            if (i > 0)
            {
                using var named = OpenAt2(Fd(directories[i - 1].Handle), item.Name, PathFlags, 0, Resolution);
                if (!LinuxProtectedMetadata.From(StatFd(named)).SameAncestorAs(item.Metadata)) throw LinuxOwnerFacts.Invalid();
            }
        }
    }

    private static void ReadMarker(SafeFileHandle file, byte[] expected, CancellationToken token)
    {
        var bytes = new byte[34];
        var length = 0;
        while (length < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(file, bytes.AsSpan(length), length);
            if (read == 0) break;
            length += read;
        }
        if (length != expected.Length || !bytes.AsSpan(0, length).SequenceEqual(expected)) throw LinuxOwnerFacts.Invalid();
        token.ThrowIfCancellationRequested();
    }

    private static void RequireArmedAbsent(int directory)
    {
        try
        {
            using var armed = OpenAt2(directory, "armed", PathFlags, 0, Resolution | 1);
        }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 }) { return; }
        throw LinuxOwnerFacts.Invalid();
    }

    /// <summary>Closes owned handles after use; the consumed marker stays in place.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _file.Dispose();
        foreach (var item in _directories.AsEnumerable().Reverse()) item.Handle.Dispose();
    }

    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt2(int oldDirectory, string oldName, int newDirectory, string newName, uint flags);
}
