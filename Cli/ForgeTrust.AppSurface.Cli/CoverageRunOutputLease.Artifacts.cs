using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Coverage.CoverageFileSystemInterop;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Extends the existing retained-directory boundary for private artifact storage.</summary>
internal sealed partial class CoverageRunOutputLease
{
    /// <summary>Gets the canonical path whose binding is retained by this lease.</summary>
    internal string ArtifactDirectoryPath => _outputPath;

    /// <summary>Gets an operating-system object identity, independent of path spelling.</summary>
    internal string ArtifactDirectoryIdentity
    {
        get
        {
            var id = OperatingSystem.IsWindows() ? GetWindowsIdentity(_outputHandle!) : GetUnixIdentity(_outputHandle!);
            return id.ToString();
        }
    }

    /// <summary>Gets the physical identity of an already-opened regular file or directory.</summary>
    /// <param name="handle">A validated native object handle retained by the caller.</param>
    internal static string GetArtifactObjectIdentity(SafeFileHandle handle)
        => (OperatingSystem.IsWindows() ? GetWindowsIdentity(handle) : GetUnixIdentity(handle)).ToString();

    /// <summary>Reads native identity, length, last-write time and change time from an open regular file.</summary>
    /// <param name="handle">A retained native handle to the file.</param>
    /// <param name="nativeCalls">Optional per-call native observations for decision tests; omission uses the actual host APIs.</param>
    /// <returns>The file state used to detect in-place changes across an archive read.</returns>
    /// <remarks>Injected observations verify managed decoding and rejection decisions, not the native filesystem or ABI.</remarks>
    internal static CoverageArtifactObjectState GetArtifactObjectState(SafeFileHandle handle, CoverageArtifactStateCalls? nativeCalls = null)
    {
        var calls = nativeCalls ?? CoverageArtifactStateCalls.Default;
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed) throw new IOException("Artifact file handle is unavailable.");

        if (calls.IsWindows)
        {
            const int fileBasicInformation = 0;
            if (!calls.WindowsBasic(handle, fileBasicInformation,
                    out var basic, (uint)Marshal.SizeOf<WindowsFileBasicInformation>())
                || !calls.WindowsInformation(handle, out var information))
                throw new IOException("Unable to inspect artifact file state.", new Win32Exception(Marshal.GetLastPInvokeError()));
            var length = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
            if (length > long.MaxValue) throw new IOException("Artifact file length exceeds the supported range.");
            return new CoverageArtifactObjectState(
                calls.Identity(handle), (long)length,
                basic.LastWriteTime / 10_000_000, basic.LastWriteTime % 10_000_000,
                basic.ChangeTime / 10_000_000, basic.ChangeTime % 10_000_000);
        }

        if (!calls.IsLinux && !calls.IsMacOS)
            throw new PlatformNotSupportedException("Native artifact file-state inspection is unavailable.");
        var stat = Marshal.AllocHGlobal(512);
        try
        {
            if (calls.UnixStat(handle.DangerousGetHandle().ToInt32(), stat) != 0)
                throw NativeIOException("Unable to inspect artifact file state.");
            long length;
            int modifiedSecondsOffset;
            int modifiedNanosecondsOffset;
            int changedSecondsOffset;
            int changedNanosecondsOffset;
            if (calls.IsMacOS)
            {
                length = Marshal.ReadInt64(stat, 96);
                modifiedSecondsOffset = 48;
                modifiedNanosecondsOffset = 56;
                changedSecondsOffset = 64;
                changedNanosecondsOffset = 72;
            }
            else if (calls.Architecture == Architecture.X64)
            {
                length = Marshal.ReadInt64(stat, 48);
                modifiedSecondsOffset = 88;
                modifiedNanosecondsOffset = 96;
                changedSecondsOffset = 104;
                changedNanosecondsOffset = 112;
            }
            else if (calls.Architecture == Architecture.Arm64)
            {
                length = Marshal.ReadInt64(stat, 48);
                modifiedSecondsOffset = 88;
                modifiedNanosecondsOffset = 96;
                changedSecondsOffset = 104;
                changedNanosecondsOffset = 112;
            }
            else
            {
                throw new PlatformNotSupportedException("Unsupported native stat layout for artifact file-state inspection.");
            }
            if (length < 0) throw new IOException("Artifact file has a negative native length.");
            return new CoverageArtifactObjectState(
                calls.Identity(handle), length,
                Marshal.ReadInt64(stat, modifiedSecondsOffset), Marshal.ReadInt64(stat, modifiedNanosecondsOffset),
                Marshal.ReadInt64(stat, changedSecondsOffset), Marshal.ReadInt64(stat, changedNanosecondsOffset));
        }
        finally { Marshal.FreeHGlobal(stat); }
    }

    /// <summary>Opens a retained directory without coverage-output ownership semantics.</summary>
    /// <param name="path">Absolute directory path, traversed without links.</param>
    /// <param name="createMissing">Whether missing components may be privately created.</param>
    /// <param name="requirePrivate">Whether current-user private permissions are required.</param>
    /// <returns>A retained directory, or null when an existing-only path is missing.</returns>
    /// <remarks>Existing permissions are validated, never repaired. Callers separately validate store/schema authority.</remarks>
    /// <param name="allowWindowsRenameSharing">Whether a cooperative lease needs compatible writer sharing rather than restore-parent exclusion.</param>
    /// <param name="unixFChmod">Optional per-lease native permission operation for failure tests; defaults to fchmod.</param>
    /// <param name="beforeWindowsCreate">Optional race seam immediately before creating a missing Windows component; production omits it.</param>
    internal static CoverageRunOutputLease? AcquireArtifactDirectory(string path, bool createMissing, bool requirePrivate, bool allowWindowsRenameSharing = false,
        Func<int, uint, int>? unixFChmod = null, Action<string>? beforeWindowsCreate = null)
    {
        var lease = new CoverageRunOutputLease(NormalizePlatformPath(Path.GetFullPath(path)), unixFChmod);
        try
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException("Private artifact directories require supported native filesystem primitives.");
            var acquired = OperatingSystem.IsWindows()
                ? lease.AcquirePrivateWindowsDirectory(createMissing, allowWindowsRenameSharing, beforeWindowsCreate)
                : lease.AcquirePrivateUnixDirectory(createMissing);
            if (!acquired)
            {
                lease.Dispose();
                return null;
            }
            if (requirePrivate) lease.ValidateArtifactPrivateAccess(lease._outputHandle!, directory: true);
            lease.VerifyBinding();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Rechecks the entire pathname against the directory objects originally acquired.</summary>
    internal void VerifyArtifactBinding() => VerifyBinding();

    /// <summary>Gets the retained directory handle for a cooperative native lock; the caller must not dispose it.</summary>
    internal SafeFileHandle ArtifactDirectoryHandle => _outputHandle!;

    /// <summary>Opens the Windows cooperative-lock file without following links or allowing replacement.</summary>
    /// <param name="name">One fixed protocol filename owned by the caller's output or private domain.</param>
    /// <param name="requirePrivate">Whether to require private ownership and a zero-length candidate coordination file.</param>
    /// <param name="readExistingOnly">Whether an existing lock requires only read access. A missing protocol lock may still be created in a writable root.</param>
    internal SafeFileHandle OpenArtifactOperationLock(string name = ".appsurface-coverage-operation.lock", bool requirePrivate = false,
        bool readExistingOnly = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ValidateArtifactComponent(name);
        VerifyBinding();
        SafeFileHandle handle;
        var path = Path.Join(_outputPath, name);
        var access = readExistingOnly ? WindowsGenericRead : WindowsGenericRead | WindowsGenericWrite;
        try
        {
            handle = OpenWindowsFile(path, access,
            WindowsOpenExisting, shareMode: WindowsShareRead | WindowsShareWrite | WindowsShareDelete);
        }
        catch (IOException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: 2 })
        {
            try { handle = CreateArtifactWindowsFile(path, WindowsShareRead | WindowsShareWrite | WindowsShareDelete); }
            catch (IOException conflict) when (conflict.InnerException is Win32Exception { NativeErrorCode: 80 or 183 })
            {
                handle = OpenWindowsFile(path, access,
                    WindowsOpenExisting, shareMode: WindowsShareRead | WindowsShareWrite | WindowsShareDelete);
            }
        }
        try
        {
            VerifyWindowsPathIdentity(handle, Path.Join(_outputPath, name));
            RejectWindowsWrongKind(handle, false);
            RejectWindowsHardLinkedArtifact(handle, name);
            if (requirePrivate)
            {
                ValidateArtifactPrivateAccess(handle, directory: false);
                if (RandomAccess.GetLength(handle) != 0) throw new InvalidDataException("The candidate coordination file must remain empty.");
            }
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Validates an existing zero-length private coordination file without creating or changing it.</summary>
    /// <param name="name">One fixed domain protocol filename.</param>
    /// <returns>The validated length, always zero.</returns>
    internal long ReadArtifactCoordinationFileLength(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("A Windows coordination file is unexpected on this host.");
        ValidateArtifactComponent(name);
        VerifyBinding();
        var path = Path.Join(_outputPath, name);
        using var handle = OpenWindowsFile(path, WindowsGenericRead, WindowsOpenExisting,
            shareMode: WindowsShareRead | WindowsShareWrite | WindowsShareDelete);
        VerifyWindowsPathIdentity(handle, path);
        RejectWindowsWrongKind(handle, false);
        RejectWindowsHardLinkedArtifact(handle, name);
        ValidateArtifactPrivateAccess(handle, false);
        var length = RandomAccess.GetLength(handle);
        if (length != 0) throw new InvalidDataException("The candidate coordination file must remain empty.");
        VerifyBinding();
        return length;
    }

    /// <summary>Verifies the named Windows operation lock still identifies the retained lock object.</summary>
    /// <param name="lockHandle">The retained lock-file handle used by the operation lease.</param>
    /// <param name="expectedIdentity">The lock-file identity captured when the lease was acquired.</param>
    /// <param name="name">The fixed filename selected by the same lease protocol.</param>
    internal void VerifyArtifactOperationLockBinding(SafeFileHandle lockHandle, string expectedIdentity,
        string name = ".appsurface-coverage-operation.lock")
    {
        ValidateArtifactComponent(name);
        VerifyBinding();
        var lockPath = Path.Join(_outputPath, name);
        using var current = OpenWindowsFile(lockPath, 0, WindowsOpenExisting,
            shareMode: WindowsShareRead | WindowsShareWrite | WindowsShareDelete);
        VerifyWindowsPathIdentity(current, lockPath);
        RejectWindowsWrongKind(current, expectDirectory: false);
        if (!string.Equals(GetArtifactObjectIdentity(lockHandle), expectedIdentity, StringComparison.Ordinal)
            || !string.Equals(GetArtifactObjectIdentity(current), expectedIdentity, StringComparison.Ordinal))
            throw new IOException("Operation lock file identity changed while the lease was held.");
        VerifyBinding();
    }

    /// <summary>Creates a private immediate child and retains its complete no-follow path.</summary>
    /// <param name="name">One portable path component.</param>
    /// <param name="beforeCreate">Optional per-call race observation after preflight; native creation still requires a fresh destination.</param>
    /// <param name="onCreated">Optional observation immediately after native creation, before validation that may fail; never called on a no-replace refusal.</param>
    /// <returns>The privately created child directory.</returns>
    internal CoverageRunOutputLease CreateArtifactDirectory(string name, Action? beforeCreate = null, Action? onCreated = null)
    {
        ValidateArtifactComponent(name);
        VerifyBinding();
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Join(_outputPath, name);
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Artifact destination already exists.");
            beforeCreate?.Invoke();
            VerifyBinding();
            CreatePrivateWindowsDirectory(path);
            onCreated?.Invoke();
        }
        else
        {
            beforeCreate?.Invoke();
            VerifyBinding();
            if (MkdirAt(_outputHandle!.DangerousGetHandle().ToInt32(), name, 0x1c0) != 0)
                throw NativeIOException("Unable to create private artifact directory.");
            onCreated?.Invoke();
            using var created = OpenAt(_outputHandle!, name, directory: true)
                ?? throw new IOException("Created private artifact directory disappeared.");
            if (_unixFChmod(created.DangerousGetHandle().ToInt32(), 0x1c0) != 0)
                throw NativeIOException("Unable to restrict newly created private directory permissions.");
        }
        VerifyBinding();
        return AcquireArtifactDirectory(Path.Join(_outputPath, name), createMissing: false, requirePrivate: true)
            ?? throw new IOException("Created artifact directory disappeared.");
    }

    /// <summary>Opens an immediate regular, singly-linked file through the retained directory.</summary>
    /// <param name="name">One portable filename.</param>
    /// <param name="createNew">Whether to atomically create a new private file; otherwise read existing bytes.</param>
    /// <param name="requirePrivate">Whether to enforce current-user private file access.</param>
    /// <returns>A caller-owned stream bound to the opened object.</returns>
    internal FileStream OpenArtifactFile(string name, bool createNew, bool requirePrivate = true)
    {
        ValidateArtifactComponent(name);
        VerifyBinding();
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                handle = createNew
                    ? CreateArtifactWindowsFile(Path.Join(_outputPath, name))
                    : OpenWindowsFile(Path.Join(_outputPath, name), WindowsGenericRead,
                        WindowsOpenExisting, shareMode: WindowsShareRead);
            }
            catch (IOException exception) when (!createNew && exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
            {
                throw new FileNotFoundException("Artifact file is unavailable.", exception);
            }
            try
            {
                VerifyWindowsPathIdentity(handle, Path.Join(_outputPath, name));
                RejectWindowsWrongKind(handle, expectDirectory: false);
                RejectWindowsHardLinkedArtifact(handle, name);
                if (requirePrivate) ValidateArtifactPrivateAccess(handle, directory: false);
            }
            catch { handle.Dispose(); throw; }
        }
        else
        {
            var flags = UnixNoFollow | UnixCloseOnExec | UnixNonBlocking
                | (createNew ? UnixWriteOnly | UnixCreate | UnixExclusive : UnixReadOnly);
            var fd = UnixOpenAt(_outputHandle!.DangerousGetHandle().ToInt32(), name, flags, 0x180);
            if (fd < 0)
            {
                if (Marshal.GetLastPInvokeError() == 2 && !createNew)
                    throw new FileNotFoundException("Artifact file is unavailable.");
                throw NativeIOException("Unable to open artifact regular file.");
            }
            handle = new SafeFileHandle((nint)fd, ownsHandle: true);
            try
            {
                if (createNew && _unixFChmod(fd, 0x180) != 0)
                    throw NativeIOException("Unable to restrict newly created artifact file permissions.");
                RejectUnixWrongKind(handle, expectDirectory: false);
                ValidateArtifactUnixMetadata(handle, directory: false, requirePrivate);
            }
            catch { handle.Dispose(); throw; }
        }
        try
        {
            VerifyBinding();
            return new FileStream(handle, createNew ? FileAccess.Write : FileAccess.Read, 81920, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Enumerates at most a caller-supplied number of immediate entries without following links.</summary>
    /// <param name="maximumEntries">Strict positive allocation and traversal bound.</param>
    /// <returns>Validated regular-file and directory names.</returns>
    internal IReadOnlyList<CoverageArtifactDirectoryEntry> EnumerateArtifactEntries(int maximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        VerifyBinding();
        var names = OperatingSystem.IsWindows()
            ? ReadArtifactWindowsNames(_outputHandle!, maximumEntries)
            : ReadArtifactUnixNames(_outputHandle!, maximumEntries);
        var entries = new List<CoverageArtifactDirectoryEntry>(names.Count);
        foreach (var name in names)
        {
            ValidateArtifactComponent(name);
            var inspected = InspectEntry(_outputHandle!, name);
            entries.Add(new(name, inspected.IsDirectory, inspected.Identity.ToString()));
        }
        VerifyBinding();
        return entries;
    }

    /// <summary>Atomically renames an immediate child, optionally replacing one regular metadata file.</summary>
    /// <param name="sourceName">Existing immediate entry.</param>
    /// <param name="destination">Retained destination directory on the same volume.</param>
    /// <param name="destinationName">New immediate entry name.</param>
    /// <param name="replaceFile">Whether one already-validated regular file may be replaced.</param>
    /// <param name="expectedSourceIdentity">Previously verified source identity, required for staged-directory publication.</param>
    internal void MoveArtifactEntry(string sourceName, CoverageRunOutputLease destination, string destinationName, bool replaceFile = false, string? expectedSourceIdentity = null)
    {
        ValidateArtifactComponent(sourceName);
        ValidateArtifactComponent(destinationName);
        VerifyBinding();
        destination.VerifyBinding();
        var source = InspectEntry(_outputHandle!, sourceName);
        if (expectedSourceIdentity is not null && !string.Equals(expectedSourceIdentity, source.Identity.ToString(), StringComparison.Ordinal))
            throw new IOException("Artifact source changed after verification.");
        if (replaceFile && source.IsDirectory) throw new IOException("Directory replacement is forbidden.");
        if (replaceFile) destination.ValidateOwnedGateArtifact(destinationName);
        if (OperatingSystem.IsWindows())
        {
            using var handle = source.IsDirectory
                ? OpenWindowsFile(Path.Join(_outputPath, sourceName), WindowsGenericRead | WindowsDelete,
                    flags: WindowsBackupSemantics | WindowsOpenReparsePoint, shareMode: WindowsShareRead | WindowsShareWrite | WindowsShareDelete)
                : OpenWindowsFile(Path.Join(_outputPath, sourceName), WindowsGenericRead | WindowsDelete,
                    shareMode: WindowsShareRead | WindowsShareDelete);
            VerifyEntryIdentity(handle, source, sourceName);
            RenameArtifactWindows(handle, destination._outputHandle!, destinationName, replaceFile);
        }
        else
        {
            var from = _outputHandle!.DangerousGetHandle().ToInt32();
            var to = destination._outputHandle!.DangerousGetHandle().ToInt32();
            var rc = replaceFile ? RenameAt(from, sourceName, to, destinationName)
                : OperatingSystem.IsMacOS() ? RenameArtifactMac(from, sourceName, to, destinationName, 4)
                : RenameArtifactLinux(from, sourceName, to, destinationName, 1);
            if (rc != 0) throw NativeIOException("Unable to atomically publish artifact entry.");
        }
        VerifyBinding();
        destination.VerifyBinding();
    }

    /// <summary>Deletes only a previously authorized immediate entry through the retained directory.</summary>
    /// <param name="name">Manager-authorized entry name; authority must be established by its caller.</param>
    /// <param name="maximumEntries">Bound for the complete child snapshot, including directories.</param>
    /// <param name="expectedIdentity">Optional identity previously verified by a reader.</param>
    /// <remarks>
    /// Unknown trees, links, changed identities and excessive depth fail closed; they are not repaired.
    /// Windows deletion completes after retained handles to the entry close. Callers keep their candidate lease
    /// through deletion and metadata publication, then dispose it before asserting namespace disappearance.
    /// </remarks>
    internal void DeleteArtifactEntry(string name, int maximumEntries, string? expectedIdentity = null)
    {
        ValidateArtifactComponent(name);
        var entries = new List<TreeEntry>();
        var root = InspectEntry(_outputHandle!, name);
        if (expectedIdentity is not null && !string.Equals(root.Identity.ToString(), expectedIdentity, StringComparison.Ordinal))
            throw new IOException("Artifact deletion candidate changed after verification.");
        CaptureArtifactSnapshot(_outputHandle!, root, string.Empty, entries, maximumEntries, 0);
        VerifyBinding();
        var authorized = BuildAuthorizedChildren(entries);
        if (OperatingSystem.IsWindows()) DeleteWindowsEntry(_outputHandle!, entries[0], authorized);
        else DeleteAuthorizedUnixEntry(_outputHandle!, entries[0], authorized);
    }

    /// <summary>Validates one cross-platform path component before any filesystem call.</summary>
    /// <param name="name">A manifest-derived filename or directory name.</param>
    internal static void ValidateArtifactComponent(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name is "." or ".." || name.Length > 240 || name.EndsWith(' ') || name.EndsWith('.')
            || name.Any(c => c < 32 || c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'))
            throw new InvalidDataException("Artifact path contains an invalid component.");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && stem[3] is >= '1' and <= '9')
            throw new InvalidDataException("Artifact path contains a reserved device name.");
    }

    private bool AcquirePrivateUnixDirectory(bool createMissing)
    {
        var root = Path.GetPathRoot(_outputPath)!;
        var current = new SafeFileHandle((nint)OpenUnixDirectory(root), ownsHandle: true);
        _unixHandles.Add(current);
        foreach (var component in Path.GetRelativePath(root, _outputPath).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = OpenAt(current, component, directory: true);
            if (next is null)
            {
                if (!createMissing) return false;
                var created = MkdirAt(current.DangerousGetHandle().ToInt32(), component, 0x1c0) == 0;
                if (!created && Marshal.GetLastPInvokeError() != UnixAlreadyExists)
                    throw NativeIOException("Unable to create private directory component.");
                next = OpenAt(current, component, directory: true) ?? throw new IOException("Private directory disappeared.");
                if (created && _unixFChmod(next.DangerousGetHandle().ToInt32(), 0x1c0) != 0)
                {
                    next.Dispose();
                    throw NativeIOException("Unable to restrict newly created private directory permissions.");
                }
            }
            RejectUnixWrongKind(next, expectDirectory: true);
            _unixHandles.Add(next);
            current = next;
        }
        _outputHandle = current;
        return true;
    }

    /// <summary>Retains and validates each Windows path component, reopening a competing creator without repairing its access.</summary>
    private bool AcquirePrivateWindowsDirectory(bool createMissing, bool allowRenameSharing, Action<string>? beforeCreate)
    {
        var root = Path.GetPathRoot(_outputPath)!;
        var currentPath = root;
        var current = OpenWindowsFile(root, 0, flags: WindowsBackupSemantics | WindowsOpenReparsePoint,
            shareMode: WindowsShareRead | WindowsShareWrite | (allowRenameSharing ? WindowsShareDelete : 0));
        _windowsHandles.Add(current);
        foreach (var component in Path.GetRelativePath(root, _outputPath).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Join(currentPath, component);
            if (!Directory.Exists(currentPath))
            {
                if (!createMissing) return false;
                beforeCreate?.Invoke(currentPath);
                CreateMissingWindowsDirectory(currentPath, CreatePrivateWindowsDirectory);
            }
            current = OpenWindowsFile(currentPath, 0, flags: WindowsBackupSemantics | WindowsOpenReparsePoint,
                shareMode: WindowsShareRead | WindowsShareWrite | (allowRenameSharing ? WindowsShareDelete : 0));
            _windowsHandles.Add(current);
            VerifyWindowsPathIdentity(current, currentPath);
            RejectWindowsWrongKind(current, expectDirectory: true);
        }
        _outputHandle = current;
        return true;
    }

    /// <summary>Accepts only an already-existing-name race during an idempotent Windows component walk.</summary>
    /// <param name="path">The missing component observed beneath the retained parent.</param>
    /// <param name="createDirectory">Private native creation operation; injectable for managed error-contract tests.</param>
    /// <remarks>The caller must reopen and verify kind, no-follow identity and required private access. Fresh destinations never use this helper.</remarks>
    internal static void CreateMissingWindowsDirectory(string path, Action<string> createDirectory)
    {
        try { createDirectory(path); }
        catch (IOException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: 183 })
        {
            // Another creator won the name; admission still depends on the retained handle checks below.
        }
    }

    /// <summary>Atomically creates one new directory with the current-user owner and protected private ACL.</summary>
    private static void CreatePrivateWindowsDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Current Windows user has no security identifier.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        CreateArtifactWindowsDirectory(path, security.GetSecurityDescriptorBinaryForm());
    }

    /// <summary>Publishes a fresh Windows directory with its already-built private security descriptor in the native create call.</summary>
    /// <param name="path">The validated absolute Windows path beneath a retained parent.</param>
    /// <param name="descriptor">The complete private security descriptor, pinned until native creation returns.</param>
    /// <param name="createDirectory">Optional per-call native creation seam; omission calls CreateDirectoryW. Simulations do not prove Windows ACL enforcement.</param>
    internal static unsafe void CreateArtifactWindowsDirectory(string path, byte[] descriptor, Func<string, nint, bool>? createDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        fixed (byte* descriptorPointer = descriptor)
        {
            var attributes = new ArtifactWindowsSecurityAttributes
            {
                Length = sizeof(ArtifactWindowsSecurityAttributes),
                SecurityDescriptor = (nint)descriptorPointer,
                InheritHandle = 0
            };
            // Unlike DirectoryInfo.Create, CreateDirectoryW reports a competing pathname.
            // Only the idempotent component walker may reopen and validate that object.
            // Fresh-destination callers always retain this no-replace refusal.
            if (!(createDirectory ?? WindowsCreateDirectory)(WindowsExtendedPath(path), (nint)(&attributes)))
                throw new IOException("Unable to create a fresh private artifact directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    // Windows takes a new file's owner from the token's default owner, which can be
    // Administrators for an elevated process. Set the current user atomically at creation;
    // accepting another owner or repairing an existing file would weaken store admission.
    private static unsafe SafeFileHandle CreateArtifactWindowsFile(string path, uint sharing = WindowsShareRead)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Current Windows user has no security identifier.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        fixed (byte* descriptorPointer = descriptor)
        {
            var attributes = new ArtifactWindowsSecurityAttributes
            {
                Length = sizeof(ArtifactWindowsSecurityAttributes),
                SecurityDescriptor = (nint)descriptorPointer,
                InheritHandle = 0
            };
            var handle = WindowsCreateFile(path, WindowsGenericRead | WindowsGenericWrite, sharing,
                (nint)(&attributes), WindowsCreateNew, WindowsOpenReparsePoint, 0);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException("Unable to create private artifact file.", new Win32Exception(error));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ArtifactWindowsSecurityAttributes
    {
        internal int Length;
        internal nint SecurityDescriptor;
        internal int InheritHandle;
    }

    private void ValidateArtifactPrivateAccess(SafeFileHandle handle, bool directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            ValidateArtifactUnixMetadata(handle, directory, requirePrivate: true);
            return;
        }
        var path = directory ? GetStableDirectoryPath(handle) : NormalizeWindowsFinalPath(GetWindowsFinalPath(handle));
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Current Windows user has no security identifier.");
        var observedRules = new List<CoverageArtifactAccessRule>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            observedRules.Add(new(rule.IdentityReference.Value, (uint)rule.FileSystemRights, rule.AccessControlType == AccessControlType.Allow));
        ValidateArtifactWindowsAccess(security.GetOwner(typeof(SecurityIdentifier))?.Value, user.Value, observedRules);
    }

    /// <summary>Rejects Windows ownership or allow rules granting access beyond the current user and system administrators.</summary>
    /// <param name="owner">Owner SID observed from the retained object's access control descriptor.</param>
    /// <param name="user">The effective current-user SID from the native identity observation.</param>
    /// <param name="rules">SID-normalized access rules, including inherited and explicit allow/deny entries.</param>
    /// <remarks>These are policy decisions over observed facts; caller tests do not establish native ACL retrieval or Windows handle binding.</remarks>
    internal static void ValidateArtifactWindowsAccess(string? owner, string user, IEnumerable<CoverageArtifactAccessRule> rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentNullException.ThrowIfNull(rules);
        if (!string.Equals(owner, user, StringComparison.Ordinal)) throw CoverageArtifactStoreFailure.PrivateAccess();
        foreach (var rule in rules)
            if (rule.Allows && rule.Rights != 0
                && !string.Equals(rule.Principal, user, StringComparison.Ordinal)
                && rule.Principal is not ("S-1-5-18" or "S-1-5-32-544"))
                throw CoverageArtifactStoreFailure.PrivateAccess();
    }

    /// <summary>Validates ownership, link count and private permissions from one retained Unix object.</summary>
    /// <param name="handle">The retained native file or directory.</param>
    /// <param name="directory">Whether directory permissions and link-count semantics apply.</param>
    /// <param name="requirePrivate">Whether exact private permissions and current-user ownership are required.</param>
    /// <param name="nativeCalls">Optional per-call stat observation for decoding tests; defaults to the actual host.</param>
    /// <param name="effectiveUserId">Optional effective user identity for policy-decision tests; defaults to geteuid.</param>
    /// <remarks>Synthetic observations test rejection policy and stat decoding, not another platform's native ABI.</remarks>
    internal static void ValidateArtifactUnixMetadata(SafeFileHandle handle, bool directory, bool requirePrivate,
        CoverageArtifactStateCalls? nativeCalls = null, uint? effectiveUserId = null)
    {
        var calls = nativeCalls ?? CoverageArtifactStateCalls.Default;
        var stat = Marshal.AllocHGlobal(512);
        try
        {
            if (calls.UnixStat(handle.DangerousGetHandle().ToInt32(), stat) != 0) throw NativeIOException("Unable to inspect artifact permissions.");
            var mac = calls.IsMacOS;
            var x64 = calls.Architecture == Architecture.X64;
            if (!mac && !x64 && calls.Architecture != Architecture.Arm64)
                throw new PlatformNotSupportedException("Unsupported native stat layout.");
            var mode = mac ? unchecked((ushort)Marshal.ReadInt16(stat, 4)) : unchecked((uint)Marshal.ReadInt32(stat, x64 ? 24 : 16));
            var owner = unchecked((uint)Marshal.ReadInt32(stat, mac ? 16 : x64 ? 28 : 24));
            var links = mac ? unchecked((ushort)Marshal.ReadInt16(stat, 6)) : x64
                ? unchecked((ulong)Marshal.ReadInt64(stat, 16)) : unchecked((uint)Marshal.ReadInt32(stat, 20));
            if (!directory && links != 1) throw new IOException("Artifact file must have exactly one hard link.");
            if (requirePrivate && (owner != (effectiveUserId ?? ArtifactEffectiveUserId()) || (mode & 0xfff) != (directory ? 0x1c0u : 0x180u)))
                throw CoverageArtifactStoreFailure.PrivateAccess();
        }
        finally { Marshal.FreeHGlobal(stat); }
    }

    /// <summary>Collects bounded immediate Windows names from the retained directory.</summary>
    /// <param name="directory">Retained directory handle.</param>
    /// <param name="maximumEntries">Strict bound, checked before retaining another name.</param>
    /// <param name="enumerate">Optional per-call name observation for iterator failure tests; defaults to the native bound path.</param>
    /// <remarks>Injected names verify the count and iterator decisions; they do not prove Windows enumeration.</remarks>
    internal static IReadOnlyList<string> ReadArtifactWindowsNames(SafeFileHandle directory, int maximumEntries,
        Func<SafeFileHandle, IEnumerable<string>>? enumerate = null)
    {
        var result = new List<string>();
        foreach (var path in enumerate is null ? Directory.EnumerateFileSystemEntries(GetStableDirectoryPath(directory)) : enumerate(directory))
        {
            if (result.Count >= maximumEntries) throw new IOException("Artifact entry inspection limit reached.");
            result.Add(Path.GetFileName(path));
        }
        return result;
    }

    /// <summary>Enumerates a bounded Unix directory with explicit descriptor/stream ownership.</summary>
    /// <param name="directory">Retained source directory handle, never disposed by this method.</param>
    /// <param name="maximumEntries">Strict entry count bound, excluding dot entries.</param>
    /// <param name="nativeCalls">Optional per-call native enumeration operations for failure/cleanup tests.</param>
    /// <remarks>Omission uses the host APIs; fake results exercise orchestration, not the kernel ABI.</remarks>
    internal static IReadOnlyList<string> ReadArtifactUnixNames(SafeFileHandle directory, int maximumEntries,
        CoverageArtifactEnumerationCalls? nativeCalls = null)
    {
        var calls = nativeCalls ?? CoverageArtifactEnumerationCalls.Default;
        var fd = calls.Duplicate(directory.DangerousGetHandle().ToInt32());
        if (fd < 0) throw NativeIOException("Unable to duplicate artifact directory descriptor.");
        if (calls.Seek(fd, 0, UnixSeekStart) < 0) { _ = calls.Close(fd); throw NativeIOException("Unable to rewind artifact directory."); }
        var stream = calls.OpenDirectory(fd);
        if (stream == 0) { _ = calls.Close(fd); throw NativeIOException("Unable to enumerate artifact directory."); }
        try
        {
            var names = new List<string>();
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = calls.ReadDirectory(stream);
                if (entry == 0)
                {
                    if (Marshal.GetLastPInvokeError() != 0) throw NativeIOException("Artifact enumeration failed.");
                    return names;
                }

                var name = Marshal.PtrToStringUTF8(entry + (OperatingSystem.IsMacOS() ? 21 : 19)) ?? throw new IOException("Invalid artifact filename.");
                if (name is "." or "..") continue;
                if (names.Count >= maximumEntries) throw new IOException("Artifact entry inspection limit reached.");
                names.Add(name);
            }
        }
        finally { _ = calls.CloseDirectory(stream); }
    }

    /// <summary>Per-call native Unix enumeration results with production descriptor ownership defaults.</summary>
    /// <remarks>Fake results must describe their own descriptors and pointers; they do not establish filesystem support.</remarks>
    internal sealed class CoverageArtifactEnumerationCalls
    {
        /// <summary>Gets the immutable default call set.</summary>
        internal static CoverageArtifactEnumerationCalls Default { get; } = new();
        /// <summary>Gets a fresh close-on-exec descriptor for the same directory.</summary>
        internal Func<int, int> Duplicate { get; init; } = DuplicateCloseOnExec;
        /// <summary>Gets the rewind operation on the fresh descriptor.</summary>
        internal Func<int, long, int, long> Seek { get; init; } = CoverageRunOutputLease.Seek;
        /// <summary>Gets the operation transferring descriptor ownership to a directory stream.</summary>
        internal Func<int, nint> OpenDirectory { get; init; } = FdOpenDirectory;
        /// <summary>Gets the next native directory entry or zero at end/failure.</summary>
        internal Func<nint, nint> ReadDirectory { get; init; } = CoverageRunOutputLease.ReadDirectory;
        /// <summary>Gets the close operation before stream ownership transfers.</summary>
        internal Func<int, int> Close { get; init; } = CoverageRunOutputLease.Close;
        /// <summary>Gets the operation closing the directory stream and its descriptor.</summary>
        internal Func<nint, int> CloseDirectory { get; init; } = CoverageRunOutputLease.CloseDirectory;
    }

    /// <summary>Atomically duplicates a Unix descriptor with close-on-exec set.</summary>
    /// <param name="descriptor">The retained descriptor to duplicate.</param>
    /// <returns>A close-on-exec duplicate, or a negative value on native failure.</returns>
    private static int DuplicateCloseOnExec(int descriptor)
        => UnixOpenAt(descriptor, ".", UnixReadOnly | UnixCloseOnExec | UnixNoFollow | UnixNonBlocking | UnixDirectory, 0);

    private static void CaptureArtifactSnapshot(SafeFileHandle parent, OutputEntry entry, string prefix, List<TreeEntry> snapshot, int maximumEntries, int depth)
    {
        if (snapshot.Count >= maximumEntries || depth > 64) throw new IOException("Artifact deletion inspection limit reached.");
        var path = string.IsNullOrEmpty(prefix) ? entry.Name : Path.Join(prefix, entry.Name);
        snapshot.Add(new(path, entry.IsDirectory, entry.Identity));
        if (!entry.IsDirectory) return;
        using var child = OperatingSystem.IsWindows() ? OpenWindowsDirectory(Path.Join(GetStableDirectoryPath(parent), entry.Name))
            : OpenAt(parent, entry.Name, directory: true) ?? throw new IOException("Artifact directory disappeared.");
        VerifyEntryIdentity(child, entry, path);
        var names = OperatingSystem.IsWindows() ? ReadArtifactWindowsNames(child, maximumEntries - snapshot.Count + 1)
            : ReadArtifactUnixNames(child, maximumEntries - snapshot.Count + 1);
        foreach (var name in names) CaptureArtifactSnapshot(child, InspectEntry(child, name), path, snapshot, maximumEntries, depth + 1);
    }

    /// <summary>Encodes a Windows handle-relative rename and rejects unsuccessful native publication.</summary>
    /// <param name="source">Source object retained for the whole native call.</param>
    /// <param name="destination">Retained destination directory handle.</param>
    /// <param name="name">An immediate filename already validated by the caller.</param>
    /// <param name="replace">Whether the caller authorized replacing one regular metadata file.</param>
    /// <param name="publish">Optional native call for buffer and failure tests; defaults to NtSetInformationFile.</param>
    /// <param name="translateError">Optional native error translation for tests; defaults to RtlNtStatusToDosError.</param>
    /// <remarks>Tests can inspect the encoded request without executing Windows or attesting to its ABI.</remarks>
    internal static void RenameArtifactWindows(SafeFileHandle source, SafeFileHandle destination, string name, bool replace,
        Func<SafeFileHandle, nint, uint, int>? publish = null, Func<int, uint>? translateError = null)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(name);
        var rootOffset = Marshal.OffsetOf<WindowsFileRenameInformation>(nameof(WindowsFileRenameInformation.RootDirectory)).ToInt32();
        var lengthOffset = Marshal.OffsetOf<WindowsFileRenameInformation>(nameof(WindowsFileRenameInformation.FileNameLength)).ToInt32();
        var nameOffset = Marshal.OffsetOf<WindowsFileRenameInformation>(nameof(WindowsFileRenameInformation.FileName)).ToInt32();
        var size = checked(Marshal.SizeOf<WindowsFileRenameInformation>() + bytes.Length);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteByte(buffer, replace ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(buffer, rootOffset, destination.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
            Marshal.Copy(bytes, 0, buffer + nameOffset, bytes.Length);
            var status = publish is null ? NtSetInformationFile(source, out _, buffer, (uint)size, WindowsFileRenameInformationClass)
                : publish(source, buffer, (uint)size);
            if (status != WindowsStatusSuccess) throw new IOException("Unable to publish artifact entry.", new Win32Exception(unchecked((int)(translateError is null ? RtlNtStatusToDosError(status) : translateError(status)))));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint ArtifactEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "renameatx_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameArtifactMac(int fromDirectory, string from, int toDirectory, string to, uint flags);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameArtifactLinux(int fromDirectory, string from, int toDirectory, string to, uint flags);
}

/// <summary>Describes a bounded, no-follow directory observation; it does not grant deletion authority.</summary>
/// <param name="Name">One immediate component.</param>
/// <param name="IsDirectory">Whether the opened object is a directory.</param>
/// <param name="Identity">Native volume/object identity of the opened entry.</param>
internal sealed record CoverageArtifactDirectoryEntry(string Name, bool IsDirectory, string Identity);

/// <summary>Per-call native observations used to verify file-state decoding without altering global platform state.</summary>
/// <remarks>
/// Defaults call the host APIs. Tests may supply a platform, native results and retained-object identity;
/// callers must not mix synthetic stat bytes with a real filesystem claim. No ownership check is bypassed in production.
/// </remarks>
internal sealed class CoverageArtifactStateCalls
{
    /// <summary>Reads Windows basic timestamps using the native result convention.</summary>
    internal delegate bool ReadWindowsBasic(SafeFileHandle handle, int informationClass,
        out WindowsFileBasicInformation information, uint size);

    /// <summary>Reads Windows size metadata using the native result convention.</summary>
    internal delegate bool ReadWindowsInformation(SafeFileHandle handle, out WindowsByHandleFileInformation information);

    /// <summary>Gets the host-bound default operations.</summary>
    internal static CoverageArtifactStateCalls Default { get; } = new();

    /// <summary>Gets whether Windows file-state decoding is selected.</summary>
    internal bool IsWindows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Gets whether Darwin stat decoding is selected.</summary>
    internal bool IsMacOS { get; init; } = OperatingSystem.IsMacOS();

    /// <summary>Gets whether Linux stat decoding is available.</summary>
    internal bool IsLinux { get; init; } = OperatingSystem.IsLinux();

    /// <summary>Gets the native process architecture used for Linux stat layouts.</summary>
    internal Architecture Architecture { get; init; } = RuntimeInformation.ProcessArchitecture;

    /// <summary>Gets the native Windows timestamp call, including its success result.</summary>
    internal ReadWindowsBasic WindowsBasic { get; init; } = WindowsGetFileBasicInformationByHandleEx;

    /// <summary>Gets the native Windows size and identity metadata call.</summary>
    internal ReadWindowsInformation WindowsInformation { get; init; } = WindowsGetFileInformationByHandle;

    /// <summary>Gets the native Unix stat call; failure leaves the P/Invoke error available.</summary>
    internal Func<int, nint, int> UnixStat { get; init; } = UnixFStat;

    /// <summary>Gets the identity observer for the same retained handle.</summary>
    internal Func<SafeFileHandle, string> Identity { get; init; } = CoverageRunOutputLease.GetArtifactObjectIdentity;
}

/// <summary>One SID-normalized Windows access rule observed through the native ACL API.</summary>
/// <param name="Principal">Security-identifier text returned for the rule's principal.</param>
/// <param name="Rights">The observed unsigned access mask; zero grants no access.</param>
/// <param name="Allows">Whether the native rule allows the observed access mask.</param>
internal sealed record CoverageArtifactAccessRule(string Principal, uint Rights, bool Allows);
