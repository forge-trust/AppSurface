using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactCapabilityTests
{
    [Theory]
    [InlineData("windows", "NTFS", true, true)]
    [InlineData("windows", "NTFS", false, false)]
    [InlineData("windows", "ReFS", true, false)]
    [InlineData("macos", "apfs", true, true)]
    [InlineData("macos", "hfs", true, false)]
    [InlineData("linux", "ext4", true, true)]
    [InlineData("linux", "overlay", true, false)]
    [InlineData("linux", "nfs", false, false)]
    [InlineData("other", "apfs", true, false)]
    [InlineData("linux", "", true, false)]
    public void IsSupportedFileSystem_ShouldRequireSupportedLocalPlatformAndType(
        string platform,
        string fileSystem,
        bool local,
        bool expected)
    {
        Assert.Equal(expected, CoverageArtifactCapability.IsSupportedFileSystem(platform, fileSystem, local));
    }

    [Fact]
    public void Verify_ShouldProbePrivateLocalRootAndRemoveOnlyItsScratchEntry()
    {
        using var tree = PrivateDirectory.Create();

        var capability = CoverageArtifactCapability.Verify(tree.Path);

        Assert.False(string.IsNullOrWhiteSpace(capability.FileSystem));
        Assert.False(string.IsNullOrWhiteSpace(capability.RootIdentity));
        Assert.True(capability.SupportsNativeLocking);
        Assert.True(capability.SupportsNoFollowTraversal);
        Assert.True(capability.SupportsAtomicNoReplaceRename);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tree.Path));
    }

    [Fact]
    public void Verify_ShouldRejectRootWithNonPrivateUnixPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tree = PrivateDirectory.Create();
        var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(tree.Path, privateMode | UnixFileMode.OtherRead);
        try
        {
            Assert.Throws<IOException>(() => CoverageArtifactCapability.Verify(tree.Path));
        }
        finally
        {
            File.SetUnixFileMode(tree.Path, privateMode);
        }
    }

    [Fact]
    public void AcquireArtifactDirectory_ShouldRejectSymlinkRootWithoutFollowingIt()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tree = PrivateDirectory.Create();
        var target = Path.Join(tree.Path, "target");
        var link = Path.Join(tree.Path, "link");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);

        try
        {
            Assert.Throws<IOException>(() => CoverageRunOutputLease.AcquireArtifactDirectory(link, false, true));
            Assert.True(Directory.Exists(target));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void GetArtifactObjectState_ShouldDetectSameLengthInPlaceMutation()
    {
        using var tree = PrivateDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(tree.Path, false, true)!;
        using var file = directory.OpenArtifactFile("mutable.bin", createNew: true);
        file.Write([1, 2, 3, 4]);
        file.Flush(flushToDisk: true);
        var before = CoverageRunOutputLease.GetArtifactObjectState(file.SafeFileHandle);

        file.Position = 0;
        file.Write([5, 6, 7, 8]);
        file.Flush(flushToDisk: true);
        var after = CoverageRunOutputLease.GetArtifactObjectState(file.SafeFileHandle);

        Assert.Equal(before.Identity, after.Identity);
        Assert.Equal(before.Length, after.Length);
        Assert.NotEqual(
            (before.ModifiedSeconds, before.ModifiedSubseconds, before.ChangedSeconds, before.ChangedSubseconds),
            (after.ModifiedSeconds, after.ModifiedSubseconds, after.ChangedSeconds, after.ChangedSubseconds));
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
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "appsurface-capability-" + Guid.NewGuid().ToString("N"));
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
