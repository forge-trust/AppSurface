namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Pure comparisons of sampled workspace metadata, never handles, custody or authentication.</summary>
/// <remarks>
/// Native callers first acquire their actual retained descriptors and statx samples, then apply these
/// same policies before mutation or custody. True describes the supplied data only. It cannot construct
/// a node, adopt a workspace, release an account or establish that any sample came from the kernel.
/// Declarations and expected permission modes are independently validated by the native caller.
/// </remarks>
internal static class LinuxWorkspaceMetadata
{
    /// <summary>Fixed producer directory permissions, shared by native operations and comparison data.</summary>
    internal const ushort ProducerDirectoryMode = 0x1c0;
    /// <summary>Fixed producer file permissions, shared by native operations and comparison data.</summary>
    internal const ushort ProducerFileMode = 0x180;

    /// <summary>Compares type, nonzero inode, exact selected IDs and exact directory permission bits.</summary>
    internal static bool MatchesDirectory(LinuxProtectedMetadata value, uint uid, uint gid, ushort mode) =>
        !(value.Uid != uid || value.Gid != gid || value.Inode == 0 || value.Mode != (ushort)(0x4000 | mode));

    /// <summary>Compares a root-owned single-link regular file to an already validated length/mode declaration.</summary>
    /// <remarks>Directory entries, hashes and the validity of the declaration remain separate native obligations.</remarks>
    internal static bool MatchesApplicationFile(LinuxProtectedMetadata value, long declaredLength, uint mode) =>
        !(value.Uid != 0 || value.Gid != 0 || value.Mode != (ushort)(0x8000 | mode)
            || value.Inode == 0 || value.LinkCount != 1 || value.Length != (ulong)declaredLength);

    /// <summary>Checks mutable/sealed producer data with the original exact owner, type and permission rules.</summary>
    /// <remarks>
    /// Unsealed directory/file modes may vary except for set-ID/sticky and group/other write bits.
    /// Sealed trees require 0700 directories and 0600 files. Mutable files may be empty; copied files
    /// have a separate positive-length rule. Directory length/link count are not file constraints.
    /// </remarks>
    internal static bool MatchesMutableProducer(LinuxProtectedMetadata value, bool directory,
        uint uid, uint gid, bool sealedTree)
    {
        var type = value.Mode & 0xf000;
        var permissions = value.Mode & 0x1ff;
        return !(value.Inode == 0 || value.Uid != uid || value.Gid != gid
            || type != (directory ? 0x4000 : 0x8000) || (value.Mode & 0x0e00) != 0
            || (permissions & 0x12) != 0
            || (directory ? sealedTree && permissions != ProducerDirectoryMode
                : value.LinkCount != 1 || value.Length > LinuxProducerWorkspaceLayout.MaximumFileBytes
                    || sealedTree && permissions != ProducerFileMode));
    }

    /// <summary>Compares a copied 0600 file: nonzero IDs, inode, one link and positive bounded length.</summary>
    /// <remarks>Exact account binding and expected copied length are still checked at the native caller.</remarks>
    internal static bool MatchesCopiedProducerFile(LinuxProtectedMetadata value) =>
        !(value.Inode == 0 || value.Uid == 0 || value.Gid == 0 || (value.Mode & 0xf000) != 0x8000
            || value.LinkCount != 1 || value.Mode != (ushort)(0x8000 | ProducerFileMode)
            || value.Length is 0 or > LinuxProducerWorkspaceLayout.MaximumFileBytes);

    /// <summary>Compares terminal root ownership, original device/inode and the exact selected seal mode.</summary>
    /// <remarks>Original metadata/mode are prevalidated. Files additionally retain one link and original length.</remarks>
    internal static bool MatchesProducerSeal(LinuxProtectedMetadata terminal, LinuxProtectedMetadata original,
        bool directory, ushort permissions) =>
        !(terminal.DeviceMajor != original.DeviceMajor || terminal.DeviceMinor != original.DeviceMinor
            || terminal.Inode != original.Inode || terminal.Uid != 0 || terminal.Gid != 0
            || terminal.Mode != (ushort)((directory ? 0x4000 : 0x8000) | permissions)
            || (!directory && (terminal.LinkCount != 1 || terminal.Length != original.Length)));

    /// <summary>Retains the application ancestor's original ownership-before-generic-safety exception order.</summary>
    /// <remarks>Ownership rejects ASEVD410; delegated ancestor safety rejects ASEVD402. This opens no path.</remarks>
    internal static void RequireApplicationAncestor(LinuxProtectedMetadata value)
    {
        if (value.Uid != 0 || value.Gid != 0) throw LinuxApplicationWorkspaceLayout.Invalid();
        value.RequireAncestor(sharedTraversal: true);
    }
}
