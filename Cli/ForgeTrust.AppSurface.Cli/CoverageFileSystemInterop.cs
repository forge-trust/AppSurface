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

    /// <summary>
    /// Reads native metadata for an open Unix descriptor into a caller-owned buffer.
    /// </summary>
    /// <param name="descriptor">The open descriptor to inspect.</param>
    /// <param name="buffer">A caller-owned buffer large enough for the platform's native stat structure.</param>
    /// <returns>Zero on success; otherwise <c>-1</c>.</returns>
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    internal static partial int UnixFStat(int descriptor, nint buffer);

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
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle WindowsCreateFile(
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
