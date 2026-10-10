using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed completion states of the original joined worker monitor task.</summary>
internal enum LinuxFailedMonitorState
{
    /// <summary>The original monitor task completed successfully.</summary>
    Completed,
    /// <summary>The original monitor task completed with a fault; its exception text is omitted.</summary>
    Faulted,
    /// <summary>The original monitor task completed through cancellation.</summary>
    Cancelled,
}

/// <summary>Closed private diagnostic families; selecting a family grants no execution authority.</summary>
internal enum LinuxFailedSettlementScenario
{
    /// <summary>Original synchronous worker-stall failure, retaining its existing schema.</summary>
    WorkerStall,
    /// <summary>Original caller cancellation with unavailable successful negative-kernel projection.</summary>
    OriginalCancellation,
}

/// <summary>Original joined lifetime and finalization facts; these flags grant no native authority.</summary>
/// <param name="StartupJoined">Whether the complete original startup task has joined.</param>
/// <param name="StopJoined">Whether the original containment and finalization task has joined.</param>
/// <param name="LifetimeFailed">The original sticky lifetime failure state.</param>
/// <param name="PhysicallySettled">The existing settlement flag, which may be false.</param>
/// <param name="Monitor">The actual completion state of the original monitor task.</param>
/// <param name="CgroupAfterPumps">Whether the retained finalization cgroup sample followed original pump joins.</param>
internal sealed record LinuxFailedSettlementState(bool StartupJoined, bool StopJoined, bool LifetimeFailed,
    bool PhysicallySettled, LinuxFailedMonitorState Monitor, bool CgroupAfterPumps);

/// <summary>Encodes bounded failure-only facts from the original joined worker; never a success receipt.</summary>
/// <remarks>
/// Only <see cref="LinuxWorkerProcess.CaptureFailedSettlementObservation"/> supplies native provenance.
/// <see cref="CreateDetached"/> is metadata-only and grants no identity, admission, custody, proof or acceptance.
/// Full raw streams and their digests are included only when both pumps reached EOF with exact retained and
/// received byte counts, no discard or error, and a combined size within the raw bound. Otherwise raw and
/// digest values are null while the actual closed pump failure and count metadata remains. The producer emits
/// this record only for compile-selected N11 or the N08/N09 failure fallback, after original joins and
/// before custody/account closure. Its scenario changes only the fixed schema label.
/// </remarks>
internal sealed class LinuxFailedSettlementObservation
{
    /// <summary>Maximum serialized JSON bytes, excluding the emitter's single line feed.</summary>
    internal const int MaximumJsonBytes = 64 * 1024;
    /// <summary>Maximum combined raw bytes eligible for exact export.</summary>
    internal const int MaximumRawBytes = 32 * 1024;
    private readonly byte[] _bytes;

    private LinuxFailedSettlementObservation(byte[] bytes) => _bytes = bytes;

    /// <summary>Returns a defensive copy of the detached JSON bytes.</summary>
    internal byte[] Bytes => _bytes.ToArray();

    /// <summary>Maps only a completed task; pending work is rejected rather than represented as joined.</summary>
    internal static LinuxFailedMonitorState ClosedTaskState(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!task.IsCompleted) throw Rejected();
        return task.IsCanceled ? LinuxFailedMonitorState.Cancelled
            : task.IsFaulted ? LinuxFailedMonitorState.Faulted : LinuxFailedMonitorState.Completed;
    }

    /// <summary>Builds fixed-schema data from copied metadata without OS access, waits or exception text.</summary>
    /// <param name="generation">Original generation value, not an ownership grant.</param>
    /// <param name="identity">Original pre-exit process sample.</param>
    /// <param name="descriptorSha256">Descriptor digest bound by original committed READY.</param>
    /// <param name="state">Original lifetime and finalization facts.</param>
    /// <param name="pending">Original closed pending-start snapshot.</param>
    /// <param name="terminal">Last original finalization read, when one completed.</param>
    /// <param name="group">Last original finalization cgroup sample, when one completed.</param>
    /// <param name="output">Original output receipt assigned after pump joins.</param>
    /// <param name="token">Original cleanup token; cancellation prevents publication.</param>
    /// <param name="scenario">Closed private diagnostic family, never a runtime execution selector.</param>
    /// <returns>Bounded detached failure data that cannot upgrade settlement.</returns>
    /// <exception cref="InvalidOperationException">A supplied closed metadata value violates the fixed schema.</exception>
    /// <exception cref="OperationCanceledException">The original cleanup token is cancelled.</exception>
    internal static LinuxFailedSettlementObservation CreateDetached(Guid generation, LinuxProcessSample identity,
        string descriptorSha256, LinuxFailedSettlementState state, SupervisionPendingStartSnapshot pending,
        LinuxUnitProperties? terminal, LinuxCgroupSample? group, SupervisionOutputReceipt output,
        CancellationToken token = default, LinuxFailedSettlementScenario scenario = LinuxFailedSettlementScenario.WorkerStall)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (generation == Guid.Empty || identity is null || state is null || pending is null || output is null
                || descriptorSha256 is not { Length: 64 }
                || descriptorSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || !state.StartupJoined || !state.StopJoined || !pending.StartReserved || !pending.StartJoined
                || !pending.Closed || !pending.StopJoined || !Enum.IsDefined(state.Monitor)
                || !Enum.IsDefined(pending.FirstFailure) || !Enum.IsDefined(scenario)) throw Rejected();
            var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, generation);
            LinuxProcessData.RequireExpected(identity, identity.Stat.Pid, identity.Uids.Real,
                identity.Gids.Real, unit, LinuxProcessSamplingRole.Worker);
            if (terminal is not null && (terminal.Id != unit.Value || terminal.ExecMainPid != identity.Stat.Pid
                || terminal.ExecMainCode is < 0 or > 6 || terminal.ExecMainStatus is < 0 or > 255)) throw Rejected();
            if (group is null && state.CgroupAfterPumps || group is not null && (group.Exists
                ? group.Populated is null || group.Frozen is null || group.DeviceMajor is null
                    || group.DeviceMinor is null || group.KernelInode is null or 0
                : group.Populated is not null || group.Frozen is not null || group.DeviceMajor is not null
                    || group.DeviceMinor is not null || group.KernelInode is not null)) throw Rejected();
            RequireStream(output.Stdout); RequireStream(output.Stderr);
            if (!Enum.IsDefined(output.Failure) || output.ReceivedBytes < 0 || output.ReceivedByteLimit is <= 0
                or > EvidenceRunBudgetLimits.MaximumProcessOutputBytes
                || output.ReceivedBytes != checked(output.Stdout.ReceivedBytes + output.Stderr.ReceivedBytes))
                throw Rejected();
            var complete = output.Successful && output.DiscardedBytes == 0
                && output.Stdout.DiscardedBytes == 0 && output.Stderr.DiscardedBytes == 0
                && output.Stdout.EndOfStream && output.Stderr.EndOfStream
                && output.Stdout.Failure == SupervisionOutputFailure.None
                && output.Stderr.Failure == SupervisionOutputFailure.None
                && output.Stdout.ReceivedBytes == output.Stdout.Prefix.Length
                && output.Stderr.ReceivedBytes == output.Stderr.Prefix.Length
                && output.ReceivedBytes <= MaximumRawBytes;
            token.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = scenario == LinuxFailedSettlementScenario.WorkerStall
                    ? "issue779-n11-original-failed-settlement-v1"
                    : "issue779-cancellation-original-failed-settlement-v1",
                generation = generation.ToString("N"),
                worker_unit = unit.Value,
                process = new { pid = identity.Stat.Pid, starttime_ticks = identity.Stat.StartTimeTicks,
                    uid4 = Ids(identity.Uids), gid4 = Ids(identity.Gids), control_group = identity.ControlGroup },
                ready = new { committed = true, descriptor_sha256 = descriptorSha256 },
                lifetime = new { startup_joined = state.StartupJoined, stop_joined = state.StopJoined,
                    failed = state.LifetimeFailed, physically_settled = state.PhysicallySettled },
                pending_start = new { start_reserved = pending.StartReserved, started = pending.Started,
                    start_joined = pending.StartJoined, closed = pending.Closed, stop_joined = pending.StopJoined,
                    first_failure = pending.FirstFailure.ToString() },
                monitor = state.Monitor.ToString(),
                terminal = terminal is null ? null : new
                { exec_main_pid = terminal.ExecMainPid, exec_main_code = terminal.ExecMainCode,
                    exec_main_status = terminal.ExecMainStatus },
                cgroup = group is null ? null : new { exists = group.Exists, populated = group.Populated,
                    frozen = group.Frozen, device_major = group.DeviceMajor, device_minor = group.DeviceMinor,
                    inode = group.KernelInode, after_pumps = state.CgroupAfterPumps },
                pumps = new { joined = true, stdout = Stream(output.Stdout, complete),
                    stderr = Stream(output.Stderr, complete), received_bytes = output.ReceivedBytes,
                    received_byte_limit = output.ReceivedByteLimit, failure = output.Failure.ToString(),
                    discarded_bytes = output.DiscardedBytes, quota_exceeded = output.QuotaExceeded,
                    stop_signal_failed = output.StopSignalFailed, export_complete = complete },
                observation_only = true, native_authority = false, native_acceptance = false,
            });
            token.ThrowIfCancellationRequested();
            if (bytes.Length is 0 or > MaximumJsonBytes) throw Rejected();
            return new(bytes);
        }
        catch (Exception error) when (error is EvidenceAdmissionException or ArgumentException
            or InvalidOperationException or OverflowException or JsonException)
        { throw Rejected(); }
    }

    private static uint[] Ids(LinuxProcessIds ids) => [ids.Real, ids.Effective, ids.Saved, ids.FileSystem];

    private static object Stream(SupervisionOutputStreamReceipt value, bool complete) => new
    {
        received_bytes = value.ReceivedBytes, retained_bytes = value.Prefix.Length,
        discarded_bytes = value.DiscardedBytes, eof = value.EndOfStream, failure = value.Failure.ToString(),
        sha256 = complete ? Convert.ToHexStringLower(SHA256.HashData(value.Prefix.AsSpan())) : null,
        raw_base64 = complete ? Convert.ToBase64String(value.Prefix.AsSpan()) : null,
    };

    private static void RequireStream(SupervisionOutputStreamReceipt value)
    {
        if (value is null || value.Prefix.IsDefault || value.ReceivedBytes < value.Prefix.Length
            || value.Prefix.Length > EvidenceRunBudgetLimits.RetainedOutputPrefixBytesPerStream
            || !Enum.IsDefined(value.Failure)) throw Rejected();
    }

    private static InvalidOperationException Rejected() =>
        new("ASEVD410: Original failed-settlement observation data rejected.");
}
