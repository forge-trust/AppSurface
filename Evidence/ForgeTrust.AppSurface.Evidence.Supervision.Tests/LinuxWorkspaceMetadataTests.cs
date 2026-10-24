using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached metadata policies only: these values never create native nodes or custody.</summary>
public sealed class LinuxWorkspaceMetadataTests
{
    private static LinuxProtectedMetadata Sample(uint uid = 0, uint gid = 0, ushort mode = 0x8124,
        uint links = 1, ulong length = 4) => new(8, 1, 42, uid, gid, mode, links, length, 1, 2, 3, 4);

    [Theory]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("inode")]
    [InlineData("type")]
    [InlineData("permissions")]
    [InlineData("special")]
    public void DirectoryRequiresExactDeclaredIdsTypeAndMode(string change)
    {
        var valid = Sample(5, 6, 0x41c0, 2, 0);
        Assert.True(LinuxWorkspaceMetadata.MatchesDirectory(valid, 5, 6, 0x1c0));
        var bad = change switch
        {
            "uid" => valid with { Uid = 7 }, "gid" => valid with { Gid = 7 }, "inode" => valid with { Inode = 0 },
            "type" => valid with { Mode = 0x81c0 }, "permissions" => valid with { Mode = 0x4140 },
            "special" => valid with { Mode = 0x49c0 }, _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.False(LinuxWorkspaceMetadata.MatchesDirectory(bad, 5, 6, 0x1c0));
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("inode")]
    [InlineData("directory")]
    [InlineData("fifo")]
    [InlineData("links-zero")]
    [InlineData("links-two")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("permissions")]
    public void ApplicationFilePreservesSingleLinkExactLengthAndDeclaredMode(string change)
    {
        var valid = Sample();
        Assert.True(LinuxWorkspaceMetadata.MatchesApplicationFile(valid, 4, 0x124));
        var bad = change switch
        {
            "uid" => valid with { Uid = 7 }, "gid" => valid with { Gid = 7 }, "inode" => valid with { Inode = 0 },
            "directory" => valid with { Mode = 0x4124 }, "fifo" => valid with { Mode = 0x1124 },
            "links-zero" => valid with { LinkCount = 0 }, "links-two" => valid with { LinkCount = 2 },
            "short" => valid with { Length = 3 }, "long" => valid with { Length = 5 },
            "permissions" => valid with { Mode = 0x81a4 }, _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.False(LinuxWorkspaceMetadata.MatchesApplicationFile(bad, 4, 0x124));
    }

    [Fact]
    public void ApplicationFileRejectsNegativeDeclaredLengthWithOtherwiseExactMetadata()
    {
        var valid = Sample(mode: 0x816d, length: 4);

        Assert.True(LinuxWorkspaceMetadata.MatchesApplicationFile(valid, 4, 0x16d));
        Assert.False(LinuxWorkspaceMetadata.MatchesApplicationFile(valid, -1, 0x16d));
    }

    public static IEnumerable<object[]> MutableBoundaries()
    {
        foreach (var directory in new[] { true, false })
        foreach (var sealedTree in new[] { true, false })
        {
            foreach (var change in new[] { "uid", "gid", "inode", "type", "setuid", "setgid", "sticky", "group-write", "other-write" }) yield return [directory, sealedTree, change];
            if (sealedTree) yield return [directory, sealedTree, "sealed-permissions"];
            if (!directory) foreach (var change in new[] { "links-zero", "links-two", "oversize" }) yield return [directory, sealedTree, change];
        }
    }

    [Theory]
    [MemberData(nameof(MutableBoundaries))]
    public void MutableAndSealedProducerPoliciesRetainEachOwnerTypeAndModeBoundary(bool directory, bool sealedTree, string change)
    {
        var permissions = sealedTree ? (directory ? 0x1c0 : 0x180) : (directory ? 0x1ed : 0x124);
        var valid = Sample(5, 6, (ushort)((directory ? 0x4000 : 0x8000) | permissions), directory ? 2u : 1u, 0);
        Assert.True(LinuxWorkspaceMetadata.MatchesMutableProducer(valid, directory, 5, 6, sealedTree));
        var bad = change switch
        {
            "uid" => valid with { Uid = 7 }, "gid" => valid with { Gid = 7 }, "inode" => valid with { Inode = 0 },
            "type" => valid with { Mode = (ushort)((directory ? 0x8000 : 0x4000) | permissions) },
            "setuid" => valid with { Mode = (ushort)(valid.Mode | 0x800) }, "setgid" => valid with { Mode = (ushort)(valid.Mode | 0x400) },
            "sticky" => valid with { Mode = (ushort)(valid.Mode | 0x200) }, "group-write" => valid with { Mode = (ushort)(valid.Mode | 0x10) },
            "other-write" => valid with { Mode = (ushort)(valid.Mode | 0x2) },
            "sealed-permissions" => valid with { Mode = (ushort)(valid.Mode & ~0x80) },
            "links-zero" => valid with { LinkCount = 0 }, "links-two" => valid with { LinkCount = 2 },
            "oversize" => valid with { Length = (ulong)LinuxProducerWorkspaceLayout.MaximumFileBytes + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.False(LinuxWorkspaceMetadata.MatchesMutableProducer(bad, directory, 5, 6, sealedTree));
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("inode")]
    [InlineData("type")]
    [InlineData("permissions")]
    [InlineData("links-zero")]
    [InlineData("links-two")]
    [InlineData("empty")]
    [InlineData("oversize")]
    public void CopiedProducerFileRequiresNonzeroOwnerSingleLinkAndPositiveBoundedLength(string change)
    {
        var valid = Sample(5, 6, 0x8180);
        Assert.True(LinuxWorkspaceMetadata.MatchesCopiedProducerFile(valid));
        var bad = change switch
        {
            "uid" => valid with { Uid = 0 }, "gid" => valid with { Gid = 0 }, "inode" => valid with { Inode = 0 },
            "type" => valid with { Mode = 0x4180 }, "permissions" => valid with { Mode = 0x81a4 },
            "links-zero" => valid with { LinkCount = 0 }, "links-two" => valid with { LinkCount = 2 },
            "empty" => valid with { Length = 0 }, "oversize" => valid with { Length = (ulong)LinuxProducerWorkspaceLayout.MaximumFileBytes + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.False(LinuxWorkspaceMetadata.MatchesCopiedProducerFile(bad));
    }

    public static IEnumerable<object[]> SealBoundaries()
    {
        foreach (var directory in new[] { true, false })
        {
            foreach (var change in new[] { "device-major", "device-minor", "inode", "uid", "gid", "type", "permissions" }) yield return [directory, change];
            if (!directory) foreach (var change in new[] { "links-zero", "links-two", "length" }) yield return [directory, change];
        }
    }

    [Theory]
    [MemberData(nameof(SealBoundaries))]
    public void ProducerSealKeepsOriginalDeviceInodeAndExactTerminalRootPolicy(bool directory, string change)
    {
        var original = Sample(5, 6, directory ? (ushort)0x41c0 : (ushort)0x8180, directory ? 2u : 1u);
        var mode = directory ? (ushort)0x1c0 : (ushort)0x180;
        var valid = original with { Uid = 0, Gid = 0 };
        Assert.True(LinuxWorkspaceMetadata.MatchesProducerSeal(valid, original, directory, mode));
        var bad = change switch
        {
            "device-major" => valid with { DeviceMajor = 9 }, "device-minor" => valid with { DeviceMinor = 2 },
            "inode" => valid with { Inode = 43 }, "uid" => valid with { Uid = 1 }, "gid" => valid with { Gid = 1 },
            "type" => valid with { Mode = (ushort)((directory ? 0x8000 : 0x4000) | mode) },
            "permissions" => valid with { Mode = (ushort)(valid.Mode ^ 0x40) },
            "links-zero" => valid with { LinkCount = 0 }, "links-two" => valid with { LinkCount = 2 },
            "length" => valid with { Length = original.Length + 1 }, _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.False(LinuxWorkspaceMetadata.MatchesProducerSeal(bad, original, directory, mode));
    }

    [Theory]
    [InlineData("uid", "ASEVD410")]
    [InlineData("gid", "ASEVD410")]
    [InlineData("inode", "ASEVD402")]
    [InlineData("type", "ASEVD402")]
    [InlineData("owner-search", "ASEVD402")]
    [InlineData("other-search", "ASEVD402")]
    [InlineData("setuid", "ASEVD402")]
    [InlineData("setgid", "ASEVD402")]
    [InlineData("group-write", "ASEVD402")]
    [InlineData("other-write", "ASEVD402")]
    [InlineData("owner-and-mode", "ASEVD410")]
    public void ApplicationAncestorPreservesOwnershipBeforeGenericSafetyRejection(string change, string code)
    {
        var valid = Sample(mode: 0x41c9, links: 2, length: 0);
        LinuxWorkspaceMetadata.RequireApplicationAncestor(valid);
        var bad = change switch
        {
            "uid" => valid with { Uid = 1 }, "gid" => valid with { Gid = 1 }, "inode" => valid with { Inode = 0 },
            "type" => valid with { Mode = 0x81c9 }, "owner-search" => valid with { Mode = 0x4189 },
            "other-search" => valid with { Mode = 0x41c8 }, "setuid" => valid with { Mode = 0x49c9 },
            "setgid" => valid with { Mode = 0x45c9 }, "group-write" => valid with { Mode = 0x41d9 },
            "other-write" => valid with { Mode = 0x41cb }, "owner-and-mode" => valid with { Uid = 1, Mode = 0x81c9 },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxWorkspaceMetadata.RequireApplicationAncestor(bad));
        Assert.Equal(code, error.Code);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void AdjacentValidLimitsAndIntentionallyUnconstrainedFieldsAreNotStrengthened()
    {
        var file = Sample(5, 6, 0x8180);
        var maximum = file with { Length = (ulong)LinuxProducerWorkspaceLayout.MaximumFileBytes };
        Assert.True(LinuxWorkspaceMetadata.MatchesCopiedProducerFile(maximum));
        Assert.True(LinuxWorkspaceMetadata.MatchesCopiedProducerFile(file with { Length = 1, Uid = 7, Gid = 8, DeviceMajor = 99, ModifySeconds = 999 }));
        foreach (var sealedTree in new[] { false, true })
        {
            Assert.True(LinuxWorkspaceMetadata.MatchesMutableProducer(maximum, false, 5, 6, sealedTree));
            Assert.True(LinuxWorkspaceMetadata.MatchesMutableProducer(file with { Length = 0 }, false, 5, 6, sealedTree));
        }
        var directory = Sample(5, 6, 0x41c0, 0, ulong.MaxValue);
        Assert.True(LinuxWorkspaceMetadata.MatchesMutableProducer(directory, true, 5, 6, true));
        Assert.True(LinuxWorkspaceMetadata.MatchesDirectory(directory, 5, 6, 0x1c0));
        Assert.True(LinuxWorkspaceMetadata.MatchesProducerSeal(directory with { Uid = 0, Gid = 0, ChangeSeconds = 999 }, directory, true, 0x1c0));
        Assert.True(LinuxWorkspaceMetadata.MatchesApplicationFile(Sample(mode: 0x816d), 4, 0x16d));
        LinuxWorkspaceMetadata.RequireApplicationAncestor(Sample(mode: 0x4041, links: 2)); // Search-only is sufficient.
        LinuxWorkspaceMetadata.RequireApplicationAncestor(Sample(mode: 0x4241, links: 2)); // Ancestor sticky is allowed.
    }
}
