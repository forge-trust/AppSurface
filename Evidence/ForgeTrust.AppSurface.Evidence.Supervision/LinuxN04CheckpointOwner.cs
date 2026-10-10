using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Detached N04 ordering and original-task retention; this object owns no native authority.</summary>
/// <remarks>Native creation, peer authentication and dispatch belong exclusively to the fixed checkpoint owner.</remarks>
internal sealed class LinuxN04CheckpointOrder
{
    private readonly object _gate = new();
    private readonly List<Task> _operations = new(3);
    private readonly TaskCompletionSource _nextAccept = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _phase;
    private bool _closed;
    private bool _failed;
    private bool _preparationClaimed;
    private bool _preparationCompleted;

    /// <summary>Gets sticky procedure failure data, not a kernel or native case outcome.</summary>
    internal bool Failed { get { lock (_gate) return _failed; } }

    /// <summary>Reserves the next-accept gate before the first original handler is dispatched.</summary>
    /// <remarks>Later handlers cannot reopen the gate; reservation performs no I/O.</remarks>
    internal void Reserve()
    {
        lock (_gate)
        {
            if (_closed) throw Rejected();
            if (_phase == 0) _phase = 1;
        }
    }

    /// <summary>Claims root preparation before READY admission; the original job token bounds its native exchange.</summary>
    internal void ClaimPreparation()
    {
        lock (_gate)
        {
            if (_closed || _failed || _phase != 1 || _preparationClaimed) throw Rejected();
            _preparationClaimed = true;
        }
    }

    /// <summary>Records the authenticated helper release without granting READY or reopening next-accept.</summary>
    internal void CompletePreparation()
    {
        lock (_gate)
        {
            if (_closed || _failed || _phase != 1 || !_preparationClaimed || _preparationCompleted) throw Rejected();
            _preparationCompleted = true;
        }
    }

    /// <summary>Claims the sole preparation or commitment operation without invoking callbacks.</summary>
    internal void Claim(bool prepared)
    {
        lock (_gate)
        {
            if (_closed || _failed || _phase != (prepared ? 1 : 3)
                || (prepared && _preparationClaimed && !_preparationCompleted)) throw Rejected();
            _phase = prepared ? 2 : 4;
        }
    }

    /// <summary>Retains the original operation before its dispatch gate can be released.</summary>
    internal void Retain(Task operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            if (_closed || _operations.Count >= 3) throw Rejected();
            _operations.Add(operation);
        }
    }

    /// <summary>Records exchange-body completion; original-task joining remains separately mandatory.</summary>
    internal void Complete(bool prepared)
    {
        lock (_gate)
        {
            if (_closed || _failed || _phase != (prepared ? 2 : 4)) throw Rejected();
            _phase = prepared ? 3 : 5;
        }
    }

    /// <summary>Waits before registration, retaining the original worker-exit task as an independent wake-up.</summary>
    /// <remarks>No success path reopens next-accept admission. Worker termination and cancellation reject.</remarks>
    internal async Task BeforeNextAcceptAsync(Task originalWorkerExit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(originalWorkerExit);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_closed || _failed) throw Rejected();
            if (_phase == 0) return;
        }
        await Task.WhenAny(_nextAccept.Task, originalWorkerExit).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        throw Rejected();
    }

    /// <summary>Closes the gate irreversibly; it grants no successful READY, EXIT or settlement.</summary>
    internal void Close()
    {
        lock (_gate) _closed = true;
        _nextAccept.TrySetResult();
    }

    /// <summary>Joins every retained original exchange, including faulted or cancellation-ignoring tasks.</summary>
    internal async Task JoinAsync()
    {
        Task[] operations;
        lock (_gate)
        {
            if (!_closed) throw Rejected();
            operations = _operations.ToArray();
        }
        try { await Task.WhenAll(operations).ConfigureAwait(false); }
        catch (Exception) { MarkFailed(); }
        if (Failed) throw Rejected();
    }

    /// <summary>Latches failure data without retaining exception text or replacing an earlier outcome.</summary>
    internal void MarkFailed() { lock (_gate) _failed = true; }

    internal static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The fixed N04 checkpoint could not be established.");
}

/// <summary>Fixed private-image N04 checkpoint, constructed only from the genuine original native holders.</summary>
/// <remarks>
/// There is no request, environment, callback, caller-PID or public selector. This image always gates the
/// first authenticated handler. Its separate root-only rendezvous is outside the guarded workspace.
/// It never serves a worker descriptor or changes any worker peer, name, cgroup, ownership or EXIT guard.
/// Every exchange is registered before dispatch; closure interrupts actual sockets and joins original
/// operations before retained descriptors close. The external original root lifetime contains native stalls.
/// The root coordinator still needs independently authenticated image/process/pidfd observations; UID0
/// rendezvous credentials and these notifications alone are not a native N04 acceptance result.
/// </remarks>
internal sealed class LinuxN04CheckpointOwner
{
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong PathFlags = 0x200000 | 0x80000;
    private const ulong ParentResolution = 0x02 | 0x04 | 0x08;
    private const ulong ChildResolution = ParentResolution | 0x01;
    private const string SocketName = "checkpoint.sock";
    private static readonly AsyncLocal<LinuxN04CheckpointOwner?> CurrentExchange = new();
    private readonly object _gate = new();
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxN04CheckpointOrder _order = new();
    private readonly Task _workerExit;
    private readonly CancellationTokenSource _stop = new();
    private readonly SafeFileHandle[] _nodes;
    private readonly LinuxProtectedMetadata[] _metadata;
    private readonly string _name;
    private readonly string _generation;
    private readonly Socket _listener;
    private Socket? _peer;
    private int _peerPid;
    private bool _closing;
    private Task? _close;

    private LinuxN04CheckpointOwner(LinuxOwnerActivation owner, LinuxWorkerProcess worker,
        Socket listener, SafeFileHandle[] nodes, LinuxProtectedMetadata[] metadata, string name)
    {
        _owner = owner; _workerExit = worker.WaitForExitAsync(); _listener = listener;
        _nodes = nodes; _metadata = metadata; _name = name; _generation = owner.RunId.ToString("N");
    }

    /// <summary>Creates the closed root-only native path from the same actual server owners.</summary>
    /// <remarks>Collisions reject without adoption/removal. Partial paths remain root private for quarantine.</remarks>
    internal static LinuxN04CheckpointOwner Create(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxControlListener listener,
        LinuxWorkerProcess worker, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts); ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(listener); ArgumentNullException.ThrowIfNull(worker);
        worker.RequireServerOwner(input, owner, accounts, workspace, listener, token);
        _ = worker.RequireWorker(token);
        LinuxProtectedDeployment.RequirePlatform();
        if (GetUid() != 0 || GetEffectiveUid() != 0 || GetGid() != 0 || GetEffectiveGid() != 0)
            throw LinuxN04CheckpointOrder.Rejected();
        var nodes = new List<SafeFileHandle>(4);
        Socket? socket = null;
        try
        {
            token.ThrowIfCancellationRequested(); owner.RequireControlIdentity(default);
            var root = OpenAt2(-100, "/", DirectoryFlags, 0, 0); nodes.Add(root);
            var rootMetadata = LinuxProtectedMetadata.From(StatFd(root)); rootMetadata.RequireAncestor(false);
            var run = OpenAt2(Fd(root), "run", DirectoryFlags, 0, ParentResolution); nodes.Add(run);
            var runMetadata = LinuxProtectedMetadata.From(StatFd(run)); runMetadata.RequireAncestor(false);
            var name = "appsurface-evidence-n04-" + owner.RunId.ToString("N");
            if (MkdirAt(Fd(run), name, 0x1c0) != 0) throw LinuxN04CheckpointOrder.Rejected();
            var directory = OpenAt2(Fd(run), name, DirectoryFlags, 0, ChildResolution); nodes.Add(directory);
            var directoryMetadata = LinuxProtectedMetadata.From(StatFd(directory)); RequirePrivate(directoryMetadata, true);
            var path = "/run/" + name + "/" + SocketName;
            socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new LinuxOwnedUnixEndPoint(path));
            var named = OpenAt2(Fd(directory), SocketName, PathFlags, 0, ChildResolution); nodes.Add(named);
            var created = LinuxProtectedMetadata.From(StatFd(named)); RequirePrivate(created, false, sealedSocket: false);
            if (FchmodAt2(452, Fd(named), string.Empty, 0x180, 0x1000) != 0) throw LinuxN04CheckpointOrder.Rejected();
            var socketMetadata = LinuxProtectedMetadata.From(StatFd(named)); RequirePrivate(socketMetadata, false);
            socket.Listen(1);
            token.ThrowIfCancellationRequested(); owner.RequireControlIdentity(default);
            worker.RequireServerOwner(input, owner, accounts, workspace, listener, token);
            var result = new LinuxN04CheckpointOwner(owner, worker, socket, nodes.ToArray(),
                [rootMetadata, runMetadata, directoryMetadata, socketMetadata], name);
            result.Check(token);
            socket = null; nodes.Clear();
            return result;
        }
        catch
        {
            try { socket?.Dispose(); } catch (Exception) { }
            foreach (var node in nodes.AsEnumerable().Reverse()) try { node.Dispose(); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Reserves next-accept before the first handler dispatch; no native callback runs under the server gate.</summary>
    internal void ReserveNextAcceptBeforeHandlerDispatch() => _order.Reserve();

    /// <summary>Waits before the next synchronous listener registration under the original root token.</summary>
    internal Task BeforeNextAcceptAsync(CancellationToken token) => _order.BeforeNextAcceptAsync(_workerExit, token);

    /// <summary>Signals pre-admission preparation and waits for the root helper under the original root job token.</summary>
    /// <remarks>Registered before dispatch; it grants no worker admission, and close joins its original socket I/O.</remarks>
    internal Task AdmissionPreparedAsync(CancellationToken token) => StartExchange(null, token);

    /// <summary>Signals genuine READY data preparation and waits for a separate authenticated root release.</summary>
    /// <remarks>The original request token/deadline and held reply ordering are unchanged.</remarks>
    internal Task ReadyPreparedAsync(CancellationToken token) => StartExchange(true, token);

    /// <summary>Signals only after the original READY write, connection release, owner check and CompleteWrite(true).</summary>
    /// <remarks>The committed notification is data, never accepted-peer or native case proof; it does not reopen next-accept.</remarks>
    internal Task ReadyCommittedAsync(CancellationToken token) => StartExchange(false, token);

    private Task StartExchange(bool? prepared, CancellationToken token)
    {
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation;
        lock (_gate)
        {
            if (_closing) throw LinuxN04CheckpointOrder.Rejected();
            if (prepared is { } ready) _order.Claim(ready); else _order.ClaimPreparation();
            operation = ExchangeOwnedAsync(dispatch.Task, prepared, token);
            _order.Retain(operation);
        }
        dispatch.SetResult();
        return operation;
    }

    private async Task ExchangeOwnedAsync(Task dispatch, bool? prepared, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        var previous = CurrentExchange.Value;
        CurrentExchange.Value = this;
        using var io = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        try
        {
            Check(io.Token);
            if (prepared is null)
            {
                Socket? acquired = await _listener.AcceptAsync(io.Token).ConfigureAwait(false);
                try
                {
                    lock (_gate)
                    {
                        if (_closing || _peer is not null) throw LinuxN04CheckpointOrder.Rejected();
                        RequirePeer(acquired);
                        _peer = acquired; acquired = null;
                    }
                }
                finally { acquired?.Dispose(); }
            }
            Socket peer;
            lock (_gate) { peer = _peer ?? throw LinuxN04CheckpointOrder.Rejected(); RequirePeer(peer); }
            var message = Encoding.ASCII.GetBytes("N04 " + (prepared is null ? "ADMISSION_PREPARED " : prepared.Value ? "READY_PREPARED " : "READY_COMMITTED ") + _generation + "\n");
            var sent = 0;
            while (sent < message.Length)
            {
                Check(io.Token); RequirePeer(peer);
                var count = await peer.SendAsync(message.AsMemory(sent), SocketFlags.None, io.Token).ConfigureAwait(false);
                if (count <= 0) throw LinuxN04CheckpointOrder.Rejected();
                sent += count;
            }
            if (prepared != false)
            {
                var expected = Encoding.ASCII.GetBytes("N04 " + (prepared is null ? "RELEASE_ADMISSION " : "RELEASE_READY ") + _generation + "\n");
                var buffer = new byte[expected.Length];
                for (var offset = 0; offset < buffer.Length; offset++)
                {
                    Check(io.Token); RequirePeer(peer);
                    if (await peer.ReceiveAsync(buffer.AsMemory(offset, 1), SocketFlags.None, io.Token).ConfigureAwait(false) != 1)
                        throw LinuxN04CheckpointOrder.Rejected();
                }
                if (!buffer.AsSpan().SequenceEqual(expected)) throw LinuxN04CheckpointOrder.Rejected();
            }
            Check(io.Token); RequirePeer(peer);
            if (prepared is { } ready) _order.Complete(ready); else _order.CompletePreparation();
        }
        catch (Exception) { _order.MarkFailed(); throw; }
        finally { CurrentExchange.Value = previous; }
    }

    /// <summary>Registers one shared close, interrupts actual rendezvous I/O and joins original operations before FD close.</summary>
    /// <remarks>Call only from the server's outer drain, never inside an exchange; paths remain quarantined.</remarks>
    internal Task CloseAndJoinAsync()
    {
        if (ReferenceEquals(CurrentExchange.Value, this)) throw LinuxN04CheckpointOrder.Rejected();
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task close;
        lock (_gate)
        {
            if (_close is not null) return _close;
            _closing = true; _order.Close();
            close = CloseCoreAsync(dispatch.Task); _close = close;
        }
        dispatch.SetResult();
        return close;
    }

    private async Task CloseCoreAsync(Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        var failed = false;
        try { _stop.Cancel(); } catch (Exception) { failed = true; }
        try { _listener.Dispose(); } catch (Exception) { failed = true; }
        Socket? peer;
        lock (_gate) peer = _peer;
        try { peer?.Dispose(); } catch (Exception) { failed = true; }
        try { await _order.JoinAsync().ConfigureAwait(false); } catch (Exception) { failed = true; }
        foreach (var node in _nodes.Reverse()) try { node.Dispose(); } catch (Exception) { failed = true; }
        try { _stop.Dispose(); } catch (Exception) { failed = true; }
        if (failed || _order.Failed) throw LinuxN04CheckpointOrder.Rejected();
    }

    private void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _owner.RequireControlIdentity(default);
        lock (_gate)
        {
            if (_closing) throw LinuxN04CheckpointOrder.Rejected();
            if (_listener.LocalEndPoint?.ToString() != "/run/" + _name + "/" + SocketName)
                throw LinuxN04CheckpointOrder.Rejected();
            for (var index = 0; index < _nodes.Length; index++)
            {
                var current = LinuxProtectedMetadata.From(StatFd(_nodes[index]));
                if (index < 3 ? !current.SameAncestorAs(_metadata[index]) : current != _metadata[index])
                    throw LinuxN04CheckpointOrder.Rejected();
                if (index is 0 or 1) current.RequireAncestor(false);
                else RequirePrivate(current, index == 2);
            }
            RequireNamed(_nodes[0], "run", _metadata[1], true, ParentResolution);
            RequireNamed(_nodes[1], _name, _metadata[2], true, ChildResolution);
            RequireNamed(_nodes[2], SocketName, _metadata[3], false, ChildResolution);
        }
        _owner.RequireControlIdentity(default);
        token.ThrowIfCancellationRequested();
    }

    private void RequirePeer(Socket peer)
    {
        uint size = 12;
        if (GetPeer(peer.SafeHandle, 1, 17, out var credentials, ref size) != 0 || size != 12
            || credentials.Pid <= 0 || credentials.Pid == Environment.ProcessId || credentials.Uid != 0 || credentials.Gid != 0
            || (_peerPid != 0 && _peerPid != credentials.Pid)) throw LinuxN04CheckpointOrder.Rejected();
        _peerPid = credentials.Pid;
    }

    private static void RequireNamed(SafeFileHandle parent, string name, LinuxProtectedMetadata expected,
        bool directory, ulong resolution)
    {
        using var named = OpenAt2(Fd(parent), name, PathFlags, 0, resolution);
        var actual = LinuxProtectedMetadata.From(StatFd(named));
        if (directory ? !actual.SameAncestorAs(expected) : actual != expected) throw LinuxN04CheckpointOrder.Rejected();
    }

    private static void RequirePrivate(LinuxProtectedMetadata metadata, bool directory, bool sealedSocket = true)
    {
        if (metadata.Uid != 0 || metadata.Gid != 0 || metadata.Inode == 0
            || (directory ? metadata.Mode != 0x41c0 : (metadata.Mode & 0xf000) != 0xc000)
            || (!directory && (metadata.LinkCount != 1 || metadata.Length != 0
                || (sealedSocket ? metadata.Mode != 0xc180 : (metadata.Mode & 0x0c12) != 0))))
            throw LinuxN04CheckpointOrder.Rejected();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Peer { internal int Pid; internal uint Uid; internal uint Gid; }
    [DllImport("libc", EntryPoint = "getuid")] private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
    [DllImport("libc", EntryPoint = "getgid")] private static extern uint GetGid();
    [DllImport("libc", EntryPoint = "getegid")] private static extern uint GetEffectiveGid();
    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetPeer(SafeSocketHandle socket, int level, int option, out Peer peer, ref uint size);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long FchmodAt2(long number, int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode, int flags);
}
