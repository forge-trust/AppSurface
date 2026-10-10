using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Private N10 rendezvous and bounded observations for the original pending-start lifetime.</summary>
/// <remarks>
/// The start frame is emitted only after the original validated StartTransientUnit reply. The same
/// startup operation then remains pending on its original token. The joined frame is available only
/// after the worker's original startup, two-stop containment, final unit/group sampling and pumps join.
/// Both frames are detached data; neither creates a lease, owner, admission, custody or acceptance.
/// </remarks>
internal sealed class LinuxN10PendingStartCheckpoint
{
    internal const int MaximumFrameBytes = 4096;
    private const string JobPrefix = "/org/freedesktop/systemd1/job/";
    private string? _unit;
    private string? _jobPath;
    private int _claimed;

    /// <summary>Emits the actual reply phase and waits inside the original start task for its original token.</summary>
    /// <param name="unit">The generated worker unit passed to the original authenticated systemd request.</param>
    /// <param name="jobPath">The actual, already validated StartTransientUnit reply.</param>
    /// <param name="startupToken">The original startup token; no timer or replacement token is created.</param>
    /// <returns>The original start task remains pending until that token is cancelled.</returns>
    internal async Task WaitAfterStartReplyAsync(LinuxUnitName unit, string jobPath, CancellationToken startupToken)
    {
        var frame = RecordStartReply(unit, jobPath);
        using var error = Console.OpenStandardError();
        await error.WriteAsync(frame.AsMemory(), startupToken).ConfigureAwait(false);
        await error.WriteAsync(new byte[] { (byte)'\n' }.AsMemory(), startupToken).ConfigureAwait(false);
        await error.FlushAsync(startupToken).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, startupToken).ConfigureAwait(false);
    }

    /// <summary>Builds the bounded trigger from the real reply supplied by the original backend.</summary>
    /// <param name="unit">Generated worker unit name.</param>
    /// <param name="jobPath">Validated job path from the original method reply.</param>
    /// <returns>The fixed bounded N10 reply-phase frame.</returns>
    /// <remarks>This internal data seam alone cannot create or reserve a systemd unit.</remarks>
    internal byte[] RecordStartReply(LinuxUnitName unit, string jobPath)
    {
        if (!EvidenceNativeQualification.PendingStartRaceEnabled || unit is null
            || !unit.Value.StartsWith("appsurface-evidence-worker-", StringComparison.Ordinal)
            || !unit.Value.EndsWith(".service", StringComparison.Ordinal)
            || string.IsNullOrEmpty(jobPath) || !jobPath.StartsWith(JobPrefix, StringComparison.Ordinal)
            || jobPath.Length <= JobPrefix.Length || jobPath.Length > MaximumFrameBytes || jobPath.Any(char.IsControl)
            || Interlocked.Exchange(ref _claimed, 1) != 0)
            throw LinuxSystemdBackend.InvalidControl();
        _unit = unit.Value;
        _jobPath = jobPath;
        var bytes = EvidenceCanonicalJson.Serialize(new
        {
            schema = "issue779-n10-pending-start-phase-v1",
            @case = "N10",
            phase = "start-transient-unit-reply",
            unit = _unit,
            job_path = _jobPath,
            native_authority = false,
            native_acceptance = false,
        });
        if (bytes.Length is 0 or > MaximumFrameBytes) throw LinuxSystemdBackend.InvalidControl();
        return bytes;
    }

    /// <summary>Encodes the original joined procedure facts without upgrading sticky failure or physical status.</summary>
    /// <param name="generation">Original owner generation.</param>
    /// <param name="unit">Original generated worker unit.</param>
    /// <param name="pending">Original pending-start coordinator snapshot after StopAsync returns.</param>
    /// <param name="startupJoined">Actual original whole-start task completion.</param>
    /// <param name="startupFailed">Actual sticky worker-lifetime state.</param>
    /// <param name="stopJoined">Actual original worker lifetime stop/finalization task completion.</param>
    /// <param name="physicallySettled">The existing worker projection, copied as-is including false.</param>
    /// <param name="stopProcedureCalls">Actual invocations of the original pending-unit stop delegate.</param>
    /// <param name="finalUnit">Actual original selected-unit properties sampled by joined finalizer.</param>
    /// <param name="unitStopped">Result of the existing selected worker recipe predicate over the retained final sample.</param>
    /// <param name="group">Original final cgroup sample after the joined pumps.</param>
    /// <param name="groupAfterPumps">Whether the original finalizer sampled that cgroup after pump join.</param>
    /// <param name="output">Original joined output collector receipt.</param>
    /// <returns>One bounded private JSON record, with custody and native acceptance explicitly false.</returns>
    internal byte[] EncodeAfterJoin(Guid generation, LinuxUnitName unit, SupervisionPendingStartSnapshot pending,
        bool startupJoined, bool startupFailed, bool stopJoined, bool physicallySettled,
        int stopProcedureCalls, LinuxUnitProperties finalUnit, bool unitStopped, LinuxCgroupSample group,
        bool groupAfterPumps, SupervisionOutputReceipt output)
    {
        if (!EvidenceNativeQualification.PendingStartRaceEnabled || generation == Guid.Empty || unit is null
            || pending is null || !Enum.IsDefined(pending.FirstFailure)
            || Volatile.Read(ref _claimed) != 1 || _unit != unit.Value || string.IsNullOrEmpty(_jobPath)
            || !startupJoined || !startupFailed || !stopJoined
            || !pending.StartReserved || pending.Started || !pending.StartJoined || !pending.Closed
            || !pending.StopJoined || pending.FirstFailure != SupervisionPendingStartFailure.StartCancelled
            || stopProcedureCalls != 2 || finalUnit is null || group is null || !groupAfterPumps || output is null)
            throw LinuxSystemdBackend.InvalidControl();

        var bytes = EvidenceCanonicalJson.Serialize(new
        {
            schema = "issue779-n10-pending-start-observation-v1",
            @case = "N10",
            phase = "original-start-stop-and-pumps-joined-before-custody",
            generation = generation.ToString("N"),
            unit = _unit,
            job_path = _jobPath,
            start_reply_observed = true,
            pending_start = new
            {
                start_reserved = pending.StartReserved,
                started = pending.Started,
                start_joined = pending.StartJoined,
                closed = pending.Closed,
                stop_joined = pending.StopJoined,
                first_failure = pending.FirstFailure.ToString(),
            },
            lifetime = new
            {
                startup_joined = startupJoined,
                startup_failed = startupFailed,
                stop_joined = stopJoined,
                physically_settled = physicallySettled,
            },
            original_stop_delegate_calls = stopProcedureCalls,
            final_unit = new
            {
                id = finalUnit.Id,
                active_state = finalUnit.ActiveState,
                sub_state = finalUnit.SubState,
                main_pid = finalUnit.MainPid,
                exec_main_pid = finalUnit.ExecMainPid,
                exec_main_code = finalUnit.ExecMainCode,
                exec_main_status = finalUnit.ExecMainStatus,
                stopped = unitStopped,
            },
            final_group_after_pumps = new
            {
                sample_taken = groupAfterPumps,
                empty = LinuxAccountUtility.GroupEmpty(group),
                exists = group.Exists,
                populated = group.Populated,
                frozen = group.Frozen,
                device_major = group.DeviceMajor,
                device_minor = group.DeviceMinor,
                inode = group.KernelInode,
            },
            pumps = new
            {
                joined = true,
                received_bytes = output.ReceivedBytes,
                discarded_bytes = output.DiscardedBytes,
                failure = output.Failure.ToString(),
                stdout_eof = output.Stdout.EndOfStream,
                stdout_failure = output.Stdout.Failure.ToString(),
                stderr_eof = output.Stderr.EndOfStream,
                stderr_failure = output.Stderr.Failure.ToString(),
            },
            root_custody_completed = false,
            native_authority = false,
            native_acceptance = false,
        });
        if (bytes.Length is 0 or > MaximumFrameBytes) throw LinuxSystemdBackend.InvalidControl();
        return bytes;
    }
}
