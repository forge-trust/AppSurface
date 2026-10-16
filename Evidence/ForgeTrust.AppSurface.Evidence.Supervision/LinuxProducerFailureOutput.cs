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
        SupervisionOutputReceipt output, string stage, LinuxUnitProperties? normal,
        LinuxUnitProperties? stopped, SupervisionPendingStartSnapshot pending, CancellationToken token)
    {
        LinuxProtectedDeployment.RequirePlatform();
        owner.RequireCleanupLaunchInput(input, token);
        if (stage is not ("binding" or "results" or "pipes" or "backend" or "start" or "running"
            or "identity" or "normal-exit" or "settlement")) throw LinuxProtectedDeployment.InvalidDeployment();
        var state = EvidenceCanonicalJson.Serialize(new
        {
            schema = "evidence-producer-failure-state-v1", stage,
            normal_code = normal?.ExecMainCode, normal_status = normal?.ExecMainStatus,
            stopped_code = stopped?.ExecMainCode, stopped_status = stopped?.ExecMainStatus,
            start_reserved = pending.StartReserved, start_joined = pending.StartJoined,
            stop_joined = pending.StopJoined, pending_failure = pending.FirstFailure.ToString(),
        });
        RetainCore(input, owner, output, "producer", state, token);
    }

    /// <summary>Retains original joined worker output without creating another reader or settlement assertion.</summary>
    /// <remarks>The original worker supplies its receipt after its stop task and collector join; missing physical proof remains missing.</remarks>
    internal static void RetainWorker(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        SupervisionOutputReceipt output, CancellationToken token)
    {
        var state = EvidenceCanonicalJson.Serialize(new { schema = "evidence-worker-failure-output-v1" });
        RetainCore(input, owner, output, "worker", state, token);
    }

    private static void RetainCore(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        SupervisionOutputReceipt output, string role, byte[] state, CancellationToken token)
    {
        LinuxProtectedDeployment.RequirePlatform();
        owner.RequireCleanupLaunchInput(input, token);
        RequirePrefix(output.Stdout);
        RequirePrefix(output.Stderr);
        using var slash = OpenAt2(-100, "/", 0x10000 | 0x80000, 0, 0);
        using var run = OpenAt2(Fd(slash), "run", 0x10000 | 0x80000, 0, 0x02 | 0x04 | 0x08);
        LinuxProtectedMetadata.From(StatFd(run)).RequireAncestor(false);
        var name = "appsurface-evidence-" + role + "-diagnostics-" + owner.RunId.ToString("N");
        if (MkdirAt(Fd(run), name, 0x1c0) != 0) throw LinuxProtectedDeployment.InvalidDeployment();
        using var root = OpenAt2(Fd(run), name, 0x10000 | 0x80000, 0, 0x01 | 0x02 | 0x04 | 0x08);
        var metadata = LinuxProtectedMetadata.From(StatFd(root));
        if (metadata.Uid != 0 || metadata.Gid != 0 || metadata.Mode != 0x41c0) throw LinuxProtectedDeployment.InvalidDeployment();
        Save(root, "stdout.prefix", output.Stdout.Prefix.AsSpan(), token);
        Save(root, "stderr.prefix", output.Stderr.Prefix.AsSpan(), token);
        Save(root, "state.json", state, token);
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
