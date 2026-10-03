using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Protected Linux launcher facts delivered over a root-owned credential-checked control channel.</summary>
/// <remarks>
/// This provisional internal schema is not an envelope, portable receipt or public provider registration.
/// BrokerPid and DescriptorPath retain actual launcher metadata; only ConnectAsync binds the PID to a root peer.
/// Application is absent in v1 and required in v2. Optional constructor defaults support internal data callers only.
/// All parsed path/profile/producer lists are defensively copied and wrapped read-only.
/// </remarks>
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
    IReadOnlyList<string> ObservationProfileIds,
    IReadOnlyList<string> ObservationProducerIds,
    IReadOnlyList<string> Paths,
    int AdmissionSeconds,
    int StartSeconds,
    int CollectionSeconds,
    int CleanupSeconds,
    int StoppingSeconds,
    string? DiffFile = null,
    string? Solution = null,
    string? DiffSha256 = null,
    EvidenceLinuxApplicationDescriptor? Application = null,
    int BrokerPid = 0,
    string? DescriptorPath = null);

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
        EvidenceLinuxWorkerDescriptor descriptor;
        TimeSpan allowance;
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), handshakeCancellation.Token).ConfigureAwait(false);
            brokerPid = RequireRootPeer(socket);
            var response = await ExchangeAsync(socket, new { op = "ready" }, handshakeCancellation.Token).ConfigureAwait(false);
            allowance = ParseWorkerRemainingAllowance(response);
            descriptor = ParseWorkerDescriptor(response.GetProperty("descriptor"));
            ValidateWorkerRuntimeBinding(descriptor, socketPath, brokerPid, Environment.ProcessId, GetUid(), GetGid(), DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (handshakeDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new EvidenceAdmissionException("ASEVD402", "Protected worker authentication exceeded its admission deadline.");
        }
        catch (EvidenceAdmissionException error) { throw NormalizeWorkerHandshakeFailure(error); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException) { throw NormalizeWorkerHandshakeFailure(error); }

        return new EvidenceLinuxWorkerSupervisor(socketPath, brokerPid, descriptor, handshakeStarted, allowance);
    }

    /// <summary>Preserves a host-selected channel diagnostic and normalizes malformed handshake failures.</summary>
    /// <param name="error">Failure from the credential or bounded ready-response checks.</param>
    /// <returns>The original ASEVD402 admission exception, or a fixed ASEVD402 with no supplied values or inner error.</returns>
    /// <remarks>This data-only helper authenticates no peer and creates no supervisor or admission capability.</remarks>
    internal static EvidenceAdmissionException NormalizeWorkerHandshakeFailure(Exception error) =>
        error is EvidenceAdmissionException admission && admission.Code == "ASEVD402"
            ? admission : InvalidWorkerDescriptor();

    /// <summary>Parses a closed v1/v2 descriptor as immutable data, without authenticating a peer or issuing admission.</summary>
    /// <param name="facts">Root descriptor object with actual launcher fields, including broker PID and descriptor path.</param>
    /// <returns>Bounded metadata; only ConnectAsync can construct an armed supervisor after runtime binding.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD402 for malformed, ambiguous or unsupported metadata.</exception>
    internal static EvidenceLinuxWorkerDescriptor ParseWorkerDescriptor(JsonElement facts)
    {
        WorkerRequire(facts.ValueKind == JsonValueKind.Object);
        WorkerRequire(Encoding.UTF8.GetByteCount(facts.GetRawText()) <= MaximumResponseBytes);
        WorkerRequire(facts.TryGetProperty("schema", out var schemaValue));
        var schema = WorkerText(schemaValue, 128);
        WorkerRequire(schema is "evidence-worker-linux-v1" or "evidence-worker-linux-v2");
        var required = new[] { "schema", "run_id", "worker_pid", "broker_pid", "worker_uid", "worker_gid", "subject_uid", "subject_gid",
            "unit", "cgroup", "job_deadline_utc", "tool_root", "subject_root", "output_parent", "output_slot", "dotnet_path", "test_output_root",
            "policy_file", "mode", "socket_path", "descriptor_path", "entry_sha256", "base_revision", "subject_revision", "workflow_identity",
            "provider", "platform", "proof_digest", "policy_sha256", "output_parent_identity", "observation_profile_ids", "observation_producer_ids",
            "paths", "admission_seconds", "start_seconds", "collection_seconds", "cleanup_seconds", "stopping_seconds" };
        if (schema == "evidence-worker-linux-v2") required = [.. required, "application"];
        WorkerObject(facts, required, ["diff_file", "diff_sha256", "solution"]);
        var workerUid = WorkerUInt(facts, "worker_uid"); var workerGid = WorkerUInt(facts, "worker_gid");
        var subjectUid = WorkerUInt(facts, "subject_uid"); var subjectGid = WorkerUInt(facts, "subject_gid");
        WorkerRequire(workerUid > 0 && workerGid > 0 && subjectUid > 0 && subjectGid > 0
            && workerUid != subjectUid && workerGid != subjectGid);
        EvidenceLinuxApplicationDescriptor? application = schema == "evidence-worker-linux-v2"
            ? ParseApplicationDescriptor(facts.GetProperty("application"), workerUid, workerGid, subjectUid, subjectGid) : null;
        var descriptor = new EvidenceLinuxWorkerDescriptor(
            schema, WorkerString(facts, "run_id", 256), WorkerInt(facts, "worker_pid", 1, int.MaxValue),
            workerUid, workerGid, subjectUid, subjectGid, WorkerString(facts, "unit", 128), WorkerString(facts, "cgroup", 256),
            WorkerDate(facts, "job_deadline_utc"), WorkerPath(facts, "tool_root"), WorkerPath(facts, "subject_root"),
            WorkerPath(facts, "output_parent"), WorkerString(facts, "output_slot", 96), WorkerPath(facts, "dotnet_path"),
            WorkerPath(facts, "test_output_root"), WorkerPath(facts, "policy_file"), WorkerString(facts, "mode", 32),
            WorkerPath(facts, "socket_path", 100), WorkerHash(facts, "entry_sha256"), WorkerString(facts, "base_revision", 256),
            WorkerString(facts, "subject_revision", 256), WorkerString(facts, "workflow_identity", 256), WorkerString(facts, "provider", 128),
            WorkerString(facts, "platform", 128), WorkerString(facts, "proof_digest", 64, allowEmpty: true), WorkerHash(facts, "policy_sha256"),
            ReadParentIdentity(facts.GetProperty("output_parent_identity")), WorkerStrings(facts, "observation_profile_ids", 32, identifiers: true),
            WorkerStrings(facts, "observation_producer_ids", 32, identifiers: true), WorkerStrings(facts, "paths", 4096, identifiers: false),
            WorkerInt(facts, "admission_seconds", 1, 30), WorkerInt(facts, "start_seconds", 1, 120),
            WorkerInt(facts, "collection_seconds", 1, 60), WorkerInt(facts, "cleanup_seconds", 1, 600), WorkerInt(facts, "stopping_seconds", 1, 30),
            WorkerOptionalPath(facts, "diff_file"), WorkerOptionalPath(facts, "solution"), WorkerOptionalHash(facts, "diff_sha256"),
            application, WorkerInt(facts, "broker_pid", 1, int.MaxValue), WorkerPath(facts, "descriptor_path"));
        var runParts = descriptor.RunId.Split('/');
        WorkerRequire(runParts.Length == 2 && runParts.All(static item => WorkerName(item, 128))
            && WorkerName(descriptor.Unit, 128) && descriptor.Unit.EndsWith(".service", StringComparison.Ordinal)
            && descriptor.Cgroup == "/system.slice/" + descriptor.Unit && WorkerName(descriptor.OutputSlot, 96));
        WorkerRequire(descriptor.BrokerPid != descriptor.WorkerPid && descriptor.Provider == "github-actions" && descriptor.Platform == "linux-x64"
            && descriptor.Mode is "observation" or "trusted" && descriptor.StoppingSeconds <= descriptor.CleanupSeconds
            && (descriptor.ProofDigest.Length == 0 || WorkerLowerHash(descriptor.ProofDigest)));
        WorkerRequire(!WorkerRootsOverlap(descriptor.ToolRoot, descriptor.SubjectRoot)
            && !WorkerRootsOverlap(descriptor.ToolRoot, descriptor.OutputParent) && !WorkerRootsOverlap(descriptor.SubjectRoot, descriptor.OutputParent)
            && Contained(descriptor.ToolRoot, descriptor.PolicyFile)
            && (descriptor.DiffFile is null ? descriptor.DiffSha256 is null : descriptor.DiffSha256 is not null && Contained(descriptor.ToolRoot, descriptor.DiffFile))
            && (descriptor.Solution is null || Contained(descriptor.SubjectRoot, descriptor.Solution)));
        var descriptorPath = descriptor.DescriptorPath!;
        var separator = descriptorPath.LastIndexOf('/');
        var controlRoot = descriptorPath[..separator];
        WorkerRequire(controlRoot.Length > 0 && descriptorPath[(separator + 1)..] == "worker-control.json"
            && descriptor.SocketPath == controlRoot + "/broker/control.sock"
            && !WorkerRootsOverlap(controlRoot, descriptor.ToolRoot) && !WorkerRootsOverlap(controlRoot, descriptor.SubjectRoot)
            && !WorkerRootsOverlap(controlRoot, descriptor.OutputParent));
        WorkerRequire(descriptor.OutputParentIdentity.Inode > 0 && descriptor.OutputParentIdentity.Uid == workerUid
            && descriptor.OutputParentIdentity.Gid == workerGid);
        return descriptor;
    }

    /// <summary>Parses the closed ready success wrapper and frozen allowance as data only.</summary>
    /// <param name="response">Exact ok/descriptor/job_remaining_seconds wrapper.</param>
    /// <returns>Positive finite allowance at most one hour; does not reset the handshake timestamp.</returns>
    internal static TimeSpan ParseWorkerRemainingAllowance(JsonElement response)
    {
        WorkerObject(response, ["ok", "descriptor", "job_remaining_seconds"], []);
        WorkerRequire(response.GetProperty("ok").ValueKind == JsonValueKind.True && response.GetProperty("descriptor").ValueKind == JsonValueKind.Object);
        var number = response.GetProperty("job_remaining_seconds");
        WorkerRequire(number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var seconds)
            && double.IsFinite(seconds) && seconds is > 0 and <= 3600);
        var allowance = TimeSpan.FromSeconds(number.GetDouble());
        WorkerRequire(allowance > TimeSpan.Zero);
        return allowance;
    }

    /// <summary>Compares parsed facts to independently observed runtime identity; constructs no supervisor or admission.</summary>
    /// <param name="descriptor">Parsed protected metadata.</param>
    /// <param name="socketPath">Actual requested control channel.</param>
    /// <param name="authenticatedBrokerPid">PID read from the actual root SO_PEERCRED.</param>
    /// <param name="currentPid">Actual current worker process ID.</param>
    /// <param name="currentUid">Actual current worker UID.</param>
    /// <param name="currentGid">Actual current worker primary GID.</param>
    /// <param name="utcNow">Actual current time, used only for expiry comparison.</param>
    internal static void ValidateWorkerRuntimeBinding(EvidenceLinuxWorkerDescriptor descriptor, string socketPath,
        int authenticatedBrokerPid, int currentPid, uint currentUid, uint currentGid, DateTimeOffset utcNow)
    {
        WorkerRequire(descriptor is not null && descriptor.SocketPath == socketPath && authenticatedBrokerPid > 0
            && descriptor.BrokerPid == authenticatedBrokerPid && descriptor.WorkerPid == currentPid
            && currentUid > 0 && currentGid > 0 && descriptor.WorkerUid == currentUid && descriptor.WorkerGid == currentGid
            && descriptor.JobDeadlineUtc > utcNow);
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

    private static bool Contained(string root, string file) => file.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static EvidenceLinuxArtifactIdentity ReadParentIdentity(JsonElement identity)
    {
        WorkerObject(identity, ["device_major", "device_minor", "inode", "uid", "gid"], []);
        var inode = identity.GetProperty("inode");
        WorkerRequire(inode.ValueKind == JsonValueKind.Number && inode.TryGetUInt64(out var number) && number > 0);
        return new(WorkerUInt(identity, "device_major"), WorkerUInt(identity, "device_minor"), inode.GetUInt64(),
            WorkerUInt(identity, "uid"), WorkerUInt(identity, "gid"));
    }

    private static void WorkerObject(JsonElement value, IReadOnlyList<string> required, IReadOnlyList<string> optional)
    {
        WorkerRequire(value.ValueKind == JsonValueKind.Object);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            WorkerRequire(names.Add(property.Name) && (required.Contains(property.Name, StringComparer.Ordinal)
                || optional.Contains(property.Name, StringComparer.Ordinal)));
        WorkerRequire(required.All(name => value.TryGetProperty(name, out _)));
    }

    private static string WorkerText(JsonElement value, int maximum, bool allowEmpty = false)
    {
        WorkerRequire(value.ValueKind == JsonValueKind.String);
        var text = value.GetString();
        WorkerRequire(text is not null && text.Length <= maximum && (allowEmpty || !string.IsNullOrWhiteSpace(text)) && !text.Any(char.IsControl));
        return text;
    }
    private static string WorkerString(JsonElement value, string name, int maximum, bool allowEmpty = false) =>
        WorkerText(value.GetProperty(name), maximum, allowEmpty);
    private static int WorkerInt(JsonElement value, string name, int minimum, int maximum)
    {
        var item = value.GetProperty(name); WorkerRequire(item.ValueKind == JsonValueKind.Number
            && item.TryGetInt32(out var number) && number >= minimum && number <= maximum);
        return item.GetInt32();
    }
    private static uint WorkerUInt(JsonElement value, string name)
    {
        var item = value.GetProperty(name); WorkerRequire(item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out _));
        return item.GetUInt32();
    }
    private static DateTimeOffset WorkerDate(JsonElement value, string name)
    {
        var item = value.GetProperty(name); WorkerRequire(item.ValueKind == JsonValueKind.String && item.TryGetDateTimeOffset(out _));
        return item.GetDateTimeOffset();
    }
    private static string WorkerHash(JsonElement value, string name)
    {
        var hash = WorkerString(value, name, 64); WorkerRequire(WorkerLowerHash(hash)); return hash;
    }
    private static string? WorkerOptionalHash(JsonElement value, string name) =>
        !value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null ? null : WorkerHash(value, name);
    private static string? WorkerOptionalPath(JsonElement value, string name) =>
        !value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null ? null : WorkerPath(value, name);
    private static string WorkerPath(JsonElement value, string name, int maximumBytes = 4095)
    {
        var path = WorkerString(value, name, maximumBytes);
        WorkerRequire(path.Length > 1 && path[0] == '/' && !path.Contains('\\') && Encoding.UTF8.GetByteCount(path) <= maximumBytes
            && WorkerSegments(path[1..]));
        return path;
    }
    private static IReadOnlyList<string> WorkerStrings(JsonElement value, string name, int maximum, bool identifiers)
    {
        var array = value.GetProperty(name); WorkerRequire(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= maximum);
        var values = array.EnumerateArray().Select(item => WorkerText(item, identifiers ? 96 : 4095)).ToArray();
        WorkerRequire(values.Distinct(StringComparer.Ordinal).Count() == values.Length);
        WorkerRequire(values.All(item => identifiers ? WorkerName(item, 96) : item[0] != '/' && !item.Contains('\\')
            && Encoding.UTF8.GetByteCount(item) <= 4095 && WorkerSegments(item)));
        return Array.AsReadOnly(values);
    }
    private static bool WorkerName(string value, int maximum) => value.Length is > 0 && value.Length <= maximum
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(static item => char.IsAsciiLetterOrDigit(item) || item is '.' or '_' or '-');
    private static bool WorkerSegments(string value) => value.Split('/').All(static part => part.Length > 0 && part is not "." and not "..");
    private static bool WorkerLowerHash(string value) => value is { Length: 64 }
        && value.All(static item => item is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool WorkerRootsOverlap(string left, string right) => left == right || Contained(left, right) || Contained(right, left);
    private static void WorkerRequire([DoesNotReturnIf(false)] bool condition) { if (!condition) throw InvalidWorkerDescriptor(); }
    private static EvidenceAdmissionException InvalidWorkerDescriptor() => new("ASEVD402", "The protected worker descriptor or acknowledgement is invalid.");

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();
    [LibraryImport("libc", EntryPoint = "getgid")]
    private static partial uint GetGid();
    [LibraryImport("libc", EntryPoint = "getsockopt")]
    private static partial int GetPeerCredentials(SafeSocketHandle socket, int level, int option,
        out PeerCredentials credentials, ref uint length);
}
