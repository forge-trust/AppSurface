using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Sampled field comparisons only; no cgroup, process, descriptor or DCP identity is created.</summary>
public sealed class LinuxDcpCgroupMetadataPolicyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExpectedNodeTypeAndRootAccessAcceptValidAdjacentMetadata(bool directory)
    {
        var metadata = Facts(directory);
        LinuxDcpCgroupMetadataPolicy.Require(metadata, directory);
        LinuxDcpCgroupMetadataPolicy.Require(metadata with { Length = 0 }, directory);
        LinuxDcpCgroupMetadataPolicy.Require(metadata with { Length = ulong.MaxValue }, directory);
        if (directory)
            LinuxDcpCgroupMetadataPolicy.Require(metadata with { LinkCount = uint.MaxValue }, directory);
    }

    [Theory]
    [InlineData("inode")]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("type")]
    [InlineData("user-read")]
    [InlineData("search")]
    [InlineData("group-write")]
    [InlineData("other-write")]
    [InlineData("setuid")]
    [InlineData("setgid")]
    [InlineData("links")]
    public void DirectoryRejectsEveryIndependentUnsafeField(string field)
    {
        var valid = Facts(true);
        LinuxDcpCgroupMetadataPolicy.Require(valid, true);
        var changed = Change(valid, field);
        AssertRejected(() => LinuxDcpCgroupMetadataPolicy.Require(changed, true));
    }

    [Theory]
    [InlineData("inode")]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("type")]
    [InlineData("user-read")]
    [InlineData("group-write")]
    [InlineData("other-write")]
    [InlineData("setuid")]
    [InlineData("setgid")]
    [InlineData("links-zero")]
    [InlineData("links-many")]
    public void FileRejectsEveryIndependentUnsafeField(string field)
    {
        var valid = Facts(false);
        LinuxDcpCgroupMetadataPolicy.Require(valid, false);
        var changed = Change(valid, field);
        AssertRejected(() => LinuxDcpCgroupMetadataPolicy.Require(changed, false));
    }

    [Theory]
    [InlineData("major")]
    [InlineData("minor")]
    [InlineData("inode")]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("mode")]
    public void RepeatedIdentityRejectsOneChangedComparedField(string field)
    {
        var original = Facts(true);
        Assert.True(LinuxDcpCgroupMetadataPolicy.SameNode(original, original));
        var changed = field switch
        {
            "major" => original with { DeviceMajor = 9 },
            "minor" => original with { DeviceMinor = 9 },
            "inode" => original with { Inode = 43 },
            "owner" => original with { Uid = 1 },
            "group" => original with { Gid = 1 },
            "mode" => original with { Mode = 0x41ed },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Assert.False(LinuxDcpCgroupMetadataPolicy.SameNode(original, changed));
    }

    [Fact]
    public void UnrelatedCgroupLengthAndLinksDoNotChangeItsRepeatedIdentity()
    {
        var original = Facts(true);
        var changed = original with { Length = ulong.MaxValue, LinkCount = 5 };
        LinuxDcpCgroupMetadataPolicy.Require(original, true);
        LinuxDcpCgroupMetadataPolicy.Require(changed, true);
        Assert.True(LinuxDcpCgroupMetadataPolicy.SameNode(original, changed));
        Assert.Equal(original, original with { });
    }

    private static LinuxDcpCgroupMetadata Facts(bool directory) => new(8, 1, 42, 0, 0,
        directory ? (ushort)0x41c0 : (ushort)0x8100, directory ? 2U : 1U, 64);

    private static LinuxDcpCgroupMetadata Change(LinuxDcpCgroupMetadata valid, string field) => field switch
    {
        "inode" => valid with { Inode = 0 },
        "owner" => valid with { Uid = 1 },
        "group" => valid with { Gid = 1 },
        "type" => valid with { Mode = (ushort)((valid.Mode & 0x0fff) | 0xa000) },
        "user-read" => valid with { Mode = (ushort)(valid.Mode & ~0x100) },
        "search" => valid with { Mode = (ushort)(valid.Mode & ~0x40) },
        "group-write" => valid with { Mode = (ushort)(valid.Mode | 0x10) },
        "other-write" => valid with { Mode = (ushort)(valid.Mode | 0x2) },
        "setuid" => valid with { Mode = (ushort)(valid.Mode | 0x800) },
        "setgid" => valid with { Mode = (ushort)(valid.Mode | 0x400) },
        "links" or "links-zero" => valid with { LinkCount = 0 },
        "links-many" => valid with { LinkCount = 2 },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static void AssertRejected(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.StartsWith("ASEVD410: The selected application DCP identity is unavailable or changed.", error.Message);
        Assert.Null(error.InnerException);
    }
}
