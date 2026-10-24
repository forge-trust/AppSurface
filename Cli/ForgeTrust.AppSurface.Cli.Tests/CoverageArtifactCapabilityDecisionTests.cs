using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactCapabilityDecisionTests
{
    [Fact]
    public void ReadLinuxMountType_ShouldChooseLongestDecodedMountpoint()
    {
        string[] mountInfoLines =
        [
            "malformed row",
            "1 0 8:1 / / rw,relatime - ext4 /dev/root rw",
            "10 1 0:99 / /mnt rw,nosuid - tmpfs tmpfs rw",
            "11 10 8:2 / /mnt/data\\040cache rw - xfs /dev/sda rw"
        ];

        var fileSystem = CoverageArtifactCapability.ReadLinuxMountType(
            "/mnt/data cache/artifacts/run",
            mountInfoLines);

        Assert.Equal("xfs", fileSystem);
    }

    [Fact]
    public void ReadLinuxMountType_ShouldMatchMountsOnComponentBoundaries()
    {
        string[] mountInfoLines =
        [
            "1 0 8:1 / / rw,relatime - ext4 /dev/root rw",
            "10 1 8:2 / /mnt/data rw - nfs server:/data rw"
        ];

        var fileSystem = CoverageArtifactCapability.ReadLinuxMountType("/mnt/database/artifacts", mountInfoLines);

        Assert.Equal("ext4", fileSystem);
    }

    [Fact]
    public void ReadLinuxMountType_ShouldFailWhenAllRecordsAreMalformedOrUnrelated()
    {
        string[] mountInfoLines =
        [
            "malformed row",
            "7 3 8:1 / /mnt rw - ext4 /dev/sda",
            "8 3 8:1 / relative rw - ext4 /dev/sdb rw"
        ];

        Assert.Throws<IOException>(() => CoverageArtifactCapability.ReadLinuxMountType("/mnt/artifacts", mountInfoLines));
    }

    [Fact]
    public void ReadFileSystem_ShouldObserveWindowsVolumeRootAndClassifyFixedDrive()
    {
        using var tree = PrivateDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(tree.Path, createMissing: false, requirePrivate: true)!;
        string? observedVolumeRoot = null;

        var observation = CoverageArtifactCapability.ReadFileSystem(
            root,
            "windows",
            volumeRoot =>
            {
                observedVolumeRoot = volumeRoot;
                return new CoverageArtifactVolumeObservation(true, "NTFS", DriveType.Fixed);
            },
            (_, _) => throw new InvalidOperationException("The Unix observer should not run in the Windows lane."),
            _ => throw new InvalidOperationException("The Linux mount reader should not run in the Windows lane."));

        Assert.Equal(Path.GetPathRoot(Path.GetFullPath(tree.Path)), observedVolumeRoot);
        Assert.Equal(("NTFS", true), observation);
    }

    [Fact]
    public void ReadFileSystem_ShouldRejectAnUnavailableWindowsVolumeBeforeUsingItsFormat()
    {
        using var tree = PrivateDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(tree.Path, createMissing: false, requirePrivate: true)!;
        var observation = new CoverageArtifactVolumeObservation(false, string.Empty, DriveType.Unknown);

        var error = Assert.Throws<IOException>(() => CoverageArtifactCapability.ReadFileSystem(
            root,
            "windows",
            _ => observation,
            (_, _) => throw new InvalidOperationException("The Unix observer should not run in the Windows lane."),
            _ => throw new InvalidOperationException("The Linux mount reader should not run in the Windows lane.")));

        Assert.Equal("The artifact volume is unavailable.", error.Message);
    }

    [Fact]
    public void ReadFileSystem_ShouldClassifyNonFixedWindowsVolumesAsNonLocal()
    {
        using var tree = PrivateDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(tree.Path, createMissing: false, requirePrivate: true)!;

        var observation = CoverageArtifactCapability.ReadFileSystem(
            root,
            "windows",
            _ => new CoverageArtifactVolumeObservation(true, "NTFS", DriveType.Network),
            (_, _) => throw new InvalidOperationException("The Unix observer should not run in the Windows lane."),
            _ => throw new InvalidOperationException("The Linux mount reader should not run in the Windows lane."));

        Assert.Equal(("NTFS", false), observation);
    }

    [Fact]
    public void ReadFileSystem_ShouldPropagateWindowsVolumeObservationFailures()
    {
        using var tree = PrivateDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(tree.Path, createMissing: false, requirePrivate: true)!;

        var error = Assert.Throws<IOException>(() => CoverageArtifactCapability.ReadFileSystem(
            root,
            "windows",
            _ => throw new IOException("volume observation failed"),
            (_, _) => throw new InvalidOperationException("The Unix observer should not run in the Windows lane."),
            _ => throw new InvalidOperationException("The Linux mount reader should not run in the Windows lane.")));

        Assert.Equal("volume observation failed", error.Message);
    }

    [Fact]
    public void ReadFileSystem_ShouldStopAfterAUnixStatFailure()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tree = PrivateDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(tree.Path, createMissing: false, requirePrivate: true)!;
        var mountInfoRead = false;

        var error = Assert.Throws<IOException>(() => CoverageArtifactCapability.ReadFileSystem(
            root,
            "linux",
            _ => throw new InvalidOperationException("The Windows observer should not run in the Linux lane."),
            (_, _) =>
            {
                Marshal.SetLastPInvokeError(5);
                return -1;
            },
            _ =>
            {
                mountInfoRead = true;
                return [];
            }));

        Assert.Equal("Unable to inspect the artifact filesystem.", error.Message);
        Assert.IsType<System.ComponentModel.Win32Exception>(error.InnerException);
        Assert.False(mountInfoRead);
    }

    [Fact]
    public void ReadFileSystem_ShouldRejectUnknownPlatformLane()
    {
        using var tree = PrivateDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(tree.Path, createMissing: false, requirePrivate: true)!;

        Assert.Throws<PlatformNotSupportedException>(() => CoverageArtifactCapability.ReadFileSystem(
            root,
            "other",
            _ => throw new InvalidOperationException("No observation should run for an unsupported platform."),
            (_, _) => throw new InvalidOperationException("No observation should run for an unsupported platform."),
            _ => throw new InvalidOperationException("No observation should run for an unsupported platform.")));
    }

    private sealed class PrivateDirectory : IDisposable
    {
        private readonly CoverageRunOutputLease _lease;

        private PrivateDirectory(string path, CoverageRunOutputLease lease)
        {
            Path = path;
            _lease = lease;
        }

        internal string Path { get; }

        internal static PrivateDirectory Create()
        {
            var path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                "appsurface-capability-decision-" + Guid.NewGuid().ToString("N"));
            var lease = CoverageRunOutputLease.AcquireArtifactDirectory(path, createMissing: true, requirePrivate: true)!;
            return new PrivateDirectory(path, lease);
        }

        public void Dispose()
        {
            _lease.Dispose();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
