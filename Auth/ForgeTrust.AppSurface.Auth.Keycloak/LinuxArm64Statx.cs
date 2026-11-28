using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Reads fields from the fixed little-endian Linux ARM64 <c>statx</c> layout.</summary>
/// <remarks>Callers provide a complete buffer and choose the required field mask and validation for their operation.</remarks>
internal readonly ref struct LinuxArm64Statx
{
    /// <summary>Size in bytes of the native result buffer.</summary>
    internal const int BufferSize = 256;

    // Linux STATX_* field bits used by the volume and sealed credential readers.
    internal const uint TypeField = 0x001;
    internal const uint ModeField = 0x002;
    internal const uint LinkCountField = 0x004;
    internal const uint UserIdField = 0x008;
    internal const uint GroupIdField = 0x010;
    internal const uint InodeField = 0x100;
    internal const uint SizeField = 0x200;
    internal const uint BirthTimeField = 0x800;

    private readonly ReadOnlySpan<byte> _bytes;

    /// <summary>Creates a view over a complete native result buffer.</summary>
    internal LinuxArm64Statx(ReadOnlySpan<byte> bytes) => _bytes = bytes;

    internal uint Mask => BinaryPrimitives.ReadUInt32LittleEndian(_bytes);
    internal uint LinkCount => BinaryPrimitives.ReadUInt32LittleEndian(_bytes[16..]);
    internal uint UserId => BinaryPrimitives.ReadUInt32LittleEndian(_bytes[20..]);
    internal uint GroupId => BinaryPrimitives.ReadUInt32LittleEndian(_bytes[24..]);
    internal ushort Mode => BinaryPrimitives.ReadUInt16LittleEndian(_bytes[28..]);
    internal ulong Inode => BinaryPrimitives.ReadUInt64LittleEndian(_bytes[32..]);
    internal ulong Size => BinaryPrimitives.ReadUInt64LittleEndian(_bytes[40..]);
    internal long BirthSeconds => BinaryPrimitives.ReadInt64LittleEndian(_bytes[80..]);
    internal uint BirthNanoseconds => BinaryPrimitives.ReadUInt32LittleEndian(_bytes[88..]);
    internal uint DeviceMajor => BinaryPrimitives.ReadUInt32LittleEndian(_bytes[136..]);
    internal uint DeviceMinor => BinaryPrimitives.ReadUInt32LittleEndian(_bytes[140..]);

    /// <summary>Checks whether the kernel supplied every requested field.</summary>
    internal bool HasFields(uint fields) => (Mask & fields) == fields;

    /// <summary>Reads metadata into a complete native buffer on a platform already qualified by the caller.</summary>
    /// <returns>Zero on success, or a failure result with the native error available to the caller.</returns>
    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    internal static extern int ReadNative(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, [Out] byte[] buffer);
}
