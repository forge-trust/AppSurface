using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Provides the native directory and metadata calls needed to inspect the pinned H2 database.</summary>
/// <remarks>Successful directory calls transfer an owned handle to the caller.</remarks>
internal interface IKeycloakVolumeIncarnationNativeOperations
{
    /// <summary>Opens one path component relative to a directory descriptor.</summary>
    /// <returns>An owned handle on success, or <see langword="null"/> with the native error code.</returns>
    SafeHandle? OpenAt(int directoryDescriptor, string path, int flags, out int error);

    /// <summary>Reads metadata for one path relative to a directory descriptor.</summary>
    /// <returns>The native result code and the associated error code.</returns>
    int Statx(int directoryDescriptor, string path, int flags, uint mask, byte[] buffer, out int error);
}

/// <summary>Identifies the observed filesystem root for one local Keycloak volume mount.</summary>
/// <param name="DeviceMajor">The root filesystem device major number.</param>
/// <param name="DeviceMinor">The root filesystem device minor number.</param>
/// <param name="Inode">The root directory inode number.</param>
/// <param name="BirthSeconds">The root directory birth time in Unix seconds.</param>
/// <param name="BirthNanoseconds">The nanosecond fraction of the root directory birth time.</param>
internal sealed record KeycloakVolumeIncarnation(
    uint DeviceMajor,
    uint DeviceMinor,
    ulong Inode,
    long BirthSeconds,
    uint BirthNanoseconds)
{
    /// <summary>The fixed root of the supervised worker's Docker volume mount.</summary>
    internal const string VolumeRoot = "/volume";
    private const int AtFdcwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int OpenDirectoryNoFollowCloseOnExec = 0x00004000 | 0x00008000 | 0x00080000;
    private const uint OwnerFields = LinuxArm64Statx.UserIdField | LinuxArm64Statx.GroupIdField;
    private const uint DatabaseOwnershipStatxMask =
        LinuxArm64Statx.TypeField | LinuxArm64Statx.LinkCountField | LinuxArm64Statx.InodeField | OwnerFields;
    private const uint RequestedStatxMask = LinuxArm64Statx.TypeField | LinuxArm64Statx.InodeField | LinuxArm64Statx.BirthTimeField;
    private const ushort FileTypeMask = 0xF000;
    private const ushort DirectoryType = 0x4000;
    private const ushort RegularFileType = 0x8000;

    /// <summary>Reads only the mount root's filesystem identity metadata.</summary>
    /// <param name="volumeRoot">The fixed worker mount or an isolated native test directory.</param>
    /// <exception cref="PlatformNotSupportedException">The process is not running on Linux ARM64.</exception>
    /// <exception cref="IOException">The root metadata could not be read by Linux <c>statx</c>.</exception>
    /// <exception cref="InvalidDataException">The returned root metadata is incomplete or invalid.</exception>
    internal static KeycloakVolumeIncarnation Read(string volumeRoot = VolumeRoot)
    {
        EnsureSupportedPlatform();
        return ReadCore(volumeRoot, LinuxNativeOperations.Instance);
    }

    /// <summary>Reads the root incarnation and numeric ownership without traversing the data volume.</summary>
    /// <param name="volumeRoot">The fixed worker mount or an isolated native test directory.</param>
    /// <remarks>Ownership selects the supported server identity. It is excluded from the incarnation comparison.</remarks>
    /// <exception cref="PlatformNotSupportedException">The process is not running on Linux ARM64.</exception>
    /// <exception cref="IOException">The root metadata could not be read by Linux <c>statx</c>.</exception>
    /// <exception cref="InvalidDataException">The returned root metadata is incomplete or invalid.</exception>
    internal static (KeycloakVolumeIncarnation Incarnation, uint Uid, uint Gid) ReadOwnership(string volumeRoot = VolumeRoot)
    {
        EnsureSupportedPlatform();
        return ReadOwnershipCore(volumeRoot, LinuxNativeOperations.Instance);
    }

    /// <summary>Reads and parses the root incarnation through the supplied native operations.</summary>
    /// <param name="volumeRoot">The fixed worker mount or an isolated native test directory.</param>
    /// <param name="nativeOperations">The native metadata operations.</param>
    /// <remarks>The production entry point enforces the Linux ARM64 platform requirement before reaching this core.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="nativeOperations"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The root metadata could not be read.</exception>
    /// <exception cref="InvalidDataException">The returned root metadata is incomplete or invalid.</exception>
    internal static KeycloakVolumeIncarnation ReadCore(
        string volumeRoot,
        IKeycloakVolumeIncarnationNativeOperations nativeOperations)
    {
        ArgumentNullException.ThrowIfNull(nativeOperations);
        return Parse(ReadStatCore(volumeRoot, RequestedStatxMask, nativeOperations));
    }

    /// <summary>Reads and parses the root incarnation and numeric ownership through the supplied native operations.</summary>
    /// <param name="volumeRoot">The fixed worker mount or an isolated native test directory.</param>
    /// <param name="nativeOperations">The native metadata operations.</param>
    /// <remarks>The production entry point enforces the Linux ARM64 platform requirement before reaching this core.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="nativeOperations"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The root metadata could not be read.</exception>
    /// <exception cref="InvalidDataException">The returned root metadata is incomplete or invalid.</exception>
    internal static (KeycloakVolumeIncarnation Incarnation, uint Uid, uint Gid) ReadOwnershipCore(
        string volumeRoot,
        IKeycloakVolumeIncarnationNativeOperations nativeOperations)
    {
        ArgumentNullException.ThrowIfNull(nativeOperations);
        return ParseOwnership(ReadStatCore(volumeRoot, RequestedStatxMask | OwnerFields, nativeOperations));
    }

    /// <summary>Reads only the owner of the existing H2 database file using no-follow directory-relative metadata calls.</summary>
    /// <param name="volumeRoot">The absolute mounted volume root; no path component may be a link.</param>
    /// <returns>The database file UID and GID, or <see langword="null"/> when <c>h2/keycloakdb.mv.db</c> is absent.</returns>
    /// <exception cref="PlatformNotSupportedException">The process is not running on Linux ARM64.</exception>
    /// <exception cref="InvalidDataException">A path component is unsafe or the database is not a valid regular file.</exception>
    /// <exception cref="IOException">The path could not be inspected.</exception>
    internal static (uint Uid, uint Gid)? ReadPinnedDatabaseOwnership(string volumeRoot = VolumeRoot)
    {
        EnsureSupportedPlatform();
        return ReadPinnedDatabaseOwnershipCore(volumeRoot, LinuxNativeOperations.Instance);
    }

    /// <summary>Inspects the pinned H2 database through the supplied native operations.</summary>
    /// <param name="volumeRoot">The absolute mounted volume root; no path component may be a link.</param>
    /// <param name="nativeOperations">Directory-relative open and metadata operations.</param>
    /// <returns>The database file UID and GID, or <see langword="null"/> when the H2 database is absent.</returns>
    /// <remarks>The production entry point enforces the Linux ARM64 platform requirement before reaching this core.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="nativeOperations"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="volumeRoot"/> is blank or relative.</exception>
    /// <exception cref="InvalidDataException">A path component is unsafe or the database metadata is invalid.</exception>
    /// <exception cref="IOException">The path or its metadata could not be inspected.</exception>
    internal static (uint Uid, uint Gid)? ReadPinnedDatabaseOwnershipCore(
        string volumeRoot,
        IKeycloakVolumeIncarnationNativeOperations nativeOperations)
    {
        ArgumentNullException.ThrowIfNull(nativeOperations);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        if (volumeRoot[0] != '/')
            throw new ArgumentException("The volume root must be absolute.", nameof(volumeRoot));

        var components = volumeRoot.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component is "." or ".."))
            throw new InvalidDataException("The volume root contains an unsafe path component.");

        var directories = new List<SafeHandle>();
        try
        {
            var rootDescriptor = OpenDirectory(nativeOperations, AtFdcwd, "/")
                ?? throw new IOException("The pinned H2 database root could not be opened.");
            directories.Add(rootDescriptor);
            var parent = rootDescriptor;
            foreach (var component in components)
            {
                parent = OpenDirectory(nativeOperations, parent.DangerousGetHandle().ToInt32(), component)
                    ?? throw new IOException("The pinned H2 database root could not be opened.");
                directories.Add(parent);
            }

            var h2Descriptor = OpenDirectory(
                nativeOperations, parent.DangerousGetHandle().ToInt32(), "h2", missingIsNull: true);
            if (h2Descriptor is null) return null;
            directories.Add(h2Descriptor);

            var stat = new byte[LinuxArm64Statx.BufferSize];
            if (nativeOperations.Statx(
                    h2Descriptor.DangerousGetHandle().ToInt32(), "keycloakdb.mv.db",
                    AtSymlinkNoFollow, DatabaseOwnershipStatxMask, stat, out var error) != 0)
            {
                if (error == 2) return null;
                throw new IOException("The pinned H2 database metadata could not be read.");
            }

            var status = new LinuxArm64Statx(stat);
            if (!status.HasFields(DatabaseOwnershipStatxMask)
                || (status.Mode & FileTypeMask) != RegularFileType || status.LinkCount == 0 || status.Inode == 0)
                throw new InvalidDataException("The pinned H2 database metadata was incomplete or unsafe.");

            return (status.UserId, status.GroupId);
        }
        finally
        {
            for (var index = directories.Count - 1; index >= 0; index--)
                directories[index].Dispose();
        }
    }

    internal static (KeycloakVolumeIncarnation Incarnation, uint Uid, uint Gid) ParseOwnership(ReadOnlySpan<byte> stat)
    {
        var incarnation = Parse(stat);
        var status = new LinuxArm64Statx(stat);
        if (!status.HasFields(OwnerFields))
            throw InvalidMetadata();
        return (incarnation, status.UserId, status.GroupId);
    }

    private static byte[] ReadStatCore(
        string volumeRoot,
        uint mask,
        IKeycloakVolumeIncarnationNativeOperations nativeOperations)
    {
        var stat = new byte[LinuxArm64Statx.BufferSize];
        if (nativeOperations.Statx(AtFdcwd, volumeRoot, AtSymlinkNoFollow, mask, stat, out _) != 0)
            throw new IOException("Keycloak volume root metadata could not be read.");
        return stat;
    }

    /// <summary>Parses the fixed Linux ARM64 <c>statx</c> fields used by <see cref="Read"/>.</summary>
    /// <param name="stat">A complete 256-byte Linux <c>statx</c> result.</param>
    /// <exception cref="InvalidDataException">The result has the wrong size or lacks a supported directory identity.</exception>
    internal static KeycloakVolumeIncarnation Parse(ReadOnlySpan<byte> stat)
    {
        if (stat.Length != LinuxArm64Statx.BufferSize)
            throw InvalidMetadata();

        var status = new LinuxArm64Statx(stat);
        if (!status.HasFields(RequestedStatxMask))
            throw InvalidMetadata();

        if ((status.Mode & FileTypeMask) != DirectoryType)
            throw InvalidMetadata();

        var inode = status.Inode;
        var birthSeconds = status.BirthSeconds;
        var birthNanoseconds = status.BirthNanoseconds;
        if (inode == 0 || birthSeconds <= 0 || birthNanoseconds >= 1_000_000_000)
            throw InvalidMetadata();

        return new KeycloakVolumeIncarnation(status.DeviceMajor, status.DeviceMinor, inode, birthSeconds, birthNanoseconds);
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("Keycloak volume identity is supported only on Linux ARM64.");
    }

    private static InvalidDataException InvalidMetadata()
        => new("Keycloak volume root metadata was incomplete or invalid.");

    private static SafeHandle? OpenDirectory(
        IKeycloakVolumeIncarnationNativeOperations nativeOperations,
        int directoryDescriptor,
        string name,
        bool missingIsNull = false)
    {
        var handle = nativeOperations.OpenAt(
            directoryDescriptor, name, OpenDirectoryNoFollowCloseOnExec, out var error);
        if (handle is not null) return handle;

        if (missingIsNull && error == 2) return null;
        if (error is 20 or 40)
            throw new InvalidDataException("The pinned H2 database path contains a link or nondirectory element.");
        throw new IOException("The pinned H2 database directory could not be opened.");
    }

    private sealed class LinuxNativeOperations : IKeycloakVolumeIncarnationNativeOperations
    {
        internal static LinuxNativeOperations Instance { get; } = new();

        public SafeHandle? OpenAt(int directoryDescriptor, string path, int flags, out int error)
        {
            var descriptor = Native.openat(directoryDescriptor, path, flags);
            error = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
            return descriptor < 0 ? null : new SafeFileHandle((nint)descriptor, ownsHandle: true);
        }

        public int Statx(int directoryDescriptor, string path, int flags, uint mask, byte[] buffer, out int error)
        {
            var result = LinuxArm64Statx.ReadNative(directoryDescriptor, path, flags, mask, buffer);
            error = result != 0 ? Marshal.GetLastPInvokeError() : 0;
            return result;
        }
    }

    private static class Native
    {
        [DllImport("libc", SetLastError = true, EntryPoint = "openat")]
        internal static extern int openat(
            int directoryDescriptor,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags);
    }
}
