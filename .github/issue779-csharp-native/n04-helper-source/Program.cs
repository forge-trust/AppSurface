using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Issue779.N04;

/// <summary>Fixed, private root test coordinator. It references no Evidence package and issues no authority.</summary>
/// <remarks>
/// The trusted fixture must qualify this image, the CLR and OS closure before execution. Root-owned input
/// files and pins select one already-created run; they do not constitute production admission. Every
/// socket operation is awaited within the one retained RunAsync task. No process is started by this image.
/// </remarks>
internal static class Program
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Runs exactly N04, retaining its original task before dispatch and publishing after cleanup.</summary>
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || RuntimeInformation.OSArchitecture != Architecture.X64) return 1;
        Selection? selected = null;
        Clock? clock = null;
        var owned = new Owned();
        string stage = "Bootstrap";
        bool failed = false;
        Observation? observation = null;
        try
        {
            selected = Selection.Parse(args);
            Native.RequireRoot();
            Native.SetUmask(0x3f); // 0077 for the one new replacement name; no input mode is changed.
            // Original fixture redirection must already be private, bounded, nonlinked regular files.
            Native.RequireOutput(1); Native.RequireOutput(2);
            clock = new Clock(selected.End);
            using var abort = clock.Token.Register(owned.CloseSockets);
            var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<Observation> original = RunAsync(dispatch.Task, selected, clock, owned, value => stage = value);
            dispatch.SetResult();
            observation = await original.ConfigureAwait(false);
        }
        catch (Exception) { failed = true; }
        finally
        {
            // A stopped original may be resumed only through its retained pidfd and unchanged live sample.
            // Failure/expiry never authorizes an arbitrary-PID signal or a fresh cleanup interval.
            if (owned.StopRegistration.PossibleStop is { } worker)
            {
                try { owned.StopRegistration.Resume(worker, () => { Require(clock is not null); worker.Recheck(clock!); worker.Signal(18, clock!); }); }
                catch (Exception) { failed = true; }
            }
            if (!owned.CloseAll()) failed = true;
            if (clock is not null)
            {
                try { await clock.DisposeAsync().ConfigureAwait(false); } catch (Exception) { failed = true; }
            }
        }
        try
        {
            Require(selected is not null && Clock.Now() < selected.End);
            var result = failed || observation is null
                ? JsonSerializer.SerializeToUtf8Bytes(new { schema = "issue779-n04-root-coordinator-result-v2",
                    status = "rejected", stage, native_authority = false })
                : JsonSerializer.SerializeToUtf8Bytes(observation);
            Require(result.Length is > 0 and <= 4095);
            Native.RequireOutput(1);
            using (var handle = Native.Duplicate(1))
            using (var output = new FileStream(handle, FileAccess.Write, 4096))
            { output.Write(result); output.WriteByte(10); output.Flush(); }
            Require(Clock.Now() < selected!.End);
            return failed || observation is null ? 1 : 0;
        }
        catch (Exception) { return 1; }
    }

    /// <summary>Executes the fixed protocol against the original selected owner and worker.</summary>
    private static async Task<Observation> RunAsync(Task dispatch, Selection selected, Clock clock,
        Owned owned, Action<string> checkpoint)
    {
        await dispatch.ConfigureAwait(false);
        checkpoint("Input"); clock.Check();
        var request = owned.Add(PathPin.Open(selected.Request, PathKind.ImmutableFile, 0, clock));
        var descriptor = owned.Add(PathPin.Open(selected.Descriptor, PathKind.Descriptor, null, clock));
        byte[] requestBytes = request.Read(65536, selected.RequestSha, clock);
        byte[] descriptorBytes = descriptor.Read(65536, selected.DescriptorSha, clock);
        Binding binding = Binding.Parse(selected, requestBytes, descriptorBytes);
        descriptor.RequireFile(0, binding.WorkerGid, 0x8120, 65536); // regular 0440.
        var image = owned.Add(PathPin.Open(binding.Runtime, PathKind.Executable, 0, clock));
        var entry = owned.Add(PathPin.Open(binding.Entry, PathKind.ImmutableFile, 0, clock));
        _ = entry.Read(20 * 1024 * 1024, binding.EntrySha, clock);
        checkpoint("Identity");
        var owner = owned.Add(ProcessPin.Capture(binding.OwnerPid, 0, 0, "owner", binding, image,
            selected.Request, clock, checkpoint));
        checkpoint("IdentityOwnerDistinct");
        Require(owner.Pid != Environment.ProcessId);
        checkpoint("Rendezvous");
        var rendezvousPath = "/run/appsurface-evidence-n04-" + selected.Generation + "/checkpoint.sock";
        // Parent dispatches only after the root checkpoint namespace is present. There is no ENOENT fallback.
        var rendezvous = owned.Add(PathPin.Open(rendezvousPath, PathKind.CheckpointSocket, 0, clock));
        var channel = owned.AddSocket(new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified));
        await channel.ConnectAsync(new UnixDomainSocketEndPoint(rendezvousPath), clock.Token).ConfigureAwait(false);
        CheckOwner();
        Peer rootPeer = Native.Peer(channel);
        Require(rootPeer == new Peer(owner.Pid, 0, 0));
        await ReceiveLineAsync(channel, "N04 READY_PREPARED " + selected.Generation + "\n", clock).ConfigureAwait(false);
        CheckOwner(); Require(Native.Peer(channel) == rootPeer);
        // READY_PREPARED follows genuine ReadyData; capture the post-exec worker, not an env/bootstrap PID.
        var worker = owned.Add(ProcessPin.Capture(binding.WorkerPid, binding.WorkerUid, binding.WorkerGid,
            "worker", binding, image, binding.Socket, clock));
        Require(worker.Pid != Environment.ProcessId && worker.Pid != owner.Pid);
        var broker = owned.Add(PathPin.Open(binding.Socket, PathKind.BrokerSocket, binding.WorkerGid, clock));
        broker.RequireParents(binding.WorkerGid, selected.Generation);
        checkpoint("WorkerStop"); CheckOwner(); worker.Recheck(clock);
        owned.StopRegistration.Stop(worker, () => worker.Signal(19, clock));
        while (worker.Recheck(clock).State is not ('T' or 't'))
        {
            CheckOwner(); await Task.Delay(10, clock.Token).ConfigureAwait(false);
        }
        checkpoint("ReadyRelease"); CheckOwner(); Require(worker.Recheck(clock).State is 'T' or 't');
        await SendAllAsync(channel, "N04 RELEASE_READY " + selected.Generation + "\n", clock).ConfigureAwait(false);
        checkpoint("ReadyCommit");
        await ReceiveLineAsync(channel, "N04 READY_COMMITTED " + selected.Generation + "\n", clock).ConfigureAwait(false);
        CheckOwner(); Require(Native.Peer(channel) == rootPeer && worker.Recheck(clock).State is 'T' or 't');
        checkpoint("Replace");
        broker.Recheck(clock); broker.UnlinkKnownSocket(clock);
        var replacement = owned.AddSocket(new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified));
        // This non-UDSEP wrapper deliberately leaves pathname lifetime with the actual root fixture.
        replacement.Bind(new OwnedUnixEndPoint(binding.Socket));
        var replacementPin = owned.Add(PathPin.Open(binding.Socket, PathKind.UnsealedReplacement, null, clock));
        replacementPin.SealSocket(binding.WorkerGid, clock);
        broker.RequireUnlinkedOriginal(clock); replacementPin.Recheck(clock);
        Require(replacement.LocalEndPoint?.ToString() == binding.Socket);
        replacement.Listen(1);
        checkpoint("WorkerResume"); CheckOwner(); Require(worker.Recheck(clock).State is 'T' or 't');
        owned.StopRegistration.Resume(worker, () => worker.Signal(18, clock));
        checkpoint("Peer");
        Socket accepted = owned.AddSocket(await replacement.AcceptAsync(clock.Token).ConfigureAwait(false));
        CheckOwner(); replacementPin.Recheck(clock); broker.RequireUnlinkedOriginal(clock);
        Peer actual = Native.Peer(accepted);
        Require(actual == new Peer(worker.Pid, binding.WorkerUid, binding.WorkerGid));
        worker.Recheck(clock); worker.RequireAlive(clock);
        // The fixed private worker waits for write EOF only after its genuine root-PID mismatch.
        // Release that source-owned hold after the live sample, without sending any application byte.
        checkpoint("ZeroEof");
        clock.Check(); accepted.Shutdown(SocketShutdown.Send); clock.Check();
        var one = new byte[1];
        int received = await accepted.ReceiveAsync(one.AsMemory(), SocketFlags.None, clock.Token).ConfigureAwait(false);
        Require(received == 0 && Native.Peer(accepted) == actual);
        checkpoint("Finalization"); CheckOwner(); replacementPin.Recheck(clock); broker.RequireUnlinkedOriginal(clock);
        return new Observation("issue779-n04-root-coordinator-result-v2", "observed", selected.Generation,
            owner.Pid, owner.StartTicks, owner.Uids, owner.Gids, owner.Groups,
            worker.Pid, worker.StartTicks, worker.Uids, worker.Gids, worker.Groups,
            true, true, true, 0, 0, true, true, false);

        void CheckOwner()
        {
            clock.Check(); Native.RequireRoot(); owner.Recheck(clock); rendezvous.Recheck(clock);
            _ = request.Read(65536, selected.RequestSha, clock);
            _ = descriptor.Read(65536, selected.DescriptorSha, clock);
        }
    }

    /// <summary>Private evidence data only. Root helper success is not N04 acceptance or production authority.</summary>
    private sealed record Observation(string schema, string status, string generation, int owner_pid,
        ulong owner_start_ticks, uint[] owner_uid4, uint[] owner_gid4, uint[] owner_groups,
        int worker_pid, ulong worker_start_ticks, uint[] worker_uid4, uint[] worker_gid4, uint[] worker_groups,
        bool worker_stop_observed, bool ready_committed_notification, bool replacement_peer_tuple_matched,
        int replacement_request_bytes, int replacement_received_bytes, bool peer_live_identity_rechecked,
        bool replacement_write_shutdown, bool native_authority);

    private static async Task ReceiveLineAsync(Socket socket, string exact, Clock clock)
    {
        byte[] expected = Encoding.ASCII.GetBytes(exact);
        var one = new byte[1];
        foreach (byte value in expected)
        {
            clock.Check(); int count = await socket.ReceiveAsync(one.AsMemory(), SocketFlags.None, clock.Token).ConfigureAwait(false);
            clock.Check(); Require(count == 1 && one[0] == value);
        }
    }

    private static async Task SendAllAsync(Socket socket, string exact, Clock clock)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(exact);
        int offset = 0;
        while (offset < bytes.Length)
        {
            clock.Check(); int count = await socket.SendAsync(bytes.AsMemory(offset), SocketFlags.None, clock.Token).ConfigureAwait(false);
            clock.Check(); Require(count > 0); offset += count;
        }
    }

    /// <summary>Closed ordered command grammar; pins are continuity data supplied by a separately trusted root fixture.</summary>
    private sealed record Selection(string Generation, string Descriptor, string DescriptorSha,
        string Request, string RequestSha, long End)
    {
        internal static Selection Parse(string[] args)
        {
            string[] keys = ["--execute", "--generation", "--descriptor", "--descriptor-sha256",
                "--request", "--request-sha256", "--deadline-monotonic-ms"];
            Require(args.Length == 13 && args[0] == keys[0]);
            for (int i = 1; i < keys.Length; i++) Require(args[2 * i - 1] == keys[i]);
            string generation = args[2];
            Require(Hex(generation, 32) && Guid.ParseExact(generation, "N") != Guid.Empty);
            string descriptor = Path(args[4]); string request = Path(args[8]);
            Require(long.TryParse(args[12], NumberStyles.None, Invariant, out long end));
            Require(descriptor == "/run/appsurface-evidence-" + generation + "/worker/worker-control.json"
                && Hex(args[6], 64) && Hex(args[10], 64)
                && end > Clock.Now() && end - Clock.Now() <= 600000);
            return new(generation, descriptor, args[6], request, args[10], end);
        }
    }

    /// <summary>Descriptor/request comparison data, never a factory for production ownership or admission.</summary>
    private sealed record Binding(int OwnerPid, int WorkerPid, uint WorkerUid, uint WorkerGid,
        string Runtime, string Entry, string EntrySha, string Socket, string Generation)
    {
        internal static Binding Parse(Selection selected, byte[] requestBytes, byte[] descriptorBytes)
        {
            using var request = Json(requestBytes); using var descriptor = Json(descriptorBytes);
            var r = Fields(request.RootElement); var d = Fields(descriptor.RootElement);
            string[] requestKeys = ["schema", "mode", "tool_root", "runtime_root", "runtime_host", "entry_path", "policy_file",
                "subject_root", "base_revision", "subject_revision", "workflow_identity", "paths", "observation_profile_ids",
                "observation_producer_ids", "job_deadline_utc", "admission_seconds", "start_seconds", "collection_seconds",
                "cleanup_seconds", "stopping_seconds", "diff_file", "diff_sha256", "solution"];
            string[] descriptorKeys = ["schema", "run_id", "worker_pid", "broker_pid", "worker_uid", "worker_gid", "subject_uid",
                "subject_gid", "unit", "cgroup", "job_deadline_utc", "tool_root", "subject_root", "output_parent", "output_slot",
                "dotnet_path", "test_output_root", "policy_file", "mode", "socket_path", "descriptor_path", "entry_sha256",
                "policy_sha256", "base_revision", "subject_revision", "workflow_identity", "provider", "platform", "proof_digest",
                "output_parent_identity", "observation_profile_ids", "observation_producer_ids", "paths", "admission_seconds",
                "start_seconds", "collection_seconds", "cleanup_seconds", "stopping_seconds", "diff_file", "diff_sha256", "solution"];
            Require(r.Keys.All(requestKeys.Contains) && requestKeys.Take(20).All(r.ContainsKey)
                && d.Keys.Order().SequenceEqual(descriptorKeys.Order()));
            Require(Text(r["schema"]) == "evidence-supervisor-linux-v1" && Text(r["mode"]) == "observation"
                && Text(d["schema"]) == "evidence-worker-linux-v1" && Text(d["run_id"]) == "csharp/" + selected.Generation
                && Text(d["provider"]) == "github-actions" && Text(d["platform"]) == "linux-x64" && Text(d["proof_digest"]) == ""
                && Text(d["mode"]) == "observation");
            foreach (string name in requestKeys.Skip(2).Where(name => name is not ("runtime_root" or "runtime_host" or "entry_path" or "job_deadline_utc")))
            {
                JsonElement left = r.TryGetValue(name, out var v) ? v : default;
                Require(Equivalent(left, d[name]));
            }
            Require(DateTimeOffset.TryParse(Text(r["job_deadline_utc"]), Invariant, DateTimeStyles.None, out var requestEnd)
                && DateTimeOffset.TryParse(Text(d["job_deadline_utc"]), Invariant, DateTimeStyles.None, out var descriptorEnd)
                && requestEnd.Offset == TimeSpan.Zero && descriptorEnd.Offset == TimeSpan.Zero
                && requestEnd == descriptorEnd && requestEnd > DateTimeOffset.UtcNow);
            string runtime = Path(Text(r["runtime_host"])); string entry = Path(Text(r["entry_path"]));
            Require(runtime == Text(d["dotnet_path"]) && !runtime.Contains('=')
                && runtime.StartsWith(Path(Text(r["runtime_root"])) + "/", StringComparison.Ordinal)
                && entry.StartsWith(Path(Text(d["tool_root"])) + "/", StringComparison.Ordinal));
            string root = "/run/appsurface-evidence-" + selected.Generation;
            string socket = root + "/worker/broker/control.sock";
            Require(Text(d["descriptor_path"]) == selected.Descriptor && Text(d["socket_path"]) == socket
                && Text(d["output_parent"]) == root + "/output" && Text(d["test_output_root"]) == root + "/raw-results"
                && Text(d["output_slot"]) == "evidence"
                && Text(d["unit"]) == "appsurface-evidence-worker-" + selected.Generation + ".service"
                && Text(d["cgroup"]) == "/system.slice/" + Text(d["unit"])
                && Hex(Text(d["entry_sha256"]), 64) && Hex(Text(d["policy_sha256"]), 64));
            int owner = d["broker_pid"].GetInt32(); int worker = d["worker_pid"].GetInt32();
            uint uid = d["worker_uid"].GetUInt32(); uint gid = d["worker_gid"].GetUInt32();
            Require(owner > 0 && worker > 0 && owner != worker && uid is > 0 and < uint.MaxValue && gid is > 0 and < uint.MaxValue);
            var output = Fields(d["output_parent_identity"]);
            Require(output.Count == 5 && output.Keys.Order().SequenceEqual(new[] { "device_major", "device_minor", "inode", "uid", "gid" }.Order())
                && output["inode"].GetUInt64() != 0 && output["uid"].GetUInt32() == uid && output["gid"].GetUInt32() == gid);
            _ = output["device_major"].GetUInt32(); _ = output["device_minor"].GetUInt32();
            return new(owner, worker, uid, gid, runtime, entry, Text(d["entry_sha256"]), socket, selected.Generation);
        }
    }

    private static JsonDocument Json(byte[] bytes)
    {
        Require(bytes.Length is > 0 and <= 65536); _ = Utf8.GetCharCount(bytes);
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        try { RejectDuplicates(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void RejectDuplicates(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in item.EnumerateObject()) { Require(seen.Add(property.Name)); RejectDuplicates(property.Value); }
        }
        else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) RejectDuplicates(child);
    }
    private static Dictionary<string, JsonElement> Fields(JsonElement item)
    {
        Require(item.ValueKind == JsonValueKind.Object);
        return item.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
    }
    private static bool Equivalent(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Undefined) return b.ValueKind == JsonValueKind.Null;
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.String => Text(a) == Text(b),
            JsonValueKind.Number => a.GetInt64() == b.GetInt64(),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength()
                && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => Equivalent(pair.First, pair.Second)),
            _ => false,
        };
    }
    private static string Text(JsonElement item)
    {
        Require(item.ValueKind == JsonValueKind.String); string value = item.GetString()!;
        Require(Utf8.GetByteCount(value) <= 4095 && !value.Any(char.IsControl)); return value;
    }
    private static string Path(string value)
    {
        Require(value.Length > 1 && value[0] == '/' && Utf8.GetByteCount(value) <= 4095
            && !value.Any(char.IsControl) && !value.Contains('\\') && !value.Contains('$') && !value.Contains('%')
            && value[1..].Split('/').All(p => p.Length > 0 && p is not ("." or "..")));
        return value;
    }
    private static bool Hex(string value, int count) => value.Length == count && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw new InvalidOperationException("n04-coordinator-rejected"); }

    /// <summary>Owned socket closure wakes blocked operations; handles close only after original RunAsync joins.</summary>
    private sealed class Owned
    {
        private readonly List<IDisposable> _resources = [];
        private readonly List<Socket> _sockets = [];
        private readonly object _gate = new();
        private bool _aborted;
        private int _closeFailed;
        internal PossibleStopRegistration<ProcessPin> StopRegistration { get; } = new();
        internal T Add<T>(T item) where T : IDisposable { _resources.Add(item); return item; }
        internal Socket AddSocket(Socket socket)
        {
            lock (_gate)
            {
                if (_aborted) { socket.Dispose(); throw new InvalidOperationException("n04-coordinator-rejected"); }
                _sockets.Add(socket); return socket;
            }
        }
        internal void CloseSockets()
        {
            Socket[] sockets;
            lock (_gate) { _aborted = true; sockets = _sockets.ToArray(); }
            foreach (var socket in sockets) try { socket.Dispose(); } catch (Exception) { Interlocked.Exchange(ref _closeFailed, 1); }
        }
        internal bool CloseAll()
        {
            CloseSockets();
            foreach (var item in _resources.AsEnumerable().Reverse()) try { item.Dispose(); } catch (Exception) { Interlocked.Exchange(ref _closeFailed, 1); }
            return Volatile.Read(ref _closeFailed) == 0;
        }
    }

    /// <summary>One inherited CLOCK_BOOTTIME deadline, matching fixture /proc/uptime rather than wall clock.</summary>
    private sealed class Clock : IAsyncDisposable
    {
        private readonly long _end;
        private readonly CancellationTokenSource _expired = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _watch;
        private int _failed;
        internal Clock(long end)
        {
            _end = end; Require(end > Now() && end - Now() <= 600000);
            var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _watch = WatchAsync(dispatch.Task); dispatch.SetResult();
        }
        internal CancellationToken Token => _expired.Token;
        internal void Check() { Require(Volatile.Read(ref _failed) == 0 && Now() < _end); Token.ThrowIfCancellationRequested(); }
        internal static long Now()
        {
            Require(Native.ClockGetTime(7, out var time) == 0 && time.Seconds >= 0 && time.Nanoseconds is >= 0 and < 1000000000);
            return checked(time.Seconds * 1000 + time.Nanoseconds / 1000000);
        }
        private async Task WatchAsync(Task dispatch)
        {
            await dispatch.ConfigureAwait(false);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    long remaining = _end - Now();
                    if (remaining <= 0) { Interlocked.Exchange(ref _failed, 1); _expired.Cancel(); return; }
                    await Task.Delay((int)Math.Min(25, remaining), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception) { Interlocked.Exchange(ref _failed, 1); _expired.Cancel(); }
        }
        public async ValueTask DisposeAsync()
        {
            bool failed = false;
            try { _stop.Cancel(); } catch (Exception) { failed = true; }
            try { await _watch.ConfigureAwait(false); } catch (Exception) { failed = true; }
            try { Check(); } catch (Exception) { failed = true; }
            try { _expired.Dispose(); } catch (Exception) { failed = true; }
            try { _stop.Dispose(); } catch (Exception) { failed = true; }
            Require(!failed);
        }
    }

    private enum PathKind { ImmutableFile, Executable, Descriptor, CheckpointSocket, BrokerSocket, UnsealedReplacement }

    /// <summary>Retains protected ancestors and one no-follow leaf. Only a pinned broker leaf may be unlinked.</summary>
    private sealed class PathPin : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;
        private readonly List<Metadata> _parents;
        private readonly string[] _parts;
        private Metadata _leaf;
        private bool _unlinked;
        internal string Absolute { get; }
        internal SafeFileHandle Leaf => _handles[^1];
        private PathPin(string absolute, List<SafeFileHandle> handles, List<Metadata> parents, string[] parts, Metadata leaf)
        { Absolute = absolute; _handles = handles; _parents = parents; _parts = parts; _leaf = leaf; }
        internal static PathPin Open(string path, PathKind kind, uint? gid, Clock clock)
        {
            Path(path); var handles = new List<SafeFileHandle>(); var parents = new List<Metadata>();
            try
            {
                clock.Check(); handles.Add(Native.Open(-100, "/", Native.Directory, 6));
                string[] parts = path[1..].Split('/');
                for (int i = 0; i < parts.Length; i++)
                {
                    Metadata parent = Native.Stat(handles[^1]); parent.RequireAncestor(); parents.Add(parent);
                    clock.Check(); handles.Add(Native.Open(Native.Fd(handles[^1]), parts[i],
                        i == parts.Length - 1 ? Native.PathOnly : Native.Directory, i == 0 ? 14UL : 15UL));
                }
                Metadata leaf = Native.Stat(handles[^1]);
                Require(leaf.Inode != 0 && leaf.Uid == 0 && (!gid.HasValue || leaf.Gid == gid.Value));
                switch (kind)
                {
                    case PathKind.ImmutableFile: Require(leaf.Type == 0x8000 && leaf.Links == 1 && (leaf.Mode & 0x12) == 0); break;
                    case PathKind.Executable: Require(leaf.Type == 0x8000 && leaf.Links == 1 && (leaf.Mode & 0x92) == 0 && (leaf.Mode & 0x49) != 0); break;
                    case PathKind.Descriptor: Require(leaf.Mode == 0x8120 && leaf.Links == 1 && leaf.Size is > 0 and <= 65536); break;
                    case PathKind.CheckpointSocket: Require(leaf.Mode == 0xc180 && leaf.Links == 1 && leaf.Size == 0); break;
                    case PathKind.BrokerSocket: Require(leaf.Mode == 0xc1b0 && leaf.Links == 1 && leaf.Size == 0); break;
                    case PathKind.UnsealedReplacement: Require(leaf.Type == 0xc000 && leaf.Links == 1 && leaf.Size == 0 && (leaf.Mode & 0x12) == 0); break;
                    default: throw new InvalidOperationException("n04-coordinator-rejected");
                }
                var result = new PathPin(path, handles, parents, parts, leaf); result.Recheck(clock); return result;
            }
            catch { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); throw; }
        }
        internal void RequireFile(uint uid, uint gid, ushort mode, int maximum) =>
            Require(_leaf.Uid == uid && _leaf.Gid == gid && _leaf.Mode == mode && _leaf.Links == 1 && _leaf.Size is > 0 && _leaf.Size <= (ulong)maximum);
        internal void Recheck(Clock clock)
        {
            clock.Check(); Require(!_unlinked);
            for (int i = 0; i < _parents.Count; i++)
            {
                Metadata actual = Native.Stat(_handles[i]); actual.RequireAncestor(); Require(actual.SameAncestor(_parents[i]));
                if (i > 0)
                {
                    using var named = Native.Open(Native.Fd(_handles[i - 1]), _parts[i - 1], Native.Directory, i == 1 ? 14UL : 15UL);
                    Require(Native.Stat(named).SameAncestor(_parents[i]));
                }
            }
            Require(Native.Stat(Leaf) == _leaf);
            using var leaf = Native.Open(Native.Fd(_handles[^2]), _parts[^1], Native.PathOnly, 15);
            Require(Native.Stat(leaf) == _leaf); clock.Check();
        }
        internal byte[] Read(int maximum, string hash, Clock clock)
        {
            Recheck(clock); Require(_leaf.Type == 0x8000 && _leaf.Size is > 0 && _leaf.Size <= (ulong)maximum && Hex(hash, 64));
            using var reader = Native.Open(Native.Fd(_handles[^2]), _parts[^1], Native.ReadOnly, 15);
            Require(Native.Stat(reader) == _leaf);
            byte[] bytes = ReadBounded(reader, maximum, clock);
            Require((ulong)bytes.Length == _leaf.Size && Native.Stat(reader) == _leaf
                && Convert.ToHexStringLower(SHA256.HashData(bytes)) == hash); Recheck(clock); return bytes;
        }
        internal void RequireParents(uint workerGid, string generation)
        {
            Require(Absolute == "/run/appsurface-evidence-" + generation + "/worker/broker/control.sock" && _parents.Count == 5);
            Require(_parents[2].Mode == 0x41e8 && _parents[2].Gid == workerGid
                && _parents[3].Mode == 0x41c8 && _parents[3].Gid == workerGid
                && _parents[4].Mode == 0x41c8 && _parents[4].Gid == workerGid);
        }
        internal void UnlinkKnownSocket(Clock clock)
        {
            Recheck(clock); Require(_leaf.Mode == 0xc1b0 && _parts[^1] == "control.sock");
            Require(Native.UnlinkAt(Native.Fd(_handles[^2]), "control.sock", 0) == 0);
            _unlinked = true; RequireUnlinkedOriginal(clock);
        }
        internal void RequireUnlinkedOriginal(Clock clock)
        {
            clock.Check(); Require(_unlinked);
            Metadata old = Native.Stat(Leaf);
            Require(old.Links == 0 && old.SameUnlinked(_leaf));
            for (int i = 0; i < _parents.Count; i++)
            {
                Metadata actual = Native.Stat(_handles[i]); actual.RequireAncestor(); Require(actual.SameAncestor(_parents[i]));
                if (i > 0)
                {
                    using var named = Native.Open(Native.Fd(_handles[i - 1]), _parts[i - 1], Native.Directory, i == 1 ? 14UL : 15UL);
                    Require(Native.Stat(named).SameAncestor(_parents[i]));
                }
            }
            clock.Check();
        }
        internal void SealSocket(uint gid, Clock clock)
        {
            Recheck(clock); Require(_leaf.Type == 0xc000 && _leaf.Links == 1 && _leaf.Uid == 0);
            Require(Native.ChownAt(Native.Fd(Leaf), "", 0, gid, 0x1100) == 0);
            Require(Native.ChmodAt(452, Native.Fd(Leaf), "", 0x1b0, 0x1000) == 0);
            Metadata actual = Native.Stat(Leaf);
            Require(actual.SameObject(_leaf) && actual.Mode == 0xc1b0 && actual.Uid == 0 && actual.Gid == gid
                && actual.Links == 1 && actual.Size == 0);
            _leaf = actual; Recheck(clock);
        }
        public void Dispose()
        {
            bool failed = false;
            foreach (var handle in _handles.AsEnumerable().Reverse()) try { handle.Dispose(); } catch (Exception) { failed = true; }
            Require(!failed);
        }
    }

    /// <summary>Retains one proc directory, fixed proc files, executable and pidfd; never signals by numeric PID.</summary>
    private sealed class ProcessPin : IDisposable
    {
        private readonly SafeFileHandle[] _handles;
        private readonly Metadata[] _metadata;
        private readonly Sample _initial;
        private readonly string[] _argv;
        private readonly string _group;
        private readonly uint _uid;
        private readonly uint _gid;
        private readonly PathPin _image;
        internal int Pid => _initial.Pid;
        internal ulong StartTicks => _initial.Start;
        internal uint[] Uids => _initial.Uids.ToArray();
        internal uint[] Gids => _initial.Gids.ToArray();
        internal uint[] Groups => _initial.Groups.ToArray();
        private ProcessPin(SafeFileHandle[] handles, Metadata[] metadata, Sample initial, string[] argv,
            string group, uint uid, uint gid, PathPin image)
        { _handles = handles; _metadata = metadata; _initial = initial; _argv = argv; _group = group; _uid = uid; _gid = gid; _image = image; }
        internal static ProcessPin Capture(int pid, uint uid, uint gid, string role, Binding binding,
            PathPin image, string operand, Clock clock, Action<string>? diagnostic = null)
        {
            Require(pid > 0 && role is "owner" or "worker"); var handles = new List<SafeFileHandle>();
            try
            {
                diagnostic?.Invoke("IdentityProcRoot");
                clock.Check(); handles.Add(Native.Open(-100, "/proc", Native.Directory, 6)); Native.RequireProc(handles[0]);
                diagnostic?.Invoke("IdentityProcDirectory");
                handles.Add(Native.Open(Native.Fd(handles[0]), pid.ToString(Invariant), Native.Directory, 15));
                diagnostic?.Invoke("IdentityFilesOpen");
                foreach (string name in new[] { "stat", "status", "cgroup", "cmdline" })
                    handles.Add(Native.Open(Native.Fd(handles[1]), name, Native.ReadOnly, 15));
                // Deliberately follow only the fixed kernel exe link, then compare its object to the selected host.
                diagnostic?.Invoke("IdentityImageOpen");
                handles.Add(Native.Open(Native.Fd(handles[1]), "exe", Native.FollowReadOnly, 0));
                diagnostic?.Invoke("IdentityPidfdOpen");
                long fd = Native.PidFdOpen(434, pid, 0); Require(fd >= 0 && fd <= int.MaxValue);
                handles.Add(new SafeFileHandle((IntPtr)fd, true));
                diagnostic?.Invoke("IdentityMetadata");
                Metadata[] metadata = handles.Take(7).Select(Native.Stat).ToArray();
                for (int i = 0; i < 6; i++) { Native.RequireProc(handles[i]); Require(metadata[i].Type == (i < 2 ? 0x4000 : 0x8000)); }
                diagnostic?.Invoke("IdentityImageObject");
                Require(metadata[6].SameObject(Native.Stat(image.Leaf)));
                string[] argv = [binding.Runtime, binding.Entry, "evidence", role == "worker" ? "worker" : "supervise",
                    role == "worker" ? "--control" : "--request", operand];
                string group = "/system.slice/appsurface-evidence-" + role + "-" + binding.Generation + ".service";
                Sample initial = ReadSample(handles.ToArray(), argv, group, uid, gid, clock, diagnostic);
                diagnostic?.Invoke("IdentityInitialPid");
                Require(initial.Pid == pid);
                var result = new ProcessPin(handles.ToArray(), metadata, initial, argv, group, uid, gid, image);
                result.Recheck(clock, diagnostic); return result;
            }
            catch { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); throw; }
        }
        internal Sample Recheck(Clock clock, Action<string>? diagnostic = null)
        {
            diagnostic?.Invoke("IdentityPinnedImage");
            clock.Check(); _image.Recheck(clock);
            diagnostic?.Invoke("IdentityStableMetadata");
            CheckBindings();
            Sample first = ReadSample(_handles, _argv, _group, _uid, _gid, clock, diagnostic);
            Sample second = ReadSample(_handles, _argv, _group, _uid, _gid, clock, diagnostic);
            diagnostic?.Invoke("IdentitySampleIdentity");
            Require(first.SameIdentity(_initial) && second.SameIdentity(first));
            diagnostic?.Invoke("IdentityStableMetadata");
            CheckBindings();
            diagnostic?.Invoke("IdentityPinnedImage");
            _image.Recheck(clock);
            diagnostic?.Invoke("IdentityFinalImageObject");
            Require(Native.Stat(_handles[6]).SameObject(Native.Stat(_image.Leaf)));
            diagnostic?.Invoke("IdentityFinalClock");
            clock.Check(); return second;

            void CheckBindings()
            {
            for (int i = 0; i < 7; i++)
            {
                clock.Check();
                Metadata actual = Native.Stat(_handles[i]); Require(actual.ProcSame(_metadata[i]));
                if (i < 6) Native.RequireProc(_handles[i]);
                string name = i switch { 0 => "/proc", 1 => Pid.ToString(Invariant), 2 => "stat", 3 => "status", 4 => "cgroup", 5 => "cmdline", _ => "exe" };
                int parent = i == 0 ? -100 : Native.Fd(_handles[i == 1 ? 0 : 1]);
                using var named = Native.Open(parent, name, i < 2 ? Native.Directory : i == 6 ? Native.FollowReadOnly : Native.ReadOnly,
                    i == 0 ? 6UL : i == 6 ? 0UL : 15UL);
                Require(Native.Stat(named).ProcSame(_metadata[i]));
            }
            }
        }
        internal void Signal(int signal, Clock clock)
        {
            Require(signal is 18 or 19); Recheck(clock);
            Require(Native.PidFdSignal(424, Native.Fd(_handles[7]), signal, IntPtr.Zero, 0) == 0); clock.Check();
        }
        internal void RequireAlive(Clock clock)
        {
            clock.Check(); Require(Native.PidFdSignal(424, Native.Fd(_handles[7]), 0, IntPtr.Zero, 0) == 0); clock.Check();
        }
        private static Sample ReadSample(SafeFileHandle[] handles, string[] argv, string group, uint uid, uint gid, Clock clock,
            Action<string>? diagnostic = null)
        {
            diagnostic?.Invoke("IdentityStatRead");
            var first = ParseStat(ReadBounded(handles[2], 16384, clock));
            diagnostic?.Invoke("IdentityStatusRead");
            string status = Decode(ReadBounded(handles[3], 65536, clock));
            uint[]? uids = null; uint[]? gids = null; uint[]? groups = null;
            string[] rows = status[..^1].Split('\n'); Require(rows.Length <= 512);
            foreach (string row in rows)
            {
                int colon = row.IndexOf(':'); Require(colon > 0 && row.Length <= 8192);
                string name = row[..colon];
                if (name.Equals("Uid", StringComparison.OrdinalIgnoreCase)) { Require(name == "Uid" && uids is null); uids = Ids(row[(colon + 1)..], 4); }
                else if (name.Equals("Gid", StringComparison.OrdinalIgnoreCase)) { Require(name == "Gid" && gids is null); gids = Ids(row[(colon + 1)..], 4); }
                else if (name.Equals("Groups", StringComparison.OrdinalIgnoreCase))
                { Require(name == "Groups" && groups is null); groups = Ids(row[(colon + 1)..], null); }
            }
            diagnostic?.Invoke("IdentityCredentials");
            Require(uids is not null && gids is not null && groups is not null && uids.All(v => v == uid) && gids.All(v => v == gid)
                && groups.Length <= 32 && groups.Distinct().Count() == groups.Length
                && groups.All(v => v == gid));
            diagnostic?.Invoke("IdentityCgroupRead");
            Require(Decode(ReadBounded(handles[4], 4096, clock)) == "0::" + group + "\n");
            diagnostic?.Invoke("IdentityArguments");
            byte[] command = ReadBounded(handles[5], 16384, clock);
            Require(command.Length > 0 && command[^1] == 0);
            string[] actualArgv = Utf8.GetString(command)[..^1].Split('\0'); Require(actualArgv.SequenceEqual(argv, StringComparer.Ordinal));
            diagnostic?.Invoke("IdentityStatAgain");
            var last = ParseStat(ReadBounded(handles[2], 16384, clock));
            diagnostic?.Invoke("IdentitySampleComparison");
            Require(first.Pid == last.Pid && first.Start == last.Start && "RSDTtKWPI".Contains(last.State));
            return new(last.Pid, last.Start, last.State, uids!, gids!, groups!);
        }
        public void Dispose()
        {
            bool failed = false;
            foreach (var handle in _handles.Reverse()) try { handle.Dispose(); } catch (Exception) { failed = true; }
            Require(!failed);
        }
    }

    private sealed record Sample(int Pid, ulong Start, char State, uint[] Uids, uint[] Gids, uint[] Groups)
    {
        internal bool SameIdentity(Sample other) => Pid == other.Pid && Start == other.Start && Uids.SequenceEqual(other.Uids)
            && Gids.SequenceEqual(other.Gids) && Groups.SequenceEqual(other.Groups);
    }
    private static (int Pid, ulong Start, char State) ParseStat(byte[] bytes)
    {
        string value = Decode(bytes); int space = value.IndexOf(' '); int closing = value.LastIndexOf(')');
        Require(space > 0 && closing > space + 1 && value[space + 1] == '(' && value[closing + 1] == ' ');
        string[] fields = value[(closing + 2)..^1].Split(' ');
        Require(fields.Length == 50 && fields[0].Length == 1 && "RSDZTtXxKWPI".Contains(fields[0][0]));
        for (int i = 1; i < fields.Length; i++)
        {
            string n = fields[i];
            Require(n.Length > 0 && (n[0] == '-'
                ? n.Length > 1 && n[1..].All(char.IsAsciiDigit) && long.TryParse(n, NumberStyles.AllowLeadingSign, Invariant, out _)
                : n.All(char.IsAsciiDigit) && ulong.TryParse(n, NumberStyles.None, Invariant, out _)));
        }
        Require(int.TryParse(value[..space], NumberStyles.None, Invariant, out int pid));
        Require(ulong.TryParse(fields[19], NumberStyles.None, Invariant, out ulong start));
        Require(pid > 0 && start > 0);
        return (pid, start, fields[0][0]);
    }
    private static uint[] Ids(string row, int? count)
    {
        string[] values = row.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        Require(values.Length <= 32 && (!count.HasValue || values.Length == count));
        return values.Select(value => { Require(uint.TryParse(value, NumberStyles.None, Invariant, out uint id));
            Require(value.Length > 0 && value.All(char.IsAsciiDigit)); return id; }).ToArray();
    }
    private static string Decode(byte[] bytes)
    {
        Require(bytes.Length > 0 && bytes[^1] == 10); string value = Utf8.GetString(bytes);
        Require(!value.Contains('\0') && !value.Contains('\r')); return value;
    }
    private static byte[] ReadBounded(SafeFileHandle fd, int maximum, Clock clock)
    {
        Require(maximum > 0 && maximum <= 256 * 1024 * 1024);
        using var stream = new MemoryStream(); var block = new byte[4096]; long offset = 0;
        while (true)
        {
            clock.Check(); int count = RandomAccess.Read(fd, block.AsSpan(0, Math.Min(block.Length, maximum + 1 - checked((int)offset))), offset);
            clock.Check(); if (count == 0) return stream.ToArray();
            offset += count; Require(offset <= maximum); stream.Write(block, 0, count);
        }
    }

    /// <summary>Standard serialization, but Socket.Bind sees EndPoint rather than UDSEP and cannot auto-delete the name.</summary>
    private sealed class OwnedUnixEndPoint : EndPoint
    {
        private readonly UnixDomainSocketEndPoint _data;
        internal OwnedUnixEndPoint(string path) { Path(path); Require(Utf8.GetByteCount(path) <= 100); _data = new(path); }
        public override AddressFamily AddressFamily => _data.AddressFamily;
        public override SocketAddress Serialize() => _data.Serialize();
        public override EndPoint Create(SocketAddress address)
        { Require(address.Family == AddressFamily.Unix); return _data.Create(address); }
        public override string ToString() => _data.ToString();
    }

    private readonly record struct Metadata(uint Major, uint Minor, ulong Inode, uint Uid, uint Gid,
        ushort Mode, uint Links, ulong Size, long ChangeSeconds, uint ChangeNanos, long ModifySeconds, uint ModifyNanos)
    {
        internal int Type => Mode & 0xf000;
        internal void RequireAncestor() => Require(Type == 0x4000 && Inode != 0 && Uid == 0 && (Mode & 0x12) == 0);
        internal bool SameObject(Metadata other) => Major == other.Major && Minor == other.Minor && Inode == other.Inode && Type == other.Type;
        internal bool SameAncestor(Metadata other) => SameObject(other) && Uid == other.Uid && Gid == other.Gid && Mode == other.Mode;
        internal bool ProcSame(Metadata other) => SameAncestor(other);
        internal bool SameUnlinked(Metadata other) => SameObject(other) && Uid == other.Uid && Gid == other.Gid && Mode == other.Mode
            && Size == other.Size && ModifySeconds == other.ModifySeconds && ModifyNanos == other.ModifyNanos;
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Peer(int Pid, uint Uid, uint Gid);

    /// <summary>Fixed Linux x64 primitives. Unsupported/blocked syscalls reject without native fallback.</summary>
    private static class Native
    {
        internal const ulong Directory = 0x200000 | 0x80000 | 0x10000;
        internal const ulong PathOnly = 0x200000 | 0x80000 | 0x20000;
        internal const ulong ReadOnly = 0x80000 | 0x800 | 0x20000;
        internal const ulong FollowReadOnly = 0x80000 | 0x800;
        internal static int Fd(SafeFileHandle fd) { Require(!fd.IsClosed && !fd.IsInvalid); return checked((int)fd.DangerousGetHandle()); }
        internal static SafeFileHandle Open(int parent, string path, ulong flags, ulong resolve)
        {
            var how = new OpenHow { Flags = flags, Resolve = resolve };
            long fd = OpenAt2(437, parent, path, ref how, 24); Require(fd >= 0 && fd <= int.MaxValue);
            return new SafeFileHandle((IntPtr)fd, true);
        }
        internal static Metadata Stat(SafeFileHandle fd) => Stat(Fd(fd));
        private static Metadata Stat(int fd)
        {
            Require(StatxCall(fd, "", 0x1000, 0x7ff, out var data) == 0 && (data.Mask & 0x7ff) == 0x7ff);
            return new(data.Major, data.Minor, data.Inode, data.Uid, data.Gid, data.Mode, data.Links,
                data.Size, data.ChangeSeconds, data.ChangeNanos, data.ModifySeconds, data.ModifyNanos);
        }
        internal static void RequireRoot()
        {
            Require(GetUid() == 0 && GetEuid() == 0 && GetGid() == 0 && GetEgid() == 0);
            int count = GetGroups(0, []); Require(count is >= 0 and <= 32);
            var groups = new uint[count]; Require(GetGroups(count, groups) == count && groups.All(g => g == 0));
        }
        internal static void RequireOutput(int fd)
        {
            Metadata metadata = Stat(fd);
            Require(metadata.Mode == 0x8180 && metadata.Uid == 0 && metadata.Gid == 0 && metadata.Links == 1 && metadata.Size == 0);
        }
        internal static SafeFileHandle Duplicate(int fd)
        {
            int copy = Fcntl(fd, 1030, 0); Require(copy >= 0); return new SafeFileHandle((IntPtr)copy, true);
        }
        internal static void RequireProc(SafeFileHandle fd) => Require(FstatFs(fd, out var fs) == 0 && fs.Type == 0x9fa0);
        internal static Peer Peer(Socket socket)
        {
            uint size = 12; Require(GetPeer(socket.SafeHandle, 1, 17, out var value, ref size) == 0 && size == 12 && value.Pid > 0);
            return value;
        }
        [StructLayout(LayoutKind.Sequential)] private struct OpenHow { internal ulong Flags; internal ulong Mode; internal ulong Resolve; }
        [StructLayout(LayoutKind.Explicit, Size = 256)] private struct Statx
        {
            [FieldOffset(0)] internal uint Mask; [FieldOffset(16)] internal uint Links; [FieldOffset(20)] internal uint Uid;
            [FieldOffset(24)] internal uint Gid; [FieldOffset(28)] internal ushort Mode; [FieldOffset(32)] internal ulong Inode;
            [FieldOffset(40)] internal ulong Size; [FieldOffset(96)] internal long ChangeSeconds; [FieldOffset(104)] internal uint ChangeNanos;
            [FieldOffset(112)] internal long ModifySeconds; [FieldOffset(120)] internal uint ModifyNanos;
            [FieldOffset(136)] internal uint Major; [FieldOffset(140)] internal uint Minor;
        }
        [StructLayout(LayoutKind.Explicit, Size = 120)] private struct FileSystem { [FieldOffset(0)] internal long Type; }
        [StructLayout(LayoutKind.Sequential)] internal struct Timespec { internal long Seconds; internal long Nanoseconds; }
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern long OpenAt2(long number, int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref OpenHow how, nuint size);
        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        private static extern int StatxCall(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx value);
        [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)] private static extern int FstatFs(SafeFileHandle fd, out FileSystem value);
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] internal static extern long PidFdOpen(long number, int pid, uint flags);
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] internal static extern long PidFdSignal(long number, int fd, int signal, IntPtr info, uint flags);
        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] internal static extern int UnlinkAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
        [DllImport("libc", EntryPoint = "fchownat", SetLastError = true)] internal static extern int ChownAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint uid, uint gid, int flags);
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] internal static extern long ChmodAt(long number, int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode, int flags);
        [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)] private static extern int GetPeer(SafeSocketHandle socket, int level, int option, out Peer peer, ref uint size);
        [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)] internal static extern int ClockGetTime(int id, out Timespec time);
        [DllImport("libc", EntryPoint = "getuid")] private static extern uint GetUid();
        [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEuid();
        [DllImport("libc", EntryPoint = "getgid")] private static extern uint GetGid();
        [DllImport("libc", EntryPoint = "getegid")] private static extern uint GetEgid();
        [DllImport("libc", EntryPoint = "umask")] internal static extern uint SetUmask(uint mask);
        [DllImport("libc", EntryPoint = "getgroups", SetLastError = true)] private static extern int GetGroups(int size, [Out] uint[] groups);
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int Fcntl(int fd, int command, int value);
    }
}
