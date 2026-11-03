using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached checkpoint metadata and copied-peer predicates only; no native owner or socket is created.</summary>
public sealed class LinuxCheckpointMetadataPolicyTests
{
    private static LinuxProtectedMetadata Metadata(uint uid = 0, uint gid = 0, ulong inode = 42,
        ushort mode = 0x41c0, uint links = 2, ulong length = 73) =>
        new(8, 1, inode, uid, gid, mode, links, length, 1, 2, 3, 4);

    [Fact]
    public void DirectoryChecksRootIdsInodeAndExactDirectoryModeButIgnoresLengthAndLinks()
    {
        Assert.True(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(), directory: true));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(uid: 1), directory: true));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(gid: 1), directory: true));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(inode: 0), directory: true));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0x41a0), directory: true));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0xc1c0), directory: true));
    }

    [Fact]
    public void UnsealedSocketRequiresSocketTypeSingleLinkZeroLengthAndClearOriginalMask()
    {
        Assert.True(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0xc1a4, links: 1, length: 0), false, sealedSocket: false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(uid: 1, mode: 0xc1a4, links: 1, length: 0), false, false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(gid: 1, mode: 0xc1a4, links: 1, length: 0), false, false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(inode: 0, mode: 0xc1a4, links: 1, length: 0), false, false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0x81a4, links: 1, length: 0), false, false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0xc1a6, links: 1, length: 0), false, false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0xc1b4, links: 1, length: 0), false, false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0xc1a4, links: 2, length: 0), false, false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0xc1a4, links: 1, length: 1), false, false));
    }

    [Fact]
    public void SealedSocketRequiresExactModeAndSocketBaseRequirements()
    {
        Assert.True(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0xc180, links: 1, length: 0), false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0xc1a0, links: 1, length: 0), false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0x8180, links: 1, length: 0), false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0xc180, links: 0, length: 0), false));
        Assert.False(LinuxCheckpointMetadataPolicy.IsPrivate(Metadata(mode: 0xc180, links: 1, length: 1), false));
    }

    [Fact]
    public void CopiedPeerRequiresFixedSizeNonselfPositiveRootPeerAndUnpinnedOrSamePid()
    {
        Assert.True(LinuxCheckpointMetadataPolicy.ValidPeer(12, 314, 0, 0, 200, 0));
        Assert.True(LinuxCheckpointMetadataPolicy.ValidPeer(12, 314, 0, 0, 200, 314));
        Assert.False(LinuxCheckpointMetadataPolicy.ValidPeer(11, 314, 0, 0, 200, 0));
        Assert.False(LinuxCheckpointMetadataPolicy.ValidPeer(13, 314, 0, 0, 200, 0));
        Assert.False(LinuxCheckpointMetadataPolicy.ValidPeer(12, 0, 0, 0, 200, 0));
        Assert.False(LinuxCheckpointMetadataPolicy.ValidPeer(12, -1, 0, 0, 200, 0));
        Assert.False(LinuxCheckpointMetadataPolicy.ValidPeer(12, 200, 0, 0, 200, 0));
        Assert.False(LinuxCheckpointMetadataPolicy.ValidPeer(12, 314, 1, 0, 200, 0));
        Assert.False(LinuxCheckpointMetadataPolicy.ValidPeer(12, 314, 0, 1, 200, 0));
        Assert.False(LinuxCheckpointMetadataPolicy.ValidPeer(12, 314, 0, 0, 200, 315));
    }
}
