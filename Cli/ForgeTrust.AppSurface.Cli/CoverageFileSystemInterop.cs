using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

#if EVIDENCE_COVERAGE_CORE
namespace ForgeTrust.AppSurface.Evidence.Coverage;
#else
namespace ForgeTrust.AppSurface.CoverageArtifacts;
#endif

/// <summary>
/// Provides the native file-system primitives shared by secure coverage path traversal.
/// </summary>
/// <remarks>
/// This type contains only the operating-system boundary. Callers remain responsible for
/// choosing no-follow flags, validating object kinds and identities, and retaining handles
/// for the lifetime of each security-sensitive operation.
/// </remarks>
internal static partial class CoverageFileSystemInterop
{
    /// <summary>Atomically creates a fresh Windows directory with the supplied private owner/ACL; an existing path is never accepted.</summary>
    /// <param name="path">An extended absolute path beneath a retained validated parent.</param>
    /// <param name="securityAttributes">Pointer to caller-pinned SECURITY_ATTRIBUTES and descriptor for this call.</param>
    /// <returns>True only for a newly created directory; the native last error identifies every failure.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WindowsCreateDirectory(string path, nint securityAttributes);

    /// <summary>Selects no-follow directory and file flags for Darwin or a supported Linux architecture.</summary>
    /// <param name="macos">Whether Darwin constants apply.</param>
    /// <param name="architecture">Native process architecture; Linux x64 and ARM64 use distinct directory and no-follow flags.</param>
    /// <returns>The native directory-only and no-follow flags, respectively.</returns>
    /// <exception cref="PlatformNotSupportedException">The Linux architecture has no verified flag mapping.</exception>
    /// <remarks>This selects native constants only. Callers still retain and verify the opened object and its ancestors.</remarks>
    internal static (int Directory, int NoFollow) UnixTraversalFlags(bool macos, Architecture architecture)
    {
        if (macos) return (0x00100000, 0x00000100);
        return architecture switch
        {
            Architecture.X64 => (0x00010000, 0x00020000),
            Architecture.Arm64 => (0x00004000, 0x00008000),
            _ => throw new PlatformNotSupportedException("Secure Unix traversal supports Darwin and Linux x64 or ARM64.")
        };
    }

    /// <summary>
    /// Opens a Unix path with the supplied native flags.
    /// </summary>
    /// <param name="path">The UTF-8 path to open.</param>
    /// <param name="flags">The platform-specific native open flags.</param>
    /// <returns>A nonnegative file descriptor on success; otherwise <c>-1</c>.</returns>
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnixOpen(string path, int flags);

    /// <summary>
    /// Opens a Unix path relative to an existing directory descriptor.
    /// </summary>
    /// <param name="directoryDescriptor">The descriptor of the directory from which traversal starts.</param>
    /// <param name="path">The UTF-8 relative path to open.</param>
    /// <param name="flags">The platform-specific native open flags.</param>
    /// <returns>A nonnegative file descriptor on success; otherwise <c>-1</c>.</returns>
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnixOpenAt(int directoryDescriptor, string path, int flags);

    /// <summary>
    /// Opens or creates a Unix path relative to an existing directory descriptor.
    /// </summary>
    /// <param name="directoryDescriptor">The descriptor of the directory from which traversal starts.</param>
    /// <param name="path">The UTF-8 relative path to open or create.</param>
    /// <param name="flags">The platform-specific native open flags.</param>
    /// <param name="mode">The permissions applied when the flags create a new object.</param>
    /// <returns>A nonnegative file descriptor on success; otherwise <c>-1</c>.</returns>
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnixOpenAt(int directoryDescriptor, string path, int flags, uint mode);

    /// <summary>Reads metadata using the native stat layout selected for the current Unix ABI.</summary>
    /// <param name="descriptor">An open Unix file descriptor.</param>
    /// <param name="buffer">Caller-owned storage for the platform's native stat structure.</param>
    /// <returns>Zero on success; otherwise <c>-1</c> with the native error available.</returns>
    internal static int UnixFStat(int descriptor, nint buffer) => InvokeUnixFStat(
        OperatingSystem.IsMacOS() ? "macos" : "unix", RuntimeInformation.ProcessArchitecture,
        descriptor, buffer, UnixFStatNative, DarwinX64FStatNative);

    /// <summary>Dispatches fstat to the ABI-compatible libc symbol.</summary>
    /// <remarks>Intel Darwin's 64-bit inode structure is exposed as <c>fstat$INODE64</c>; arm64 uses <c>fstat</c>.</remarks>
    internal static int InvokeUnixFStat(
        string platform,
        Architecture architecture,
        int descriptor,
        nint buffer,
        Func<int, nint, int> unixFStat,
        Func<int, nint, int> darwinX64FStat)
    {
        ArgumentNullException.ThrowIfNull(unixFStat);
        ArgumentNullException.ThrowIfNull(darwinX64FStat);
        return platform.Equals("macos", StringComparison.OrdinalIgnoreCase) && architecture == Architecture.X64
            ? darwinX64FStat(descriptor, buffer)
            : unixFStat(descriptor, buffer);
    }

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int UnixFStatNative(int descriptor, nint buffer);

    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int DarwinX64FStatNative(int descriptor, nint buffer);

    /// <summary>
    /// Changes the permissions of an open Unix descriptor.
    /// </summary>
    /// <param name="descriptor">The open descriptor whose permissions should be changed.</param>
    /// <param name="mode">The requested Unix permission bits.</param>
    /// <returns>Zero on success; otherwise <c>-1</c>.</returns>
    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    internal static partial int UnixFChmod(int descriptor, uint mode);

    /// <summary>
    /// Opens or creates a Windows file-system object without applying managed path traversal.
    /// </summary>
    /// <param name="fileName">The UTF-16 path to open.</param>
    /// <param name="desiredAccess">The requested native access mask.</param>
    /// <param name="shareMode">The native sharing mask retained for the handle lifetime.</param>
    /// <param name="securityAttributes">An optional native security-attributes pointer.</param>
    /// <param name="creationDisposition">The native create-or-open disposition.</param>
    /// <param name="flagsAndAttributes">The native file flags and attributes.</param>
    /// <param name="templateFile">An optional template-file handle.</param>
    /// <returns>A handle that is invalid when the native open fails.</returns>
    internal static SafeFileHandle WindowsCreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile)
        => WindowsCreateFileNative(WindowsExtendedPath(Path.GetFullPath(fileName)), desiredAccess,
            shareMode, securityAttributes, creationDisposition, flagsAndAttributes, templateFile);

    /// <summary>Formats an already absolute Windows path for native Unicode file APIs without the legacy MAX_PATH limit.</summary>
    /// <param name="absolutePath">Absolute drive, UNC, or already extended path; callers normalize navigation before this boundary.</param>
    /// <returns>The extended drive or UNC spelling, preserving an existing extended prefix.</returns>
    /// <remarks>This changes only native path spelling. Callers still validate containment, reparse points, identity and access rights.</remarks>
    internal static string WindowsExtendedPath(string absolutePath)
    {
        var path = absolutePath.Replace('/', '\\');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle WindowsCreateFileNative(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>
    /// Reads the attribute and reparse-tag metadata for an open Windows handle.
    /// </summary>
    /// <param name="handle">The open handle to inspect.</param>
    /// <param name="fileInformationClass">The native information-class identifier.</param>
    /// <param name="fileInformation">Receives the file attributes and reparse tag.</param>
    /// <param name="bufferSize">The size of <paramref name="fileInformation"/> in bytes.</param>
    /// <returns><see langword="true"/> on success; otherwise <see langword="false"/>.</returns>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WindowsGetFileInformationByHandleEx(
        SafeFileHandle handle,
        int fileInformationClass,
        out WindowsFileAttributeTagInformation fileInformation,
        uint bufferSize);

    /// <summary>
    /// Reads basic timestamps and attributes for an open Windows file handle.
    /// </summary>
    /// <param name="handle">The open file-system handle.</param>
    /// <param name="fileInformationClass">The native information-class identifier.</param>
    /// <param name="fileInformation">Receives the basic timestamps and attributes.</param>
    /// <param name="bufferSize">The size of <paramref name="fileInformation"/> in bytes.</param>
    /// <returns><see langword="true"/> on success; otherwise <see langword="false"/>.</returns>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WindowsGetFileBasicInformationByHandleEx(
        SafeFileHandle handle,
        int fileInformationClass,
        out WindowsFileBasicInformation fileInformation,
        uint bufferSize);

    /// <summary>
    /// Reads stable file-system identity metadata for an open Windows handle.
    /// </summary>
    /// <param name="handle">The open handle to inspect.</param>
    /// <param name="fileInformation">Receives the native identity and size metadata.</param>
    /// <returns><see langword="true"/> on success; otherwise <see langword="false"/>.</returns>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WindowsGetFileInformationByHandle(
        SafeFileHandle handle,
        out WindowsByHandleFileInformation fileInformation);

    /// <summary>
    /// Gets the Windows object type associated with an open handle.
    /// </summary>
    /// <param name="handle">The open handle to classify.</param>
    /// <returns>The native file-type constant, or zero when the call fails.</returns>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    internal static partial uint WindowsGetFileType(SafeFileHandle handle);

    /// <summary>
    /// Resolves the final Windows path for an open handle into the supplied buffer.
    /// </summary>
    /// <param name="handle">The open file-system handle.</param>
    /// <param name="path">The destination buffer, including space for the terminator.</param>
    /// <param name="flags">The native volume-name formatting flags.</param>
    /// <returns>
    /// The path length excluding the terminator, or the required buffer length when the buffer
    /// is too small; zero indicates failure and leaves the native error available to the caller.
    /// </returns>
    [ExcludeFromCodeCoverage(Justification = "Windows-only final-path marshalling is exercised by the Windows security test lane.")]
    internal static unsafe uint WindowsGetFinalPathNameByHandle(
        SafeFileHandle handle,
        Span<char> path,
        uint flags)
    {
        fixed (char* pathPointer = path)
        {
            return WindowsGetFinalPathNameByHandleNative(handle, pathPointer, (uint)path.Length, flags);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint WindowsGetFinalPathNameByHandleNative(
        SafeFileHandle handle,
        char* path,
        uint pathLength,
        uint flags);

    /// <summary>
    /// Contains Windows file attributes and the reparse tag for an open handle.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct WindowsFileAttributeTagInformation
    {
        /// <summary>
        /// Gets or sets the native file-attribute bit field.
        /// </summary>
        internal uint FileAttributes;

        /// <summary>
        /// Gets or sets the native reparse tag.
        /// </summary>
        internal uint ReparseTag;
    }

    /// <summary>Contains native Windows basic timestamps and attributes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowsFileBasicInformation
    {
        /// <summary>Gets or sets the creation time in 100-nanosecond intervals.</summary>
        internal long CreationTime;

        /// <summary>Gets or sets the last-access time in 100-nanosecond intervals.</summary>
        internal long LastAccessTime;

        /// <summary>Gets or sets the last-write time in 100-nanosecond intervals.</summary>
        internal long LastWriteTime;

        /// <summary>Gets or sets the change time in 100-nanosecond intervals.</summary>
        internal long ChangeTime;

        /// <summary>Gets or sets the native file-attribute bit field.</summary>
        internal uint FileAttributes;
    }

    /// <summary>
    /// Contains stable identity and size metadata for an open Windows handle.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct WindowsByHandleFileInformation
    {
        /// <summary>Gets or sets the native file-attribute bit field.</summary>
        internal uint FileAttributes;

        /// <summary>Gets or sets the creation timestamp.</summary>
        internal WindowsFileTime CreationTime;

        /// <summary>Gets or sets the last-access timestamp.</summary>
        internal WindowsFileTime LastAccessTime;

        /// <summary>Gets or sets the last-write timestamp.</summary>
        internal WindowsFileTime LastWriteTime;

        /// <summary>Gets or sets the serial number of the containing volume.</summary>
        internal uint VolumeSerialNumber;

        /// <summary>Gets or sets the high word of the file size.</summary>
        internal uint FileSizeHigh;

        /// <summary>Gets or sets the low word of the file size.</summary>
        internal uint FileSizeLow;

        /// <summary>Gets or sets the number of links to the file.</summary>
        internal uint NumberOfLinks;

        /// <summary>Gets or sets the high word of the stable file identifier.</summary>
        internal uint FileIndexHigh;

        /// <summary>Gets or sets the low word of the stable file identifier.</summary>
        internal uint FileIndexLow;
    }

    /// <summary>
    /// Represents the blittable two-word layout of a native Windows <c>FILETIME</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct WindowsFileTime
    {
        /// <summary>Gets or sets the low word of the native timestamp.</summary>
        internal uint LowDateTime;

        /// <summary>Gets or sets the high word of the native timestamp.</summary>
        internal uint HighDateTime;
    }
}
