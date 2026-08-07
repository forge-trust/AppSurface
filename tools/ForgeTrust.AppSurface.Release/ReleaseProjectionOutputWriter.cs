using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Writes tagged release projections through retained directory handles.
/// </summary>
/// <remarks>
/// Every directory component is opened without following links and remains open until the temporary file is atomically renamed. Unix uses
/// <c>openat</c> and <c>renameat</c> relative to the retained descriptor. Windows retains non-delete-sharing directory handles, which pins the
/// traversed path while the temporary file is created, and uses a relative <c>FILE_RENAME_INFO</c> target for replacement.
/// </remarks>
internal sealed class ReleaseProjectionOutputWriter : IDisposable
{
    private const string DocumentationPath = "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state";
    private const int UnixReadOnly = 0;
    private const int UnixWriteOnly = 1;
    private const int UnixNoEntry = 2;
    private const int UnixAlreadyExists = 17;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFileType = 0x8000;
    private const uint WindowsGenericRead = 0x80000000;
    private const uint WindowsGenericWrite = 0x40000000;
    private const uint WindowsDelete = 0x00010000;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsCreateNew = 1;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsWriteThrough = 0x80000000;
    private const uint WindowsAttributeReparsePoint = 0x00000400;
    private const uint WindowsAttributeDirectory = 0x00000010;
    private const int WindowsFileAttributeTagInfo = 9;
    private const int WindowsFileRenameInfo = 3;
    private static readonly AsyncLocal<Action<string>?> DirectoryOpenedHook = new();
    private static readonly AsyncLocal<Action?> TemporaryFileOpenedHook = new();
    private static readonly AsyncLocal<int?> UnixFChmodFailureError = new();
    private readonly string _directoryPath;
    private readonly List<SafeFileHandle> _directoryHandles = [];
    private SafeFileHandle? _directoryHandle;

    private ReleaseProjectionOutputWriter(string directoryPath)
    {
        _directoryPath = NormalizePlatformPath(directoryPath);
    }

    /// <summary>
    /// Creates the output directory securely and atomically replaces the target projection.
    /// </summary>
    /// <param name="outputPath">Absolute logical path to the output file.</param>
    /// <param name="yaml">UTF-8 YAML content to write.</param>
    /// <param name="cancellationToken">Token that cancels before the replacement is committed.</param>
    internal static async Task WriteAsync(string outputPath, string yaml, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(outputPath)
            ?? throw InvalidOutputPath(outputPath, "The output path has no parent directory.");
        var outputFileName = Path.GetFileName(outputPath);
        if (string.IsNullOrWhiteSpace(outputFileName))
        {
            throw InvalidOutputPath(outputPath, "The output path has no file name.");
        }

        try
        {
            using var writer = new ReleaseProjectionOutputWriter(directoryPath);
            writer.AcquireDirectory();
            writer.RejectExistingOutputEntry(outputFileName);
            DirectoryOpenedHook.Value?.Invoke(directoryPath);
            cancellationToken.ThrowIfCancellationRequested();

            var temporaryFileName = $".{outputFileName}.{Guid.NewGuid():N}.tmp";
            var replaced = false;
            using var temporaryFile = writer.CreateTemporaryFile(temporaryFileName);
            try
            {
                TemporaryFileOpenedHook.Value?.Invoke();
                await using (var stream = new FileStream(temporaryFile, FileAccess.Write, bufferSize: 4096, isAsync: false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(yaml), cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.ReplaceTemporaryFile(temporaryFile, temporaryFileName, outputFileName);
                    replaced = true;
                }
            }
            finally
            {
                if (!replaced)
                {
                    try
                    {
                        writer.DeleteTemporaryFile(temporaryFileName);
                    }
                    catch (Exception ex) when (cancellationToken.IsCancellationRequested && (ex is IOException or UnauthorizedAccessException))
                    {
                        // Preserve the caller's cancellation result even when best-effort temporary-file cleanup also fails.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw InvalidOutputPath(outputPath, ex.Message);
        }
    }

    /// <summary>
    /// Runs a callback after the target directory is safely opened and before the temporary file is created.
    /// </summary>
    /// <param name="callback">Callback used by tests to simulate a parent-directory replacement race.</param>
    /// <returns>A scope that restores the previous callback.</returns>
    /// <remarks>
    /// The callback is async-flow-local so concurrent tests cannot alter another write. Production code leaves the callback unset.
    /// </remarks>
    internal static IDisposable UseDirectoryOpenedHookForTesting(Action<string> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var previous = DirectoryOpenedHook.Value;
        DirectoryOpenedHook.Value = callback;
        return new DirectoryOpenedHookScope(previous);
    }

    /// <summary>
    /// Runs a callback after the temporary output file is created and before its content is written.
    /// </summary>
    /// <param name="callback">Callback used by tests to deterministically cancel a write after temporary-file creation.</param>
    /// <returns>A scope that restores the previous callback.</returns>
    /// <remarks>
    /// The callback is async-flow-local so concurrent tests cannot alter another write. Production code leaves the callback unset.
    /// </remarks>
    internal static IDisposable UseTemporaryFileOpenedHookForTesting(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var previous = TemporaryFileOpenedHook.Value;
        TemporaryFileOpenedHook.Value = callback;
        return new TemporaryFileOpenedHookScope(previous);
    }

    /// <summary>
    /// Forces Unix temporary-file permission hardening to fail with a specified native error code.
    /// </summary>
    /// <param name="error">Non-zero native error code returned by the test-only failure seam.</param>
    /// <returns>A scope that restores the previous failure seam.</returns>
    /// <remarks>
    /// The seam is async-flow-local so tests can verify cleanup after a permission-hardening failure without depending on host filesystem behavior.
    /// Production code leaves the seam unset and calls <c>fchmod</c> directly.
    /// </remarks>
    internal static IDisposable UseUnixFChmodFailureForTesting(int error)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(error);
        var previous = UnixFChmodFailureError.Value;
        UnixFChmodFailureError.Value = error;
        return new UnixFChmodFailureScope(previous);
    }

    /// <summary>
    /// Canonicalizes fixed macOS temporary-directory aliases before no-follow traversal.
    /// </summary>
    /// <param name="path">Absolute path to canonicalize.</param>
    /// <param name="isMacOs">Optional platform override used by focused tests.</param>
    /// <returns>The physical macOS temporary path or the original path on other platforms.</returns>
    internal static string NormalizePlatformPath(string path, bool? isMacOs = null)
    {
        if (!(isMacOs ?? OperatingSystem.IsMacOS()))
        {
            return path;
        }

        if (string.Equals(path, "/tmp", StringComparison.Ordinal) || path.StartsWith("/tmp/", StringComparison.Ordinal))
        {
            return "/private" + path;
        }

        return string.Equals(path, "/var", StringComparison.Ordinal) || path.StartsWith("/var/", StringComparison.Ordinal)
            ? "/private" + path
            : path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var handle in _directoryHandles.AsEnumerable().Reverse())
        {
            handle.Dispose();
        }

        _directoryHandles.Clear();
        _directoryHandle = null;
    }

    private void AcquireDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            AcquireWindowsDirectory();
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            AcquireUnixDirectory();
            return;
        }

        throw new NotSupportedException("Secure tagged projection output is supported only on Windows, Linux, and macOS.");
    }

    private void AcquireUnixDirectory()
    {
        var root = Path.GetPathRoot(_directoryPath) ?? throw new IOException("The output directory has no filesystem root.");
        var current = new SafeFileHandle((nint)OpenUnixDirectory(root), ownsHandle: true);
        _directoryHandles.Add(current);
        var relativeDirectory = Path.GetRelativePath(root, _directoryPath);
        foreach (var component in relativeDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = TryOpenUnixDirectory(current, component);
            if (next is null)
            {
                if (MkdirAt(current.DangerousGetHandle().ToInt32(), component, Convert.ToUInt32("700", 8)) != 0
                    && Marshal.GetLastPInvokeError() != UnixAlreadyExists)
                {
                    throw NativeIOException($"Unable to create output directory component '{component}'.");
                }

                next = TryOpenUnixDirectory(current, component)
                    ?? throw NativeIOException($"Unable to open output directory component '{component}' after it was created.");
            }

            _directoryHandles.Add(next);
            current = next;
        }

        _directoryHandle = current;
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only traversal is covered by the Windows security test lane.")]
    private void AcquireWindowsDirectory()
    {
        var root = Path.GetPathRoot(_directoryPath) ?? throw new IOException("The output directory has no filesystem root.");
        var currentPath = root;
        var current = OpenWindowsDirectory(root);
        _directoryHandles.Add(current);
        foreach (var component in Path.GetRelativePath(root, _directoryPath)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Join(currentPath, component);
            var next = TryOpenWindowsDirectory(currentPath);
            if (next is null)
            {
                Directory.CreateDirectory(currentPath);
                next = OpenWindowsDirectory(currentPath);
            }

            _directoryHandles.Add(next);
            current = next;
        }

        _directoryHandle = current;
    }

    private void RejectExistingOutputEntry(string outputFileName)
    {
        if (OperatingSystem.IsWindows())
        {
            RejectExistingWindowsOutputEntry(outputFileName);
            return;
        }

        RejectExistingUnixOutputEntry(outputFileName);
    }

    private void RejectExistingUnixOutputEntry(string outputFileName)
    {
        var descriptor = UnixOpenAt(
            DirectoryDescriptor,
            outputFileName,
            UnixReadOnly | UnixCloseOnExec | UnixNoFollow | UnixNonBlocking);
        if (descriptor < 0)
        {
            if (Marshal.GetLastPInvokeError() == UnixNoEntry)
            {
                return;
            }

            throw NativeIOException($"Unable to securely inspect output file '{outputFileName}'.");
        }

        using var entry = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        RejectUnixWrongKind(entry, expectDirectory: false);
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only output inspection is covered by the Windows security test lane.")]
    private void RejectExistingWindowsOutputEntry(string outputFileName)
    {
        var outputPath = Path.Join(_directoryPath, outputFileName);
        var entry = WindowsCreateFile(
            outputPath,
            WindowsGenericRead,
            WindowsShareRead | WindowsShareWrite,
            0,
            WindowsOpenExisting,
            WindowsBackupSemantics | WindowsOpenReparsePoint,
            0);
        if (entry.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            entry.Dispose();
            if (error is 2 or 3)
            {
                return;
            }

            throw new IOException($"Unable to securely inspect output file '{outputFileName}'.", new Win32Exception(error));
        }

        using (entry)
        {
            RejectWindowsReparsePoint(entry);
            RejectWindowsWrongKind(entry, expectDirectory: false);
        }
    }

    private SafeFileHandle CreateTemporaryFile(string temporaryFileName)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsTemporaryFile(temporaryFileName);
        }

        var descriptor = UnixOpenAt(
            DirectoryDescriptor,
            temporaryFileName,
            UnixWriteOnly | UnixCloseOnExec | UnixNoFollow | UnixNonBlocking | UnixCreate | UnixExclusive,
            0x180);
        if (descriptor < 0)
        {
            throw NativeIOException($"Unable to create temporary output file '{temporaryFileName}'.");
        }

        var temporaryFile = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        var testFailureError = UnixFChmodFailureError.Value;
        if (testFailureError is { } testError)
        {
            Marshal.SetLastPInvokeError(testError);
        }

        if (testFailureError is not null || UnixFChmod(descriptor, 0x180) != 0)
        {
            var nativeError = Marshal.GetLastPInvokeError();
            temporaryFile.Dispose();
            _ = UnlinkAt(DirectoryDescriptor, temporaryFileName, 0);
            Marshal.SetLastPInvokeError(nativeError);
            throw NativeIOException($"Unable to secure temporary output file '{temporaryFileName}'.");
        }

        return temporaryFile;
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only temporary-file creation is covered by the Windows security test lane.")]
    private SafeFileHandle CreateWindowsTemporaryFile(string temporaryFileName)
    {
        var handle = WindowsCreateFile(
            Path.Join(_directoryPath, temporaryFileName),
            WindowsGenericWrite | WindowsDelete,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            0,
            WindowsCreateNew,
            WindowsOpenReparsePoint | WindowsWriteThrough,
            0);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new IOException($"Unable to create temporary output file '{temporaryFileName}'.", new Win32Exception(error));
    }

    private void ReplaceTemporaryFile(SafeFileHandle temporaryFile, string temporaryFileName, string outputFileName)
    {
        if (OperatingSystem.IsWindows())
        {
            ReplaceWindowsTemporaryFile(temporaryFile, outputFileName);
            return;
        }

        if (RenameAt(DirectoryDescriptor, temporaryFileName, DirectoryDescriptor, outputFileName) != 0)
        {
            throw NativeIOException($"Unable to atomically replace output file '{outputFileName}'.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only relative replacement is covered by the Windows security test lane.")]
    private void ReplaceWindowsTemporaryFile(SafeFileHandle temporaryFile, string outputFileName)
    {
        var fileNameBytes = Encoding.Unicode.GetBytes(outputFileName);
        var fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = IntPtr.Size == 8 ? 16 : 8;
        var information = Marshal.AllocHGlobal(checked(fileNameOffset + fileNameBytes.Length));
        try
        {
            Marshal.WriteInt32(information, 1);
            Marshal.WriteIntPtr(information, rootDirectoryOffset, _directoryHandle!.DangerousGetHandle());
            Marshal.WriteInt32(information, fileNameLengthOffset, fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, information + fileNameOffset, fileNameBytes.Length);
            if (!SetFileInformationByHandle(temporaryFile, WindowsFileRenameInfo, information, (uint)(fileNameOffset + fileNameBytes.Length)))
            {
                throw new IOException($"Unable to atomically replace output file '{outputFileName}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(information);
        }
    }

    private void DeleteTemporaryFile(string temporaryFileName)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Delete(Path.Join(_directoryPath, temporaryFileName));
            return;
        }

        if (UnlinkAt(DirectoryDescriptor, temporaryFileName, 0) != 0 && Marshal.GetLastPInvokeError() != UnixNoEntry)
        {
            throw NativeIOException($"Unable to remove temporary output file '{temporaryFileName}'.");
        }
    }

    private int DirectoryDescriptor => _directoryHandle!.DangerousGetHandle().ToInt32();

    private static SafeFileHandle? TryOpenUnixDirectory(SafeFileHandle parent, string name)
    {
        var descriptor = UnixOpenAt(
            parent.DangerousGetHandle().ToInt32(),
            name,
            UnixReadOnly | UnixCloseOnExec | UnixNoFollow | UnixNonBlocking | UnixDirectory);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        }

        return Marshal.GetLastPInvokeError() == UnixNoEntry
            ? null
            : throw NativeIOException($"Unable to securely open output directory component '{name}'.");
    }

    private static int OpenUnixDirectory(string path)
    {
        var descriptor = UnixOpen(path, UnixReadOnly | UnixCloseOnExec | UnixNoFollow | UnixNonBlocking | UnixDirectory);
        return descriptor >= 0
            ? descriptor
            : throw NativeIOException($"Unable to securely open output directory '{path}'.");
    }

    [ExcludeFromCodeCoverage(Justification = "Native stat layouts vary by supported Unix platform and architecture.")]
    private static void RejectUnixWrongKind(SafeFileHandle handle, bool expectDirectory)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (UnixFStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
            {
                throw NativeIOException("Unable to inspect output file type.");
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
                throw new NotSupportedException($"Secure output inspection is unsupported on {RuntimeInformation.ProcessArchitecture} Linux.");
            }

            var expectedKind = expectDirectory ? 0x4000u : UnixRegularFileType;
            if ((mode & UnixFileTypeMask) != expectedKind)
            {
                throw new IOException(expectDirectory ? "Output entry is not a directory." : "Output entry is not a regular file.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only no-follow traversal is covered by the Windows security test lane.")]
    private static SafeFileHandle? TryOpenWindowsDirectory(string path)
    {
        var handle = WindowsCreateFile(
            path,
            WindowsGenericRead,
            WindowsShareRead | WindowsShareWrite,
            0,
            WindowsOpenExisting,
            WindowsBackupSemantics | WindowsOpenReparsePoint,
            0);
        if (!handle.IsInvalid)
        {
            RejectWindowsReparsePoint(handle);
            RejectWindowsWrongKind(handle, expectDirectory: true);
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error is 2 or 3)
        {
            return null;
        }

        throw new IOException($"Unable to securely open output directory '{path}'.", new Win32Exception(error));
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only no-follow traversal is covered by the Windows security test lane.")]
    private static SafeFileHandle OpenWindowsDirectory(string path)
        => TryOpenWindowsDirectory(path)
           ?? throw new IOException($"Output directory '{path}' was not found after creation.");

    [ExcludeFromCodeCoverage(Justification = "Windows-only metadata inspection is covered by the Windows security test lane.")]
    private static void RejectWindowsReparsePoint(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                WindowsFileAttributeTagInfo,
                out var information,
                (uint)Marshal.SizeOf<WindowsFileAttributeTagInformation>()))
        {
            throw new IOException("Unable to inspect output handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if ((information.FileAttributes & WindowsAttributeReparsePoint) != 0)
        {
            throw new IOException("Output path contains a symbolic link or reparse point.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only metadata inspection is covered by the Windows security test lane.")]
    private static void RejectWindowsWrongKind(SafeFileHandle handle, bool expectDirectory)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                WindowsFileAttributeTagInfo,
                out var information,
                (uint)Marshal.SizeOf<WindowsFileAttributeTagInformation>()))
        {
            throw new IOException("Unable to inspect output handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if (((information.FileAttributes & WindowsAttributeDirectory) != 0) != expectDirectory)
        {
            throw new IOException(expectDirectory ? "Output entry is not a directory." : "Output entry is not a regular file.");
        }
    }

    private static ReleaseToolException InvalidOutputPath(string outputPath, string detail)
    {
        return new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-inspect-output-path-invalid",
            "Inspect output must use an ordinary file beneath a securely opened directory.",
            $"The output path {outputPath} could not be written safely: {detail}",
            "Pass an ordinary temporary YAML file path outside the repository source tree.",
            DocumentationPath));
    }

    private static IOException NativeIOException(string message)
        => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise these native flag values.")]
    private static int UnixCloseOnExec => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise these native flag values.")]
    private static int UnixDirectory => OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise these native flag values.")]
    private static int UnixNoFollow => OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise these native flag values.")]
    private static int UnixNonBlocking => OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise these native flag values.")]
    private static int UnixCreate => OperatingSystem.IsMacOS() ? 0x00000200 : 0x00000040;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise these native flag values.")]
    private static int UnixExclusive => OperatingSystem.IsMacOS() ? 0x00000800 : 0x00000080;

    private sealed class DirectoryOpenedHookScope(Action<string>? previous) : IDisposable
    {
        public void Dispose()
        {
            DirectoryOpenedHook.Value = previous;
        }
    }

    private sealed class TemporaryFileOpenedHookScope(Action? previous) : IDisposable
    {
        public void Dispose()
        {
            TemporaryFileOpenedHook.Value = previous;
        }
    }

    private sealed class UnixFChmodFailureScope(int? previous) : IDisposable
    {
        public void Dispose()
        {
            UnixFChmodFailureError.Value = previous;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct WindowsFileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, ExactSpelling = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, ExactSpelling = true)]
    private static extern int UnixOpenAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, ExactSpelling = true)]
    private static extern int UnixOpenAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true, ExactSpelling = true)]
    private static extern int MkdirAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true, ExactSpelling = true)]
    private static extern int RenameAt(
        int oldDirectoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true, ExactSpelling = true)]
    private static extern int UnlinkAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true, ExactSpelling = true)]
    private static extern int UnixFStat(int descriptor, nint buffer);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true, ExactSpelling = true)]
    private static extern int UnixFChmod(int descriptor, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle WindowsCreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,
        int fileInformationClass,
        out WindowsFileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle handle,
        int fileInformationClass,
        nint fileInformation,
        uint bufferSize);
}
