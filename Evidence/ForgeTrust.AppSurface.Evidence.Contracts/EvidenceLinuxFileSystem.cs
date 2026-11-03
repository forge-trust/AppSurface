using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Shared Linux x64 descriptor operations; none of these operations issues admission.</summary>
/// <remarks>
/// Callers validate the platform before calling, own every returned handle, and must join users
/// before closing it. Resolution flags, expected ownership and path policy belong to the caller.
/// Unsupported or blocked openat2 has no path-based fallback and retains the known native errno.
/// </remarks>
internal static class EvidenceLinuxFileSystem
{
    /// <summary>Opens relative to an owned descriptor with the caller's exact openat2 policy.</summary>
    internal static SafeFileHandle OpenAt2(int directory, string path, ulong flags, uint mode, ulong resolve)
    {
        var how = new OpenHow { Flags = flags, Mode = mode, Resolve = resolve };
        var fd = SyscallOpenAt2(437, directory, path, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 1 or 22 or 38 or 95)
                throw new PlatformNotSupportedException("Required Linux openat2 resolution is unsupported or blocked.", new System.ComponentModel.Win32Exception(error));
            throw new IOException("Safe descriptor-relative filesystem operation failed.", new System.ComponentModel.Win32Exception(error));
        }
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    /// <summary>Reads required identity and change-time fields from the actual retained descriptor.</summary>
    internal static Statx StatFd(SafeFileHandle handle)
    {
        const uint required = 0x7ff;
        if (StatxCall(Fd(handle), string.Empty, 0x1000, required, out var info) != 0)
            throw new IOException("Could not inspect a retained Linux filesystem handle.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        if ((info.Mask & required) != required)
            throw new PlatformNotSupportedException("Filesystem identity or change-time fields are unavailable.");
        return info;
    }

    /// <summary>Gets a live descriptor for a synchronous call while its owner prevents disposal.</summary>
    internal static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());

    /// <summary>Closes a transferred raw descriptor exactly at the caller's ownership boundary.</summary>
    internal static void CloseFd(int fd) { if (fd >= 0) Close(fd); }

    /// <summary>Attempts exclusive directory creation; returns the native result and preserves errno.</summary>
    internal static int MkdirAt(int directory, string path, uint mode) => MkdirAtCall(directory, path, mode);

    /// <summary>Reads one bounded getdents64 block from a retained directory; does not reset its position.</summary>
    internal static int ReadDirectory(SafeFileHandle directory, byte[] buffer)
    {
        var result = SyscallGetDents(217, Fd(directory), buffer, (uint)buffer.Length);
        if (result < 0 || result > buffer.Length)
            throw new IOException("Could not enumerate a retained Linux directory.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        return checked((int)result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow { internal ulong Flags; internal ulong Mode; internal ulong Resolve; }

    /// <summary>Linux statx layout, used only as sampled metadata rather than an ownership capability.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct Statx
    {
        [FieldOffset(0)] internal uint Mask; [FieldOffset(4)] internal uint BlockSize; [FieldOffset(16)] internal uint Nlink;
        [FieldOffset(20)] internal uint Uid; [FieldOffset(24)] internal uint Gid; [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode; [FieldOffset(40)] internal ulong Size; [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
        [FieldOffset(96)] private StatxTimestamp ChangeTime;
        [FieldOffset(112)] private StatxTimestamp ModifyTime;
        internal readonly long ChangeSeconds => ChangeTime.Seconds;
        internal readonly uint ChangeNanoseconds => ChangeTime.Nanoseconds;
        internal readonly long ModifySeconds => ModifyTime.Seconds;
        internal readonly uint ModifyNanoseconds => ModifyTime.Nanoseconds;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StatxTimestamp { internal long Seconds; internal uint Nanoseconds; private int _reserved; }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long SyscallOpenAt2(long number, int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref OpenHow how, nuint size);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long SyscallGetDents(long number, int directory, [Out] byte[] buffer, uint size);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int StatxCall(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx buffer);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int MkdirAtCall(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int descriptor);
}
