using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Immutable bounded v1 worker-descriptor and ready-response bytes, as data only.</summary>
/// <remarks>
/// The caller must separately authenticate every process, account, output FD, deployment digest and original
/// remaining allowance. This serializer performs no native operation, admission, channel authentication or
/// clock reset. A positive ready object is wire data only and may be published only after the root owner has
/// established the actual bindings. Standalone descriptor JSON fits 64 KiB; ready JSON reserves one byte
/// for the framing LF, so its complete response line fits 64 KiB. Neither JSON object is truncated.
/// The existing Contracts parsers validate structural compatibility without constructing an armed supervisor.
/// </remarks>
internal sealed class EvidenceWorkerDescriptorData
{
    /// <summary>Maximum bytes for the complete standalone descriptor JSON.</summary>
    internal const int MaximumJsonBytes = 64 * 1024;
    /// <summary>Maximum ready JSON bytes, reserving one LF byte within the 64 KiB response-line limit.</summary>
    internal const int MaximumReadyJsonBytes = MaximumJsonBytes - 1;
    private readonly byte[] _descriptor;
    private readonly byte[] _ready;

    private EvidenceWorkerDescriptorData(byte[] descriptor, byte[] ready)
    {
        _descriptor = descriptor;
        _ready = ready;
    }

    /// <summary>Gets a detached byte snapshot of the exact v1 descriptor, including all three nullable options.</summary>
    internal byte[] DescriptorBytes => _descriptor.ToArray();
    /// <summary>Gets a detached snapshot of the complete ok/descriptor/job_remaining_seconds ready wrapper.</summary>
    internal byte[] ReadyBytes => _ready.ToArray();
    /// <summary>Gets the complete standalone descriptor byte count.</summary>
    internal int DescriptorLength => _descriptor.Length;
    /// <summary>Gets the complete ready-wrapper byte count, including its descriptor and allowance.</summary>
    internal int ReadyLength => _ready.Length;

    /// <summary>Serializes root-selected metadata without authenticating or activating any of it.</summary>
    /// <param name="request">Already parsed immutable observation request; its lists remain copied metadata.</param>
    /// <param name="generation">Nonempty generation shared by the closed layout and generated worker name.</param>
    /// <param name="workerPid">Positive worker PID data, distinct from the broker.</param>
    /// <param name="brokerPid">Positive broker PID data; no SO_PEERCRED check occurs here.</param>
    /// <param name="accounts">Distinct nonroot account data, not an actual account holder.</param>
    /// <param name="layout">Closed layout from this exact generation and account data.</param>
    /// <param name="outputParentIdentity">Measured-parent comparison data with exact worker UID/GID and nonzero inode.</param>
    /// <param name="entrySha256">64 lowercase hexadecimal characters; no bytes are hashed here.</param>
    /// <param name="policySha256">64 lowercase hexadecimal characters; no policy authority is issued.</param>
    /// <param name="remaining">Original positive frozen remainder, at most one hour; no timer is started.</param>
    /// <returns>Copied immutable JSON data only after both complete objects and existing structural parsers accept.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD402 with no supplied text or inner exception.</exception>
    internal static EvidenceWorkerDescriptorData Create(EvidenceSupervisorRequest request, Guid generation,
        int workerPid, int brokerPid, LinuxRunAccountSnapshot accounts, LinuxWorkspaceLayout layout,
        EvidenceLinuxArtifactIdentity outputParentIdentity, string entrySha256, string policySha256,
        TimeSpan remaining)
    {
        try
        {
            Require(request is not null && accounts is not null && layout is not null && generation != Guid.Empty
                && workerPid > 0 && brokerPid > 0 && workerPid != brokerPid
                && LowerHash(entrySha256) && LowerHash(policySha256)
                && remaining > TimeSpan.Zero && remaining <= TimeSpan.FromHours(1));
            var expected = LinuxWorkspaceLayout.Create(generation, accounts);
            Require(layout.AccountData == accounts && layout.Name == expected.Name && layout.Root == expected.Root
                && layout.ControlRoot == expected.ControlRoot && layout.ControlSocket == expected.ControlSocket
                && layout.DescriptorPath == expected.DescriptorPath && layout.OutputParent == expected.OutputParent
                && layout.RawResultsRoot == expected.RawResultsRoot && layout.WorkerGid == expected.WorkerGid
                && layout.Directories.SequenceEqual(expected.Directories)
                && outputParentIdentity.Inode > 0 && outputParentIdentity.Uid == accounts.WorkerUid
                && outputParentIdentity.Gid == accounts.WorkerGid);

            var descriptor = Serialize(writer => WriteDescriptor(writer, request, generation, workerPid,
                brokerPid, accounts, layout, outputParentIdentity, entrySha256, policySha256));
            using (var document = JsonDocument.Parse(descriptor))
                _ = EvidenceLinuxWorkerSupervisor.ParseWorkerDescriptor(document.RootElement);
            var ready = Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WritePropertyName("descriptor");
                writer.WriteRawValue(descriptor, skipInputValidation: false);
                writer.WriteNumber("job_remaining_seconds", remaining.TotalSeconds);
                writer.WriteEndObject();
            }, MaximumReadyJsonBytes);
            using (var document = JsonDocument.Parse(ready))
            {
                _ = EvidenceLinuxWorkerSupervisor.ParseWorkerRemainingAllowance(document.RootElement);
                _ = EvidenceLinuxWorkerSupervisor.ParseWorkerDescriptor(document.RootElement.GetProperty("descriptor"));
            }
            return new(descriptor, ready);
        }
        catch (EvidenceAdmissionException) { throw Rejected(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
                                     or JsonException or KeyNotFoundException or OverflowException)
        { throw Rejected(); }
    }

    /// <summary>Field-based metadata overload for portable controls that cannot construct the Contracts identity type.</summary>
    /// <remarks>These numbers are only comparison data; this overload opens no FD and manufactures no lease.</remarks>
    /// <param name="request">Already parsed immutable request.</param>
    /// <param name="generation">Selected generation data.</param>
    /// <param name="workerPid">Worker PID data.</param>
    /// <param name="brokerPid">Distinct broker PID data.</param>
    /// <param name="accounts">Account snapshot data.</param>
    /// <param name="layout">Closed layout data.</param>
    /// <param name="deviceMajor">Output-parent device major data.</param>
    /// <param name="deviceMinor">Output-parent device minor data.</param>
    /// <param name="inode">Nonzero output-parent inode data.</param>
    /// <param name="uid">Exact worker UID data.</param>
    /// <param name="gid">Exact worker GID data.</param>
    /// <param name="entrySha256">Validated entry digest spelling.</param>
    /// <param name="policySha256">Validated policy digest spelling.</param>
    /// <param name="remaining">Original remaining allowance data.</param>
    /// <returns>Structurally checked byte snapshots, never runtime authority.</returns>
    internal static EvidenceWorkerDescriptorData Create(EvidenceSupervisorRequest request, Guid generation,
        int workerPid, int brokerPid, LinuxRunAccountSnapshot accounts, LinuxWorkspaceLayout layout,
        uint deviceMajor, uint deviceMinor, ulong inode, uint uid, uint gid,
        string entrySha256, string policySha256, TimeSpan remaining) =>
        Create(request, generation, workerPid, brokerPid, accounts, layout,
            new EvidenceLinuxArtifactIdentity(deviceMajor, deviceMinor, inode, uid, gid),
            entrySha256, policySha256, remaining);

    private static void WriteDescriptor(Utf8JsonWriter writer, EvidenceSupervisorRequest request, Guid generation,
        int workerPid, int brokerPid, LinuxRunAccountSnapshot accounts, LinuxWorkspaceLayout layout,
        EvidenceLinuxArtifactIdentity identity, string entry, string policy)
    {
        var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, generation).Value;
        writer.WriteStartObject();
        writer.WriteString("schema", "evidence-worker-linux-v1");
        writer.WriteString("run_id", "csharp/" + generation.ToString("N"));
        writer.WriteNumber("worker_pid", workerPid); writer.WriteNumber("broker_pid", brokerPid);
        writer.WriteNumber("worker_uid", accounts.WorkerUid); writer.WriteNumber("worker_gid", accounts.WorkerGid);
        writer.WriteNumber("subject_uid", accounts.SubjectUid); writer.WriteNumber("subject_gid", accounts.SubjectGid);
        writer.WriteString("unit", unit); writer.WriteString("cgroup", "/system.slice/" + unit);
        writer.WriteString("job_deadline_utc", request.JobDeadlineUtc);
        writer.WriteString("tool_root", request.ToolRoot); writer.WriteString("subject_root", request.SubjectRoot);
        writer.WriteString("output_parent", layout.OutputParent);
        writer.WriteString("output_slot", LinuxWorkspaceLayout.ArtifactSlot);
        writer.WriteString("dotnet_path", request.RuntimeHost); writer.WriteString("test_output_root", layout.RawResultsRoot);
        writer.WriteString("policy_file", request.PolicyFile); writer.WriteString("mode", "observation");
        writer.WriteString("socket_path", layout.ControlSocket); writer.WriteString("descriptor_path", layout.DescriptorPath);
        writer.WriteString("entry_sha256", entry); writer.WriteString("policy_sha256", policy);
        writer.WriteString("base_revision", request.BaseRevision); writer.WriteString("subject_revision", request.SubjectRevision);
        writer.WriteString("workflow_identity", request.WorkflowIdentity);
        writer.WriteString("provider", "github-actions"); writer.WriteString("platform", "linux-x64");
        writer.WriteString("proof_digest", string.Empty);
        writer.WriteStartObject("output_parent_identity");
        writer.WriteNumber("device_major", identity.DeviceMajor); writer.WriteNumber("device_minor", identity.DeviceMinor);
        writer.WriteNumber("inode", identity.Inode); writer.WriteNumber("uid", identity.Uid); writer.WriteNumber("gid", identity.Gid);
        writer.WriteEndObject();
        WriteList(writer, "observation_profile_ids", request.ObservationProfileIds);
        WriteList(writer, "observation_producer_ids", request.ObservationProducerIds);
        WriteList(writer, "paths", request.Paths);
        writer.WriteNumber("admission_seconds", request.AdmissionSeconds); writer.WriteNumber("start_seconds", request.StartSeconds);
        writer.WriteNumber("collection_seconds", request.CollectionSeconds); writer.WriteNumber("cleanup_seconds", request.CleanupSeconds);
        writer.WriteNumber("stopping_seconds", request.StoppingSeconds);
        writer.WriteString("diff_file", request.DiffFile); writer.WriteString("diff_sha256", request.DiffSha256);
        writer.WriteString("solution", request.Solution);
        writer.WriteEndObject();
    }

    private static void WriteList(Utf8JsonWriter writer, string name, IReadOnlyList<string> items)
    {
        writer.WriteStartArray(name);
        foreach (var item in items) { writer.WriteStringValue(item); writer.Flush(); }
        writer.WriteEndArray();
    }

    private static byte[] Serialize(Action<Utf8JsonWriter> write, int maximumBytes = MaximumJsonBytes)
    {
        using var stream = new BoundedJsonStream(maximumBytes);
        using (var writer = new Utf8JsonWriter(stream)) { write(writer); writer.Flush(); }
        return stream.Snapshot();
    }

    private static bool LowerHash(string? value) => value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw Rejected(); }
    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD402", "The worker descriptor data is invalid or exceeds its bound.");

    private sealed class BoundedJsonStream(int maximumBytes) : Stream
    {
        private readonly byte[] _bytes = new byte[maximumBytes];
        private int _length;
        internal byte[] Snapshot() => _bytes.AsSpan(0, _length).ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Require(buffer.Length <= _bytes.Length - _length);
            buffer.CopyTo(_bytes.AsSpan(_length));
            _length += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
