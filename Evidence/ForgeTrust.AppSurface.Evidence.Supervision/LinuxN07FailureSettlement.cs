using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Detached failure-only observations of original joins; never successful native settlement.</summary>
/// <remarks>
/// The native holder supplies its original identity, final selected-unit sample, existing final cgroup
/// read and full joined output. Faulted/cancelled monitor tasks remain faulted/cancelled. This separate
/// projection cannot set PhysicallySettled, weaken custody, clear failure or authenticate uploaded JSON.
/// CreateDetached is a pure metadata seam, not a process factory. No exception is retained. A private
/// immutable snapshot retains full original stdout/stderr bytes solely for bounded root-private transfer;
/// it must never be rendered as a safe public diagnostic or used as authority.
/// </remarks>
internal sealed class LinuxN07FailureSettlement
{
    internal const int MaximumJsonBytes = 4096;
    /// <summary>Maximum complete stderr transfer; larger output is inconclusive, never truncated.</summary>
    internal const int MaximumPrivateStderrBytes = 64 * 1024;
    /// <summary>Maximum private serialized raw-pair frame including its later LF.</summary>
    internal const int MaximumPrivateRawFrameBytes = 96 * 1024;
    private readonly byte[] _bytes;
    private readonly byte[] _stdout;
    private readonly byte[] _stderr;
    private readonly long _stdoutReceivedBytes;
    private readonly long _stderrReceivedBytes;
    private LinuxN07FailureSettlement(byte[] bytes, SupervisionOutputReceipt output)
    {
        _bytes = bytes;
        _stdout = output.Stdout.Prefix.ToArray();
        _stderr = output.Stderr.Prefix.ToArray();
        _stdoutReceivedBytes = output.Stdout.ReceivedBytes;
        _stderrReceivedBytes = output.Stderr.ReceivedBytes;
    }
    internal byte[] Bytes => _bytes.ToArray();

    /// <summary>Encodes only complete original join/output facts and an actual normal or signal terminal.</summary>
    /// <remarks>No live sampling, token renewal, I/O, authority or acceptance occurs in this data encoder.</remarks>
    internal static LinuxN07FailureSettlement CreateDetached(Guid generation, LinuxProcessSample identity,
        LinuxUnitProperties terminal, LinuxCgroupSample group, SupervisionOutputReceipt output,
        TaskStatus monitorStatus, bool startupJoined, bool pendingStopJoined, bool unitJoined,
        bool groupJoined, bool pipesClosed)
    {
        if (generation == Guid.Empty || identity is null || terminal is null || group is null || output is null
            || !startupJoined || !pendingStopJoined || !unitJoined || !groupJoined || !pipesClosed
            || monitorStatus is not (TaskStatus.RanToCompletion or TaskStatus.Faulted or TaskStatus.Canceled))
            throw Rejected();
        var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, generation);
        LinuxProcessData.RequireExpected(identity, identity.Stat.Pid, identity.Uids.Real,
            identity.Gids.Real, unit, LinuxProcessSamplingRole.Worker);
        var normal = terminal.ExecMainCode == 1 && terminal.ExecMainStatus is >= 0 and <= 255;
        var signal = terminal.ExecMainCode is 2 or 3 && terminal.ExecMainStatus is >= 1 and <= 64;
        if (terminal.Id != unit.Value || terminal.LoadState != "loaded" || terminal.MainPid != 0
            || terminal.ExecMainPid != identity.Stat.Pid || !(normal || signal)
            || terminal.Type != "exec" || terminal.KillMode != "control-group" || !terminal.RemainAfterExit
            || terminal.User != identity.Uids.Real.ToString(CultureInfo.InvariantCulture)
            || terminal.Group != identity.Gids.Real.ToString(CultureInfo.InvariantCulture)
            || terminal.ControlGroup != string.Empty && terminal.ControlGroup != identity.ControlGroup
            || !((terminal.ActiveState == "inactive" && terminal.SubState == "dead")
                || (terminal.ActiveState == "failed" && terminal.SubState == "failed"))) throw Rejected();
        if (output.Stdout is null || output.Stderr is null
            || !LinuxAccountUtility.GroupEmpty(group) || !output.Successful
            || output.ReceivedByteLimit is <= 0 or > EvidenceRunBudgetLimits.MaximumProcessOutputBytes
            ) throw Rejected();
        RequireStream(output.Stdout); RequireStream(output.Stderr);
        if (output.Stdout.ReceivedBytes != 0 || output.Stderr.Prefix.Length > MaximumPrivateStderrBytes) throw Rejected();
        RequirePrecleanupPrefix(output.Stderr.Prefix.AsSpan());
        if (output.DiscardedBytes != 0 || output.ReceivedBytes < 0
            || output.ReceivedBytes != checked(output.Stdout.ReceivedBytes + output.Stderr.ReceivedBytes))
            throw Rejected();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "issue779-n07-failure-settlement-v1", generation = generation.ToString("N"),
            worker_unit = unit.Value,
            process = new { pid = identity.Stat.Pid, starttime_ticks = identity.Stat.StartTimeTicks,
                uid4 = Ids(identity.Uids), gid4 = Ids(identity.Gids), control_group = identity.ControlGroup },
            terminal = new { exec_main_pid = terminal.ExecMainPid, exec_main_code = terminal.ExecMainCode,
                exec_main_status = terminal.ExecMainStatus, active_state = terminal.ActiveState,
                sub_state = terminal.SubState, source = "original-final-selected-unit-read" },
            monitor = new { completed = true, status = monitorStatus.ToString(),
                completed_successfully = monitorStatus == TaskStatus.RanToCompletion },
            joins = new { startup = startupJoined, pending_stop = pendingStopJoined,
                selected_unit = unitJoined, group = groupJoined, monitor = true, pumps = pipesClosed },
            cgroup = new { exists = group.Exists, populated = group.Populated, frozen = group.Frozen,
                device_major = group.DeviceMajor, device_minor = group.DeviceMinor, inode = group.KernelInode },
            pumps = new { stdout = Stream(output.Stdout), stderr = Stream(output.Stderr),
                received_bytes = output.ReceivedBytes, received_byte_limit = output.ReceivedByteLimit,
                discarded_bytes = output.DiscardedBytes, failure = "None" },
            observation_only = true, native_authority = false, native_acceptance = false,
        });
        if (bytes.Length is 0 or > MaximumJsonBytes) throw Rejected();
        return new(bytes, output);
    }

    /// <summary>Serializes the immutable full pair as one PRIVATE base64 JSON frame, never public diagnostics.</summary>
    /// <param name="token">The original cleanup token; no timer, replacement allowance or writer is created.</param>
    /// <returns>Fresh UTF8 JSON bytes without LF; the complete frame is at most96KiB including LF.</returns>
    /// <remarks>
    /// CreateDetached already required actual complete EOFs, no error/discard, zero stdout, original joins
    /// and the exact N07 allocation prefix. The native caller remains the original bound worker holder;
    /// detached test inputs grant nothing. Byte-array JSON values encode base64 directly, preserving every
    /// byte without text conversion or HTML escaping expansion. Decode only into root600 private capture.
    /// This method cannot acquire a process/FD, authenticate supplied JSON, clear failure or issue custody.
    /// </remarks>
    internal byte[] SerializePrivateStreams(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_stdoutReceivedBytes != 0 || _stdout.Length != 0
            || _stderrReceivedBytes != _stderr.Length || _stderr.Length > MaximumPrivateStderrBytes) throw Rejected();
        RequirePrecleanupPrefix(_stderr);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "issue779-n07-joined-worker-raw-v1",
            stdout_base64 = _stdout, stderr_base64 = _stderr,
            stdout_bytes = _stdoutReceivedBytes, stderr_bytes = _stderrReceivedBytes,
            native_authority = false,
        });
        if (bytes.Length == 0 || checked(bytes.Length + 1) > MaximumPrivateRawFrameBytes) throw Rejected();
        token.ThrowIfCancellationRequested();
        return bytes;
    }

    /// <summary>Checks the complete original prefix for the one frame, without emitting any worker text.</summary>
    internal static void RequirePrecleanupPrefix(ReadOnlySpan<byte> prefix)
    {
        var newline = prefix.IndexOf((byte)'\n');
        if (newline < 0) throw Rejected();
        SupervisionN07PrecleanupObservation.Validate(prefix[..newline].ToArray());
        // A second frame or injected schema can never be credited by final projection.
        var tail = System.Text.Encoding.UTF8.GetString(prefix[(newline + 1)..]);
        if (tail.Contains("issue779-n07-precleanup-allocation-fault-v1", StringComparison.Ordinal)) throw Rejected();
    }

    private static uint[] Ids(LinuxProcessIds value) => [value.Real, value.Effective, value.Saved, value.FileSystem];
    private static object Stream(SupervisionOutputStreamReceipt stream) => new
    {
        received_bytes = stream.ReceivedBytes, retained_bytes = stream.Prefix.Length,
        discarded_bytes = stream.DiscardedBytes, eof = stream.EndOfStream, failure = "None",
        sha256 = Convert.ToHexStringLower(SHA256.HashData(stream.Prefix.AsSpan())),
    };
    private static void RequireStream(SupervisionOutputStreamReceipt stream)
    {
        if (stream.Prefix.IsDefault || stream.Prefix.Length > 1024 * 1024 || stream.ReceivedBytes < 0
            || stream.ReceivedBytes != stream.Prefix.Length || !stream.EndOfStream
            || stream.Failure != SupervisionOutputFailure.None) throw Rejected();
    }
    private static InvalidOperationException Rejected() => new("N07 failure settlement data rejected.");
}
