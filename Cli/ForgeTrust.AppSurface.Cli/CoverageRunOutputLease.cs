using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Holds the filesystem objects that lead to a coverage output directory while ownership is
/// inspected and the directory is prepared. The lease prevents pathname validation from being
/// separated from mutation by an ancestor rename or link replacement.
/// </summary>
internal sealed class CoverageRunOutputLease : IDisposable
{
    private const string MarkerFileName = ".appsurface-coverage-output";
    private const string MarkerContents = "AppSurface coverage output directory\n";
    private const int MaximumMarkerBytes = 128;
    private readonly string _outputPath;
    private readonly List<SafeFileHandle> _windowsHandles = [];
    private readonly List<SafeFileHandle> _unixHandles = [];
    private SafeFileHandle? _outputHandle;

    private CoverageRunOutputLease(string outputPath)
    {
        _outputPath = outputPath;
    }

    /// <summary>
    /// Independently opens or creates every output-path component without following links.
    /// </summary>
    /// <param name="outputPath">Absolute output directory path.</param>
    /// <returns>A lease retaining every opened component until disposal.</returns>
    internal static CoverageRunOutputLease Acquire(string outputPath)
    {
        var lease = new CoverageRunOutputLease(NormalizePlatformPath(outputPath));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                lease.AcquireWindows(createMissing: true);
            }
            else
            {
                lease.AcquireUnix(createMissing: true);
            }

            return lease;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lease.Dispose();
            throw CoverageRunOutputGuard.UnsafeOutput(
                $"the output path could not be securely acquired ({ex.GetType().Name}): {outputPath}");
        }
    }

    /// <summary>
    /// Validates the existing path and output ownership without creating missing components.
    /// </summary>
    /// <param name="outputPath">Absolute output directory path.</param>
    internal static void ValidateExisting(string outputPath)
    {
        using var lease = new CoverageRunOutputLease(NormalizePlatformPath(outputPath));
        var exists = OperatingSystem.IsWindows()
            ? lease.AcquireWindows(createMissing: false)
            : lease.AcquireUnix(createMissing: false);
        if (exists)
        {
            lease.ValidateOwnedTree();
        }
    }

    /// <summary>
    /// Revalidates ownership, optionally removes known artifacts, and creates the marker and
    /// projects directory through the retained output object.
    /// </summary>
    /// <param name="clean">Whether known artifacts should be removed.</param>
    /// <param name="beforeMutation">Optional test seam called after acquisition and before mutation.</param>
    /// <param name="beforeCleanup">Optional test seam called after authorization revalidation and before cleanup.</param>
    internal void Prepare(bool clean, Action? beforeMutation, Action? beforeCleanup)
    {
        var state = ValidateOwnedTree();
        beforeMutation?.Invoke();
        VerifyBinding();
        EnsureOwnershipUnchanged(state, ValidateOwnedTree());
        beforeCleanup?.Invoke();
        if (OperatingSystem.IsWindows())
        {
            PrepareWindows(clean, state);
        }
        else
        {
            PrepareUnix(clean, state);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var handle in _windowsHandles.AsEnumerable().Reverse())
        {
            handle.Dispose();
        }

        foreach (var handle in _unixHandles.AsEnumerable().Reverse())
        {
            handle.Dispose();
        }

        _windowsHandles.Clear();
        _unixHandles.Clear();
        _outputHandle = null;
    }

    private OwnershipState ValidateOwnedTree()
    {
        var entries = EnumerateEntries(_outputHandle!);
        var marker = entries.FirstOrDefault(entry => string.Equals(entry.Name, MarkerFileName, StringComparison.Ordinal));
        if (marker is not null && marker.IsDirectory)
        {
            throw new IOException("The coverage ownership marker is not a regular file.");
        }

        if (marker is not null)
        {
            ValidateMarker(marker);
        }

        var snapshot = CaptureTreeSnapshot(_outputHandle!, entries);

        var artifacts = entries.Where(entry => !string.Equals(entry.Name, MarkerFileName, StringComparison.Ordinal)).ToArray();
        if (artifacts.Length > 0 && marker is null)
        {
            throw CoverageRunOutputGuard.UnsafeOutput("--output already contains files and is not marked as AppSurface-owned.");
        }

        return new OwnershipState(marker is not null, snapshot);
    }

    private static void EnsureOwnershipUnchanged(OwnershipState expected, OwnershipState actual)
    {
        if (expected.HasMarker != actual.HasMarker
            || !expected.Snapshot.SequenceEqual(actual.Snapshot))
        {
            throw new IOException("The coverage output contents changed before cleanup.");
        }
    }

    private void VerifyBinding()
    {
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsPathIdentity(_outputHandle!, _outputPath);
            return;
        }

        using var reboundLease = new CoverageRunOutputLease(_outputPath);
        if (!reboundLease.AcquireUnix(createMissing: false)
            || !GetUnixIdentity(_outputHandle!).Equals(GetUnixIdentity(reboundLease._outputHandle!)))
        {
            throw new IOException("The output directory binding changed before mutation.");
        }
    }

    private void PrepareUnix(bool clean, OwnershipState state)
    {
        var descriptor = _outputHandle!.DangerousGetHandle().ToInt32();
        if (clean && state.HasMarker)
        {
            DeleteKnownUnixEntries(descriptor, BuildAuthorizedChildren(state.Snapshot));
        }

        if (!state.HasMarker)
        {
            WriteUnixMarker(descriptor);
        }

        EnsureUnixDirectory(descriptor, "projects");
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only output preparation is exercised by the Windows test lane.")]
    private void PrepareWindows(bool clean, OwnershipState state)
    {
        VerifyWindowsPathIdentity(_outputHandle!, _outputPath);
        if (clean && state.HasMarker)
        {
            DeleteKnownWindowsEntries(BuildAuthorizedChildren(state.Snapshot));
        }

        if (!state.HasMarker)
        {
            WriteWindowsMarker();
        }

        var projects = Path.Join(_outputPath, "projects");
        Directory.CreateDirectory(projects);
        using var projectHandle = OpenWindowsDirectory(projects);
        VerifyWindowsPathIdentity(projectHandle, projects);
    }

    private bool AcquireUnix(bool createMissing)
    {
        var root = Path.GetPathRoot(_outputPath) ?? throw new IOException("The output path has no filesystem root.");
        var rootDescriptor = OpenUnixDirectory(root);
        var current = new SafeFileHandle((nint)rootDescriptor, ownsHandle: true);
        _unixHandles.Add(current);
        var relative = Path.GetRelativePath(root, _outputPath);
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = OpenAt(current, component, directory: true, notDirectoryIsMissing: false);
            if (next is null)
            {
                if (!createMissing)
                {
                    return false;
                }

                if (MkdirAt(current.DangerousGetHandle().ToInt32(), component, Convert.ToUInt32("755", 8)) != 0
                    && Marshal.GetLastPInvokeError() != UnixAlreadyExists)
                {
                    throw NativeIOException($"Unable to create output path component '{component}'.");
                }

                next = OpenAt(current, component, directory: true, notDirectoryIsMissing: false)
                    ?? throw NativeIOException($"Unable to open newly created output path component '{component}'.");
            }

            _unixHandles.Add(next);
            current = next;
        }

        _outputHandle = current;
        return true;
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only output acquisition is exercised by the Windows test lane.")]
    private bool AcquireWindows(bool createMissing)
    {
        var root = Path.GetPathRoot(_outputPath) ?? throw new IOException("The output path has no filesystem root.");
        var currentPath = root;
        var current = OpenWindowsDirectory(root);
        _windowsHandles.Add(current);
        VerifyWindowsPathIdentity(current, root);
        var components = Path.GetRelativePath(root, _outputPath)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            currentPath = Path.Join(currentPath, component);
            if (!Directory.Exists(currentPath))
            {
                if (File.Exists(currentPath))
                {
                    throw new IOException($"Output path component is a file: {currentPath}");
                }

                if (!createMissing)
                {
                    return false;
                }

                using var parentMutationLease = OpenWindowsDirectory(
                    GetStableDirectoryPath(current),
                    denyWriteSharing: true);
                if (GetWindowsIdentity(parentMutationLease) != GetWindowsIdentity(current))
                {
                    throw new IOException($"Output path parent changed before creating '{component}'.");
                }

                if (!Directory.Exists(currentPath))
                {
                    if (File.Exists(currentPath))
                    {
                        throw new IOException($"Output path component is a file: {currentPath}");
                    }

                    Directory.CreateDirectory(currentPath);
                }
            }

            current = OpenWindowsDirectory(currentPath, denyWriteSharing: index == components.Length - 1);
            _windowsHandles.Add(current);
            VerifyWindowsPathIdentity(current, currentPath);
        }

        _outputHandle = current;
        return true;
    }

    private static IReadOnlyList<OutputEntry> EnumerateEntries(SafeFileHandle directory)
    {
        var names = OperatingSystem.IsWindows()
            ? Directory.EnumerateFileSystemEntries(GetStableDirectoryPath(directory)).Select(path => Path.GetFileName(path))
            : EnumerateUnixNames(directory);
        return names
            .Select(name => InspectEntry(directory, name))
            .ToArray();
    }

    private static IReadOnlyList<string> EnumerateUnixNames(SafeFileHandle directory)
    {
        var duplicated = Duplicate(directory.DangerousGetHandle().ToInt32());
        if (duplicated < 0)
        {
            throw NativeIOException("Unable to duplicate the retained output directory descriptor.");
        }

        if (Seek(duplicated, 0, UnixSeekStart) < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = Close(duplicated);
            Marshal.SetLastPInvokeError(error);
            throw NativeIOException("Unable to rewind the retained output directory descriptor.");
        }

        var directoryStream = FdOpenDirectory(duplicated);
        if (directoryStream == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = Close(duplicated);
            Marshal.SetLastPInvokeError(error);
            throw NativeIOException("Unable to enumerate the retained output directory descriptor.");
        }

        try
        {
            var names = new List<string>();
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = ReadDirectory(directoryStream);
                if (entry == 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                    {
                        throw new IOException("Unable to enumerate the retained output directory descriptor.", new Win32Exception(error));
                    }

                    return names;
                }

                var nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;
                var namePointer = entry + nameOffset;
                var name = Marshal.PtrToStringUTF8(namePointer)
                    ?? throw new IOException("The output directory contained an invalid entry name.");
                if (name is not "." and not "..")
                {
                    names.Add(name);
                }
            }
        }
        finally
        {
            _ = CloseDirectory(directoryStream);
        }
    }

    private static OutputEntry InspectEntry(SafeFileHandle parent, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Join(GetStableDirectoryPath(parent), name);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"The existing artifact tree contains a symbolic link or reparse point: {path}");
            }

            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            using var handle = isDirectory
                ? OpenWindowsDirectory(path)
                : OpenWindowsFile(path, WindowsGenericRead);
            VerifyWindowsPathIdentity(handle, path);
            RejectWindowsWrongKind(handle, isDirectory);
            return new OutputEntry(name, isDirectory, GetWindowsIdentity(handle));
        }

        using var directory = OpenAt(parent, name, directory: true, notDirectoryIsMissing: true);
        if (directory is not null)
        {
            RejectUnixWrongKind(directory, expectDirectory: true);
            return new OutputEntry(name, IsDirectory: true, GetUnixIdentity(directory));
        }

        using var file = OpenAt(parent, name, directory: false);
        if (file is null)
        {
            throw NativeIOException($"Output entry '{name}' could not be opened without following links.");
        }

        RejectUnixWrongKind(file, expectDirectory: false);

        return new OutputEntry(name, IsDirectory: false, GetUnixIdentity(file));
    }

    private static IReadOnlyList<TreeEntry> CaptureTreeSnapshot(
        SafeFileHandle parent,
        IReadOnlyList<OutputEntry> entries)
    {
        var snapshot = new List<TreeEntry>();
        foreach (var entry in entries)
        {
            CaptureTreeSnapshot(parent, entry, prefix: string.Empty, snapshot);
        }

        return snapshot
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static void CaptureTreeSnapshot(
        SafeFileHandle parent,
        OutputEntry entry,
        string prefix,
        ICollection<TreeEntry> snapshot)
    {
        var relativePath = string.IsNullOrEmpty(prefix) ? entry.Name : Path.Join(prefix, entry.Name);
        snapshot.Add(new TreeEntry(relativePath, entry.IsDirectory, entry.Identity));
        if (!entry.IsDirectory)
        {
            return;
        }

        using var child = OperatingSystem.IsWindows()
            ? OpenWindowsDirectory(Path.Join(GetStableDirectoryPath(parent), entry.Name))
            : OpenAt(parent, entry.Name, directory: true) ?? throw new IOException($"Output directory '{entry.Name}' changed during inspection.");
        VerifyEntryIdentity(child, entry, relativePath);
        foreach (var descendant in EnumerateEntries(child))
        {
            CaptureTreeSnapshot(child, descendant, relativePath, snapshot);
        }
    }

    private static void VerifyEntryIdentity(SafeFileHandle handle, OutputEntry expected, string relativePath)
    {
        var actual = OperatingSystem.IsWindows() ? GetWindowsIdentity(handle) : GetUnixIdentity(handle);
        if (actual != expected.Identity)
        {
            throw new IOException($"Output entry '{relativePath}' changed during inspection.");
        }
    }

    private void ValidateMarker(OutputEntry marker)
    {
        using var handle = OperatingSystem.IsWindows()
            ? OpenWindowsMarker(marker.Name)
            : OpenAt(_outputHandle!, marker.Name, directory: false)
                ?? throw new IOException("The coverage ownership marker changed during inspection.");
        if (!OperatingSystem.IsWindows())
        {
            RejectUnixWrongKind(handle, expectDirectory: false);
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: MaximumMarkerBytes, isAsync: false);
        if (stream.Length > MaximumMarkerBytes)
        {
            throw new IOException("The coverage ownership marker has unexpected contents.");
        }

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: MaximumMarkerBytes,
            leaveOpen: false);
        var contents = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!string.Equals(contents, MarkerContents, StringComparison.Ordinal)
            && !string.Equals(contents, MarkerContents.TrimEnd('\n'), StringComparison.Ordinal))
        {
            throw new IOException("The coverage ownership marker has unexpected contents.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only marker validation is exercised by the Windows test lane.")]
    private SafeFileHandle OpenWindowsMarker(string name)
    {
        var path = Path.Join(_outputPath, name);
        var handle = OpenWindowsFile(path, WindowsGenericRead);
        try
        {
            VerifyWindowsPathIdentity(handle, path);
            RejectWindowsWrongKind(handle, expectDirectory: false);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void DeleteKnownUnixEntries(
        int descriptor,
        IReadOnlyDictionary<string, IReadOnlyList<TreeEntry>> authorizedChildren)
    {
        using var parent = new SafeFileHandle((nint)descriptor, ownsHandle: false);
        foreach (var entry in GetAuthorizedChildren(authorizedChildren, parentPath: string.Empty)
            .Where(entry => IsKnownOutputEntry(entry) || IsPatternOutputEntry(entry)))
        {
            DeleteAuthorizedUnixEntry(parent, entry, authorizedChildren);
        }
    }

    private static void DeleteAuthorizedUnixEntry(
        SafeFileHandle parent,
        TreeEntry expected,
        IReadOnlyDictionary<string, IReadOnlyList<TreeEntry>> authorizedChildren)
    {
        var name = Path.GetFileName(expected.RelativePath);
        var quarantineName = $".appsurface-clean-{Guid.NewGuid():N}";
        if (RenameAt(
            parent.DangerousGetHandle().ToInt32(),
            name,
            parent.DangerousGetHandle().ToInt32(),
            quarantineName) != 0)
        {
            throw NativeIOException($"Unable to quarantine coverage artifact '{expected.RelativePath}'.");
        }

        using var quarantined = OpenAt(parent, quarantineName, expected.IsDirectory)
            ?? throw new IOException($"Coverage artifact '{expected.RelativePath}' changed while it was quarantined.");
        var actualIdentity = GetUnixIdentity(quarantined);
        if (actualIdentity != expected.Identity)
        {
            throw new IOException($"Coverage artifact '{expected.RelativePath}' was replaced before cleanup.");
        }

        RejectUnixWrongKind(quarantined, expected.IsDirectory);
        if (expected.IsDirectory)
        {
            foreach (var child in GetAuthorizedChildren(authorizedChildren, expected.RelativePath))
            {
                DeleteAuthorizedUnixEntry(quarantined, child, authorizedChildren);
            }

            if (EnumerateUnixNames(quarantined).Count != 0)
            {
                throw new IOException($"Coverage directory '{expected.RelativePath}' changed during cleanup.");
            }
        }

        quarantined.Dispose();
        if (UnlinkAt(
            parent.DangerousGetHandle().ToInt32(),
            quarantineName,
            expected.IsDirectory ? UnixRemoveDirectory : 0) != 0)
        {
            throw NativeIOException($"Unable to remove quarantined coverage artifact '{expected.RelativePath}'.");
        }
    }

    private void DeleteKnownWindowsEntries(IReadOnlyDictionary<string, IReadOnlyList<TreeEntry>> authorizedChildren)
    {
        foreach (var entry in GetAuthorizedChildren(authorizedChildren, parentPath: string.Empty)
            .Where(entry => IsKnownOutputEntry(entry) || IsPatternOutputEntry(entry)))
        {
            DeleteWindowsEntry(_outputHandle!, entry, authorizedChildren);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only handle-relative cleanup is exercised by the Windows test lane.")]
    private static void DeleteWindowsEntry(
        SafeFileHandle parent,
        TreeEntry entry,
        IReadOnlyDictionary<string, IReadOnlyList<TreeEntry>> authorizedChildren)
    {
        var name = Path.GetFileName(entry.RelativePath);
        var path = Path.Join(GetStableDirectoryPath(parent), name);
        using var handle = entry.IsDirectory
            ? OpenWindowsDirectory(path, WindowsDelete, denyWriteSharing: true)
            : OpenWindowsFile(path, WindowsDelete | WindowsGenericRead);
        VerifyWindowsPathIdentity(handle, path);
        RejectWindowsWrongKind(handle, entry.IsDirectory);
        if (GetWindowsIdentity(handle) != entry.Identity)
        {
            throw new IOException($"Coverage artifact '{entry.RelativePath}' was replaced before cleanup.");
        }

        if (entry.IsDirectory)
        {
            foreach (var child in GetAuthorizedChildren(authorizedChildren, entry.RelativePath))
            {
                DeleteWindowsEntry(handle, child, authorizedChildren);
            }
        }

        var disposition = new WindowsFileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
            handle,
            WindowsFileDispositionInfo,
            ref disposition,
            (uint)Marshal.SizeOf<WindowsFileDispositionInformation>()))
        {
            throw new IOException($"Unable to remove coverage artifact '{entry.RelativePath}' by handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TreeEntry>> BuildAuthorizedChildren(
        IReadOnlyList<TreeEntry> authorizedSnapshot)
        => authorizedSnapshot
            .GroupBy(entry => Path.GetDirectoryName(entry.RelativePath) ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TreeEntry>)group.ToArray(),
                StringComparer.Ordinal);

    private static IReadOnlyList<TreeEntry> GetAuthorizedChildren(
        IReadOnlyDictionary<string, IReadOnlyList<TreeEntry>> authorizedChildren,
        string parentPath)
    {
        return authorizedChildren.TryGetValue(parentPath, out var children) ? children : [];
    }

    private static void WriteUnixMarker(int descriptor)
    {
        var flags = UnixWriteOnly | UnixCreate | UnixExclusive | UnixNoFollow | UnixCloseOnExec;
        var markerDescriptor = OpenAtNative(descriptor, MarkerFileName, flags, Convert.ToUInt32("644", 8));
        if (markerDescriptor < 0)
        {
            throw NativeIOException("Unable to securely write the coverage ownership marker.");
        }

        using var stream = new FileStream(new SafeFileHandle((nint)markerDescriptor, ownsHandle: true), FileAccess.Write);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(MarkerContents.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    private void WriteWindowsMarker()
    {
        var path = Path.Join(_outputPath, MarkerFileName);
        using var handle = OpenWindowsFile(path, WindowsGenericWrite, WindowsCreateNew);
        VerifyWindowsPathIdentity(handle, path);
        RejectWindowsWrongKind(handle, expectDirectory: false);
        using var stream = new FileStream(handle, FileAccess.Write);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(MarkerContents.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    private static void EnsureUnixDirectory(int descriptor, string name)
    {
        using var parent = new SafeFileHandle((nint)descriptor, ownsHandle: false);
        using var existing = OpenAt(parent, name, directory: true);
        if (existing is not null)
        {
            return;
        }

        if (MkdirAt(descriptor, name, Convert.ToUInt32("755", 8)) != 0 && Marshal.GetLastPInvokeError() != UnixAlreadyExists)
        {
            throw NativeIOException($"Unable to create output directory '{name}'.");
        }

        using var created = OpenAt(parent, name, directory: true)
            ?? throw new IOException($"Output directory '{name}' was replaced while it was created.");
    }

    private static bool IsKnownOutputEntry(OutputEntry entry)
        => IsKnownOutputEntry(entry.Name, entry.IsDirectory);

    private static bool IsKnownOutputEntry(TreeEntry entry)
        => IsKnownOutputEntry(Path.GetFileName(entry.RelativePath), entry.IsDirectory);

    private static bool IsKnownOutputEntry(string name, bool isDirectory)
        => isDirectory
            ? name is "projects" or "reportgenerator"
            : name is "coverage.cobertura.xml" or "coverage.json" or "coverage-gate.json" or "coverage-gate.md"
                or "coverage-watchdog.json" or "summary.txt" or "timings.json" or "reportgenerator-summary.txt"
                or CoverageRunSlowTestDiagnosticsWriter.MarkdownFileName or CoverageRunSlowTestDiagnosticsWriter.JsonFileName;

    private static bool IsPatternOutputEntry(OutputEntry entry)
        => IsPatternOutputEntry(entry.Name, entry.IsDirectory);

    private static bool IsPatternOutputEntry(TreeEntry entry)
        => IsPatternOutputEntry(Path.GetFileName(entry.RelativePath), entry.IsDirectory);

    private static bool IsPatternOutputEntry(string name, bool isDirectory)
        => !isDirectory
            && (name.StartsWith("junit-", StringComparison.Ordinal) || name.StartsWith("test-results-", StringComparison.Ordinal))
            && name.EndsWith(".xml", StringComparison.Ordinal);

    private static SafeFileHandle? OpenAt(
        SafeFileHandle parent,
        string name,
        bool directory,
        bool notDirectoryIsMissing = false)
    {
        var flags = UnixReadOnly | UnixCloseOnExec | UnixNoFollow | UnixNonBlocking | (directory ? UnixDirectory : 0);
        var descriptor = OpenAtNative(parent.DangerousGetHandle().ToInt32(), name, flags, 0);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        return error == UnixNoEntry || directory && notDirectoryIsMissing && error == UnixNotDirectory
            ? null
            : throw NativeIOException($"Unable to securely open output entry '{name}'.");
    }

    private static int OpenUnixDirectory(string path)
    {
        var descriptor = OpenNative(path, UnixReadOnly | UnixCloseOnExec | UnixNoFollow | UnixDirectory | UnixNonBlocking);
        return descriptor >= 0 ? descriptor : throw NativeIOException($"Unable to securely open output directory '{path}'.");
    }

    private static FileObjectIdentity GetUnixIdentity(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (FStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
            {
                throw NativeIOException("Unable to inspect the output directory identity.");
            }

            return OperatingSystem.IsMacOS()
                ? new FileObjectIdentity(unchecked((uint)Marshal.ReadInt32(buffer, 0)), unchecked((ulong)Marshal.ReadInt64(buffer, 8)))
                : new FileObjectIdentity(unchecked((ulong)Marshal.ReadInt64(buffer, 0)), unchecked((ulong)Marshal.ReadInt64(buffer, 8)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only file identity inspection is exercised by the Windows test lane.")]
    private static FileObjectIdentity GetWindowsIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException("Unable to identify an output entry.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return new FileObjectIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static void RejectUnixWrongKind(SafeFileHandle handle, bool expectDirectory)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (FStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
            {
                throw NativeIOException("Unable to inspect an output entry.");
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
                throw new IOException($"Secure output inspection is unsupported on {RuntimeInformation.ProcessArchitecture} Linux.");
            }

            var expectedKind = expectDirectory ? UnixDirectoryType : UnixRegularFileType;
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

    [ExcludeFromCodeCoverage(Justification = "Windows-only handle opening is exercised by the Windows test lane.")]
    private static SafeFileHandle OpenWindowsDirectory(
        string path,
        uint access = 0,
        bool denyWriteSharing = false)
        => OpenWindowsFile(
            path,
            access,
            WindowsOpenExisting,
            WindowsBackupSemantics | WindowsOpenReparsePoint,
            denyWriteSharing ? WindowsShareRead : WindowsShareRead | WindowsShareWrite);

    [ExcludeFromCodeCoverage(Justification = "Windows-only handle opening is exercised by the Windows test lane.")]
    private static SafeFileHandle OpenWindowsFile(
        string path,
        uint access,
        uint disposition = WindowsOpenExisting,
        uint flags = WindowsOpenReparsePoint,
        uint shareMode = WindowsShareRead | WindowsShareWrite)
    {
        var handle = CreateFile(path, access, shareMode, 0, disposition, flags, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Unable to securely open output path '{path}'.", new Win32Exception(error));
        }

        return handle;
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only identity validation is exercised by the Windows test lane.")]
    private static void VerifyWindowsPathIdentity(SafeFileHandle handle, string expectedPath)
    {
        RejectWindowsReparse(handle);
        var actual = NormalizeWindowsFinalPath(GetWindowsFinalPath(handle));
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Output path resolved to a different filesystem object: {expectedPath}");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only reparse validation is exercised by the Windows test lane.")]
    private static void RejectWindowsReparse(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, WindowsFileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<WindowsFileAttributeTagInformation>()))
        {
            throw new IOException("Unable to inspect output handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if ((info.FileAttributes & WindowsAttributeReparsePoint) != 0)
        {
            throw new IOException("Output path contains a reparse point.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only object-kind validation is exercised by the Windows test lane.")]
    private static void RejectWindowsWrongKind(SafeFileHandle handle, bool expectDirectory)
    {
        if (!GetFileInformationByHandleEx(handle, WindowsFileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<WindowsFileAttributeTagInformation>()))
        {
            throw new IOException("Unable to inspect output handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var isDirectory = (info.FileAttributes & WindowsAttributeDirectory) != 0;
        if (isDirectory != expectDirectory)
        {
            throw new IOException(expectDirectory ? "Output entry is not a directory." : "Output entry is not a regular file.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only final path resolution is exercised by the Windows test lane.")]
    private static string GetWindowsFinalPath(SafeFileHandle handle)
    {
        var builder = new StringBuilder(512);
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
            if (length == 0)
            {
                throw new IOException("Unable to resolve output handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            if (length < builder.Capacity)
            {
                return builder.ToString();
            }

            builder.EnsureCapacity(checked((int)length + 1));
            builder.Clear();
        }
    }

    private static string NormalizeWindowsFinalPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path));

    private static string GetStableDirectoryPath(SafeFileHandle handle)
        => OperatingSystem.IsWindows()
            ? NormalizeWindowsFinalPath(GetWindowsFinalPath(handle))
            : throw new PlatformNotSupportedException("Unix directories are enumerated directly from retained descriptors.");

    /// <summary>
    /// Canonicalizes fixed operating-system aliases before safety comparisons and no-follow traversal.
    /// </summary>
    /// <param name="path">An absolute platform path.</param>
    /// <returns>The path with only fixed operating-system aliases canonicalized.</returns>
    internal static string NormalizePlatformPath(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return path;
        }

        // macOS exposes these stable operating-system aliases as root symlinks. Canonicalize
        // them before no-follow traversal so ordinary temporary paths remain usable without
        // permitting a user-controlled ancestor link.
        if (string.Equals(path, "/tmp", StringComparison.Ordinal) || path.StartsWith("/tmp/", StringComparison.Ordinal))
        {
            return "/private" + path;
        }

        return string.Equals(path, "/var", StringComparison.Ordinal) || path.StartsWith("/var/", StringComparison.Ordinal)
            ? "/private" + path
            : path;
    }

    private static IOException NativeIOException(string message)
        => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    private static int UnixCloseOnExec => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;
    private static int UnixDirectory => OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;
    private static int UnixNoFollow => OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;
    private static int UnixNonBlocking => OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;
    private static int UnixCreate => OperatingSystem.IsMacOS() ? 0x00000200 : 0x00000040;
    private static int UnixExclusive => OperatingSystem.IsMacOS() ? 0x00000800 : 0x00000080;
    private static int UnixRemoveDirectory => OperatingSystem.IsMacOS() ? 0x080 : 0x200;
    private const int UnixReadOnly = 0;
    private const int UnixWriteOnly = 1;
    private const int UnixNoEntry = 2;
    private const int UnixNotDirectory = 20;
    private const int UnixAlreadyExists = 17;
    private const int UnixSeekStart = 0;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixDirectoryType = 0x4000;
    private const uint UnixRegularFileType = 0x8000;
    private const uint WindowsGenericRead = 0x80000000;
    private const uint WindowsGenericWrite = 0x40000000;
    private const uint WindowsDelete = 0x00010000;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsCreateNew = 1;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsAttributeReparsePoint = 0x00000400;
    private const uint WindowsAttributeDirectory = 0x00000010;
    private const int WindowsFileAttributeTagInfo = 9;
    private const int WindowsFileDispositionInfo = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
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

    private sealed record OutputEntry(string Name, bool IsDirectory, FileObjectIdentity Identity);
    private readonly record struct TreeEntry(string RelativePath, bool IsDirectory, FileObjectIdentity Identity);
    private readonly record struct OwnershipState(
        bool HasMarker,
        IReadOnlyList<TreeEntry> Snapshot);
    private readonly record struct FileObjectIdentity(ulong DeviceOrVolume, ulong FileId);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtNative(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameAt(
        int oldDirectoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, nint buffer);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Duplicate(int descriptor);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern nint FdOpenDirectory(int descriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern nint ReadDirectory(nint directoryStream);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDirectory(nint directoryStream);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static extern long Seek(int descriptor, long offset, int origin);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle handle,
        int fileInformationClass,
        ref WindowsFileDispositionInformation fileInformation,
        uint bufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        StringBuilder path,
        uint pathLength,
        uint flags);
}
