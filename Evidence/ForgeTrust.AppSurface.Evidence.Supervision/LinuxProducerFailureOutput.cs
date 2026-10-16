using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Retains only the original joined producer prefixes under an exclusive root-only diagnostic generation.</summary>
/// <remarks>No second reader, workload dispatch, cleanup authority or successful result is created.</remarks>
internal static class LinuxProducerFailureOutput
{
    /// <summary>Saves bounded bytes after actual pump settlement while original root teardown remains valid.</summary>
    internal static void Retain(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        SupervisionOutputReceipt output, CancellationToken token)
    {
        LinuxProtectedDeployment.RequirePlatform();
        owner.RequireCleanupLaunchInput(input, token);
        RequirePrefix(output.Stdout);
        RequirePrefix(output.Stderr);
        using var slash = OpenAt2(-100, "/", 0x10000 | 0x80000, 0, 0);
        using var run = OpenAt2(Fd(slash), "run", 0x10000 | 0x80000, 0, 0x02 | 0x04 | 0x08);
        LinuxProtectedMetadata.From(StatFd(run)).RequireAncestor(false);
        var name = "appsurface-evidence-producer-diagnostics-" + owner.RunId.ToString("N");
        if (MkdirAt(Fd(run), name, 0x1c0) != 0) throw LinuxProtectedDeployment.InvalidDeployment();
        using var root = OpenAt2(Fd(run), name, 0x10000 | 0x80000, 0, 0x01 | 0x02 | 0x04 | 0x08);
        var metadata = LinuxProtectedMetadata.From(StatFd(root));
        if (metadata.Uid != 0 || metadata.Gid != 0 || metadata.Mode != 0x41c0) throw LinuxProtectedDeployment.InvalidDeployment();
        Save(root, "stdout.prefix", output.Stdout.Prefix.AsSpan(), token);
        Save(root, "stderr.prefix", output.Stderr.Prefix.AsSpan(), token);
        using var named = OpenAt2(Fd(run), name, 0x10000 | 0x80000, 0, 0x01 | 0x02 | 0x04 | 0x08);
        if (!LinuxProtectedMetadata.From(StatFd(named)).SameAncestorAs(metadata)) throw LinuxProtectedDeployment.InvalidDeployment();
        owner.RequireCleanupLaunchInput(input, token);
    }

    /// <summary>Checks the retained-byte ceiling without treating missing EOF or prefix length as physical-exit proof.</summary>
    internal static void RequirePrefix(SupervisionOutputStreamReceipt stream)
    {
        if (stream.Prefix.IsDefault || stream.Prefix.Length > EvidenceRunBudgetLimits.RetainedOutputPrefixBytesPerStream
            || stream.ReceivedBytes < stream.Prefix.Length) throw LinuxProtectedDeployment.InvalidDeployment();
    }

    private static void Save(SafeFileHandle root, string name, ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = OpenAt2(Fd(root), name, 0x01 | 0x40 | 0x80 | 0x80000, 0x180, 0x01 | 0x02 | 0x04 | 0x08);
        RandomAccess.Write(file, bytes, 0);
        var metadata = LinuxProtectedMetadata.From(StatFd(file));
        if (metadata.Uid != 0 || metadata.Gid != 0 || metadata.Mode != 0x8180 || metadata.LinkCount != 1
            || metadata.Length != (ulong)bytes.Length) throw LinuxProtectedDeployment.InvalidDeployment();
        using var named = OpenAt2(Fd(root), name, 0x200000 | 0x80000, 0, 0x01 | 0x02 | 0x04 | 0x08);
        if (LinuxProtectedMetadata.From(StatFd(named)) != metadata) throw LinuxProtectedDeployment.InvalidDeployment();
        token.ThrowIfCancellationRequested();
    }
}
