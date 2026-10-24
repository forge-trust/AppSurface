namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Compares the original sampled report-root, file and inventory data without opening native objects.</summary>
/// <remarks>The authentic custody holder still supplies retained descriptors, named reopen, selected owners,
/// deadlines and producer settlement. Passing these comparisons grants none of those capabilities.</remarks>
internal static class LinuxReportCustodyMetadata
{
    /// <summary>Compares exactly the original stable file fields; timestamp churn is checked elsewhere.</summary>
    internal static bool SameFileSample(LinuxProtectedMetadata left, LinuxProtectedMetadata right) =>
        left.DeviceMajor == right.DeviceMajor && left.DeviceMinor == right.DeviceMinor && left.Inode == right.Inode
        && left.Uid == right.Uid && left.Gid == right.Gid && left.Mode == right.Mode
        && left.LinkCount == right.LinkCount && left.Length == right.Length;

    /// <summary>Requires exact count, original order and complete report rows at repeated inventory checks.</summary>
    internal static bool SameInventory(IReadOnlyList<LinuxRetainedReportSet.ReportInfo> left,
        IReadOnlyList<LinuxRetainedReportSet.ReportInfo> right) =>
        left.Count == right.Count && left.SequenceEqual(right);

    /// <summary>Requires the original root-owned nonwritable, exact 0711 results parent and nonzero links.</summary>
    /// <remarks>The inherited ancestor guard runs first and preserves its original diagnostic.</remarks>
    internal static void RequireResultsParent(LinuxProtectedMetadata metadata)
    {
        metadata.RequireAncestor(false);
        if (metadata.Gid != 0 || metadata.Mode != 0x41c9 || metadata.LinkCount == 0)
            throw LinuxProducerArtifactCustody.Invalid();
    }

    /// <summary>Requires the selected root's original sampled identity and exact 0700 directory mode.</summary>
    /// <remarks>The retained-report identity guard runs first; metadata does not establish selection.</remarks>
    internal static void RequireSelectedRoot(LinuxProtectedMetadata metadata, LinuxProtectedMetadata expected,
        uint uid, uint gid)
    {
        LinuxRetainedReportSet.RequireRootIdentity(metadata, expected, uid, gid);
        if (metadata.Mode != 0x41c0) throw LinuxProducerArtifactCustody.Invalid();
    }
}
