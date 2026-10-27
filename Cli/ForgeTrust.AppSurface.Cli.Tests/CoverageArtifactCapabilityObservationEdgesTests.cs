using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactCapabilityObservationEdgesTests
{
    [Theory]
    [InlineData(0x0000ef53u, "ext4")]
    [InlineData(0x00001234u, "unknown-0x00001234")]
    public void LinuxFsMagicAndMountTypeRemainDistinctObservations(uint magic, string expected)
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        var mountRead = 0;
        var observed = CoverageArtifactCapability.ReadFileSystem(root, "linux",
            _ => throw new InvalidOperationException(),
            (_, buffer) => { Marshal.WriteInt32(buffer, unchecked((int)magic)); return 0; },
            path =>
            {
                mountRead++;
                Assert.Equal(root.ArtifactDirectoryPath, path);
                return ["1 2 0:1 / / rw - ext4 /dev/root rw"];
            });
        Assert.Equal((expected, true), observed);
        Assert.Equal(1, mountRead);
    }

    [Theory]
    [InlineData("/", "ext4")]
    [InlineData("/mnt", "ext4")]
    [InlineData("/mnt/with space", "apfs")]
    public void LinuxParserPreservesEscapesAndExactMountBoundaries(string path, string expected)
    {
        var observed = CoverageArtifactCapability.ReadLinuxMountType(path,
            ["1 2 0:1 / / rw - ext4 /dev/root rw", "3 2 0:2 / /mnt/with\\040space rw - apfs device rw"]);
        Assert.Equal(expected, observed);
    }

    [Fact]
    public void InvalidCapabilityRootAndUnavailableVolumeNeverClaimSupport()
    {
        Assert.Throws<ArgumentException>(() => CoverageArtifactCapability.Verify(" "));
        using var temp = TestDirectory.Create();
        Assert.Throws<DirectoryNotFoundException>(() => CoverageArtifactCapability.Verify(Path.Join(temp.Path, "missing")));
        Assert.False(CoverageArtifactCapability.IsSupportedFileSystem("linux", null, true));
    }
}
