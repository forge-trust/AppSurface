using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Immutable bounded detached facts for a source-selected negative control, never native authority.</summary>
/// <remarks>
/// Production obtains this data only from <see cref="LinuxWorkerProcess.CaptureNegativeObservation"/> after
/// original holder, task, peer, group and output checks. <see cref="CreateDetached"/> intentionally accepts
/// metadata for pure tests: matching JSON cannot authenticate its producer or recreate a live owner.
/// Fixture source/image binding, actual expected allocation failure, unchanged filesystem objects,
/// quarantine/NSS disposition and independent positive prerequisites remain separate requirements.
/// No method issues admission, custody, a lease, proof, eligibility or acceptance. The kernel result retains
/// no raw output; its separately named joined-stream codec exports copied data only to private failure retention.
/// </remarks>
internal sealed class LinuxNegativeKernelObservation
{
    /// <summary>Maximum complete JSON bytes, before returning a copied result; a caller's LF is additional.</summary>
    internal const int MaximumJsonBytes = 4096;

    /// <summary>Maximum complete original raw stdout/stderr pair permitted in the private cancellation export.</summary>
    internal const int MaximumJoinedStreamBytes = 64 * 1024;

    /// <summary>Maximum private joined-stream JSON line including the caller's single terminal LF.</summary>
    internal const int MaximumJoinedStreamLineBytes = 96 * 1024;
    private readonly byte[] _bytes;

    private LinuxNegativeKernelObservation(byte[] bytes) => _bytes = bytes;

    /// <summary>Gets a defensive copy; changes to a returned array cannot mutate the frozen observation.</summary>
    internal byte[] Bytes => _bytes.ToArray();

    /// <summary>Gets the complete bounded JSON length, not a native measurement or execution allowance.</summary>
    internal int Length => _bytes.Length;

    /// <summary>Checks detached shapes and encodes copied facts without filesystem, process or network access.</summary>
    /// <param name="generation">Nonempty original generation data; selecting it grants no ownership.</param>
    /// <param name="identity">Original captured proc sample, or detached test metadata.</param>
    /// <param name="terminal">Original monitored selected normal-exit unit facts, or detached test metadata.</param>
    /// <param name="group">Fresh existing custody-guard sample, or detached test metadata.</param>
    /// <param name="output">Original joined full-retention output facts, or detached test metadata.</param>
    /// <param name="readyDescriptorSha256">Descriptor digest associated with an actual committed READY in production.</param>
    /// <param name="token">Original caller token; no deadline or cancellation source is created.</param>
    /// <returns>Detached immutable JSON. Calling this data seam does not establish the stated provenance.</returns>
    /// <exception cref="InvalidOperationException">Fixed closed rejection, without supplied text or inner error.</exception>
    /// <exception cref="OperationCanceledException">The original token is cancelled, without side effects.</exception>
    /// <remarks>
    /// CLD_EXITED/status is recorded rather than replaced with a generic root/join result. Status zero is
    /// encodable data and does not qualify a negative case. Both EOFs and complete retention are mandatory
    /// to hash actual full bytes; discarded prefixes, signals, ambiguous terminal and malformed data reject.
    /// Group absence retains null inode/population data. Empty group data alone never proves physical exit.
    /// </remarks>
    internal static LinuxNegativeKernelObservation CreateDetached(Guid generation, LinuxProcessSample identity,
        LinuxUnitProperties terminal, LinuxCgroupSample group, SupervisionOutputReceipt output,
        string readyDescriptorSha256, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (generation == Guid.Empty || identity is null || terminal is null || group is null
                || output is null || !IsDigest(readyDescriptorSha256)) throw Rejected();
            var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, generation);
            if (identity.Stat.Pid is 0 or > int.MaxValue) throw Rejected();
            LinuxProcessData.RequireExpected(identity, identity.Stat.Pid, identity.Uids.Real,
                identity.Gids.Real, unit, LinuxProcessSamplingRole.Worker);
            if (terminal.Id != unit.Value || terminal.LoadState != "loaded" || terminal.MainPid != 0
                || terminal.ExecMainPid != identity.Stat.Pid || terminal.ExecMainCode != 1
                || terminal.ExecMainStatus is < 0 or > 255 || terminal.Type != "exec"
                || terminal.KillMode != "control-group" || !terminal.RemainAfterExit
                || terminal.User != identity.Uids.Real.ToString(CultureInfo.InvariantCulture)
                || terminal.Group != identity.Gids.Real.ToString(CultureInfo.InvariantCulture)
                || terminal.ControlGroup != string.Empty && terminal.ControlGroup != identity.ControlGroup
                || !((terminal.ActiveState == "active" && terminal.SubState == "exited")
                    || (terminal.ActiveState == "inactive" && terminal.SubState == "dead")
                    || (terminal.ActiveState == "failed" && terminal.SubState == "failed"))) throw Rejected();
            if (!LinuxAccountUtility.GroupEmpty(group) || output.Stdout is null || output.Stderr is null
                || !output.Successful
                || output.ReceivedByteLimit is <= 0 or > EvidenceRunBudgetLimits.MaximumProcessOutputBytes)
                throw Rejected();
            RequireStream(output.Stdout);
            RequireStream(output.Stderr);
            if (output.DiscardedBytes != 0 || output.ReceivedBytes < 0
                || output.ReceivedBytes != checked(output.Stdout.ReceivedBytes + output.Stderr.ReceivedBytes))
                throw Rejected();
            token.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "issue779-negative-kernel-observation-v1",
                generation = generation.ToString("N"), worker_unit = unit.Value,
                process = new
                {
                    pid = identity.Stat.Pid, starttime_ticks = identity.Stat.StartTimeTicks,
                    uid4 = Ids(identity.Uids), gid4 = Ids(identity.Gids), control_group = identity.ControlGroup,
                },
                ready = new { committed = true, descriptor_sha256 = readyDescriptorSha256 },
                terminal = new
                {
                    exec_main_pid = terminal.ExecMainPid, exec_main_code = terminal.ExecMainCode,
                    exec_main_status = terminal.ExecMainStatus, active_state = terminal.ActiveState,
                    sub_state = terminal.SubState,
                },
                cgroup = new
                {
                    exists = group.Exists, populated = group.Populated, frozen = group.Frozen,
                    device_major = group.DeviceMajor, device_minor = group.DeviceMinor, inode = group.KernelInode,
                },
                pumps = new
                {
                    stdout = Stream(output.Stdout), stderr = Stream(output.Stderr),
                    received_bytes = output.ReceivedBytes, received_byte_limit = output.ReceivedByteLimit,
                    failure = "None", discarded_bytes = output.DiscardedBytes,
                },
                joins = new { startup = true, pending_stop = true, monitor = true, server = true, pumps = true },
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

    /// <summary>Encodes a complete detached output pair as copied base64 data without reading any stream.</summary>
    /// <param name="generation">Original generation in production; detached metadata establishes no owner.</param>
    /// <param name="output">Original immutable joined receipt, or metadata for pure codec controls.</param>
    /// <param name="token">Original cleanup token; no new allowance or cancellation source is created.</param>
    /// <returns>One bounded private JSON object, without LF, granting no native authority or acceptance.</returns>
    /// <exception cref="InvalidOperationException">Fixed rejection with no raw output or inner exception.</exception>
    /// <exception cref="OperationCanceledException">Original cancellation, preserved before and after encoding.</exception>
    /// <remarks>
    /// Production calls only through the original worker holder after its unchanged negative-observation
    /// guards. Both actual EOFs, no pump/shared failure, no discarded bytes and exact original shared
    /// accounting are required. The 64 KiB limit is an additional export limit, never a raised pump quota.
    /// Pure calls cannot prove joins, signal delivery, caller cancellation, process identity or custody.
    /// Fixed fields and base64/hash alphabets alone use relaxed escaping to fit the 96 KiB line bound;
    /// the result belongs only in root-private failure retention, never an HTML or public diagnostic flow.
    /// </remarks>
    internal static byte[] EncodeJoinedStreamsDetached(Guid generation, SupervisionOutputReceipt output,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (generation == Guid.Empty || output is null || output.Stdout is null || output.Stderr is null
                || output.ReceivedByteLimit is <= 0 or > EvidenceRunBudgetLimits.MaximumProcessOutputBytes)
                throw Rejected();
            RequireStream(output.Stdout);
            RequireStream(output.Stderr);
            var received = checked(output.Stdout.ReceivedBytes + output.Stderr.ReceivedBytes);
            if (!output.Successful || output.DiscardedBytes != 0 || output.ReceivedBytes != received
                || received is < 0 or > MaximumJoinedStreamBytes) throw Rejected();
            token.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "issue779-cancellation-joined-streams-v1",
                generation = generation.ToString("N"),
                stdout = JoinedStream(output.Stdout), stderr = JoinedStream(output.Stderr),
                received_bytes = received, received_byte_limit = output.ReceivedByteLimit,
                observation_only = true, native_authority = false, native_acceptance = false,
            }, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            token.ThrowIfCancellationRequested();
            if (bytes.Length is 0 or >= MaximumJoinedStreamLineBytes) throw Rejected();
            return bytes;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or OverflowException or JsonException)
        { throw Rejected(); }
    }

    private static object JoinedStream(SupervisionOutputStreamReceipt value) => new
    {
        received_bytes = value.ReceivedBytes, retained_bytes = value.Prefix.Length,
        discarded_bytes = value.DiscardedBytes, eof = value.EndOfStream, failure = "None",
        sha256 = Convert.ToHexStringLower(SHA256.HashData(value.Prefix.AsSpan())),
        base64 = Convert.ToBase64String(value.Prefix.AsSpan()),
    };

    private static uint[] Ids(LinuxProcessIds value) =>
        [value.Real, value.Effective, value.Saved, value.FileSystem];

    private static object Stream(SupervisionOutputStreamReceipt value) => new
    {
        received_bytes = value.ReceivedBytes, retained_bytes = value.Prefix.Length,
        discarded_bytes = value.DiscardedBytes, eof = value.EndOfStream, failure = "None",
        sha256 = Convert.ToHexStringLower(SHA256.HashData(value.Prefix.AsSpan())),
    };

    private static void RequireStream(SupervisionOutputStreamReceipt value)
    {
        if (value is null || value.Prefix.IsDefault || value.ReceivedBytes < 0
            || value.Prefix.Length > 1024 * 1024 || value.ReceivedBytes != value.Prefix.Length
            || !value.EndOfStream || value.Failure != SupervisionOutputFailure.None) throw Rejected();
    }

    private static bool IsDigest(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static InvalidOperationException Rejected() =>
        new("ASEVD410: Negative kernel observation data rejected.");
}
