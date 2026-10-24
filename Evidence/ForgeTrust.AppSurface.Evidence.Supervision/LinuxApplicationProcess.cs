using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Owns one authentic AppHost/DCP/resource unit from pending dispatch through physical cleanup.</summary>
/// <remarks>Construction requires reference-equal retained root objects. Client identity strings are
/// comparisons only. All start and readiness work is registered before dispatch and joins before file/account
/// release. A returned startup receipt is not health; health requires a separately authenticated kernel peer.</remarks>
internal sealed partial class LinuxApplicationProcess
{
    private readonly object _gate = new();
    private readonly EvidenceProtectedLaunchInput _input;
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxRunAccounts _accounts;
    private readonly LinuxRunWorkspace _workerWorkspace;
    private readonly LinuxProducerWorkspace _producerWorkspace;
    private readonly LinuxApplicationWorkspace _workspace;
    private readonly EvidenceApplicationSelection _selection;
    private readonly SupervisionRunOutputBudget _budget;
    private readonly SupervisionApplicationLifetime _lifetime;
    private readonly SupervisionPendingStart _pending;
    private readonly SupervisionWorkRegistry _ledger;
    private SupervisionWorkRegistry.Workload? _activeUnit;
    private LinuxOutputPipes? _pipes;
    private Task<SupervisionOutputReceipt>? _collect;
    private CancellationTokenSource? _outputDeadline;
    private LinuxSystemdBackend? _backend;
    private LinuxSystemdBackend? _monitorBackend;
    private LinuxApplicationUnit? _recipe;
    private LinuxProcessIdentity? _appHost;
    private LinuxProcessIdentity? _resource;
    private LinuxApplicationDcpIdentity? _dcp;
    private SupervisionOutputReceipt? _output;
    private LinuxUnitProperties? _stoppedTerminal;
    private Task? _stop;
    private Task? _monitor;
    private readonly CancellationTokenSource _monitorStop = new();
    private readonly SupervisionApplicationMonitorState _monitorState = new();
    private int _dispatchAttempted;
    private int _failed;
    private int _settled;
    private EvidenceLinuxApplicationStartReceipt? _started;
    private EvidenceLinuxApplicationResourceReceipt? _ready;

    private LinuxApplicationProcess(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workerWorkspace, LinuxProducerWorkspace producerWorkspace,
        LinuxApplicationWorkspace workspace, EvidenceApplicationSelection selection,
        SupervisionWorkRegistry ledger, SupervisionRunOutputBudget budget)
    {
        _input = input; _owner = owner; _accounts = accounts; _workerWorkspace = workerWorkspace;
        _producerWorkspace = producerWorkspace; _workspace = workspace; _selection = selection;
        _ledger = ledger; _budget = budget; _lifetime = new(ledger);
        _pending = new(LinuxUnitName.Create(LinuxUnitRole.Application, owner.RunId));
    }
    /// <summary>Gets the generated unit name as data, never a unit owner.</summary>
    internal LinuxUnitName Unit => _pending.Unit;
    /// <summary>Gets whether pending starts, processes, group and both pumps physically joined.</summary>
    internal bool PhysicallySettled => Volatile.Read(ref _settled) != 0;
    /// <summary>Gets actual received output only after physical settlement.</summary>
    internal SupervisionOutputReceipt? Output => PhysicallySettled ? _output : throw Rejected();

    /// <summary>Retains the private application owner before pipes, callbacks or any process-start I/O.</summary>
    internal static LinuxApplicationProcess Create(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workerWorkspace, LinuxProducerWorkspace producerWorkspace,
        LinuxApplicationWorkspace workspace, EvidenceApplicationSelection selection,
        SupervisionWorkRegistry ledger, SupervisionRunOutputBudget budget, CancellationToken token)
    {
        owner.RequireLaunchInput(input, token); accounts.RequireOwnedBy(owner, token);
        workerWorkspace.RequireOwnedBy(owner, accounts, token); producerWorkspace.RequireOwnedBy(owner, accounts, token);
        workspace.RequireOwnedBy(owner, accounts, token);
        if (workspace.EntryDigest != selection.EntryDigest) throw Rejected();
        var result = new LinuxApplicationProcess(input, owner, accounts, workerWorkspace, producerWorkspace,
            workspace, selection, ledger, budget);
        workspace.RetainApplication(result);
        return result;
    }

    /// <summary>Consumes one startup attempt before comparing request identity or dispatching native work.</summary>
    internal Task<EvidenceLinuxApplicationStartReceipt> StartAsync(EvidenceApplicationStartControlRequest request,
        CancellationToken token) => _lifetime.StartAsync(async ct =>
        {
            if (request.ApplicationId != _selection.Definition.Id || request.EntryDigest != _selection.EntryDigest) throw Rejected();
            RequireLive(ct);
            _workspace.RecheckBundle(ct);
            _activeUnit = _ledger.BeginWorkload();
            _outputDeadline = LinuxOutputPipes.CreateDrainDeadline(_owner.TeardownCancellation, _input.Remaining);
            _pipes = LinuxOutputPipes.Create();
            _collect = _pipes.BeginCollectAsync(_outputDeadline.Token,
                receivedByteLimit: _selection.Definition.Capabilities.MaximumOutputBytes,
                prefixByteLimit: 64 * 1024, runBudget: _budget);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.Remaining.Ticks,
                TimeSpan.FromSeconds(Math.Min(_selection.Definition.Capabilities.StartSeconds, _input.Request.StartSeconds)).Ticks)));
            _backend = await LinuxSystemdBackend.ConnectAsync(startup.Token).ConfigureAwait(false);
            _recipe = LinuxApplicationUnit.Create(Unit, _owner.Unit, _input.Request.RuntimeHost, _input.Request.RuntimeRoot,
                _input.Request.ToolRoot, _input.Request.SubjectRoot, _workerWorkspace.OutputParent,
                _workerWorkspace.Layout.ControlRoot, _producerWorkspace.Root, _workspace.BundleRoot, _workspace.AppHostPath,
                _workspace.ScratchRoot, _workspace.ProtectedOutputRoot, _workspace.InputPath,
                _accounts.ApplicationUid, _accounts.ApplicationGid, new LinuxApplicationLimits(
                    _selection.Definition.Capabilities.ScratchBytes, _selection.Definition.Capabilities.MemoryBytes,
                    _selection.Definition.Capabilities.MaximumTasks, _selection.Definition.Capabilities.MaximumOutputBytes,
                    _selection.Definition.Capabilities.StartSeconds, _selection.Definition.Capabilities.StoppingSeconds),
                _owner.Remaining - _owner.CleanupAllowance, _pipes.StandardOutput, _pipes.StandardError);
            _workspace.Arm(startup.Token);
            try
            {
                await _pending.StartAsync(startToken =>
                {
                    RequireLive(startToken);
                    Interlocked.Exchange(ref _dispatchAttempted, 1);
                    return _backend.StartApplicationAsync(_recipe, startToken);
                }, startup.Token).ConfigureAwait(false);
            }
            finally { _pipes.CloseWriteCopies(); }
            while (true)
            {
                RequirePolling(startup.Token);
                var sample = await _backend.ReadUnitAsync(Unit, startup.Token).ConfigureAwait(false);
                if (_recipe.IsRunning(sample))
                {
                    _appHost = LinuxProcessIdentity.CaptureApplication(sample.MainPid, _accounts.ApplicationUid,
                        _accounts.ApplicationGid, Unit, startup.Token);
                    _appHost.Recheck(startup.Token);
                    _started = new(_owner.RunId.ToString("N"), checked((int)_appHost.Pid),
                        _accounts.ApplicationUid, _accounts.ApplicationGid, _appHost.ControlGroup);
                    _monitorBackend = await LinuxSystemdBackend.ConnectAsync(startup.Token).ConfigureAwait(false);
                    _monitor = MonitorAsync();
                    Emit(new
                    {
                        schema = "evidence-application-native-v1",
                        phase = "started",
                        lease_id = _started.LeaseId,
                        unit = Unit.Value,
                        apphost_pid = _started.AppHostPid,
                        uid = _started.ApplicationUid,
                        gid = _started.ApplicationGid,
                        cgroup = _started.Cgroup
                    });
                    return _started;
                }
                if (sample.ExecMainCode != 0 || sample.ExecMainStatus != 0) throw Rejected();
                await Task.Delay(25, startup.Token).ConfigureAwait(false);
            }
        }, token);

    /// <summary>Consumes one declared readiness attempt and authenticates actual resource credentials and HTTP bytes.</summary>
    internal Task<EvidenceLinuxApplicationResourceReceipt> WaitAsync(EvidenceResourceWaitControlRequest request,
        CancellationToken token) => _lifetime.WaitResourceAsync(request.ResourceId, async ct =>
        {
            var started = _started ?? throw Rejected();
            var resource = _selection.Definition.Resources.SingleOrDefault(x => x.Declaration.Id == request.ResourceId)
                ?? throw Rejected();
            if (request.LeaseId != started.LeaseId) throw Rejected();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.Remaining.Ticks,
                TimeSpan.FromSeconds(resource.Declaration.DeadlineSeconds).Ticks)));
            while (true)
            {
                RequirePolling(deadline.Token);
                _appHost!.Recheck(deadline.Token);
                try
                {
                    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(_workspace.ScratchRoot + "/http.sock"), deadline.Token).ConfigureAwait(false);
                    var peer = ReadPeer(socket);
                    if (peer.Pid == _started!.AppHostPid || peer.Uid != _accounts.ApplicationUid || peer.Gid != _accounts.ApplicationGid) throw Rejected();
                    _resource = LinuxProcessIdentity.CaptureApplication(checked((uint)peer.Pid), peer.Uid, peer.Gid, Unit, deadline.Token);
                    LinuxApplicationDcpIdentity.RequireResourceCommand(_resource, _input.Request.RuntimeHost,
                        _workspace.BundleRoot + "/resource/NativeHttpResource.dll", _workspace.ScratchRoot + "/http.sock", deadline.Token);
                    EmitReadiness(new { schema = "evidence-application-readiness-v1", stage = "resource-peer-validated", pid = peer.Pid });
                    await using var stream = new NetworkStream(socket, ownsSocket: false);
                    await stream.WriteAsync("GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                    var bytes = new byte[4097]; var length = 0;
                    while (length < bytes.Length)
                    {
                        var count = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        length += count;
                    }
                    RequireHealth(bytes.AsSpan(0, length));
                    EmitReadiness(new { schema = "evidence-application-readiness-v1", stage = "http-validated", received_bytes = length });
                    if (ReadPeer(socket) != peer) throw Rejected();
                    _resource.Recheck(deadline.Token); _appHost.Recheck(deadline.Token);
                    _workspace.RecheckBundle(deadline.Token);
                    var captureFailures = 0;
                    while (_dcp is null)
                    {
                        RequirePolling(deadline.Token);
                        _resource.Recheck(deadline.Token); _appHost.Recheck(deadline.Token);
                        LinuxDcpCaptureFailure? captureFailure = null;
                        try
                        {
                            _dcp = LinuxApplicationDcpIdentity.CaptureForResource(Unit, _accounts.ApplicationUid, _accounts.ApplicationGid,
                                _workspace.DcpPath, _resource, deadline.Token, out captureFailure);
                        }
                        catch (EvidenceAdmissionException) when (!deadline.IsCancellationRequested)
                        {
                            if (++captureFailures == 1) EmitReadiness(new
                            {
                                schema = "evidence-application-readiness-v1",
                                stage = "dcp-capture-rejected",
                                checkpoint = captureFailure?.Stage.ToString(),
                                error_kind = captureFailure?.ErrorKind,
                                native_errno = captureFailure?.NativeErrno,
                                observed_processes = captureFailure?.ObservedProcesses,
                                matching_executables = captureFailure?.MatchingExecutables
                            });
                            // A member can fork/exit during the two complete cgroup/proc samples.
                            // Retry observation within this same readiness deadline; no partial sample grants health.
                            await Task.Delay(25, deadline.Token).ConfigureAwait(false);
                        }
                    }
                    _dcp.RequireResourcePeer(_resource, _input.Request.RuntimeHost, deadline.Token);
                    RequirePolling(deadline.Token);
                    _ready = new(started.LeaseId, request.ResourceId, peer.Uid, started.Cgroup, 200, length);
                    Emit(new
                    {
                        schema = "evidence-application-native-v1",
                        phase = "ready",
                        lease_id = started.LeaseId,
                        resource_id = request.ResourceId,
                        resource_pid = peer.Pid,
                        uid = peer.Uid,
                        gid = peer.Gid,
                        cgroup = started.Cgroup,
                        http_status = 200,
                        received_bytes = length,
                        dcp_checked = true,
                        dcp_pid = _dcp.Pid
                    });
                    return _ready;
                }
                catch (SocketException error) when (error.NativeErrorCode is 2 or 111
                    || error.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.ConnectionRefused)
                { await Task.Delay(25, deadline.Token).ConfigureAwait(false); }
            }
        }, token);

    /// <summary>Rechecks authentic application and resource identity before a producer may dispatch.</summary>
    internal void RequireReady(CancellationToken token)
    {
        RequirePolling(token);
        if (_ready is null || _started is null || _appHost is null || _resource is null || _dcp is null
            || _monitor?.IsFaulted == true || _stop is not null) throw Rejected();
        _appHost.Recheck(token); _resource.Recheck(token); _dcp.RequireResourcePeer(_resource, _input.Request.RuntimeHost, token); _workspace.RecheckBundle(token);
        RequirePolling(token);
    }

    /// <summary>Strict bounded HTTP parser; application output and JSON never supply readiness authority.</summary>
    internal static void RequireHealth(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > 4096 || bytes.Contains((byte)0)) throw Rejected();
        var text = Encoding.ASCII.GetString(bytes);
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (split < 0 || !text.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal)
            || text[(split + 4)..] != "native-http-ready") throw Rejected();
        var headers = text[..split].Split("\r\n");
        var lengths = headers.Skip(1).Where(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (lengths.Length != 1 || !int.TryParse(lengths[0][15..].Trim(), out var length)
            || length != "native-http-ready".Length
            || headers.Any(x => x.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))) throw Rejected();
    }

    /// <summary>Shares one irreversible stop; joins original application operations before final group/pump checks.</summary>
    internal Task StopAndJoinAsync(CancellationToken token)
    {
        lock (_gate) return _stop ??= StopCoreAsync(token);
    }
    private async Task StopCoreAsync(CancellationToken token)
    {
        var failed = false;
        _monitorState.BeginStop();
        try { _monitorStop.Cancel(); } catch { failed = true; }
        try { await _lifetime.StopAndJoinAsync(StopUnitAsync, token).ConfigureAwait(false); }
        catch { failed = true; }
        try
        {
            if (_monitor is not null)
            {
                try { await _monitor.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_monitorStop.IsCancellationRequested) { }
                catch { _ledger.RecordFailure(SupervisionWorkFailure.WorkloadFailed); }
            }
            _owner.RequireCleanup(token);
            if (Volatile.Read(ref _dispatchAttempted) != 0)
            {
                if (_backend is null || _recipe is null) throw Rejected();
                var first = await _backend.ReadUnitAsync(Unit, token).ConfigureAwait(false);
                if (!_recipe.IsStopped(first) || !LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(Unit, token))) throw Rejected();
                await Task.Delay(25, token).ConfigureAwait(false);
                var second = await _backend.ReadUnitAsync(Unit, token).ConfigureAwait(false);
                if (!_recipe.IsStopped(second)) throw Rejected();
                LinuxApplicationTerminalPolicy.RequireStableMain(first, second, _appHost?.Pid);
                if (!LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(Unit, token))) throw Rejected();
                _stoppedTerminal = second;
                // Nonzero/signal execution remains an unsuccessful procedure even when root containment
                // safely joined. Do not conflate workload outcome with permission to secure its files.
                if (second.ExecMainCode != 1 || second.ExecMainStatus != 0)
                    _ledger.RecordFailure(SupervisionWorkFailure.WorkloadFailed);
            }
            _pipes?.CloseWriteCopies();
            if (_collect is not null)
            {
                _output = await _pipes!.JoinAsync().ConfigureAwait(false);
                try { _workspace.RetainPrivateOutput(_output, token); }
                catch (Exception diagnostic) when (diagnostic is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { }
                if (!_output.Drained) throw Rejected();
            }
            if (_pipes is not null) await _pipes.DisposeAsync().ConfigureAwait(false);
            _appHost?.Dispose(); _resource?.Dispose(); _dcp?.Dispose(); _monitorBackend?.Dispose(); _backend?.Dispose(); _outputDeadline?.Dispose(); _monitorStop.Dispose();
            _owner.RequireCleanup(token);
            if (_pending.Snapshot is not { Closed: true, StopJoined: true } || failed) throw Rejected();
            Interlocked.Exchange(ref _settled, 1);
            Emit(new
            {
                schema = "evidence-application-native-v1",
                phase = "settled",
                lease_id = _owner.RunId.ToString("N"),
                unit = Unit.Value,
                group_empty_twice = true,
                pumps_joined = _output is null || _output.Drained,
                received_bytes = _output?.ReceivedBytes ?? 0,
                ledger_failure = _ledger.FirstFailure.ToString(),
                monitor_failed = _monitorState.Failed,
                main_code = _stoppedTerminal?.ExecMainCode,
                main_status = _stoppedTerminal?.ExecMainStatus,
                pending_start_joined = !_pending.Snapshot.StartReserved || _pending.Snapshot.StartJoined
            });
        }
        catch { failed = true; }
        finally
        {
            if (_activeUnit is not null && !_activeUnit.Complete()) failed = true;
            if (failed) { Interlocked.Exchange(ref _failed, 1); _ledger.RecordFailure(SupervisionWorkFailure.WorkloadFailed); }
        }
        if (failed) throw Rejected();
    }

    private async Task MonitorAsync()
    {
        try
        {
            while (_monitorState.ShouldObserve)
            {
                var token = _monitorStop.Token;
                // The application remains owned when STOP closes new-work admission. Monitor that
                // existing execution under the original control identity/deadline until its stop boundary.
                _owner.RequireControlIdentity(token);
                _input.Deadline.Check(token);
                if (_budget.QuotaExceeded || _collect?.IsFaulted == true || _collect?.IsCanceled == true) throw Rejected();
                var sample = await _monitorBackend!.ReadUnitAsync(Unit, token).ConfigureAwait(false);
                if (!_recipe!.IsRunning(sample) || sample.MainPid != _appHost!.Pid) throw Rejected();
                _resource?.Recheck(token); _dcp?.Recheck(token);
                if (_ready is not null) _dcp!.RequireResourcePeer(_resource!, _input.Request.RuntimeHost, token);
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_monitorStop.IsCancellationRequested) { }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            if (_monitorState.RecordObservationFailure())
            {
                _ledger.RecordFailure(SupervisionWorkFailure.WorkloadFailed);
                throw;
            }
        }
    }

    private static void EmitReadiness<T>(T value)
    {
        try { Emit(value); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { }
    }

    private static void Emit<T>(T value)
    {
        var bytes = EvidenceCanonicalJson.Serialize(value);
        if (bytes.Length > 4096) throw Rejected();
        using var stream = Console.OpenStandardError();
        stream.Write(bytes); stream.Write("\n"u8); stream.Flush();
    }

    private async Task StopUnitAsync(CancellationToken token)
    {
        await _pending.StopAsync(async ct =>
        {
            if (Volatile.Read(ref _dispatchAttempted) == 0) return;
            _owner.RequireCleanup(ct);
            using var backend = await LinuxSystemdBackend.ConnectAsync(ct).ConfigureAwait(false);
            var cooperative = false;
            using (var grace = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                grace.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.CleanupRemaining.Ticks,
                    TimeSpan.FromSeconds(_selection.Definition.Capabilities.StoppingSeconds).Ticks)));
                try
                {
                    var before = await backend.ReadUnitAsync(Unit, grace.Token).ConfigureAwait(false);
                    _recipe!.RequirePolicy(before);
                    if (before.MainPid != 0)
                        await backend.TerminateApplicationMainAsync(Unit, grace.Token).ConfigureAwait(false);
                    while (true)
                    {
                        _owner.RequireCleanup(grace.Token);
                        var sample = await backend.ReadUnitAsync(Unit, grace.Token).ConfigureAwait(false);
                        _recipe.RequirePolicy(sample);
                        if (sample.MainPid == 0 && sample.ExecMainPid > 0 && sample.ExecMainCode == 1
                            && LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(Unit, grace.Token)))
                        { cooperative = true; break; }
                        await Task.Delay(25, grace.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (grace.IsCancellationRequested && !ct.IsCancellationRequested) { }
                catch (Exception error) when (!ct.IsCancellationRequested
                    && error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                {
                    // An unconfirmed grace observation cannot bypass physical containment. The fresh
                    // authenticated connection below must still stop/KILL and prove the final unit/group.
                    cooperative = false;
                }
            }
            // Deactivate the retained RemainAfterExit unit. Only an unconfirmed grace escalates;
            // KILL all immediately follows the joined stop request rather than starting a second grace.
            // Grace cancellation may abort its D-Bus connection. The original starting connection
            // still holds AddRef; forced containment authenticates a fresh independent connection.
            using var containment = await LinuxSystemdBackend.ConnectAsync(ct).ConfigureAwait(false);
            await containment.StopUnitAsync(Unit, ct).ConfigureAwait(false);
            if (!cooperative) await containment.KillUnitAsync(Unit, ct).ConfigureAwait(false);
            _owner.RequireCleanup(ct);
        }, token).ConfigureAwait(false);
    }
    /// <summary>Requires actual original references and physical cleanup, not detached receipt booleans.</summary>
    internal void RequirePhysicalSettlement(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        LinuxApplicationWorkspace workspace, CancellationToken token)
    {
        if (!ReferenceEquals(owner, _owner) || !ReferenceEquals(accounts, _accounts) || !ReferenceEquals(workspace, _workspace)
            || !PhysicallySettled || Volatile.Read(ref _failed) != 0 || _stop is not { IsCompletedSuccessfully: true }) throw Rejected();
        _owner.RequireControlIdentity(token);
        if (Volatile.Read(ref _dispatchAttempted) != 0 && !LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(Unit, token))) throw Rejected();
        _owner.RequireControlIdentity(token);
    }
    private void RequireLive(CancellationToken token)
    { _owner.RequireLaunchInput(_input, token); _accounts.RequireOwnedBy(_owner, token); _workspace.RequireOwnedBy(_owner, _accounts, token); }
    private void RequirePolling(CancellationToken token)
    {
        _owner.RequireActive(token); _input.Deadline.Check(token);
        if (_budget.QuotaExceeded || _collect?.IsFaulted == true || _collect?.IsCanceled == true) throw Rejected();
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Credentials(int Pid, uint Uid, uint Gid);
    private static Credentials ReadPeer(Socket socket)
    {
        uint length = 12;
        if (GetPeer(socket.SafeHandle, 1, 17, out var value, ref length) != 0 || length != 12 || value.Pid <= 0) throw Rejected();
        return value;
    }
    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetPeer(SafeSocketHandle socket, int level, int option, out Credentials value, ref uint length);
    /// <summary>Creates the existing fixed rejection diagnostic without exposing ownership authority.</summary>
    /// <returns>A sanitized <see cref="EvidenceAdmissionException"/> with no inner exception or caller data.</returns>
    internal static EvidenceAdmissionException Rejected() => new("ASEVD410", "Restricted application ownership or independent resource readiness was not confirmed.");
}
