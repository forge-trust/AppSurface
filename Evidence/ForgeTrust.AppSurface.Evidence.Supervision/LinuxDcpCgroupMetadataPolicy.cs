namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Detached cgroup-node stat data; it owns no descriptor and authenticates no native object.</summary>
internal readonly record struct LinuxDcpCgroupMetadata(uint DeviceMajor, uint DeviceMinor, ulong Inode,
    uint Uid, uint Gid, ushort Mode, uint LinkCount, ulong Length);

/// <summary>Checks the original sampled cgroup-node access and repeated-identity rules.</summary>
/// <remarks>The caller must first check the actual cgroup2 filesystem and obtain statx data from its
/// retained descriptor. These comparisons cannot open, select or authenticate a cgroup, process or DCP.</remarks>
internal static class LinuxDcpCgroupMetadataPolicy
{
    /// <summary>Requires the original root ownership, type, permissions and link counts.</summary>
    /// <param name="metadata">Copied actual metadata, or detached comparison data in a portable control.</param>
    /// <param name="directory">Whether the retained native caller selected a directory or cgroup.procs file.</param>
    /// <remarks>Directories require owner read/search and at least two links; files require owner read
    /// and one link. Group/other writes and set-user/set-group bits remain forbidden. No new mode policy
    /// is selected here, and byte length does not establish cgroup identity.</remarks>
    internal static void Require(LinuxDcpCgroupMetadata metadata, bool directory)
    {
        if (metadata.Inode == 0 || metadata.Uid != 0 || metadata.Gid != 0
            || (metadata.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)
            || (metadata.Mode & 0x0c12) != 0
            || (directory ? (metadata.Mode & 0x140) != 0x140 || metadata.LinkCount < 2
                : (metadata.Mode & 0x100) == 0 || metadata.LinkCount != 1))
            throw LinuxApplicationDcpIdentity.Rejected();
    }

    /// <summary>Compares the repeated native identity/access fields, ignoring changing length and links.</summary>
    /// <remarks>Each sample is independently validated before comparison. Equality is detached data and
    /// cannot replace the caller's retained descriptors, named reopen, filesystem check or cancellation.</remarks>
    internal static bool SameNode(LinuxDcpCgroupMetadata left, LinuxDcpCgroupMetadata right) =>
        left.DeviceMajor == right.DeviceMajor && left.DeviceMinor == right.DeviceMinor
        && left.Inode == right.Inode && left.Uid == right.Uid && left.Gid == right.Gid
        && left.Mode == right.Mode;
}
