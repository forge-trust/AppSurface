using System.Text;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Discovers report paths beneath one actual borrowed results-directory descriptor.</summary>
/// <remarks>
/// This procedure borrows its native directory descriptor and expected owner data. It opens no selected
/// root, chooses no path, retains no handle after returning, and grants no producer or report-custody
/// authority. The owning producer verifies readiness and selects the root before calling this procedure.
/// </remarks>
internal static class LinuxProducerReportDiscovery
{
    private const int MaximumDiscoveryNodes = 4096;
    private const ulong PathFlags = 0x200000 | 0x80000; // O_PATH | O_CLOEXEC.
    private const ulong DirectoryFlags = 0x10000 | 0x80000; // O_DIRECTORY | O_CLOEXEC.
    private const ulong ChildResolution = 0x02 | 0x04 | 0x08 | 0x01;
    private const string ReportName = "coverage.cobertura.xml";

    /// <summary>Returns sorted canonical report paths after descriptor-relative checks on the borrowed root.</summary>
    internal static string[] Discover(SafeFileHandle root, uint uid, uint gid,
        LinuxInputDeadline deadline, CancellationToken token)
    {
        var paths = new List<string>();
        long total = 0;
        var nodes = 1;
        Visit(root, string.Empty, 0, uid, gid, deadline, token, paths, ref nodes, ref total);
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private static void Visit(SafeFileHandle directory, string prefix, int depth, uint uid, uint gid,
        LinuxInputDeadline deadline, CancellationToken token, List<string> paths, ref int nodes, ref long total)
    {
        deadline.Check(token);
        var before = LinuxProtectedMetadata.From(StatFd(directory));
        LinuxRetainedReportSet.RequireMetadata(before, true, uid, gid);
        var names = ReadNames(directory, before, deadline, token);
        foreach (var name in names)
        {
            deadline.Check(token);
            if (++nodes > MaximumDiscoveryNodes) throw LinuxProducerArtifactCustody.Invalid();
            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            if (path.Split('/').Length > 32 || Encoding.UTF8.GetByteCount(path) > 4095)
                throw LinuxProducerArtifactCustody.Invalid();

            using var probe = OpenAt2(Fd(directory), name, PathFlags, 0, ChildResolution);
            var metadata = LinuxProtectedMetadata.From(StatFd(probe));
            deadline.Check(token);
            var type = metadata.Mode & 0xf000;
            if (type == 0x4000)
            {
                LinuxRetainedReportSet.RequireMetadata(metadata, true, uid, gid);
                using var child = OpenAt2(Fd(directory), name, DirectoryFlags, 0, ChildResolution);
                var opened = LinuxProtectedMetadata.From(StatFd(child));
                deadline.Check(token);
                if (!opened.SameAncestorAs(metadata)) throw LinuxProducerArtifactCustody.Invalid();
                Visit(child, path, depth + 1, uid, gid, deadline, token, paths, ref nodes, ref total);
                RequireNamed(directory, name, metadata, directory: true, uid, gid, deadline, token);
                continue;
            }

            if (type != 0x8000 || !StringComparer.Ordinal.Equals(name, ReportName))
                throw LinuxProducerArtifactCustody.Invalid();
            LinuxRetainedReportSet.RequireMetadata(metadata, false, uid, gid);
            if (paths.Count >= LinuxRetainedReportSet.MaximumFiles)
                throw LinuxProducerArtifactCustody.Invalid();
            total = LinuxRetainedReportSet.AddLength(total, metadata.Length);
            paths.Add(path);
            RequireNamed(directory, name, metadata, directory: false, uid, gid, deadline, token);
        }

        var after = LinuxProtectedMetadata.From(StatFd(directory));
        LinuxRetainedReportSet.RequireMetadata(after, true, uid, gid);
        if (!after.SameAncestorAs(before)
            || !ReadNames(directory, after, deadline, token).SequenceEqual(names, StringComparer.Ordinal))
            throw LinuxProducerArtifactCustody.Invalid();
        if (depth > 0 && names.Length == 0) throw LinuxProducerArtifactCustody.Invalid();
        deadline.Check(token);
    }

    private static string[] ReadNames(SafeFileHandle directory, LinuxProtectedMetadata expected,
        LinuxInputDeadline deadline, CancellationToken token)
    {
        deadline.Check(token);
        using var fresh = OpenAt2(Fd(directory), ".", DirectoryFlags, 0, ChildResolution);
        var opened = LinuxProtectedMetadata.From(StatFd(fresh));
        if (!opened.SameAncestorAs(expected)) throw LinuxProducerArtifactCustody.Invalid();
        var buffer = new byte[32 * 1024];
        var names = SupervisionDirectoryRead.ReadActual(fresh, buffer,
            () => deadline.Check(token), () => deadline.Check(token),
            MaximumDiscoveryNodes, MaximumDiscoveryNodes, null, LinuxProducerArtifactCustody.Invalid);
        deadline.Check(token);
        return names;
    }

    private static void RequireNamed(SafeFileHandle parent, string name, LinuxProtectedMetadata expected,
        bool directory, uint uid, uint gid, LinuxInputDeadline deadline, CancellationToken token)
    {
        deadline.Check(token);
        using var named = OpenAt2(Fd(parent), name, PathFlags, 0, ChildResolution);
        var current = LinuxProtectedMetadata.From(StatFd(named));
        deadline.Check(token);
        if (directory)
        {
            LinuxRetainedReportSet.RequireMetadata(current, true, uid, gid);
            if (!current.SameAncestorAs(expected)) throw LinuxProducerArtifactCustody.Invalid();
        }
        else if (!LinuxReportCustodyMetadata.SameFileSample(current, expected))
            throw LinuxProducerArtifactCustody.Invalid();
    }
}
