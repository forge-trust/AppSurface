using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached rename-identity comparisons; these controls never acquire or consume an owner marker.</summary>
public sealed class LinuxOwnerGuardDataTests
{
    [Theory]
    [InlineData("device-major")]
    [InlineData("device-minor")]
    [InlineData("inode")]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("mode")]
    [InlineData("links")]
    [InlineData("length")]
    [InlineData("modify-seconds")]
    [InlineData("modify-nanoseconds")]
    public void RenameComparisonRejectsEachChangedIdentityOrContentMetadataField(string field)
    {
        var before = Marker();
        Assert.True(LinuxOwnerGuard.SameMarkerObject(before, before));
        var changed = field switch
        {
            "device-major" => before with { DeviceMajor = before.DeviceMajor + 1 },
            "device-minor" => before with { DeviceMinor = before.DeviceMinor + 1 },
            "inode" => before with { Inode = before.Inode + 1 },
            "uid" => before with { Uid = 123 },
            "gid" => before with { Gid = 124 },
            "mode" => before with { Mode = 0x8184 },
            "links" => before with { LinkCount = 2 },
            "length" => before with { Length = 34 },
            "modify-seconds" => before with { ModifySeconds = before.ModifySeconds + 1 },
            "modify-nanoseconds" => before with { ModifyNanoseconds = before.ModifyNanoseconds + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Assert.False(LinuxOwnerGuard.SameMarkerObject(before, changed));
        Assert.False(LinuxOwnerGuard.SameMarkerObject(changed, before));
    }

    [Fact]
    public void RenameComparisonAllowsOnlyTheExpectedCtimeChange()
    {
        var before = Marker();
        var after = before with { ChangeSeconds = 40, ChangeNanoseconds = 900 };
        LinuxOwnerGuard.RequireMarker(before);
        LinuxOwnerGuard.RequireMarker(after);
        Assert.True(LinuxOwnerGuard.SameMarkerObject(before, after));
    }

    private static LinuxProtectedMetadata Marker() => new(1, 2, 3, 0, 0, 0x8180, 1, 33, 10, 2, 10, 3);
}
