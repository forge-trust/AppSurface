using System.Net.Sockets;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed listener metadata checks; sampled values cannot bind a socket or authenticate a worker.</summary>
internal static class LinuxControlListenerPolicy
{
    /// <summary>Fixed filename beneath the actual retained broker parent.</summary>
    internal const string SocketName = "control.sock";
    /// <summary>Maximum retained accepted connections and native pending connection backlog.</summary>
    internal const int MaximumConnections = 32;

    /// <summary>Requires a fresh root-created single-link socket inode, before descriptor-relative sealing.</summary>
    internal static void RequireCreated(LinuxProtectedMetadata value)
    {
        if (value.Inode == 0 || value.Uid != 0 || value.Gid != 0 || value.LinkCount != 1 || value.Length != 0
            || (value.Mode & 0xf000) != 0xc000 || (value.Mode & 0x0e00) != 0) throw Invalid();
    }

    /// <summary>Requires the exact root:worker 0660 socket policy, without extra permission or special bits.</summary>
    internal static void RequireSealed(LinuxProtectedMetadata value, uint workerGid)
    {
        if (workerGid is 0 or uint.MaxValue || value.Inode == 0 || value.Uid != 0 || value.Gid != workerGid
            || value.Mode != 0xc1b0 || value.LinkCount != 1 || value.Length != 0) throw Invalid();
    }

    /// <summary>Rejects identity, ownership, size, permission or timestamp substitution of the retained socket.</summary>
    internal static void RequireSameSocket(LinuxProtectedMetadata value, LinuxProtectedMetadata expected, uint workerGid)
    {
        RequireSealed(value, workerGid);
        RequireSealed(expected, workerGid);
        if (value != expected) throw Invalid();
    }

    /// <summary>Checks selected worker data against this owner's generation and independently selected account IDs.</summary>
    /// <remarks>Only the private native factory's retained process object establishes actual kernel continuity.</remarks>
    internal static void RequireWorkerSelection(Guid generation, uint pid, uint uid, uint gid,
        LinuxUnitName unit, LinuxProcessSamplingRole role, uint workerUid, uint workerGid)
    {
        RequireWorkerGeneration(generation, unit, role);
        if (workerUid is 0 or uint.MaxValue || workerGid is 0 or uint.MaxValue
            || uid != workerUid || gid != workerGid) throw Invalid();
        LinuxProcessData.RequireSelection(pid, uid, gid, unit, role);
    }

    /// <summary>Compares worker role/name data with the exact owner generation; it grants no native identity.</summary>
    internal static void RequireWorkerGeneration(Guid generation, LinuxUnitName unit, LinuxProcessSamplingRole role)
    {
        if (generation == Guid.Empty || role != LinuxProcessSamplingRole.Worker || unit is null
            || unit.Value != LinuxUnitName.Create(LinuxUnitRole.Worker, generation).Value) throw Invalid();
    }

    /// <summary>Creates a fixed rejection without native exception text, selected paths or peer IDs.</summary>
    internal static EvidenceAdmissionException Invalid() =>
        new("ASEVD402", "The protected control listener is unavailable or changed.");
}

/// <summary>Actual root-owned UNIX listener bound beneath one retained, single-use run workspace.</summary>
/// <remarks>
/// The private factory requires actual owner/accounts/workspace objects. It never adopts or unlinks a collision.
/// A retained O_PATH socket inode is sealed using fchownat/fchmodat2 with AT_EMPTY_PATH, without pathname chmod
/// fallback. The socket is listening only after all root:worker 0660 and retained-name checks succeed. Linux x64
/// openat2/statx/fchmodat2 support is mandatory. The initial root socket may have restrictive umask permissions;
/// creation grants no listening access before sealing. Worker permissions and unit grants are unchanged.
///
/// Bind before worker launch; capture the actual worker PID/kernel tuple, publish the descriptor, then accept.
/// The same retained worker identity is pinned for every connection; process continuity and actual SO_PEERCRED
/// are checked again at each network read/write. Accept ownership precedes I/O and successful connections remain
/// retained until ReleaseAsync or disposal actually closes them. These checks establish transport ownership,
/// never worker admission, procedure selection, workload exit or artifact custody.
///
/// The server must register handler ownership before awaiting AcceptAsync and join handler I/O separately.
/// Dispose closes the listening socket, joins the original pending accept and every retained connection close,
/// then closes native path handles. An accept ignoring cancellation remains owned until it really finishes or
/// the independent OS owner lifetime terminates root. Closing the listener does not delete its pathname, output,
/// descriptor or accounts. Custody and account teardown must follow all handlers, workers and pumps joining.
/// </remarks>
internal sealed partial class LinuxControlListener : IAsyncDisposable
{
    private const ulong PathFlags = 0x200000 | 0x80000; // O_PATH | O_CLOEXEC.
    private const ulong ChildResolution = 0x01 | 0x02 | 0x04 | 0x08;
    private const int EmptyPath = 0x1000;
    private readonly object _gate = new();
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxRunAccounts _accounts;
    private readonly LinuxRunWorkspace _workspace;
    private readonly Socket _socket;
    private readonly SafeFileHandle _parent;
    private readonly SafeFileHandle _namedSocket;
    private readonly LinuxProtectedMetadata _parentMetadata;
    private readonly LinuxProtectedMetadata _socketMetadata;
    private readonly SupervisionAcceptOwnership<LinuxControlConnection> _accepts;
    private readonly LinuxControlFailureLatch _failures = new();
    private LinuxProcessIdentity? _worker;
    private TaskCompletionSource? _disposeCompletion;
    private int _socketClosed;
    private bool _failed;
    private bool _closing;

    private LinuxControlListener(LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxRunWorkspace workspace,
        Socket socket, SafeFileHandle parent, SafeFileHandle namedSocket,
        LinuxProtectedMetadata parentMetadata, LinuxProtectedMetadata socketMetadata)
    {
        _owner = owner; _accounts = accounts; _workspace = workspace;
        _socket = socket; _parent = parent; _namedSocket = namedSocket;
        _parentMetadata = parentMetadata; _socketMetadata = socketMetadata;
        _accepts = new(AcceptActualAsync, CloseSocket, static connection => connection.DisposeAsync());
    }

    /// <summary>Gets the fixed bound path as data; it cannot create another listener or authenticate a peer.</summary>
    internal string SocketPath => _workspace.ControlSocket;

    /// <summary>Gets the first closed accept checkpoint; absence is not connection or native success.</summary>
    /// <remarks>Contains no peer, pathname, raw exception or capability; only the actual server reads this data after failure.</remarks>
    internal LinuxControlFailure? FirstFailure => _failures.First;

    /// <summary>Returns original socket comparison metadata only after this actual listener's successful native drain.</summary>
    /// <remarks>These fields grant no custody; the native transfer must reopen and compare the same named inode.</remarks>
    internal LinuxProtectedMetadata RequireCustodySocket(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        LinuxRunWorkspace workspace, CancellationToken token)
    {
        owner.RequireControlIdentity(token);
        lock (_gate)
        {
            if (!ReferenceEquals(owner, _owner) || !ReferenceEquals(accounts, _accounts)
                || !ReferenceEquals(workspace, _workspace) || _disposeCompletion?.Task.IsCompletedSuccessfully != true
                || !_closing || _failed || _accepts.Failed || Volatile.Read(ref _socketClosed) == 0)
                throw LinuxControlListenerPolicy.Invalid();
            return _socketMetadata;
        }
    }

    /// <summary>Requires this retained listener to belong to the same actual owner, accounts and workspace.</summary>
    /// <remarks>Use before worker dispatch; matching pathname data alone is insufficient.</remarks>
    internal void RequireOwnedBy(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        LinuxRunWorkspace workspace, CancellationToken token)
    {
        lock (_gate)
        {
            try
            {
                if (!ReferenceEquals(owner, _owner) || !ReferenceEquals(accounts, _accounts)
                    || !ReferenceEquals(workspace, _workspace)) throw LinuxControlListenerPolicy.Invalid();
                owner.RequireActive(token);
                accounts.RequireOwnedBy(owner, token);
                Check(token);
            }
            catch { Fail(); throw; }
        }
    }

    /// <summary>Claims and binds the workspace's only listener, sealing the actual retained socket before Listen.</summary>
    /// <remarks>Failure closes every obtained handle, quarantines the workspace and preserves any collision/path.</remarks>
    internal static LinuxControlListener Bind(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        LinuxRunWorkspace workspace, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(workspace);
        SafeFileHandle? parent = null;
        SafeFileHandle? named = null;
        Socket? socket = null;
        try
        {
            LinuxProtectedDeployment.RequirePlatform();
            parent = workspace.ClaimListenerParent(owner, accounts, token);
            var parentMetadata = LinuxProtectedMetadata.From(StatFd(parent));
            LinuxWorkspaceLayout.RequireDirectory(parentMetadata,
                new("broker", "worker", 0, accounts.WorkerGid, 0x1c8));
            socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            workspace.RequireOwnedBy(owner, accounts, token);
            socket.Bind(new UnixDomainSocketEndPoint(workspace.ControlSocket));
            named = OpenAt2(Fd(parent), LinuxControlListenerPolicy.SocketName, PathFlags, 0, ChildResolution);
            var created = LinuxProtectedMetadata.From(StatFd(named));
            LinuxControlListenerPolicy.RequireCreated(created);
            RequireNamed(parent, created);
            workspace.RequireOwnedBy(owner, accounts, token);
            // Linux 6.5+ fchmodat2 supports the actual O_PATH inode via AT_EMPTY_PATH. No path-based fallback.
            if (FchownAt(named, string.Empty, 0, accounts.WorkerGid, EmptyPath) != 0
                || FchmodAt2(452, Fd(named), string.Empty, 0x1b0, EmptyPath) != 0)
                throw LinuxControlListenerPolicy.Invalid();
            var sealedSocket = LinuxProtectedMetadata.From(StatFd(named));
            LinuxControlListenerPolicy.RequireSealed(sealedSocket, accounts.WorkerGid);
            RequireNamed(parent, sealedSocket);
            workspace.RequireOwnedBy(owner, accounts, token);
            socket.Listen(LinuxControlListenerPolicy.MaximumConnections);
            if (!LinuxProtectedMetadata.From(StatFd(parent)).SameAncestorAs(parentMetadata))
                throw LinuxControlListenerPolicy.Invalid();
            RequireNamed(parent, sealedSocket);
            workspace.RequireOwnedBy(owner, accounts, token);
            var result = new LinuxControlListener(owner, accounts, workspace, socket, parent, named,
                parentMetadata, sealedSocket);
            socket = null; parent = null; named = null;
            return result;
        }
        catch (OperationCanceledException) { workspace.Quarantine(); throw; }
        catch (Exception error) when (Recoverable(error))
        { workspace.Quarantine(); throw LinuxControlListenerPolicy.Invalid(); }
        finally
        {
            try { socket?.Dispose(); }
            finally { try { named?.Dispose(); } finally { parent?.Dispose(); } }
        }
    }

    /// <summary>Accepts one connection for the same actual worker, after sealed descriptor publication.</summary>
    /// <remarks>
    /// Only one pending accept is allowed, with at most 32 retained connections. Cancellation closes the actual
    /// listening socket, joins the actual pending accept and disposes any late connection before rejecting.
    /// The caller retains the process identity until every accepted connection and handler has joined.
    /// </remarks>
    internal Task<LinuxControlConnection> AcceptAsync(LinuxProcessIdentity worker, CancellationToken token)
    {
        var stage = LinuxControlFailureStage.ListenerState;
        lock (_gate)
        {
            try
            {
                Check(token, ref stage);
                stage = LinuxControlFailureStage.ListenerWorkerSelection;
                ArgumentNullException.ThrowIfNull(worker);
                RequireWorker(worker, token, ref stage);
                stage = LinuxControlFailureStage.ListenerDescriptor;
                if (!_workspace.DescriptorWritten || (_worker is not null && !ReferenceEquals(_worker, worker)))
                    throw LinuxControlListenerPolicy.Invalid();
                _worker = worker;
            }
            catch (Exception error)
            { _failures.Capture(stage, null, error); Fail(); throw; }
        }
        return _accepts.AcceptAsync(token);
    }

    /// <summary>Joins one retained connection close; the handler must still join its original read/write tasks.</summary>
    internal Task ReleaseAsync(LinuxControlConnection connection) => _accepts.ReleaseAsync(connection);

    /// <summary>Closes admission and shares the actual accept/result/native-handle drain across all callers.</summary>
    /// <remarks>Does not unlink the socket or establish worker/cgroup exit, workspace custody or account cleanup.</remarks>
    public ValueTask DisposeAsync()
    {
        // Reject same-owner callback reentrancy before mutating native closure state. The returned
        // drain always refers to the registered actual accept/close task, including a failed drain.
        var drain = _accepts.DisposeAsync();
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeCompletion is not null) return new(_disposeCompletion.Task);
            _closing = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeCompletion = completion;
        }
        _ = DisposeActualAsync(drain, completion);
        return new(completion.Task);
    }

    private async Task<LinuxControlConnection> AcceptActualAsync(CancellationToken token)
    {
        Socket? accepted = null;
        var stage = LinuxControlFailureStage.ListenerState;
        try
        {
            lock (_gate)
            {
                if (_closing || Volatile.Read(ref _socketClosed) != 0) throw new SupervisionAcceptShutdownException();
                Check(token, ref stage);
            }
            using var cancellation = token.UnsafeRegister(static state => ((LinuxControlListener)state!).CloseSocket(), this);
            stage = LinuxControlFailureStage.ListenerNativeAccept;
            try { accepted = await _socket.AcceptAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { throw new SupervisionAcceptShutdownException(); }
            catch (ObjectDisposedException) when (Volatile.Read(ref _socketClosed) != 0)
            { throw new SupervisionAcceptShutdownException(); }
            catch (SocketException error) when (Volatile.Read(ref _socketClosed) != 0
                && error.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
            { throw new SupervisionAcceptShutdownException(); }
            lock (_gate)
            {
                stage = LinuxControlFailureStage.ListenerState;
                if (_closing || Volatile.Read(ref _socketClosed) != 0) throw new SupervisionAcceptShutdownException();
                Check(token, ref stage);
                stage = LinuxControlFailureStage.ListenerWorkerSelection;
                var worker = _worker ?? throw LinuxControlListenerPolicy.Invalid();
                RequireWorker(worker, token, ref stage);
                stage = LinuxControlFailureStage.ListenerAcceptedPeer;
                var result = LinuxControlConnection.Accept(accepted, _owner, worker);
                accepted = null; // The connection factory owns the socket, including rejection.
                return result;
            }
        }
        catch (SupervisionAcceptShutdownException) { throw; }
        catch (OperationCanceledException error)
        {
            lock (_gate) { if (!_closing) { _failures.Capture(stage, null, error); Fail(); } }
            throw;
        }
        catch (Exception error) when (Recoverable(error))
        {
            lock (_gate) { if (!_closing) { _failures.Capture(stage, null, error); Fail(); } }
            throw LinuxControlListenerPolicy.Invalid();
        }
        finally { accepted?.Dispose(); }
    }

    private void Check(CancellationToken token)
    {
        var stage = LinuxControlFailureStage.Unknown;
        Check(token, ref stage);
    }

    // A ref checkpoint records ordering only. It cannot skip a native check, alter rejection,
    // authenticate a worker or construct a successful listener/connection.
    private void Check(CancellationToken token, ref LinuxControlFailureStage stage)
    {
        stage = LinuxControlFailureStage.ListenerState;
        if (_closing || _failed || Volatile.Read(ref _socketClosed) != 0) throw LinuxControlListenerPolicy.Invalid();
        stage = LinuxControlFailureStage.ListenerCancellation;
        token.ThrowIfCancellationRequested();
        stage = LinuxControlFailureStage.ListenerWorkspace;
        _workspace.RequireControlOwnedBy(_owner, _accounts, token);
        stage = LinuxControlFailureStage.ListenerParent;
        var parent = LinuxProtectedMetadata.From(StatFd(_parent));
        if (!parent.SameAncestorAs(_parentMetadata)) throw LinuxControlListenerPolicy.Invalid();
        stage = LinuxControlFailureStage.ListenerSocketMetadata;
        LinuxControlListenerPolicy.RequireSameSocket(LinuxProtectedMetadata.From(StatFd(_namedSocket)),
            _socketMetadata, _workspace.Layout.WorkerGid);
        stage = LinuxControlFailureStage.ListenerSocketName;
        RequireNamed(_parent, _socketMetadata);
        stage = LinuxControlFailureStage.ListenerEndpoint;
        if (!_socket.IsBound || _socket.LocalEndPoint is not UnixDomainSocketEndPoint endpoint
            || endpoint.ToString() != SocketPath) throw LinuxControlListenerPolicy.Invalid();
        stage = LinuxControlFailureStage.ListenerCancellation;
        token.ThrowIfCancellationRequested();
    }

    private void RequireWorker(LinuxProcessIdentity worker, CancellationToken token, ref LinuxControlFailureStage stage)
    {
        stage = LinuxControlFailureStage.ListenerWorkerSelection;
        LinuxControlListenerPolicy.RequireWorkerSelection(_owner.RunId, worker.Pid, worker.Uid, worker.Gid,
            worker.Unit, worker.Role, _workspace.Layout.AccountData.WorkerUid, _workspace.Layout.WorkerGid);
        stage = LinuxControlFailureStage.ListenerCancellation;
        token.ThrowIfCancellationRequested();
        stage = LinuxControlFailureStage.ListenerOwnerIdentity;
        _owner.RequireControlIdentity(default);
        stage = LinuxControlFailureStage.ListenerWorkerIdentity;
        worker.Recheck(default);
        stage = LinuxControlFailureStage.ListenerOwnerIdentity;
        _owner.RequireControlIdentity(default);
        stage = LinuxControlFailureStage.ListenerCancellation;
        token.ThrowIfCancellationRequested();
    }

    private async Task DisposeActualAsync(ValueTask drain, TaskCompletionSource completion)
    {
        var failed = false;
        try { await drain.ConfigureAwait(false); }
        catch (Exception error) when (Recoverable(error)) { failed = true; }
        try { _namedSocket.Dispose(); }
        catch (Exception error) when (Recoverable(error)) { failed = true; }
        try { _parent.Dispose(); }
        catch (Exception error) when (Recoverable(error)) { failed = true; }
        lock (_gate) failed |= _failed || _accepts.Failed;
        if (failed)
        {
            lock (_gate) Fail();
            completion.TrySetException(LinuxControlListenerPolicy.Invalid());
        }
        else completion.TrySetResult();
    }

    private void CloseSocket()
    {
        if (Interlocked.Exchange(ref _socketClosed, 1) == 0)
        {
            try { _socket.Dispose(); }
            catch (Exception error) when (Recoverable(error))
            { lock (_gate) Fail(); throw LinuxControlListenerPolicy.Invalid(); }
        }
    }

    private void Fail() { _failed = true; _workspace.Quarantine(); }

    private static void RequireNamed(SafeFileHandle parent, LinuxProtectedMetadata expected)
    {
        using var named = OpenAt2(Fd(parent), LinuxControlListenerPolicy.SocketName, PathFlags, 0, ChildResolution);
        if (LinuxProtectedMetadata.From(StatFd(named)) != expected) throw LinuxControlListenerPolicy.Invalid();
    }

    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    [DllImport("libc", EntryPoint = "fchownat", SetLastError = true)]
    private static extern int FchownAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint uid, uint gid, int flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long FchmodAt2(long number, int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode, int flags);
}
