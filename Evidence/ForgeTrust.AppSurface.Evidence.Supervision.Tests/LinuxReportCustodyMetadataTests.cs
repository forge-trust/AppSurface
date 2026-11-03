using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached metadata and report rows only; no root, selected handle, producer or custody is fabricated.</summary>
public sealed class LinuxReportCustodyMetadataTests
{
    private static readonly LinuxProtectedMetadata Root = new(8, 1, 42, 12345, 12346, 0x41c0, 2, 0, 1, 2, 3, 4);

    [Theory]
    [InlineData("major")]
    [InlineData("minor")]
    [InlineData("inode")]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("mode")]
    [InlineData("links")]
    [InlineData("length")]
    public void FileSampleRejectsEachChangedOriginalIdentityOrContentBound(string field)
    {
        var file = Root with { Mode = 0x8180, LinkCount = 1, Length = 2 };
        Assert.True(LinuxReportCustodyMetadata.SameFileSample(file, file));
        var changed = field switch
        {
            "major" => file with { DeviceMajor = 9 }, "minor" => file with { DeviceMinor = 2 },
            "inode" => file with { Inode = 43 }, "uid" => file with { Uid = 12344 },
            "gid" => file with { Gid = 12344 }, "mode" => file with { Mode = 0x8100 },
            "links" => file with { LinkCount = 2 }, "length" => file with { Length = 3 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Assert.False(LinuxReportCustodyMetadata.SameFileSample(file, changed));
    }

    [Fact]
    public void FileComparisonKeepsTimestampChecksSeparateFromStableIdentity()
    {
        var file = Root with { Mode = 0x8180, LinkCount = 1, Length = 2 };
        var changed = file with { ChangeSeconds = 2, ChangeNanoseconds = 3, ModifySeconds = 4, ModifyNanoseconds = 5 };
        Assert.True(LinuxReportCustodyMetadata.SameFileSample(file, changed));
        Assert.NotEqual(file, changed);
    }

    [Fact]
    public void InventoryComparisonRequiresCountOrderAndEveryCopiedReportField()
    {
        var first = new LinuxRetainedReportSet.ReportInfo("a/coverage.cobertura.xml", 1, new string('a', 64));
        var second = new LinuxRetainedReportSet.ReportInfo("b/coverage.cobertura.xml", 2, new string('b', 64));
        LinuxRetainedReportSet.ReportInfo[] expected = [first, second];
        Assert.True(LinuxReportCustodyMetadata.SameInventory([], []));
        Assert.True(LinuxReportCustodyMetadata.SameInventory(expected, [first, second]));
        Assert.False(LinuxReportCustodyMetadata.SameInventory(expected, [first]));
        Assert.False(LinuxReportCustodyMetadata.SameInventory(expected, [second, first]));
        Assert.False(LinuxReportCustodyMetadata.SameInventory(expected, [first, second with { RelativePath = "c/coverage.cobertura.xml" }]));
        Assert.False(LinuxReportCustodyMetadata.SameInventory(expected, [first, second with { Length = 3 }]));
        Assert.False(LinuxReportCustodyMetadata.SameInventory(expected, [first, second with { Sha256 = new string('c', 64) }]));
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("inode")]
    [InlineData("type")]
    [InlineData("owner-search")]
    [InlineData("group-write")]
    [InlineData("mode")]
    [InlineData("links")]
    public void ResultsParentRejectsEachUnsafeFieldWithTheOriginalAncestorGuardFirst(string field)
    {
        var valid = Root with { Uid = 0, Gid = 0, Mode = 0x41c9 };
        LinuxReportCustodyMetadata.RequireResultsParent(valid);
        var changed = field switch
        {
            "uid" => valid with { Uid = 1 }, "gid" => valid with { Gid = 1 },
            "inode" => valid with { Inode = 0 }, "type" => valid with { Mode = 0x81c9 },
            "owner-search" => valid with { Mode = 0x4189 }, "group-write" => valid with { Mode = 0x41d9 },
            "mode" => valid with { Mode = 0x41c8 }, "links" => valid with { LinkCount = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var error = Record.Exception(() => LinuxReportCustodyMetadata.RequireResultsParent(changed));
        if (field is "uid" or "inode" or "type" or "owner-search" or "group-write")
            Assert.Equal("ASEVD402", Assert.IsType<EvidenceAdmissionException>(error).Code);
        else
            Assert.Equal("The producer report set is unavailable or changed.", Assert.IsType<IOException>(error).Message);
        Assert.NotNull(error);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("12345", error.Message);
    }

    [Fact]
    public void SelectedRootKeepsThePriorIdentityValidationAndRequiresTheExactPrivateMode()
    {
        LinuxReportCustodyMetadata.RequireSelectedRoot(Root, Root, Root.Uid, Root.Gid);
        Assert.Throws<IOException>(() => LinuxReportCustodyMetadata.RequireSelectedRoot(
            Root with { Inode = 43 }, Root, Root.Uid, Root.Gid));
        var readable = Root with { Mode = 0x41c4 };
        // This mode still passes the retained-set guard, making the stricter wrapper meaningful.
        LinuxRetainedReportSet.RequireRootIdentity(readable, readable, readable.Uid, readable.Gid);
        var error = Assert.Throws<IOException>(() => LinuxReportCustodyMetadata.RequireSelectedRoot(
            readable, readable, readable.Uid, readable.Gid));
        Assert.Equal("The producer report set is unavailable or changed.", error.Message);
        Assert.Null(error.InnerException);
    }
}
