using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.CoverageArtifacts.CoverageFileSystemInterop;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Holds the filesystem objects that lead to a coverage output directory while ownership is
/// inspected and the directory is prepared. The lease prevents pathname validation from being
/// separated from mutation by an ancestor rename or link replacement.
/// </summary>
internal sealed partial class CoverageRunOutputLease : IDisposable
{
    private const string MarkerFileName = ".appsurface-coverage-output";
    private const string MarkerContents = "AppSurface coverage output directory\n";
    private const int MaximumMarkerBytes = 128;
    private readonly string _outputPath;
    private readonly Func<int, uint, int> _unixFChmod;
    private readonly Func<int, string, int, int> _unixUnlinkAt;
    private readonly List<SafeFileHandle> _windowsHandles = [];
    private readonly List<SafeFileHandle> _unixHandles = [];
    private SafeFileHandle? _outputHandle;

    private CoverageRunOutputLease(
        string outputPath,
        Func<int, uint, int>? unixFChmod = null,
        Func<int, string, int, int>? unixUnlinkAt = null)
    {
        _outputPath = outputPath;
        _unixFChmod = unixFChmod ?? UnixFChmod;
        _unixUnlinkAt = unixUnlinkAt ?? UnlinkAt;
    }

    /// <summary>
    /// Independently opens or creates every output-path component without following links.
    /// </summary>
    /// <param name="outputPath">Absolute output directory path.</param>
    /// <param name="unixFChmod">Optional Unix permission operation used to verify marker-creation failures.</param>
    /// <param name="unixUnlinkAt">Optional Unix unlink operation used to verify staged-artifact cleanup failures.</param>
    /// <returns>A lease retaining every opened component until disposal.</returns>
    internal static CoverageRunOutputLease Acquire(
        string outputPath,
        Func<int, uint, int>? unixFChmod = null,
        Func<int, string, int, int>? unixUnlinkAt = null)
    {
        var lease = new CoverageRunOutputLease(NormalizePlatformPath(outputPath), unixFChmod, unixUnlinkAt);
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
    /// Independently opens every existing output-path component without creating a missing directory.
    /// </summary>
    /// <param name="outputPath">Absolute output directory path.</param>
    /// <returns>A retained lease, or <see langword="null"/> when the output directory does not exist.</returns>
    /// <remarks>
    /// This is used by explicit coverage cleanup so a preview or cleanup of a missing output never creates an empty
    /// <c>TestResults</c> directory. Existing components are still opened without following links.
    /// </remarks>
    internal static CoverageRunOutputLease? AcquireExisting(string outputPath)
    {
        var lease = new CoverageRunOutputLease(NormalizePlatformPath(outputPath));
        try
        {
            var exists = OperatingSystem.IsWindows()
                ? lease.AcquireWindows(createMissing: false)
                : lease.AcquireUnix(createMissing: false);
            if (exists)
            {
                return lease;
            }

            lease.Dispose();
            return null;
        }
        catch
        {
            lease.Dispose();
            throw;
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

    /// <summary>
    /// Returns AppSurface-owned entries that an explicit coverage clean operation may remove and, when requested,
    /// deletes them through this retained output-directory lease.
    /// </summary>
    /// <param name="apply">Whether the known entries should be deleted after validation.</param>
    /// <returns>A marker-ownership result and the relative entries selected for cleanup.</returns>
    /// <remarks>
    /// The method neither creates an ownership marker nor creates the <c>projects</c> directory. An empty unmarked
    /// directory therefore remains a no-op, while a populated unmarked directory still fails closed through
    /// <see cref="ValidateOwnedTree"/>.
    /// </remarks>
    internal CoverageOwnedCleanupPlan CleanKnownOwnedArtifacts(bool apply)
    {
        var state = ValidateOwnedTree();
        if (!state.HasMarker)
        {
            return new CoverageOwnedCleanupPlan(IsOwned: false, []);
        }

        var artifacts = state.Snapshot
            .Where(entry => IsKnownOutputEntry(entry) || IsPatternOutputEntry(entry))
            .Select(entry => entry.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!apply || artifacts.Length == 0)
        {
            return new CoverageOwnedCleanupPlan(IsOwned: true, artifacts);
        }

        VerifyBinding();
        EnsureOwnershipUnchanged(state, ValidateOwnedTree());
        if (OperatingSystem.IsWindows())
        {
            DeleteKnownWindowsEntries(BuildAuthorizedChildren(state.Snapshot));
        }
        else
        {
            DeleteKnownUnixEntries(
                _outputHandle!.DangerousGetHandle().ToInt32(),
                BuildAuthorizedChildren(state.Snapshot));
        }

        return new CoverageOwnedCleanupPlan(IsOwned: true, artifacts);
    }

    /// <summary>
    /// Validates that each named gate artifact is either absent or a regular file in the retained
    /// output directory.
    /// </summary>
    /// <param name="artifactNames">Owned gate artifact filenames to validate.</param>
    internal void ValidateOwnedGateArtifacts(IReadOnlyList<string> artifactNames)
    {
        ArgumentNullException.ThrowIfNull(artifactNames);
        foreach (var artifactName in artifactNames)
        {
            ValidateOwnedGateArtifact(artifactName);
        }
    }

    /// <summary>
    /// Writes one owned gate artifact through the retained output directory.
    /// </summary>
    /// <param name="artifactName">Owned gate artifact filename.</param>
    /// <param name="contents">Complete UTF-8 artifact content.</param>
    /// <param name="cancellationToken">Cancellation token for the artifact content write.</param>
    /// <returns>A task that completes after the complete artifact content is committed.</returns>
    /// <remarks>
    /// The content is staged privately before promotion so cancellation or a write failure leaves a
    /// pre-existing report intact.
    /// </remarks>
    internal Task WriteOwnedGateArtifactAsync(
        string artifactName,
        string contents,
        CancellationToken cancellationToken)
    {
        ValidateOwnedGateArtifactName(artifactName);
        ArgumentNullException.ThrowIfNull(contents);
        return OperatingSystem.IsWindows()
            ? WriteWindowsOwnedGateArtifactAsync(artifactName, contents, cancellationToken)
            : WriteUnixOwnedGateArtifactAsync(artifactName, contents, cancellationToken);
    }

    /// <summary>
    /// Removes one owned gate artifact through the retained output directory when it exists.
    /// </summary>
    /// <param name="artifactName">Owned gate artifact filename.</param>
    internal void DeleteOwnedGateArtifact(string artifactName)
    {
        ValidateOwnedGateArtifactName(artifactName);
        if (OperatingSystem.IsWindows())
        {
            var path = GetOwnedGateArtifactPath(artifactName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        var descriptor = _outputHandle!.DangerousGetHandle().ToInt32();
        if (UnlinkAt(descriptor, artifactName, flags: 0) != 0
            && Marshal.GetLastPInvokeError() != UnixNoEntry)
        {
            throw NativeIOException($"Unable to remove coverage gate artifact '{artifactName}'.");
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

            var isOutputDirectory = index == components.Length - 1;
            current = OpenWindowsDirectory(
                currentPath,
                access: isOutputDirectory ? WindowsGenericRead | WindowsGenericWrite : 0,
                denyWriteSharing: isOutputDirectory);
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

    [ExcludeFromCodeCoverage(Justification = "Platform-specific entry inspection is exercised by the Windows and macOS security lanes; one merged run cannot execute both implementations.")]
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

        var disposition = new WindowsFileDispositionInformation { DeleteFile = 1 };
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

    private void WriteUnixMarker(int descriptor)
    {
        var flags = UnixWriteOnly | UnixCreate | UnixExclusive | UnixNoFollow | UnixCloseOnExec;
        var markerDescriptor = UnixOpenAt(descriptor, MarkerFileName, flags, Convert.ToUInt32("644", 8));
        if (markerDescriptor < 0)
        {
            throw NativeIOException("Unable to securely write the coverage ownership marker.");
        }

        using var stream = new FileStream(new SafeFileHandle((nint)markerDescriptor, ownsHandle: true), FileAccess.Write);
        if (_unixFChmod(markerDescriptor, Convert.ToUInt32("644", 8)) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = UnlinkAt(descriptor, MarkerFileName, 0);
            Marshal.SetLastPInvokeError(error);
            throw NativeIOException("Unable to set secure permissions on the coverage ownership marker.");
        }

        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(MarkerContents.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only marker creation is exercised by the Windows test lane.")]
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

    private void ValidateOwnedGateArtifact(string artifactName)
    {
        ValidateOwnedGateArtifactName(artifactName);
        if (OperatingSystem.IsWindows())
        {
            _ = GetOwnedGateArtifactPath(artifactName);
            return;
        }

        using var artifact = OpenAt(_outputHandle!, artifactName, directory: false);
        if (artifact is not null)
        {
            RejectUnixWrongKind(artifact, expectDirectory: false);
        }
    }

    private async Task WriteUnixOwnedGateArtifactAsync(
        string artifactName,
        string contents,
        CancellationToken cancellationToken)
    {
        var descriptor = _outputHandle!.DangerousGetHandle().ToInt32();
        var temporaryName = $".{artifactName}.{Guid.NewGuid():N}.tmp";
        var temporaryDescriptor = UnixOpenAt(
            descriptor,
            temporaryName,
            UnixWriteOnly | UnixCreate | UnixExclusive | UnixNoFollow | UnixCloseOnExec,
            Convert.ToUInt32("644", 8));
        if (temporaryDescriptor < 0)
        {
            throw NativeIOException($"Unable to stage coverage gate artifact '{artifactName}'.");
        }

        using var temporaryHandle = new SafeFileHandle((nint)temporaryDescriptor, ownsHandle: true);
        Exception? writeFailure = null;
        try
        {
            if (_unixFChmod(temporaryDescriptor, Convert.ToUInt32("644", 8)) != 0)
            {
                throw NativeIOException($"Unable to set permissions on staged coverage gate artifact '{artifactName}'.");
            }

            await using (var stream = new FileStream(
                temporaryHandle,
                FileAccess.Write,
                bufferSize: 4096,
                isAsync: false))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(contents.AsMemory(), cancellationToken);
            }

            if (RenameAt(descriptor, temporaryName, descriptor, artifactName) != 0)
            {
                throw NativeIOException($"Unable to promote coverage gate artifact '{artifactName}'.");
            }
        }
        catch (Exception ex)
        {
            writeFailure = ex;
            throw;
        }
        finally
        {
            if (_unixUnlinkAt(descriptor, temporaryName, 0) != 0
                && Marshal.GetLastPInvokeError() != UnixNoEntry
                && writeFailure is null)
            {
                throw NativeIOException($"Unable to clean up staged coverage gate artifact '{artifactName}'.");
            }
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows output leases deny competing directory writes while permitting staged-file promotion; the Windows security lane exercises the platform-specific binding.")]
    private async Task WriteWindowsOwnedGateArtifactAsync(
        string artifactName,
        string contents,
        CancellationToken cancellationToken)
    {
        var path = GetOwnedGateArtifactPath(artifactName);
        var temporaryPath = GetOwnedGateArtifactTemporaryPath(artifactName);
        var promoted = false;
        Exception? writeFailure = null;
        try
        {
            using (var writeHandle = OpenWindowsFile(
                       temporaryPath,
                       WindowsGenericWrite | WindowsGenericRead,
                       WindowsCreateNew,
                       shareMode: WindowsShareRead))
            {
                VerifyWindowsPathIdentity(writeHandle, temporaryPath);
                RejectWindowsWrongKind(writeHandle, expectDirectory: false);
                RejectWindowsHardLinkedArtifact(writeHandle, artifactName);
                await using var stream = new FileStream(
                    writeHandle,
                    FileAccess.Write,
                    bufferSize: 4096,
                    isAsync: false);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(contents.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }

            if (File.Exists(path))
            {
                using var destinationHandle = OpenWindowsFile(path, WindowsGenericRead, shareMode: WindowsShareRead);
                VerifyWindowsPathIdentity(destinationHandle, path);
                RejectWindowsWrongKind(destinationHandle, expectDirectory: false);
                RejectWindowsHardLinkedArtifact(destinationHandle, artifactName);
            }

            // The native rename operates on this source handle, so allow delete sharing until promotion completes.
            using (var promotionHandle = OpenWindowsFile(
                       temporaryPath,
                       WindowsGenericRead | WindowsDelete,
                       shareMode: WindowsShareRead | WindowsShareDelete))
            {
                VerifyWindowsPathIdentity(promotionHandle, temporaryPath);
                RejectWindowsWrongKind(promotionHandle, expectDirectory: false);
                RejectWindowsHardLinkedArtifact(promotionHandle, artifactName);
                PromoteWindowsOwnedGateArtifact(promotionHandle, artifactName);
            }

            promoted = true;
        }
        catch (Exception ex)
        {
            writeFailure = ex;
            throw;
        }
        finally
        {
            if (!promoted && File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (writeFailure is not null
                    && ex is IOException or UnauthorizedAccessException)
                {
                    // Preserve the primary artifact-write failure when best-effort staging cleanup also fails.
                }
            }
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows output leases deny competing directory writes while permitting staged-file promotion; the Windows security lane exercises the platform-specific binding.")]
    private string GetOwnedGateArtifactTemporaryPath(string artifactName)
    {
        var path = Path.GetFullPath(Path.Join(_outputPath, $".{artifactName}.{Guid.NewGuid():N}.tmp"));
        if (!string.Equals(Path.GetDirectoryName(path), _outputPath, CoverageOutputPathPolicy.GetPathComparison()))
        {
            throw new IOException($"Coverage report artifact '{artifactName}' is outside its output directory.");
        }

        return path;
    }

    [ExcludeFromCodeCoverage(Justification = "Windows native artifact promotion is exercised by the Windows security lane.")]
    private static void PromoteWindowsOwnedGateArtifact(
        SafeFileHandle sourceHandle,
        string artifactName)
    {
        var fileNameBytes = Encoding.Unicode.GetBytes(artifactName);
        var rootDirectoryOffset = Marshal.OffsetOf<WindowsFileRenameInformation>(nameof(WindowsFileRenameInformation.RootDirectory)).ToInt32();
        var fileNameLengthOffset = Marshal.OffsetOf<WindowsFileRenameInformation>(nameof(WindowsFileRenameInformation.FileNameLength)).ToInt32();
        var fileNameOffset = Marshal.OffsetOf<WindowsFileRenameInformation>(nameof(WindowsFileRenameInformation.FileName)).ToInt32();
        var bufferSize = checked(Marshal.SizeOf<WindowsFileRenameInformation>() + fileNameBytes.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteInt32(buffer, 0, 1);
            // With a simple name and null RootDirectory, NtSetInformationFile renames within the
            // source file's existing directory. The retained output lease continues to deny
            // competing directory-write access during the promotion.
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, fileNameLengthOffset, fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, IntPtr.Add(buffer, fileNameOffset), fileNameBytes.Length);
            Marshal.WriteInt16(buffer, fileNameOffset + fileNameBytes.Length, 0);
            var status = NtSetInformationFile(
                sourceHandle,
                out _,
                buffer,
                (uint)bufferSize,
                WindowsFileRenameInformationClass);
            if (status != WindowsStatusSuccess)
            {
                throw new IOException(
                    $"Unable to promote coverage gate artifact '{artifactName}'.",
                    new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows output leases deny competing directory writes; the Windows security lane exercises the platform-specific binding.")]
    private string GetOwnedGateArtifactPath(string artifactName)
    {
        var path = Path.GetFullPath(Path.Join(_outputPath, artifactName));
        if (!string.Equals(Path.GetDirectoryName(path), _outputPath, CoverageOutputPathPolicy.GetPathComparison()))
        {
            throw new IOException($"Coverage report artifact '{artifactName}' is outside its output directory.");
        }

        if (Directory.Exists(path))
        {
            throw new IOException($"Coverage report artifact '{path}' must be a regular file, not a directory.");
        }

        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException($"Coverage report artifact '{path}' must not be a symbolic link.");
        }

        return path;
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only hard-link inspection is exercised by the Windows security lane.")]
    private static void RejectWindowsHardLinkedArtifact(SafeFileHandle handle, string artifactName)
    {
        if (!WindowsGetFileInformationByHandle(handle, out var information))
        {
            throw new IOException("Unable to inspect coverage report artifact links.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if (information.NumberOfLinks > 1)
        {
            throw new IOException($"Coverage report artifact '{artifactName}' must not be a hard link.");
        }
    }

    private static void ValidateOwnedGateArtifactName(string artifactName)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactName);
        if (!string.Equals(Path.GetFileName(artifactName), artifactName, StringComparison.Ordinal)
            || artifactName.Contains(Path.DirectorySeparatorChar)
            || artifactName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new IOException($"Coverage report artifact '{artifactName}' is outside its output directory.");
        }
    }

    private static bool IsKnownOutputEntry(TreeEntry entry)
        => IsKnownOutputEntry(Path.GetFileName(entry.RelativePath), entry.IsDirectory);

    private static bool IsKnownOutputEntry(string name, bool isDirectory)
        => isDirectory
            ? name is "projects" or "reportgenerator"
            : name is "coverage.cobertura.xml" or "coverage.json" or CoverageGateArtifactNames.Json or CoverageGateArtifactNames.Markdown
                or CoverageGateArtifactNames.PatchTargetsJson or CoverageGateArtifactNames.PatchTargetsMarkdown
                or "coverage-watchdog.json" or "summary.txt" or "timings.json" or "reportgenerator-summary.txt"
                or CoverageRunSlowTestDiagnosticsWriter.MarkdownFileName or CoverageRunSlowTestDiagnosticsWriter.JsonFileName;

    private static bool IsPatternOutputEntry(OutputEntry entry)
        => IsPatternOutputEntry(entry.Name, entry.IsDirectory);

    private static bool IsPatternOutputEntry(TreeEntry entry)
        => IsPatternOutputEntry(Path.GetFileName(entry.RelativePath), entry.IsDirectory);

    private static bool IsPatternOutputEntry(string name, bool isDirectory)
        => !isDirectory
            && ((name.StartsWith("junit-", StringComparison.Ordinal) || name.StartsWith("test-results-", StringComparison.Ordinal))
                && name.EndsWith(".xml", StringComparison.Ordinal)
                || IsSlowTestDiagnosticsTemporaryEntry(name)
                || IsCoverageGateTemporaryEntry(name));

    private static bool IsSlowTestDiagnosticsTemporaryEntry(string name)
        => IsSlowTestDiagnosticsTemporaryEntry(name, CoverageRunSlowTestDiagnosticsWriter.MarkdownFileName)
            || IsSlowTestDiagnosticsTemporaryEntry(name, CoverageRunSlowTestDiagnosticsWriter.JsonFileName);

    private static bool IsCoverageGateTemporaryEntry(string name)
        => IsCoverageGateTemporaryEntry(name, CoverageGateArtifactNames.Json)
            || IsCoverageGateTemporaryEntry(name, CoverageGateArtifactNames.Markdown)
            || IsCoverageGateTemporaryEntry(name, CoverageGateArtifactNames.PatchTargetsJson)
            || IsCoverageGateTemporaryEntry(name, CoverageGateArtifactNames.PatchTargetsMarkdown);

    private static bool IsCoverageGateTemporaryEntry(string name, string artifactName)
    {
        var prefix = $".{artifactName}.";
        const string suffix = ".tmp";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var identifier = name[prefix.Length..^suffix.Length];
        return Guid.TryParseExact(identifier, "N", out _);
    }

    private static bool IsSlowTestDiagnosticsTemporaryEntry(string name, string artifactName)
    {
        var prefix = $".{artifactName}.";
        var suffix = name.EndsWith(".tmp", StringComparison.Ordinal)
            ? ".tmp"
            : name.EndsWith(".backup", StringComparison.Ordinal)
                ? ".backup"
                : null;
        if (suffix is null || !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var identifier = name[prefix.Length..^suffix.Length];
        return Guid.TryParseExact(identifier, "N", out _);
    }

    private static SafeFileHandle? OpenAt(
        SafeFileHandle parent,
        string name,
        bool directory,
        bool notDirectoryIsMissing = false)
    {
        var flags = UnixReadOnly | UnixCloseOnExec | UnixNoFollow | UnixNonBlocking | (directory ? UnixDirectory : 0);
        var descriptor = UnixOpenAt(parent.DangerousGetHandle().ToInt32(), name, flags, 0);
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
        var descriptor = UnixOpen(path, UnixReadOnly | UnixCloseOnExec | UnixNoFollow | UnixDirectory | UnixNonBlocking);
        return descriptor >= 0 ? descriptor : throw NativeIOException($"Unable to securely open output directory '{path}'.");
    }

    [ExcludeFromCodeCoverage(Justification = "Native stat identity layouts vary by Unix OS; the platform security lanes exercise their supported layouts.")]
    private static FileObjectIdentity GetUnixIdentity(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (UnixFStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
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
        if (!WindowsGetFileInformationByHandle(handle, out var information))
        {
            throw new IOException("Unable to identify an output entry.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return new FileObjectIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    [ExcludeFromCodeCoverage(Justification = "Native stat kind layouts vary by Unix OS and architecture; the platform security lanes exercise their supported layouts.")]
    private static void RejectUnixWrongKind(SafeFileHandle handle, bool expectDirectory)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (UnixFStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
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
            // Windows evaluates delete sharing across every retained ancestor while resolving a
            // staged-child rename. Retain the write-sharing denial only on the output directory so
            // another process cannot open that directory for direct mutation while this lease is active.
            denyWriteSharing
                ? WindowsShareRead | WindowsShareDelete
                : WindowsShareRead | WindowsShareWrite | WindowsShareDelete);

    [ExcludeFromCodeCoverage(Justification = "Windows-only handle opening is exercised by the Windows test lane.")]
    private static SafeFileHandle OpenWindowsFile(
        string path,
        uint access,
        uint disposition = WindowsOpenExisting,
        uint flags = WindowsOpenReparsePoint,
        uint shareMode = WindowsShareRead | WindowsShareWrite)
    {
        var handle = WindowsCreateFile(path, access, shareMode, 0, disposition, flags, 0);
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
        if (!WindowsGetFileInformationByHandleEx(handle, WindowsFileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<WindowsFileAttributeTagInformation>()))
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
        if (!WindowsGetFileInformationByHandleEx(handle, WindowsFileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<WindowsFileAttributeTagInformation>()))
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
        var buffer = new char[512];
        while (true)
        {
            var length = WindowsGetFinalPathNameByHandle(handle, buffer, 0);
            if (length == 0)
            {
                throw new IOException("Unable to resolve output handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, checked((int)length));
            }

            buffer = new char[checked((int)length + 1)];
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only extended-path normalization is exercised by the Windows test lane.")]
    private static string NormalizeWindowsFinalPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path));

    [ExcludeFromCodeCoverage(Justification = "Windows-only retained-handle path resolution is exercised by the Windows test lane.")]
    private static string GetStableDirectoryPath(SafeFileHandle handle)
        => OperatingSystem.IsWindows()
            ? NormalizeWindowsFinalPath(GetWindowsFinalPath(handle))
            : throw new PlatformNotSupportedException("Unix directories are enumerated directly from retained descriptors.");

    /// <summary>
    /// Canonicalizes fixed operating-system aliases before safety comparisons and no-follow traversal.
    /// </summary>
    /// <param name="path">An absolute platform path.</param>
    /// <returns>The path with only fixed operating-system aliases canonicalized.</returns>
    [ExcludeFromCodeCoverage(Justification = "macOS fixed-alias normalization is exercised by the macOS test lane; other platforms intentionally return unchanged paths.")]
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

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
    private static int UnixCloseOnExec => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
    private static int UnixDirectory => OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
    private static int UnixNoFollow => OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
    private static int UnixNonBlocking => OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
    private static int UnixCreate => OperatingSystem.IsMacOS() ? 0x00000200 : 0x00000040;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
    private static int UnixExclusive => OperatingSystem.IsMacOS() ? 0x00000800 : 0x00000080;

    [ExcludeFromCodeCoverage(Justification = "The platform security lanes exercise the OS-specific native flag values.")]
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
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsCreateNew = 1;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsAttributeReparsePoint = 0x00000400;
    private const uint WindowsAttributeDirectory = 0x00000010;
    private const int WindowsFileAttributeTagInfo = 9;
    private const int WindowsFileRenameInformationClass = 10;
    private const int WindowsFileDispositionInfo = 4;
    private const int WindowsStatusSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileDispositionInformation
    {
        public int DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsFileRenameInformation
    {
        public int ReplaceIfExists;
        public nint RootDirectory;
        public int FileNameLength;
        public char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsIoStatusBlock
    {
        public int Status;
        public nuint Information;
    }

    private sealed record OutputEntry(string Name, bool IsDirectory, FileObjectIdentity Identity);
    private readonly record struct TreeEntry(string RelativePath, bool IsDirectory, FileObjectIdentity Identity);
    private readonly record struct OwnershipState(
        bool HasMarker,
        IReadOnlyList<TreeEntry> Snapshot);
    [ExcludeFromCodeCoverage(Justification = "Platform-native identity data is exercised by the platform security lanes.")]
    private readonly record struct FileObjectIdentity(ulong DeviceOrVolume, ulong FileId);

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MkdirAt(int directoryDescriptor, string path, uint mode);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(int directoryDescriptor, string path, int flags);

    [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt(
        int oldDirectoryDescriptor,
        string oldPath,
        int newDirectoryDescriptor,
        string newPath);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int Duplicate(int descriptor);

    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static partial nint FdOpenDirectory(int descriptor);

    [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static partial nint ReadDirectory(nint directoryStream);

    [LibraryImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static partial int CloseDirectory(nint directoryStream);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int descriptor);

    [LibraryImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static partial long Seek(int descriptor, long offset, int origin);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle handle,
        int fileInformationClass,
        ref WindowsFileDispositionInformation fileInformation,
        uint bufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("ntdll.dll", EntryPoint = "NtSetInformationFile")]
    private static partial int NtSetInformationFile(
        SafeFileHandle handle,
        out WindowsIoStatusBlock ioStatusBlock,
        nint fileInformation,
        uint length,
        int fileInformationClass);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError")]
    private static partial uint RtlNtStatusToDosError(int status);
}
