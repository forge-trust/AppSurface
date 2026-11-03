using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Data-only named-entry comparison; no descriptor, filesystem observation or workspace is created.</summary>
public sealed class LinuxNamedMetadataComparisonTests
{
    private static LinuxProtectedMetadata Sample(ushort mode = 0x41c0) =>
        new(8, 1, 42, 5, 6, mode, 2, 0, 1, 2, 3, 4);

    [Fact]
    public void DirectoryAcceptsUnrelatedChildListMetadataButRequiresEveryAncestorIdentityAndPolicyField()
    {
        var expected = Sample();
        var adjacentValid = expected with
        {
            LinkCount = 3, Length = 99, ChangeSeconds = 8, ChangeNanoseconds = 9,
            ModifySeconds = 10, ModifyNanoseconds = 11,
        };

        Assert.True(LinuxNamedMetadataComparison.Matches(adjacentValid, expected));

        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { DeviceMajor = 9 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { DeviceMinor = 2 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { Inode = 43 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { Uid = 7 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { Gid = 7 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { Mode = 0x41e0 }, expected));
    }

    [Fact]
    public void NonDirectoryRequiresExactSampledMetadata()
    {
        var expected = Sample(0x8124) with { LinkCount = 1 };

        Assert.True(LinuxNamedMetadataComparison.Matches(expected, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { DeviceMajor = 9 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { DeviceMinor = 2 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { Inode = 43 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { Uid = 7 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { Gid = 7 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { Mode = 0x8125 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { LinkCount = 2 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { Length = 1 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { ChangeSeconds = 8 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { ChangeNanoseconds = 9 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { ModifySeconds = 10 }, expected));
        Assert.False(LinuxNamedMetadataComparison.Matches(expected with { ModifyNanoseconds = 9 }, expected));
    }
}
