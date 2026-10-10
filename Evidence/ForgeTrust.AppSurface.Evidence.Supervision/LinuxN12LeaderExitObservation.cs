using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Detached private N12 data; no record, constructor or validator establishes native ownership or acceptance.</summary>
/// <param name="Generation">Original owner generation data.</param>
/// <param name="LeaderPid">Original retained main PID.</param>
/// <param name="Descendant">Root-sampled live child facts before stop.</param>
/// <param name="GroupBeforeStop">Actual selected group data before stop; recursive population must be true.</param>
/// <param name="Terminal">Authenticated original natural leader-exit metadata, not group or pump settlement.</param>
internal sealed record LinuxN12LeaderExitObservation(Guid Generation, uint LeaderPid, LinuxProcessSample Descendant,
    LinuxCgroupSample GroupBeforeStop, LinuxUnitProperties Terminal)
{
    /// <summary>Validates before-stop data through an intentional pure seam; never constructs a native holder or lease.</summary>
    internal static void RequireBeforeStop(LinuxUnitProperties terminal, uint leaderPid, LinuxProcessSample descendant,
        uint workerUid, uint workerGid, LinuxUnitName unit, LinuxCgroupSample group, bool outputJoined)
    {
        if (terminal is null || descendant is null || group is null || unit is null || leaderPid is 0 or > int.MaxValue
            || terminal.MainPid != 0 || terminal.ExecMainPid != leaderPid || terminal.ExecMainCode != 1 || terminal.ExecMainStatus != 0
            || terminal.Id != unit.Value || outputJoined || !group.Exists || group.Populated != true || group.Frozen != false
            || group.KernelInode is null or 0 || descendant.Stat.Pid == leaderPid) throw LinuxSystemdBackend.InvalidControl();
        LinuxProcessData.RequireExpected(descendant, descendant.Stat.Pid, workerUid, workerGid, unit, LinuxProcessSamplingRole.Worker);
    }

    /// <summary>Encodes detached joined data under a 4 KiB bound; root exit, account cleanup and source bindings remain separate proof requirements.</summary>
    internal byte[] EncodeJoined(SupervisionOutputReceipt output, LinuxCgroupSample finalGroup)
    {
        if (Generation == Guid.Empty || output is not { Successful: true } || !LinuxAccountUtility.GroupEmpty(finalGroup))
            throw LinuxSystemdBackend.InvalidControl();
        var bytes = EvidenceCanonicalJson.Serialize(new
        {
            schema = "issue779-csharp-n12-descendant-observation-v1",
            generation = Generation.ToString("N"),
            leader_pid = LeaderPid,
            leader_exit_code = Terminal.ExecMainCode,
            leader_exit_status = Terminal.ExecMainStatus,
            descendant_pid = Descendant.Stat.Pid,
            descendant_start_ticks = Descendant.Stat.StartTimeTicks,
            descendant_uids = Descendant.Uids,
            descendant_gids = Descendant.Gids,
            descendant_cgroup = Descendant.ControlGroup,
            group_before_stop = GroupBeforeStop,
            pumps_joined_before_stop = false,
            stdout_eof = output.Stdout.EndOfStream,
            stderr_eof = output.Stderr.EndOfStream,
            received_bytes = output.ReceivedBytes,
            stdout_bytes = output.Stdout.ReceivedBytes,
            stderr_bytes = output.Stderr.ReceivedBytes,
            group_empty_after_stop = true,
            custody_and_accounts_not_yet_closed = true,
            native_authority = false,
            native_acceptance = false,
        });
        if (bytes.Length > 4096) throw LinuxSystemdBackend.InvalidControl();
        return bytes;
    }
}
