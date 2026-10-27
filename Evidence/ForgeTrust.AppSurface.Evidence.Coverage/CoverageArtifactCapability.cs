using System.Runtime.InteropServices;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Describes the native filesystem guarantees verified for an artifact root.</summary>
internal sealed partial record CoverageArtifactCapability(
    string FileSystem,
    string RootIdentity,
    bool SupportsNativeLocking,
    bool SupportsNoFollowTraversal,
    bool SupportsAtomicNoReplaceRename)
{
    private const uint LinuxExtSuperMagic = 0x0000ef53;

    /// <summary>Verifies that an existing private root supports the optional artifact operations.</summary>
    /// <param name="root">An existing private directory on a supported local filesystem.</param>
    /// <returns>The filesystem and physical identity observed while the native probes succeeded.</returns>
    /// <remarks>
    /// The probe uses only a uniquely named, manager-owned child directory and removes that exact
    /// child through the retained private-root lease. It never invokes another process.
    /// </remarks>
    internal static CoverageArtifactCapability Verify(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullPath = Path.GetFullPath(root);
        using var privateRoot = CoverageRunOutputLease.AcquireArtifactDirectory(fullPath, createMissing: false, requirePrivate: true)
            ?? throw new DirectoryNotFoundException("The artifact capability root does not exist.");
        var (fileSystem, isLocal) = ReadFileSystem(privateRoot);
        var platform = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "other";
        if (!IsSupportedFileSystem(platform, fileSystem, isLocal))
            throw new IOException($"Artifact storage requires a local NTFS, APFS, or ext4 filesystem; found '{fileSystem}'.");

        privateRoot.VerifyArtifactBinding();
        var rootIdentity = privateRoot.ArtifactDirectoryIdentity;
        var scratchName = ".appsurface-capability-" + Guid.NewGuid().ToString("N");
        using var scratch = privateRoot.CreateArtifactDirectory(scratchName);
        var scratchIdentity = scratch.ArtifactDirectoryIdentity;
        try
        {
            // Reopening the manager-created directory through the regular retained-path boundary
            // exercises its no-follow traversal and confirms that the path still names this object.
            using (var reopened = CoverageRunOutputLease.AcquireArtifactDirectory(
                       scratch.ArtifactDirectoryPath, createMissing: false, requirePrivate: true)
                   ?? throw new IOException("The capability scratch directory disappeared."))
            {
                if (!string.Equals(reopened.ArtifactDirectoryIdentity, scratchIdentity, StringComparison.Ordinal))
                    throw new IOException("The capability scratch directory identity changed.");
            }

            VerifyLocking(scratch.ArtifactDirectoryPath);
            VerifyAtomicNoReplaceRename(scratch);
            scratch.VerifyArtifactBinding();
            privateRoot.VerifyArtifactBinding();
            if (!string.Equals(privateRoot.ArtifactDirectoryIdentity, rootIdentity, StringComparison.Ordinal))
                throw new IOException("The artifact capability root identity changed during probing.");
            return new CoverageArtifactCapability(fileSystem, rootIdentity, true, true, true);
        }
        finally
        {
            scratch.Dispose();
            privateRoot.DeleteArtifactEntry(scratchName, maximumEntries: 16, expectedIdentity: scratchIdentity);
            privateRoot.VerifyArtifactBinding();
        }
    }

    /// <summary>Pure filesystem/platform classification seam used by the supported-lane tests.</summary>
    internal static bool IsSupportedFileSystem(string platform, string? fileSystem, bool isLocalVolume)
    {
        if (!isLocalVolume || string.IsNullOrWhiteSpace(fileSystem)) return false;
        return platform.ToLowerInvariant() switch
        {
            "windows" => string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase),
            "macos" => string.Equals(fileSystem, "apfs", StringComparison.OrdinalIgnoreCase),
            "linux" => string.Equals(fileSystem, "ext4", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static void VerifyLocking(string scratchPath)
    {
        using var held = CoverageArtifactOperationLease.Acquire(scratchPath, exclusive: true);
        var rejected = false;
        try
        {
            using var contender = CoverageArtifactOperationLease.Acquire(scratchPath, exclusive: true);
        }
        catch (CoverageExecutionException)
        {
            rejected = true;
        }
        if (!rejected) throw new IOException("The native exclusive artifact lock admitted a second writer.");
        held.VerifyBinding();
    }

    private static void VerifyAtomicNoReplaceRename(CoverageRunOutputLease scratch)
    {
        var source = scratch.CreateArtifactDirectory("rename-source");
        var occupied = scratch.CreateArtifactDirectory("rename-occupied");
        var sourceIdentity = source.ArtifactDirectoryIdentity;
        var occupiedIdentity = occupied.ArtifactDirectoryIdentity;
        source.Dispose();
        occupied.Dispose();
        var rejected = false;
        try
        {
            scratch.MoveArtifactEntry("rename-source", scratch, "rename-occupied");
        }
        catch (IOException)
        {
            rejected = true;
        }
        if (!rejected) throw new IOException("The native rename replaced an existing destination.");

        var entries = scratch.EnumerateArtifactEntries(16).ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        if (!string.Equals(entries["rename-source"].Identity, sourceIdentity, StringComparison.Ordinal)
            || !string.Equals(entries["rename-occupied"].Identity, occupiedIdentity, StringComparison.Ordinal))
            throw new IOException("The failed no-replace rename changed an entry identity.");

        scratch.MoveArtifactEntry("rename-source", scratch, "rename-published", expectedSourceIdentity: sourceIdentity);
        entries = scratch.EnumerateArtifactEntries(16).ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        if (!string.Equals(entries["rename-published"].Identity, sourceIdentity, StringComparison.Ordinal))
            throw new IOException("The native atomic rename changed the source identity.");
    }

    private static (string FileSystem, bool IsLocal) ReadFileSystem(CoverageRunOutputLease root)
    {
        var platform = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "other";
        return ReadFileSystem(root, platform, ObserveWindowsVolume, InvokeCurrentUnixFStatFs,
            _ => File.ReadLines("/proc/self/mountinfo"));
    }

    private static int InvokeCurrentUnixFStatFs(int descriptor, nint buffer) => InvokeUnixFStatFs(
        OperatingSystem.IsMacOS() ? "macos" : "unix", RuntimeInformation.ProcessArchitecture,
        descriptor, buffer, UnixFStatFs, DarwinX64FStatFs);

    /// <summary>Dispatches fstatfs to the symbol matching the native filesystem-stat structure ABI.</summary>
    /// <remarks>Intel Darwin uses <c>fstatfs$INODE64</c>; arm64 Darwin and Linux use <c>fstatfs</c>.</remarks>
    internal static int InvokeUnixFStatFs(string platform, Architecture architecture, int descriptor, nint buffer,
        Func<int, nint, int> unixFStatFs, Func<int, nint, int> darwinX64FStatFs)
    {
        ArgumentNullException.ThrowIfNull(unixFStatFs);
        ArgumentNullException.ThrowIfNull(darwinX64FStatFs);
        return platform.Equals("macos", StringComparison.OrdinalIgnoreCase) && architecture == Architecture.X64
            ? darwinX64FStatFs(descriptor, buffer)
            : unixFStatFs(descriptor, buffer);
    }

    /// <summary>
    /// Reads filesystem metadata through narrow platform observations while keeping the retained
    /// directory handle and all path validation under the normal capability probe.
    /// </summary>
    /// <param name="root">The already-validated retained private-root lease.</param>
    /// <param name="platform">The platform lane: <c>windows</c>, <c>macos</c>, or <c>linux</c>.</param>
    /// <param name="windowsVolumeObservation">
    /// Reads readiness, filesystem format, and drive type for a Windows volume root. It is called
    /// only in the Windows lane and should avoid reading the format when the volume is not ready.
    /// </param>
    /// <param name="unixFStatFs">Runs <c>fstatfs</c> against the retained directory descriptor and supplied buffer.</param>
    /// <param name="linuxMountInfo">Returns Linux mountinfo lines for the retained directory path.</param>
    /// <returns>The observed filesystem name and whether the volume is local.</returns>
    /// <remarks>
    /// Production supplies <see cref="DriveInfo"/>, the native <c>fstatfs</c> call, and
    /// <see cref="File.ReadLines(string)"/>. Tests may inject observations to verify result and
    /// failure orchestration; injected values do not establish native filesystem guarantees. The
    /// platform argument exists to exercise each decision lane on any test host. All callers still
    /// need a real retained lease, and the public capability probe continues its no-follow path,
    /// identity, locking, and rename checks after this observation succeeds.
    /// </remarks>
    internal static (string FileSystem, bool IsLocal) ReadFileSystem(
        CoverageRunOutputLease root,
        string platform,
        Func<string, CoverageArtifactVolumeObservation> windowsVolumeObservation,
        Func<int, nint, int> unixFStatFs,
        Func<string, IEnumerable<string>> linuxMountInfo)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentNullException.ThrowIfNull(windowsVolumeObservation);
        ArgumentNullException.ThrowIfNull(unixFStatFs);
        ArgumentNullException.ThrowIfNull(linuxMountInfo);

        switch (platform.ToLowerInvariant())
        {
            case "windows":
                {
                    var volumeRoot = Path.GetPathRoot(root.ArtifactDirectoryPath)
                        ?? throw new IOException("Unable to identify the artifact volume root.");
                    var volume = windowsVolumeObservation(volumeRoot);
                    if (!volume.IsReady) throw new IOException("The artifact volume is unavailable.");
                    return (volume.FileSystem, volume.DriveType == DriveType.Fixed);
                }
            case "macos":
            case "linux":
                {
                    var buffer = Marshal.AllocHGlobal(4096);
                    try
                    {
                        for (var index = 0; index < 4096; index++) Marshal.WriteByte(buffer, index, 0);
                        var descriptor = root.ArtifactDirectoryHandle.DangerousGetHandle().ToInt32();
                        var result = unixFStatFs(descriptor, buffer);
                        if (result != 0)
                            throw new IOException("Unable to inspect the artifact filesystem.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
                        if (platform.Equals("macos", StringComparison.OrdinalIgnoreCase))
                        {
                            // Darwin struct statfs includes two MAXPATHLEN mount names; reserve the full ABI size.
                            var name = Marshal.PtrToStringUTF8(buffer + 72, 16)?.TrimEnd('\0') ?? string.Empty;
                            const int mountLocalFlag = 0x00001000;
                            return (name, (Marshal.ReadInt32(buffer, 64) & mountLocalFlag) != 0);
                        }

                        var magic = unchecked((uint)Marshal.ReadInt32(buffer));
                        var mountType = ReadLinuxMountType(root.ArtifactDirectoryPath, linuxMountInfo(root.ArtifactDirectoryPath));
                        return (magic == LinuxExtSuperMagic ? mountType : $"unknown-0x{magic:x8}", true);
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            default:
                throw new PlatformNotSupportedException("Artifact capability checks support Windows, macOS, and Linux.");
        }
    }

    private static CoverageArtifactVolumeObservation ObserveWindowsVolume(string volumeRoot)
    {
        var drive = new DriveInfo(volumeRoot);
        if (!drive.IsReady) return new CoverageArtifactVolumeObservation(false, string.Empty, drive.DriveType);
        return new CoverageArtifactVolumeObservation(true, drive.DriveFormat, drive.DriveType);
    }

    /// <summary>Finds the filesystem type of the longest Linux mountpoint containing a path.</summary>
    /// <param name="path">An absolute Linux path, normally taken from the retained directory lease.</param>
    /// <param name="mountInfoLines">Lines from <c>/proc/self/mountinfo</c>; ordering does not affect the result.</param>
    /// <returns>The filesystem type from the matching mountinfo record.</returns>
    /// <remarks>
    /// Structurally malformed records and records with non-absolute mountpoints are skipped. Mount
    /// paths are decoded using mountinfo octal escapes and matched on path-component boundaries;
    /// if no valid record contains <paramref name="path"/>, the parser throws <see cref="IOException"/>.
    /// Callers should pass the retained absolute Linux path and the complete mountinfo snapshot.
    /// </remarks>
    internal static string ReadLinuxMountType(string path, IEnumerable<string> mountInfoLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(mountInfoLines);
        string? bestMount = null;
        string? bestType = null;
        foreach (var line in mountInfoLines)
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;
            var left = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var right = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (left.Length < 6 || right.Length < 3) continue;
            var mount = DecodeMountField(left[4]);
            if (!mount.StartsWith("/", StringComparison.Ordinal) || !IsLinuxPathWithinMount(path, mount)) continue;
            if (bestMount is null || mount.Length > bestMount.Length)
            {
                bestMount = mount;
                bestType = right[0];
            }
        }
        return bestType ?? throw new IOException("Unable to identify the artifact mount filesystem.");
    }

    private static bool IsLinuxPathWithinMount(string path, string mount)
    {
        if (mount == "/") return path.StartsWith("/", StringComparison.Ordinal);
        return path.Equals(mount, StringComparison.Ordinal)
            || path.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    private static string DecodeMountField(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 3 < value.Length
                && IsOctal(value[index + 1]) && IsOctal(value[index + 2]) && IsOctal(value[index + 3]))
            {
                result.Append((char)((value[index + 1] - '0') * 64 + (value[index + 2] - '0') * 8 + value[index + 3] - '0'));
                index += 3;
            }
            else result.Append(value[index]);
        }
        return result.ToString();
    }

    private static bool IsOctal(char value) => value is >= '0' and <= '7';

    [LibraryImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static partial int UnixFStatFs(int descriptor, nint buffer);

    [LibraryImport("libc", EntryPoint = "fstatfs$INODE64", SetLastError = true)]
    private static partial int DarwinX64FStatFs(int descriptor, nint buffer);

}

/// <summary>Windows volume metadata returned by a capability filesystem observation.</summary>
/// <param name="IsReady">Whether the volume can currently be inspected.</param>
/// <param name="FileSystem">The reported volume format; ignored when <paramref name="IsReady"/> is false.</param>
/// <param name="DriveType">The reported drive type used to classify local storage.</param>
internal readonly record struct CoverageArtifactVolumeObservation(bool IsReady, string FileSystem, DriveType DriveType);

/// <summary>Native file identity and mutation fingerprint captured from one open handle.</summary>
/// <param name="Identity">Stable native volume/object identity.</param>
/// <param name="Length">Native file length in bytes.</param>
/// <param name="ModifiedSeconds">Last-write timestamp seconds or Windows FILETIME seconds.</param>
/// <param name="ModifiedSubseconds">Last-write subsecond component in native units.</param>
/// <param name="ChangedSeconds">Change timestamp seconds or Windows FILETIME seconds.</param>
/// <param name="ChangedSubseconds">Change subsecond component in native units.</param>
internal readonly record struct CoverageArtifactObjectState(
    string Identity,
    long Length,
    long ModifiedSeconds,
    long ModifiedSubseconds,
    long ChangedSeconds,
    long ChangedSubseconds);
