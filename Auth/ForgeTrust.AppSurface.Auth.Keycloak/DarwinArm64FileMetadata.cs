using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Exposes the fields read from the Darwin ARM64 <c>struct stat</c> by Keycloak custody paths.</summary>
/// <remarks>
/// The layout was verified against the Darwin ARM64 SDK with <c>sizeof</c> and <c>offsetof</c>.
/// Callers qualify the platform, open the handle, and enforce the metadata checks for their operation.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 144)]
internal struct DarwinArm64FileMetadata
{
    [FieldOffset(0)] internal int Device;
    [FieldOffset(4)] internal ushort Mode;
    [FieldOffset(6)] internal ushort Links;
    [FieldOffset(8)] internal ulong Inode;
    [FieldOffset(16)] internal uint Owner;
    [FieldOffset(80)] internal long BirthSeconds;
    [FieldOffset(88)] internal long BirthNanoseconds;
    [FieldOffset(96)] internal long Size;
    [FieldOffset(116)] internal uint Flags;

    /// <summary>Reads metadata for an already opened handle on a Darwin ARM64 process.</summary>
    /// <returns>Zero on success. The native error is available to the caller on failure.</returns>
    [DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
    internal static extern int ReadNative(SafeFileHandle handle, out DarwinArm64FileMetadata metadata);
}
