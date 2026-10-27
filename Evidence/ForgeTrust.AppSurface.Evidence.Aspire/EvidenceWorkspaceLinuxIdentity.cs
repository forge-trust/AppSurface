using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Retains a Linux directory object so child writes cannot change its identity or permit inode reuse.</summary>
/// <remarks>
/// Linux CreationTimeUtc may fall back to mutable directory timestamps. Device/inode identity is independent of
/// those timestamps. Opens reject links and nondirectories; the held descriptor keeps the original object alive
/// across path replacement. This boundary supports the verified Linux x64 and ARM64 native stat layouts only.
/// </remarks>
internal sealed partial class EvidenceWorkspaceLinuxIdentity : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly (ulong Device, ulong Inode) _identity;

    private EvidenceWorkspaceLinuxIdentity(SafeFileHandle handle, (ulong Device, ulong Inode) identity)
    {
        _handle = handle;
        _identity = identity;
    }

    /// <summary>Opens an existing directory without following its final path component.</summary>
    internal static EvidenceWorkspaceLinuxIdentity Open(string path)
    {
        var (directory, noFollow) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (0x00010000, 0x00020000),
            Architecture.Arm64 => (0x00004000, 0x00008000),
            _ => throw new PlatformNotSupportedException("Evidence workspace identity requires Linux x64 or ARM64."),
        };
        // O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK.
        var descriptor = UnixOpen(path, directory | noFollow | 0x00080000 | 0x00000800);
        if (descriptor < 0) throw new IOException("Evidence workspace directory identity is unavailable.");
        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try { return new EvidenceWorkspaceLinuxIdentity(handle, Read(handle)); }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Checks that a no-follow reopen still names the retained directory object.</summary>
    internal bool Matches(string path)
    {
        using var current = Open(path);
        return !_handle.IsClosed && current._identity == _identity;
    }

    /// <summary>Releases the retained directory descriptor after owned cleanup or failed admission.</summary>
    public void Dispose() => _handle.Dispose();

    private static (ulong Device, ulong Inode) Read(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (UnixFStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
                throw new IOException("Evidence workspace directory identity is unavailable.");
            return (unchecked((ulong)Marshal.ReadInt64(buffer, 0)), unchecked((ulong)Marshal.ReadInt64(buffer, 8)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnixOpen(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fstat")]
    private static partial int UnixFStat(int descriptor, nint buffer);
}
