using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Protected Linux launcher facts delivered over a root-owned credential-checked control channel.</summary>
/// <remarks>This provisional internal schema is not an envelope, portable receipt or public provider registration.</remarks>
internal sealed record EvidenceLinuxWorkerDescriptor(
    string Schema,
    string RunId,
    int WorkerPid,
    uint WorkerUid,
    uint WorkerGid,
    uint SubjectUid,
    uint SubjectGid,
    string Unit,
    string Cgroup,
    DateTimeOffset JobDeadlineUtc,
    string ToolRoot,
    string SubjectRoot,
    string OutputParent,
    string OutputSlot,
    string DotnetPath,
    string TestOutputRoot,
    string PolicyFile,
    string Mode,
    string SocketPath,
    string EntrySha256,
    string BaseRevision,
    string SubjectRevision,
    string WorkflowIdentity,
    string Provider,
    string Platform,
    string ProofDigest,
    string PolicySha256,
    EvidenceLinuxArtifactIdentity OutputParentIdentity,
    string[] ObservationProfileIds,
    string[] ObservationProducerIds,
    string[] Paths,
    int AdmissionSeconds,
    int StartSeconds,
    int CollectionSeconds,
    int CleanupSeconds,
    int StoppingSeconds,
    string? DiffFile = null,
    string? Solution = null,
    string? DiffSha256 = null);

/// <summary>A bounded restricted-child result delivered only after the broker confirms child and pump exit.</summary>
internal sealed record EvidenceRestrictedProcessResult(int ExitCode, string Stdout, string Stderr, bool OutputTruncated,
    long ReceivedBytes = 0);

/// <summary>Bounded subject report copied from a broker-retained file only after subject and pump exit.</summary>
internal sealed record EvidenceRestrictedArtifact(string RelativePath, ReadOnlyMemory<byte> Contents);

/// <summary>Talks to the independently armed, root-owned systemd launcher through a credential-checked Unix socket.</summary>
/// <remarks>
/// The broker supplies protected facts and owns all subject process groups. The worker never launches subject code
/// directly. Admission closure is local and irreversible; stopping uses a fresh connection and can interrupt a
/// concurrent blocked run request. No timeout response is treated as an exit acknowledgement.
/// </remarks>
internal sealed partial class EvidenceLinuxWorkerSupervisor : IEvidenceExecutionSupervisor
{
    private const int MaximumResponseBytes = 3 * 1024 * 1024;
    private readonly string _socketPath;
    private readonly int _brokerPid;
    private readonly long _started;
    private readonly TimeSpan _allowance;
    private int _closed;
    private EvidenceLinuxWorkerSupervisor(string socketPath, int brokerPid, EvidenceLinuxWorkerDescriptor descriptor,
        long handshakeStarted, TimeSpan jobAllowance)
    {
        _socketPath = socketPath;
        _brokerPid = brokerPid;
        Descriptor = descriptor;
        _started = handshakeStarted;
        _allowance = jobAllowance;
    }

    /// <summary>Gets the exact protected descriptor captured at handshake, with no environment-derived authority.</summary>
    internal EvidenceLinuxWorkerDescriptor Descriptor { get; }
    /// <inheritdoc />
    public string RunId => Descriptor.RunId;
    /// <summary>Gets the conservative broker-frozen monotonic allowance, including time spent in the handshake.</summary>
    internal TimeSpan JobRemaining
    {
        get
        {
            var remaining = _allowance - TimeProvider.System.GetElapsedTime(_started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    /// <inheritdoc />
    public bool IsArmed => JobRemaining > TimeSpan.Zero
        && Environment.ProcessId == Descriptor.WorkerPid && GetUid() == Descriptor.WorkerUid;

    /// <summary>Checks the root broker peer and validates its single worker/run descriptor before callbacks.</summary>
    internal static async Task<EvidenceLinuxWorkerSupervisor> ConnectAsync(string socketPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new EvidenceAdmissionException("ASEVD402", "Linux worker supervision is unavailable on this platform.");
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        if (!Path.IsPathFullyQualified(socketPath) || socketPath.Length > 100)
            throw new EvidenceAdmissionException("ASEVD402", "The protected control channel is invalid.");
        using var handshakeDeadline = new CancellationTokenSource(EvidenceRunBudgetLimits.Admission);
        using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handshakeDeadline.Token);
        var handshakeStarted = TimeProvider.System.GetTimestamp();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        int brokerPid;
        JsonElement response;
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), handshakeCancellation.Token).ConfigureAwait(false);
            brokerPid = RequireRootPeer(socket);
            response = await ExchangeAsync(socket, new { op = "ready" }, handshakeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (handshakeDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new EvidenceAdmissionException("ASEVD402", "Protected worker authentication exceeded its admission deadline.");
        }
        var remainingSeconds = response.GetProperty("job_remaining_seconds").GetDouble();
        if (!double.IsFinite(remainingSeconds) || remainingSeconds is <= 0 or > 3600)
            throw new EvidenceAdmissionException("ASEVD402", "The protected monotonic job allowance is invalid.");
        var facts = response.GetProperty("descriptor");
        var descriptor = new EvidenceLinuxWorkerDescriptor(
            Text(facts, "schema"), Text(facts, "run_id"), facts.GetProperty("worker_pid").GetInt32(),
            facts.GetProperty("worker_uid").GetUInt32(), facts.GetProperty("worker_gid").GetUInt32(),
            facts.GetProperty("subject_uid").GetUInt32(), facts.GetProperty("subject_gid").GetUInt32(),
            Text(facts, "unit"), Text(facts, "cgroup"), facts.GetProperty("job_deadline_utc").GetDateTimeOffset(),
            Text(facts, "tool_root"), Text(facts, "subject_root"), Text(facts, "output_parent"), Text(facts, "output_slot"),
            Text(facts, "dotnet_path"), Text(facts, "test_output_root"),
            Text(facts, "policy_file"), Text(facts, "mode"), Text(facts, "socket_path"), Text(facts, "entry_sha256"),
            Text(facts, "base_revision"), Text(facts, "subject_revision"), Text(facts, "workflow_identity"),
            Text(facts, "provider"), Text(facts, "platform"), Text(facts, "proof_digest", allowEmpty: true),
            Text(facts, "policy_sha256"), ReadParentIdentity(facts.GetProperty("output_parent_identity")),
            Strings(facts, "observation_profile_ids"), Strings(facts, "observation_producer_ids"), Strings(facts, "paths"),
            facts.GetProperty("admission_seconds").GetInt32(), facts.GetProperty("start_seconds").GetInt32(),
            facts.GetProperty("collection_seconds").GetInt32(), facts.GetProperty("cleanup_seconds").GetInt32(), facts.GetProperty("stopping_seconds").GetInt32(),
            Optional(facts, "diff_file"), Optional(facts, "solution"), Optional(facts, "diff_sha256"));
        if (descriptor.Schema != "evidence-worker-linux-v1" || descriptor.SocketPath != socketPath
            || descriptor.WorkerPid != Environment.ProcessId || descriptor.WorkerUid != GetUid() || descriptor.WorkerGid != GetGid()
            || descriptor.WorkerUid == 0 || descriptor.SubjectUid == 0 || descriptor.SubjectUid == descriptor.WorkerUid
            || descriptor.SubjectGid == descriptor.WorkerGid || descriptor.JobDeadlineUtc <= DateTimeOffset.UtcNow
            || !descriptor.Cgroup.StartsWith("/system.slice/", StringComparison.Ordinal)
            || descriptor.ToolRoot == descriptor.SubjectRoot || descriptor.OutputParent == descriptor.SubjectRoot
            || descriptor.OutputParent == descriptor.ToolRoot
            || new[] { descriptor.ToolRoot, descriptor.SubjectRoot, descriptor.OutputParent, descriptor.PolicyFile,
                descriptor.DotnetPath, descriptor.TestOutputRoot }.Any(static value => !Path.IsPathFullyQualified(value))
            || !Contained(descriptor.ToolRoot, descriptor.PolicyFile)
            || (descriptor.DiffFile is not null && (!Contained(descriptor.ToolRoot, descriptor.DiffFile)
                || descriptor.DiffSha256 is not { Length: 64 } || !descriptor.DiffSha256.All(char.IsAsciiHexDigit)))
            || (descriptor.DiffFile is null && descriptor.DiffSha256 is not null)
            || descriptor.Provider != "github-actions" || descriptor.Platform != "linux-x64"
            || descriptor.AdmissionSeconds is < 1 or > 30 || descriptor.StartSeconds is < 1 or > 120
            || descriptor.CollectionSeconds is < 1 or > 60 || descriptor.CleanupSeconds is < 1 or > 600
            || descriptor.StoppingSeconds is < 1 or > 30 || descriptor.StoppingSeconds > descriptor.CleanupSeconds)
        {
            throw new EvidenceAdmissionException("ASEVD402", "The protected worker descriptor is stale, mismatched or unsupported.");
        }

        return new EvidenceLinuxWorkerSupervisor(socketPath, brokerPid, descriptor, handshakeStarted,
            TimeSpan.FromSeconds(remainingSeconds));
    }

    /// <inheritdoc />
    public void CloseAdmission() => Interlocked.Exchange(ref _closed, 1);

    /// <inheritdoc />
    public async ValueTask RequestStopAsync(CancellationToken stoppingToken)
    {
        CloseAdmission();
        await RequestAsync(new { op = "stop" }, stoppingToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WaitForOwnedExitAsync(CancellationToken stoppingToken)
    {
        var response = await RequestAsync(new { op = "wait" }, stoppingToken).ConfigureAwait(false);
        if (!response.GetProperty("owned_exit").GetBoolean())
            throw new EvidenceAdmissionException("ASEVD410", "Restricted child or output pump exit was not confirmed.");
    }

    /// <summary>Reports terminal worker completion only after local work, artifact finalization and cleanup stop.</summary>
    internal async ValueTask CompleteWorkerAsync(CancellationToken stoppingToken) =>
        _ = await RequestAsync(new { op = "exit" }, stoppingToken).ConfigureAwait(false);

    /// <summary>Launches a tokenized command only through the restricted subject broker, returning acknowledged bounded output.</summary>
    internal async Task<EvidenceRestrictedProcessResult> RunSubjectAsync(
        string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) != 0 || !IsArmed)
            throw new EvidenceAdmissionException("ASEVD410", "Subject launch admission is closed.");
        var response = await RequestAsync(new { op = "run", executable, arguments, working_directory = workingDirectory }, cancellationToken).ConfigureAwait(false);
        return new EvidenceRestrictedProcessResult(response.GetProperty("exit_code").GetInt32(),
            Text(response, "stdout", allowEmpty: true), Text(response, "stderr", allowEmpty: true),
            response.GetProperty("output_truncated").GetBoolean(), response.GetProperty("received_bytes").GetInt64());
    }

    /// <summary>Collects a finite set of bounded hostile reports through retained broker descriptors.</summary>
    /// <remarks>The resulting bytes remain untrusted producer input; numeric assertions are evaluated by protected code.</remarks>
    internal async Task<IReadOnlyList<EvidenceRestrictedArtifact>> CollectArtifactsAsync(string relativeRoot,
        CancellationToken cancellationToken)
    {
        if (!IsArmed) throw new EvidenceAdmissionException("ASEVD402", "The protected worker deadline expired.");
        var response = await RequestAsync(new { op = "artifacts", relative_root = relativeRoot }, cancellationToken).ConfigureAwait(false);
        var declared = response.GetProperty("artifacts");
        if (declared.ValueKind != JsonValueKind.Array || declared.GetArrayLength() > 64)
            throw new EvidenceAdmissionException("ASEVD420", "The restricted report count exceeds its limit.");
        var results = new List<EvidenceRestrictedArtifact>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var item in declared.EnumerateArray())
        {
            var path = EvidenceArtifactValidation.NormalizeRelativePath(Text(item, "path"));
            var length = item.GetProperty("length_bytes").GetInt64();
            if (!names.Add(path) || length is < 0 or > EvidenceCanonicalJson.MaximumInputBytes
                || length > EvidenceRunBudgetLimits.MaximumArtifactBytes - total)
                throw new EvidenceAdmissionException("ASEVD420", "Restricted report declarations exceed their byte or identity limits.");
            total += length;
            using var contents = new MemoryStream((int)length);
            var offset = 0;
            do
            {
                var chunk = await RequestAsync(new { op = "artifact", relative_root = relativeRoot,
                    relative_path = path, offset }, cancellationToken).ConfigureAwait(false);
                var encoded = Text(chunk, "bytes_base64", allowEmpty: true);
                if (encoded.Length > 4 * ((128 * 1024 + 2) / 3))
                    throw new EvidenceAdmissionException("ASEVD420", "Restricted report chunk exceeds its limit.");
                var bytes = Convert.FromBase64String(encoded);
                if (bytes.Length > 128 * 1024 || bytes.Length > length - offset)
                    throw new EvidenceAdmissionException("ASEVD420", "Restricted report changed or exceeded its declared size.");
                contents.Write(bytes);
                offset += bytes.Length;
                var end = chunk.GetProperty("end").GetBoolean();
                if (end != (offset == length) || (!end && bytes.Length == 0))
                    throw new EvidenceAdmissionException("ASEVD420", "Restricted report transfer did not match its declared size.");
                if (end) break;
            } while (true);
            results.Add(new EvidenceRestrictedArtifact(path, contents.ToArray()));
        }
        return results;
    }

    private async Task<JsonElement> RequestAsync<T>(T request, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken).ConfigureAwait(false);
        if (RequireRootPeer(socket) != _brokerPid)
            throw new EvidenceAdmissionException("ASEVD402", "The protected broker identity changed.");
        return await ExchangeAsync(socket, request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ExchangeAsync<T>(Socket socket, T request, CancellationToken cancellationToken)
    {
        var bytes = EvidenceCanonicalJson.Serialize(request);
        if (bytes.Length > 64 * 1024 - 1) throw new EvidenceAdmissionException("ASEVD402", "Control request exceeds its byte limit.");
        using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (output.Length <= MaximumResponseBytes)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EvidenceAdmissionException("ASEVD402", "Protected control acknowledgement is missing.");
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            var length = newline < 0 ? count : newline;
            if (length > MaximumResponseBytes - output.Length)
                throw new EvidenceAdmissionException("ASEVD420", "Control response exceeds its byte limit.");
            output.Write(buffer, 0, length);
            if (newline < 0) continue;
            var response = EvidenceCanonicalJson.Deserialize<JsonElement>(output.ToArray(), MaximumResponseBytes);
            if (!response.GetProperty("ok").GetBoolean())
                throw new EvidenceAdmissionException("ASEVD420", "The protected broker rejected execution or its output budget.");
            return response;
        }

        throw new EvidenceAdmissionException("ASEVD420", "Control response exceeds its byte limit.");
    }

    private static int RequireRootPeer(Socket socket)
    {
        // Socket.GetSocketOption rejects SO_PEERCRED on .NET. Read the Linux ucred directly
        // through libc while SafeSocketHandle retains ownership for the native call.
        uint length = 12;
        if (GetPeerCredentials(socket.SafeHandle, 1, 17, out var credentials, ref length) != 0
            || length != 12 || credentials.Pid <= 0 || credentials.Uid != 0)
            throw new EvidenceAdmissionException("ASEVD402", "A root-owned independent supervisor is required.");
        return credentials.Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials
    {
        internal int Pid;
        internal uint Uid;
        internal uint Gid;
    }

    private static string Text(JsonElement value, string name, bool allowEmpty = false)
    {
        var text = value.GetProperty(name).GetString();
        if (text is null || (!allowEmpty && string.IsNullOrWhiteSpace(text)))
            throw new EvidenceAdmissionException("ASEVD402", "A required control field is missing.");
        return text;
    }

    private static string[] Strings(JsonElement value, string name) =>
        value.GetProperty(name).EnumerateArray().Select(static item => item.GetString() ?? throw new EvidenceAdmissionException("ASEVD402", "A required control list is invalid.")).ToArray();
    private static string? Optional(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind != JsonValueKind.Null ? item.GetString() : null;
    private static bool Contained(string root, string file) => file.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static EvidenceLinuxArtifactIdentity ReadParentIdentity(JsonElement identity) => new(
        identity.GetProperty("device_major").GetUInt32(), identity.GetProperty("device_minor").GetUInt32(),
        identity.GetProperty("inode").GetUInt64(), identity.GetProperty("uid").GetUInt32(), identity.GetProperty("gid").GetUInt32());

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();
    [LibraryImport("libc", EntryPoint = "getgid")]
    private static partial uint GetGid();
    [LibraryImport("libc", EntryPoint = "getsockopt")]
    private static partial int GetPeerCredentials(SafeSocketHandle socket, int level, int option,
        out PeerCredentials credentials, ref uint length);
}
