using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.CoverageArtifacts;

/// <summary>
/// Opens collector artifacts without following symbolic links or accepting non-regular files.
/// </summary>
internal static class CoverageRunArtifactReader
{
    private const uint WindowsGenericRead = 0x80000000;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsFlagBackupSemantics = 0x02000000;
    private const uint WindowsFlagOpenReparsePoint = 0x00200000;
    private const uint WindowsAttributeDirectory = 0x00000010;
    private const uint WindowsAttributeReparsePoint = 0x00000400;
    private const uint WindowsFileTypeDisk = 0x0001;
    private const int WindowsFileAttributeTagInfo = 9;
    private const uint WindowsVolumeNameDos = 0;

    /// <summary>
    /// Opens <paramref name="candidate"/> for reading and validates the opened object itself.
    /// </summary>
    /// <param name="projectOutputDirectory">The trusted project output root.</param>
    /// <param name="rawResultsDirectory">The unique raw-results directory for this invocation.</param>
    /// <param name="candidate">The enumerated collector artifact path.</param>
    /// <param name="beforeWindowsCandidateOpen">An optional test seam invoked after Windows parent handles are held.</param>
    /// <remarks>
    /// Every untrusted path component is traversed without following links on Unix. On Windows, the
    /// opened handle is rejected when it is a reparse point or non-disk file, and its final path must
    /// remain beneath the already-open raw-results directory. Unsupported platforms fail closed.
    /// </remarks>
    public static FileStream OpenRegularFile(
        string projectOutputDirectory,
        string rawResultsDirectory,
        string candidate,
        Action? beforeWindowsCandidateOpen = null)
    {
        ValidateLexicalContainment(projectOutputDirectory, rawResultsDirectory, candidate);

        if (OperatingSystem.IsWindows())
        {
            return OpenWindows(rawResultsDirectory, candidate, beforeWindowsCandidateOpen);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return OpenUnix(projectOutputDirectory, rawResultsDirectory, candidate);
        }

        throw new IOException($"Collector artifacts are not supported securely on {RuntimeInformation.OSDescription}.");
    }

    private static FileStream OpenUnix(
        string projectOutputDirectory,
        string rawResultsDirectory,
        string candidate)
    {
        var directoryFlags = UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory;
        var projectOutput = Path.GetFullPath(projectOutputDirectory);
        var rawResults = Path.GetFullPath(rawResultsDirectory);
        var root = OpenUnixHandle(projectOutput, directoryFlags);
        var openedDirectories = new List<SafeFileHandle> { root };
        try
        {
            var rawComponents = ValidateUnixRelativeArtifactPath(Path.GetRelativePath(projectOutput, rawResults));
            var candidateComponents = ValidateUnixRelativeArtifactPath(
                Path.GetRelativePath(rawResults, Path.GetFullPath(candidate)));

            var current = root;
            foreach (var component in rawComponents.Concat(candidateComponents[..^1]))
            {
                var directory = OpenUnixHandleAt(current, component, directoryFlags);
                openedDirectories.Add(directory);
                current = directory;
            }

            var file = OpenUnixHandleAt(
                current,
                candidateComponents[^1],
                UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenNonBlocking);
            try
            {
                if (!UnixHandleIsRegularFile(file))
                {
                    throw new IOException("Collector artifact is not a regular file.");
                }

                return new FileStream(file, FileAccess.Read, bufferSize: 81920, isAsync: false);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var directory in openedDirectories)
            {
                directory.Dispose();
            }
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only handle traversal is exercised by the Windows test lane; Unix coverage runs cannot execute it.")]
    private static FileStream OpenWindows(
        string rawResultsDirectory,
        string candidate,
        Action? beforeCandidateOpen)
    {
        using var raw = OpenWindowsHandle(
            rawResultsDirectory,
            WindowsFlagBackupSemantics | WindowsFlagOpenReparsePoint,
            WindowsDirectoryShareMode);
        RejectWindowsReparseOrWrongKind(raw, expectDirectory: true);
        var rawFinalPath = NormalizeWindowsFinalPath(GetWindowsFinalPath(raw));
        if (!string.Equals(rawFinalPath, NormalizeWindowsPath(rawResultsDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Collector raw-results path contains a reparse point.");
        }

        var openedDirectories = OpenWindowsCandidateDirectories(rawResultsDirectory, candidate);
        try
        {
            beforeCandidateOpen?.Invoke();
            var file = OpenWindowsHandle(candidate, WindowsFlagBackupSemantics | WindowsFlagOpenReparsePoint);
            try
            {
                RejectWindowsReparseOrWrongKind(file, expectDirectory: false);
                if (GetFileType(file) != WindowsFileTypeDisk)
                {
                    throw new IOException("Collector artifact is not a regular disk file.");
                }

                var candidateFinalPath = NormalizeWindowsFinalPath(GetWindowsFinalPath(file));
                if (!IsPathWithin(rawFinalPath, candidateFinalPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Collector artifact escaped the raw-results directory.");
                }

                if (!string.Equals(candidateFinalPath, NormalizeWindowsPath(candidate), StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Collector artifact path contains a reparse point.");
                }

                RevalidateWindowsDirectoryIdentities(openedDirectories);
                return new FileStream(file, FileAccess.Read, bufferSize: 81920, isAsync: false);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var directory in openedDirectories)
            {
                directory.Handle.Dispose();
            }
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only handle traversal is exercised by the Windows test lane; Unix coverage runs cannot execute it.")]
    private static IReadOnlyList<WindowsOpenedDirectory> OpenWindowsCandidateDirectories(
        string rawResultsDirectory,
        string candidate)
    {
        var raw = NormalizeWindowsPath(rawResultsDirectory);
        var candidateDirectory = Path.GetDirectoryName(NormalizeWindowsPath(candidate))
            ?? throw new IOException("Collector artifact path has no parent directory.");
        var relativeDirectory = Path.GetRelativePath(raw, candidateDirectory);
        var directories = new List<WindowsOpenedDirectory>();
        if (relativeDirectory == ".")
        {
            return directories;
        }

        try
        {
            var current = raw;
            foreach (var component in relativeDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Join(current, component);
                var directory = OpenWindowsHandle(
                    current,
                    WindowsFlagBackupSemantics | WindowsFlagOpenReparsePoint,
                    WindowsDirectoryShareMode);
                try
                {
                    RejectWindowsReparseOrWrongKind(directory, expectDirectory: true);
                    if (!string.Equals(
                            NormalizeWindowsFinalPath(GetWindowsFinalPath(directory)),
                            NormalizeWindowsPath(current),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("Collector artifact parent path contains a reparse point.");
                    }

                    directories.Add(new WindowsOpenedDirectory(
                        new WindowsDirectoryIdentity(current, GetWindowsFileIdentity(directory)),
                        directory));
                }
                catch
                {
                    directory.Dispose();
                    throw;
                }
            }

            return directories;
        }
        catch
        {
            foreach (var directory in directories)
            {
                directory.Handle.Dispose();
            }

            throw;
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only handle revalidation is exercised by the Windows test lane; Unix coverage runs cannot execute it.")]
    private static void RevalidateWindowsDirectoryIdentities(IReadOnlyList<WindowsOpenedDirectory> expected)
    {
        foreach (var expectedDirectory in expected)
        {
            RejectWindowsReparseOrWrongKind(expectedDirectory.Handle, expectDirectory: true);
            var finalPath = NormalizeWindowsFinalPath(GetWindowsFinalPath(expectedDirectory.Handle));
            EnsureWindowsDirectoryIdentityUnchanged(
                expectedDirectory.Identity,
                new WindowsDirectoryIdentity(finalPath, GetWindowsFileIdentity(expectedDirectory.Handle)));
        }
    }

    /// <summary>
    /// Rejects a Windows directory that no longer names the same opened file-system object.
    /// </summary>
    /// <param name="expected">The path and identity captured before opening the artifact.</param>
    /// <param name="actual">The path and identity captured after opening the artifact.</param>
    internal static void EnsureWindowsDirectoryIdentityUnchanged(
        WindowsDirectoryIdentity expected,
        WindowsDirectoryIdentity actual)
    {
        if (!string.Equals(
                NormalizeWindowsPath(expected.Path),
                NormalizeWindowsPath(actual.Path),
                StringComparison.OrdinalIgnoreCase)
            || expected.Identity != actual.Identity)
        {
            throw new IOException("Collector artifact parent directory changed while the artifact was opened.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only file identity inspection is exercised by the Windows test lane; Unix coverage runs cannot execute it.")]
    private static WindowsFileIdentity GetWindowsFileIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException("Unable to identify collector artifact directory handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return new WindowsFileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static SafeFileHandle OpenUnixHandle(string path, int flags)
    {
        var descriptor = Open(path, flags);
        return CreateUnixHandle(descriptor, path);
    }

    private static SafeFileHandle OpenUnixHandleAt(SafeFileHandle directory, string component, int flags)
    {
        var descriptor = OpenAt(directory.DangerousGetHandle().ToInt32(), component, flags);
        return CreateUnixHandle(descriptor, component);
    }

    private static SafeFileHandle CreateUnixHandle(int descriptor, string path)
    {
        if (descriptor < 0)
        {
            throw new IOException($"Unable to securely open collector artifact path '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    [ExcludeFromCodeCoverage(Justification = "Native stat layouts vary by Unix OS and architecture; the platform security lanes exercise their supported layouts.")]
    private static bool UnixHandleIsRegularFile(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (FStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
            {
                throw new IOException("Unable to inspect collector artifact handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            uint mode;
            if (OperatingSystem.IsMacOS())
            {
                mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
            }
            else if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                mode = unchecked((uint)Marshal.ReadInt32(buffer, 24));
            }
            else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                mode = unchecked((uint)Marshal.ReadInt32(buffer, 16));
            }
            else
            {
                throw new IOException($"Secure artifact inspection is unsupported on {RuntimeInformation.ProcessArchitecture} Linux.");
            }

            return (mode & 0xF000) == 0x8000;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only handle opening is exercised by the Windows test lane; Unix coverage runs cannot execute it.")]
    private static SafeFileHandle OpenWindowsHandle(
        string path,
        uint flags,
        uint shareMode = WindowsShareRead | WindowsShareWrite | WindowsShareDelete)
    {
        var handle = CreateFile(
            path,
            WindowsGenericRead,
            shareMode,
            0,
            WindowsOpenExisting,
            flags,
            0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Unable to securely open collector artifact path '{path}'.", new Win32Exception(error));
        }

        return handle;
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only reparse inspection is exercised by the Windows test lane; Unix coverage runs cannot execute it.")]
    private static void RejectWindowsReparseOrWrongKind(SafeFileHandle handle, bool expectDirectory)
    {
        if (!GetFileInformationByHandleEx(handle, WindowsFileAttributeTagInfo, out var information, (uint)Marshal.SizeOf<WindowsFileAttributeTagInformation>()))
        {
            throw new IOException("Unable to inspect collector artifact handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if ((information.FileAttributes & WindowsAttributeReparsePoint) != 0)
        {
            throw new IOException("Collector artifact path contains a reparse point.");
        }

        var isDirectory = (information.FileAttributes & WindowsAttributeDirectory) != 0;
        if (isDirectory != expectDirectory)
        {
            throw new IOException(expectDirectory
                ? "Collector raw-results path is not a directory."
                : "Collector artifact is not a regular file.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only final-path resolution is exercised by the Windows test lane; Unix coverage runs cannot execute it.")]
    private static string GetWindowsFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        var builder = new StringBuilder(capacity);
        while (true)
        {
            builder.EnsureCapacity(capacity);
            builder.Clear();
            var length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, WindowsVolumeNameDos);
            if (length == 0)
            {
                throw new IOException("Unable to resolve collector artifact handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            if (length < builder.Capacity)
            {
                return builder.ToString();
            }

            capacity = checked((int)length + 1);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only extended-path normalization is exercised by the Windows test lane; Unix coverage runs cannot execute it.")]
    private static string NormalizeWindowsFinalPath(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        var normalized = path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[extendedUncPrefix.Length..]
            : path.StartsWith(extendedPrefix, StringComparison.Ordinal)
                ? path[extendedPrefix.Length..]
                : path;
        return NormalizeWindowsPath(normalized);
    }

    private static string NormalizeWindowsPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void ValidateLexicalContainment(string projectOutputDirectory, string rawResultsDirectory, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var project = Path.GetFullPath(projectOutputDirectory);
        var raw = Path.GetFullPath(rawResultsDirectory);
        var file = Path.GetFullPath(candidate);
        if (!IsPathWithin(project, raw, comparison) || !IsPathWithin(raw, file, comparison))
        {
            throw new IOException("Collector artifact path escaped its invocation directory.");
        }
    }

    /// <summary>
    /// Splits a Unix artifact path only after proving that it is a relative descent with no navigation components.
    /// </summary>
    internal static string[] ValidateUnixRelativeArtifactPath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new IOException("Collector artifact path does not name a relative file.");
        }

        var components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0
            || components.Any(component => component is "." or ".."))
        {
            throw new IOException("Collector artifact path escaped its invocation directory.");
        }

        return components;
    }

    /// <summary>
    /// Gets the Windows sharing policy used while holding an artifact parent directory open.
    /// </summary>
    internal static uint WindowsDirectoryShareMode => WindowsShareRead | WindowsShareWrite;

    private static bool IsPathWithin(string root, string path, StringComparison comparison)
        => path.StartsWith(
            Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            comparison);

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
    private static int UnixOpenCloseOnExec => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
    private static int UnixOpenDirectory => OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
    private static int UnixOpenNoFollow => OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
    private static int UnixOpenNonBlocking => OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;
    private const int UnixOpenReadOnly = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>
    /// Identifies one Windows file-system object independently of its path.
    /// </summary>
    /// <param name="VolumeSerialNumber">The serial number of the volume containing the object.</param>
    /// <param name="FileId">The file identifier reported for the opened object.</param>
    [ExcludeFromCodeCoverage(Justification = "Windows-only identity data is exercised by the Windows test lane.")]
    internal readonly record struct WindowsFileIdentity(uint VolumeSerialNumber, ulong FileId);

    /// <summary>
    /// Associates a Windows directory path with the identity of its opened file-system object.
    /// </summary>
    /// <param name="Path">The normalized directory path.</param>
    /// <param name="Identity">The identity captured from the opened directory handle.</param>
    internal readonly record struct WindowsDirectoryIdentity(string Path, WindowsFileIdentity Identity);

    [ExcludeFromCodeCoverage(Justification = "Windows-only retained directory state is exercised by the Windows test lane.")]
    private sealed record WindowsOpenedDirectory(
        WindowsDirectoryIdentity Identity,
        SafeFileHandle Handle);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, nint buffer);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,
        int fileInformationClass,
        out WindowsFileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out WindowsByHandleFileInformation fileInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        StringBuilder path,
        uint pathLength,
        uint flags);
}
