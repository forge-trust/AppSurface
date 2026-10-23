#if EVIDENCE_PRIVATE_N16
using System.Diagnostics;
#endif
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Owns authenticated control I/O for empty, coverage, and compile-selected application Observations.</summary>
/// <remarks>
/// The private factory retains the same native owners and same-image worker holder before worker dispatch.
/// RunCoreAsync requires its joined startup and actual live kernel identity before accepting requests. The
/// factory independently resolves protected policy/diff bytes; no caller-injected plan, handler, producer or
/// join assertion can enroll a workload. Coverage composition registers the actual producer in the descendant
/// ledger; application operations require a separately selected compiled definition and retained native owner. The worker
/// itself is excluded: its stop/wait request cannot join its own process or active control handler.
/// The N16/N13/N14 private images add one fixed server-owned blocked workload after registering it in this ledger;
/// only N16 releases it through authenticated STOP/WAIT. N13/N14 emit a compile-owned harness phase line after
/// the real acceptance write and keep the original request/body pending for external owner termination. Ordinary
/// images have no such dispatch or barrier. Exit ACK is only protocol completion. The outer root owner must subsequently join the worker's original
/// native/output tasks, recheck custody, close filesystem ownership and delete accounts strictly.
/// </remarks>
internal sealed class LinuxEmptyObservationControlServer
{
    // Keep common async declarations first so qualification selectors do not shift
    // their compiler-generated state-machine identities. Preserve field initializer order.

    private async Task RunOwnedAsync(Task dispatch, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        try
        {
#if EVIDENCE_PRIVATE_N04
            // Fixed private N04 image: acquisition belongs to the already registered original run.
            // No caller/environment selector or detached checkpoint task exists.
            if (EvidenceNativeQualification.PeerReplacementEnabled)
                _n04 = LinuxN04CheckpointOwner.Create(_input, _owner, _accounts, _workspace, _listener, _worker, token);
#endif
#if EVIDENCE_PRIVATE_N07
            if (EvidenceNativeQualification.ParentReplacementEnabled)
            {
                try
                {
                    _n07 = LinuxN07CheckpointOwner.Create(_input, _owner, _accounts, _workspace, _listener, _worker, token);
                }
                catch (Exception error) when (Recoverable(error))
                {
                    _failures.Capture(LinuxControlFailureStage.N07CheckpointCreate, null, error);
                    throw;
                }
            }
#endif
            await RunCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            var closed = true;
#if EVIDENCE_PRIVATE_N04
            if (_n04 is not null)
                try { await _n04.CloseAndJoinAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error))
                { _failures.Capture(LinuxControlFailureStage.HandlerJoin, null, error); closed = false; _sequence.RecordFailure(); }
#endif
#if EVIDENCE_PRIVATE_N07
            if (_n07 is not null)
                try { await _n07.CloseAndJoinAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error))
                { _failures.Capture(LinuxControlFailureStage.HandlerJoin, null, error); closed = false; _sequence.RecordFailure(); }
#endif
            if (_coverage is not null)
                try { _coverage.Dispose(); }
                catch (Exception error) when (Recoverable(error))
                { _failures.Capture(LinuxControlFailureStage.HandlerJoin, null, error); closed = false; _sequence.RecordFailure(); }
#if EVIDENCE_PRIVATE_N07
            Interlocked.Exchange(ref _n07LocalCloseAttemptCompleted, 1);
#endif
            try { _replyOrder.Dispose(); }
            catch (Exception error) when (Recoverable(error))
            { _failures.Capture(LinuxControlFailureStage.ReplyGateClose, null, error); closed = false; _sequence.RecordFailure(); }
            if (!closed) throw Rejected();
            Interlocked.Exchange(ref _localOwnersClosed, 1);
        }
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        var stage = LinuxControlFailureStage.PeerCheck;
        try
        {
            var peer = _worker.RequireWorker(token);
            stage = LinuxControlFailureStage.WorkerExitTask;
            var naturalExit = _worker.WaitForExitAsync();
            stage = LinuxControlFailureStage.RequestLifetime;
            using var requests = CancellationTokenSource.CreateLinkedTokenSource(token, _owner.TeardownCancellation);
            stage = LinuxControlFailureStage.RequestLifetime;
            requests.CancelAfter(_owner.CleanupRemaining);
            stage = LinuxControlFailureStage.RequestLifetime;
            using var accepts = CancellationTokenSource.CreateLinkedTokenSource(requests.Token);
            stage = LinuxControlFailureStage.RequestLifetime;
            var handlers = new List<Task>(SupervisionWorkRegistry.MaximumActiveControls);
            Task<LinuxControlConnection>? pending = null;
            using var cancellationLifetime = CancellationTokenSource.CreateLinkedTokenSource(requests.Token);
            var signalDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
            _cancellationSignal = EvidenceNativeQualification.CancellationEnabled
                ? SignalCancellationOwnedAsync(signalDispatch.Task, cancellationLifetime.Token)
                : Task.CompletedTask;
#else
            _cancellationSignal = Task.CompletedTask;
#endif
            signalDispatch.SetResult(); // Task retained before its actual wait/syscall dispatch, outside gates.
            try
            {
                while (!_exitIntent.Task.IsCompleted && !_exitCommitted.Task.IsCompleted && !naturalExit.IsCompleted)
                {
                    stage = LinuxControlFailureStage.AcceptLoop;
                    requests.Token.ThrowIfCancellationRequested();
                    if (_cancellationFailed.Task.IsCompleted) await _cancellationSignal.ConfigureAwait(false);
                    // Join each original completed handler before removing its retained task. No proxy
                    // wait or success snapshot substitutes for observing the actual procedure.
                    for (var index = handlers.Count - 1; index >= 0; index--)
                        if (handlers[index].IsCompleted)
                        {
                            stage = LinuxControlFailureStage.HandlerJoin;
                            await handlers[index].ConfigureAwait(false);
                            handlers.RemoveAt(index);
                        }
                    if (handlers.Count == SupervisionWorkRegistry.MaximumActiveControls)
                    {
                        stage = LinuxControlFailureStage.CapacityWait;
                        await Task.WhenAny(handlers.Append(_exitIntent.Task).Append(_exitCommitted.Task).Append(naturalExit).Append(_cancellationFailed.Task))
                            .WaitAsync(requests.Token).ConfigureAwait(false);
                        continue;
                    }
                    // This pause occurs before RegisterAccept's synchronous worker/name checks.
                    // It is separate from the irreversible EXIT admission-close barrier.
#if EVIDENCE_PRIVATE_N04
                    if (_n04 is not null) await _n04.BeforeNextAcceptAsync(requests.Token).ConfigureAwait(false);
#endif
#if EVIDENCE_PRIVATE_N07
                    if (_n07 is not null)
                    {
                        stage = LinuxControlFailureStage.N07BeforeNextAccept;
                        await _n07.BeforeNextAcceptAsync(requests.Token).ConfigureAwait(false);
                    }
#endif
                    stage = LinuxControlFailureStage.Accept;
                    SupervisionAcceptOwnership<LinuxControlConnection>.RegisteredAccept registeredAccept;
                    lock (_executionGate)
                    {
                        if (_exitIntent.Task.IsCompleted) break;
                        registeredAccept = _listener.RegisterAccept(peer, accepts.Token);
                    }
                    pending = registeredAccept.Task;
                    registeredAccept.Dispatch(); // Actual accept work starts outside both admission gates.
                    stage = LinuxControlFailureStage.AcceptJoin;
                    var next = await Task.WhenAny(pending, _exitIntent.Task, _exitCommitted.Task, naturalExit, _cancellationFailed.Task)
                        .WaitAsync(requests.Token).ConfigureAwait(false);
                    if (ReferenceEquals(next, _cancellationFailed.Task)) await _cancellationSignal.ConfigureAwait(false);
                    if (_exitIntent.Task.IsCompleted || !ReferenceEquals(next, pending)) break;
                    stage = LinuxControlFailureStage.AcceptJoin;
                    var connection = await pending.ConfigureAwait(false);
                    pending = null;
                    stage = LinuxControlFailureStage.ControlRegistration;
                    var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_executionGate)
                    {
                        if (_exitIntent.Task.IsCompleted) break; // Published result remains listener-owned for final close.
#if EVIDENCE_PRIVATE_N04
                        _n04?.ReserveNextAcceptBeforeHandlerDispatch();
#endif
#if EVIDENCE_PRIVATE_N07
                        _n07?.ReserveNextAcceptBeforeHandlerDispatch();
#endif
                        var control = _ledger.BeginControl();
                        stage = LinuxControlFailureStage.HandlerDispatch;
                        handlers.Add(HandleRegisteredAsync(dispatch.Task, connection, control, requests.Token));
                    }
                    dispatch.SetResult();
                }
            }
            catch (Exception error) when (Recoverable(error))
            {
                _failures.Capture(stage, null, error,
                    stage is LinuxControlFailureStage.Accept or LinuxControlFailureStage.AcceptJoin ? _listener.FirstFailure : null);
                _sequence.RecordFailure();
            }
            finally
            {
                var ioJoined = true;
                var exitIntent = _exitIntent.Task.IsCompleted;
                Task? failedDrain = null;
                if (!exitIntent)
                {
                    // A workload handler can await a descendant that only containment can release.
                    // Start the original cleanup clock and stop before joining any such handler.
                    stage = LinuxControlFailureStage.DescendantsStop;
                    try { failedDrain = _sequence.BeginFailedDrain(CleanupToken()); }
                    catch (Exception error) when (Recoverable(error))
                    { _failures.Capture(stage, null, error); _sequence.RecordFailure(); }
                }
                // Cancel and join this original added task under the same requests/root deadline.
                try { cancellationLifetime.Cancel(); }
                catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); ioJoined = false; _sequence.RecordFailure(); }
                try { await _cancellationSignal.ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationLifetime.IsCancellationRequested) { }
                catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); ioJoined = false; _sequence.RecordFailure(); }
                stage = LinuxControlFailureStage.AcceptCancel;
                try { accepts.Cancel(); }
                catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); _sequence.RecordFailure(); }
                // Interrupt and join original root-rendezvous I/O before joining its parent handlers.
#if EVIDENCE_PRIVATE_N04
                try { if (_n04 is not null) await _n04.CloseAndJoinAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error))
                { _failures.Capture(LinuxControlFailureStage.HandlerJoin, null, error); ioJoined = false; _sequence.RecordFailure(); }
#endif
#if EVIDENCE_PRIVATE_N07
                try { if (_n07 is not null) await _n07.CloseAndJoinAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error))
                { _failures.Capture(LinuxControlFailureStage.HandlerJoin, null, error); ioJoined = false; _sequence.RecordFailure(); }
#endif
                // Valid EXIT intent retains live handlers through their writes. Failure without EXIT still
                // closes all results first to interrupt their original I/O, as before.
                stage = LinuxControlFailureStage.ListenerClose;
                try
                {
                    if (exitIntent) await _listener.CloseAcceptAdmissionAsync().ConfigureAwait(false);
                    else await _listener.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error, _listener.FirstFailure); ioJoined = false; _sequence.RecordFailure(); }
                if (pending is not null)
                {
                    stage = LinuxControlFailureStage.PendingAcceptJoin;
                    try { await pending.ConfigureAwait(false); }
                    catch (Exception error) when (Recoverable(error)) { /* Listener drain independently latches unexpected failure; pending accept cancellation is expected. */ }
                }
                stage = LinuxControlFailureStage.HandlersJoin;
                try { await Task.WhenAll(handlers).ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); ioJoined = false; _sequence.RecordFailure(); }
                if (exitIntent)
                {
                    stage = LinuxControlFailureStage.ListenerClose;
                    try { await _listener.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error, _listener.FirstFailure); ioJoined = false; _sequence.RecordFailure(); }
                }
                stage = LinuxControlFailureStage.DescendantsStop;
                try { await (failedDrain ?? _sequence.StopAsync(CleanupToken())).ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); _sequence.RecordFailure(); }
                stage = LinuxControlFailureStage.ControlsJoin;
                try { await _ledger.CloseAndJoinControlsAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { _failures.Capture(stage, null, error); ioJoined = false; _sequence.RecordFailure(); }
#if EVIDENCE_PRIVATE_N07
                if (_n07 is not null && (pending is null || pending.IsCompleted)
                    && handlers.All(task => task.IsCompleted) && _ledger.ActiveControls == 0)
                    Interlocked.Exchange(ref _n07OriginalTasksCompleted, 1);
#endif
                if (ioJoined && Volatile.Read(ref _connectionCloseFailed) == 0)
                    Interlocked.Exchange(ref _ioJoined, 1);
            }
            stage = LinuxControlFailureStage.FinalCancellation;
            token.ThrowIfCancellationRequested();
            stage = LinuxControlFailureStage.CleanupBound;
            RequireCleanupBound();
            stage = LinuxControlFailureStage.OwnerCheck;
            _owner.RequireControlIdentity(default);
            if (!_sequence.ExitAcknowledged || !_ledger.IsSettled)
            {
                _failures.Capture(naturalExit.IsCompleted && !_exitCommitted.Task.IsCompleted
                    ? LinuxControlFailureStage.WorkerTerminalTaskCompleted : LinuxControlFailureStage.ProtocolIncomplete, null, null);
                throw Rejected();
            }
            stage = LinuxControlFailureStage.CleanupBound;
            RequireCleanupBound();
        }
        catch (Exception error) when (Recoverable(error))
        {
            _failures.Capture(stage, null, error,
            stage == LinuxControlFailureStage.PeerCheck ? _worker.IdentityFailure : null); throw;
        }
    }

    private async Task HandleRegisteredAsync(Task dispatch, LinuxControlConnection connection,
        SupervisionWorkRegistry.Control control, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        await HandleAsync(connection, control, token).ConfigureAwait(false);
    }

    private async Task HandleAsync(LinuxControlConnection connection, SupervisionWorkRegistry.Control control,
        CancellationToken rootToken)
    {
        SupervisionControlSequence.Reply? claim = null;
        var heldReplyOrder = false;
        var released = false;
        var committedExit = false;
        CancellationTokenRegistration cleanupCancellation = default;
        var isCleanupRequest = false;
        var stage = LinuxControlFailureStage.RequestLifetime;
        EvidenceControlOperation? operation = null;
        try
        {
            using var io = CancellationTokenSource.CreateLinkedTokenSource(rootToken);
            try
            {
                io.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.CleanupRemaining.Ticks,
                    TimeSpan.FromSeconds(_input.Request.AdmissionSeconds).Ticks)));
                stage = LinuxControlFailureStage.RequestRead;
                var request = await connection.ReadRequestAsync(io.Token).ConfigureAwait(false);
                operation = request.Operation;
                stage = LinuxControlFailureStage.RequestClassify;
                byte[]? coverageResponse = null;
                if (request is EvidenceRunControlRequest or EvidenceArtifactsControlRequest or EvidenceArtifactControlRequest
                    or EvidenceApplicationStartControlRequest or EvidenceResourceWaitControlRequest)
                {
                    if (_coverage is null || !_sequence.ReadyAcknowledged || _ledger.IsWorkloadAdmissionClosed) throw Rejected();
                    _owner.RequireActive(io.Token);
                    // Request framing retained AdmissionSeconds. Actual declared work now uses the
                    // original job token, not a fresh admission timeout, and does not hold reply order.
                    io.Token.ThrowIfCancellationRequested();
                    io.CancelAfter(Timeout.InfiniteTimeSpan);
                    using var jobOperation = CancellationTokenSource.CreateLinkedTokenSource(rootToken);
                    jobOperation.CancelAfter(_owner.Remaining);
                    switch (request)
                    {
                        case EvidenceApplicationStartControlRequest app:
                            if (_application is null) throw Rejected();
                            var started = await _application.StartAsync(app, jobOperation.Token).ConfigureAwait(false);
                            coverageResponse = EvidenceCanonicalJson.Serialize(new
                            {
                                ok = true,
                                lease_id = started.LeaseId,
                                apphost_pid = started.AppHostPid,
                                application_uid = started.ApplicationUid,
                                application_gid = started.ApplicationGid,
                                cgroup = started.Cgroup,
                                owned = true
                            });
                            break;
                        case EvidenceResourceWaitControlRequest readiness:
                            if (_application is null) throw Rejected();
                            var ready = await _application.WaitAsync(readiness, jobOperation.Token).ConfigureAwait(false);
                            coverageResponse = EvidenceCanonicalJson.Serialize(new
                            {
                                ok = true,
                                lease_id = ready.LeaseId,
                                resource_id = ready.ResourceId,
                                application_uid = ready.ApplicationUid,
                                cgroup = ready.Cgroup,
                                kernel_peer_checked = true,
                                http_status = ready.HttpStatus,
                                healthy = true,
                                received_bytes = ready.ReceivedBytes
                            });
                            break;
                        case EvidenceRunControlRequest run:
                            _application?.RequireReady(jobOperation.Token);
                            var result = await _coverage.RunAsync(run, jobOperation.Token).ConfigureAwait(false);
#if EVIDENCE_PRIVATE_C3_SDK
                            if (result.ExitCode == 1)
                            {
                                if (_application is null) throw LinuxSystemdBackend.InvalidControl();
                                _application.RequireReady(jobOperation.Token);
                                // Private observation before the response permits SDK failure cleanup. This rechecks
                                // original live identities/readiness state; it does not perform another HTTP probe.
                                Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    schema = "evidence-sdk-producer-failure-v1", producer_exit_code = result.ExitCode,
                                    application_unit = _application.Unit.Value, original_application_ready_guard = true,
                                    native_acceptance = false
                                }));
                            }
#endif
                            coverageResponse = EvidenceCanonicalJson.Serialize(new
                            {
                                ok = true,
                                exit_code = result.ExitCode,
                                stdout = result.Stdout,
                                stderr = result.Stderr,
                                output_truncated = result.OutputTruncated,
                                received_bytes = result.ReceivedBytes
                            });
                            break;
                        case EvidenceArtifactsControlRequest inventory:
                            var reports = _coverage.Inventory(inventory.RelativeRoot, jobOperation.Token);
                            coverageResponse = EvidenceCanonicalJson.Serialize(new
                            {
                                ok = true,
                                artifacts = reports.Select(static item => new { path = item.RelativePath, length_bytes = item.Length }).ToArray()
                            });
                            break;
                        case EvidenceArtifactControlRequest read:
                            var bytes = _coverage.Read(read, jobOperation.Token);
                            coverageResponse = EvidenceCanonicalJson.Serialize(new
                            {
                                ok = true,
                                bytes_base64 = Convert.ToBase64String(bytes),
                                end = _coverage.IsEnd(read.Path, read.Offset + bytes.Length, jobOperation.Token)
                            });
                            break;
                    }
                    _owner.RequireActive(jobOperation.Token);
                }
                isCleanupRequest = request is EvidenceStopControlRequest or EvidenceWaitControlRequest or EvidenceExitControlRequest;
                if (isCleanupRequest)
                {
                    stage = LinuxControlFailureStage.CleanupRegistration;
                    var cleanupToken = CleanupToken();
                    cleanupCancellation = cleanupToken.UnsafeRegister(static state =>
                        ((CancellationTokenSource)state!).Cancel(), io);
                    cleanupToken.ThrowIfCancellationRequested();
                }
                // STOP closes work admission before waiting behind an earlier response. It never joins
                // this control handler or the worker that issued the request.
                if (request is EvidenceStopControlRequest)
                {
                    stage = LinuxControlFailureStage.Stop;
                    var stopTask = _sequence.StopAsync(CleanupToken());
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
                if (EvidenceNativeQualification.AcceptedBlockedWorkEnabled)
                {
                    _n16StopStarted.TrySetResult();
#if EVIDENCE_PRIVATE_N16
                    ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone.StopStarted);
#endif
                }
#endif
                    await stopTask.ConfigureAwait(false);
                }
                // WAIT joins only an already owned STOP, outside response ordering. It never starts
                // containment itself or holds the reply gate while that original procedure is pending.
                else if (request is EvidenceWaitControlRequest)
                {
                    stage = LinuxControlFailureStage.WaitJoin;
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
                if (EvidenceNativeQualification.AcceptedBlockedWorkEnabled && Volatile.Read(ref _n16WorkClaimed) == 1)
                {
                    lock (_n16Gate)
                        if (_n16Work?.IsCompleted == false && _ledger.ActiveWorkloads == 1 && _ledger.ActiveControls >= 3)
                            Volatile.Write(ref _n16ControlsOverlapped, 1);
#if EVIDENCE_PRIVATE_N16
                    if (Volatile.Read(ref _n16ControlsOverlapped) == 1)
                        ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone.ControlsOverlapped);
#endif
                    _n16WaitStarted.TrySetResult();
#if EVIDENCE_PRIVATE_N16
                    ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone.WaitStarted);
#endif
                    await _n16StopStarted.Task.WaitAsync(io.Token).ConfigureAwait(false);
                }
#endif
                    await _sequence.JoinStartedStopAsync().ConfigureAwait(false);
                }
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
            else if (request is EvidenceAcceptedBlockedWorkControlRequest)
            {
                if (!EvidenceNativeQualification.AcceptedBlockedWorkEnabled) throw Rejected();
                lock (_owner.WorkAdmissionGate)
                {
                    _owner.RequireActive(io.Token);
                    lock (_n16Gate)
                    {
                        stage = LinuxControlFailureStage.RequestClassify;
                        if (Interlocked.Exchange(ref _n16WorkClaimed, 1) != 0) throw Rejected();
#if EVIDENCE_PRIVATE_N16
                        ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone.WorkClaimed);
#endif
                        var registration = _ledger.BeginWorkload();
                        _n16Work = RunN16BlockedWorkAsync(registration);
                    }
                }
                await _n16BodyEntered.Task.WaitAsync(io.Token).ConfigureAwait(false);
                // Acceptance is an intermediate frame on this original connection. Do not
                // hold the reply-order gate while the actual workload/request remains blocked.
                await connection.WriteAcceptedBlockedWorkAsync(io.Token).ConfigureAwait(false);
                Volatile.Write(ref _n16AcceptedCommitted, 1);
#if EVIDENCE_PRIVATE_N16
                ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone.AcceptedWriteCommitted);
#endif
                if (EvidenceNativeQualification.OwnerDeathAcceptedWorkEnabled)
                {
                    var frame = LinuxOwnerDeathPhaseFrame.Create();
                    if (frame is null || frame.Length > 96 || !LinuxOwnerDeathPhaseFrame.IsValid(frame))
                        throw Rejected();
                    var stdout = Console.OpenStandardOutput();
                    await stdout.WriteAsync(frame.AsMemory(), io.Token).ConfigureAwait(false);
                    await stdout.FlushAsync(io.Token).ConfigureAwait(false);
                }
                Task originalWork;
                lock (_n16Gate) originalWork = _n16Work ?? throw Rejected();
                // The handler wait may cancel so final server draining can reach containment.
                // Its original body stays retained in _n16Work and the workload ledger; the
                // stop callback below always releases and joins that task before custody.
                await originalWork.WaitAsync(io.Token).ConfigureAwait(false);
            }
#endif
                stage = LinuxControlFailureStage.ReplyGate;
                await _replyOrder.WaitAsync(io.Token).ConfigureAwait(false);
                heldReplyOrder = true;
                byte[] response;
                switch (request)
                {
                    case EvidenceRunControlRequest or EvidenceArtifactsControlRequest or EvidenceArtifactControlRequest
                        or EvidenceApplicationStartControlRequest or EvidenceResourceWaitControlRequest:
                        response = coverageResponse ?? throw Rejected();
                        break;
                    case EvidenceReadyControlRequest:
                        stage = LinuxControlFailureStage.ReadyAuthorization;
                        _owner.RequireActive(io.Token);
                        // Connect/read above kept its Admission bound. The fixed root-helper audit
                        // now precedes admission and consumes this handler's original root/job token.
                        io.Token.ThrowIfCancellationRequested();
                        io.CancelAfter(Timeout.InfiniteTimeSpan);
                        await connection.WritePreparationAsync((long)_owner.Remaining.TotalMilliseconds, io.Token).ConfigureAwait(false);
#if EVIDENCE_PRIVATE_N04
                        if (_n04 is not null) await _n04.AdmissionPreparedAsync(io.Token).ConfigureAwait(false);
#endif
                        io.Token.ThrowIfCancellationRequested(); _owner.RequireActive(io.Token);
                        io.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.Remaining.Ticks,
                            TimeSpan.FromSeconds(_input.Request.AdmissionSeconds).Ticks)));
                        await connection.WriteAdmissionStartAsync(io.Token).ConfigureAwait(false);
                        stage = LinuxControlFailureStage.ReadyClaim;
                        claim = _sequence.ClaimReady();
                        stage = LinuxControlFailureStage.ReadyData;
                        response = _worker.CreateReadyData(io.Token).ReadyBytes;
#if EVIDENCE_PRIVATE_N04
                        if (_n04 is not null) await _n04.ReadyPreparedAsync(io.Token).ConfigureAwait(false);
#endif
#if EVIDENCE_PRIVATE_N07
                        if (_n07 is not null) await _n07.ReadyPreparedAsync(io.Token).ConfigureAwait(false);
#endif
                        break;
                    case EvidenceStopControlRequest:
                        stage = LinuxControlFailureStage.CleanupBound;
                        RequireCleanupBound();
                        stage = LinuxControlFailureStage.ResponseData;
                        response = EvidenceCanonicalJson.Serialize(new { ok = true });
                        break;
                    case EvidenceWaitControlRequest:
                        stage = LinuxControlFailureStage.CleanupBound;
                        RequireCleanupBound();
                        stage = LinuxControlFailureStage.WaitClaim;
                        claim = _sequence.ClaimWait();
                        stage = LinuxControlFailureStage.ResponseData;
                        response = EvidenceCanonicalJson.Serialize(new { ok = true, owned_exit = claim.Positive });
                        break;
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
                case EvidenceAcceptedBlockedWorkControlRequest:
                    response = EvidenceCanonicalJson.Serialize(new { ok = true, work_joined = true });
                    break;
#endif
                    case EvidenceExitControlRequest:
                        stage = LinuxControlFailureStage.CleanupBound;
                        RequireCleanupBound();
                        stage = LinuxControlFailureStage.OwnerCheck;
                        _owner.RequireControlIdentity(io.Token);
                        stage = LinuxControlFailureStage.ExitClaim;
                        claim = _sequence.ClaimExit();
                        lock (_executionGate) _exitIntent.TrySetResult(); // Intent is not ACK or success.
                        stage = LinuxControlFailureStage.ListenerClose;
                        await _listener.CloseAcceptAdmissionAsync().ConfigureAwait(false);
                        RequireCleanupBound();
                        stage = LinuxControlFailureStage.ResponseData;
                        response = EvidenceCanonicalJson.Serialize(new { ok = true });
                        break;
                    default:
                        throw Rejected();
                }
                stage = LinuxControlFailureStage.ResponseWrite;
                await connection.WriteResponseAsync(response, io.Token).ConfigureAwait(false);
                stage = LinuxControlFailureStage.ConnectionRelease;
                await _listener.ReleaseAsync(connection).ConfigureAwait(false);
                released = true;
                stage = LinuxControlFailureStage.PostWriteCheck;
                if (isCleanupRequest) RequireCleanupBound();
                stage = LinuxControlFailureStage.OwnerCheck;
                _owner.RequireControlIdentity(io.Token);
                stage = LinuxControlFailureStage.PostWriteCheck;
                if (isCleanupRequest) RequireCleanupBound();
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
            if (request is EvidenceAcceptedBlockedWorkControlRequest)
            {
                Volatile.Write(ref _n16ResponseCommitted, 1);
#if EVIDENCE_PRIVATE_N16
                ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone.ResponseCommitted);
#endif
            }
            else if (request is EvidenceStopControlRequest && Volatile.Read(ref _n16WorkClaimed) == 1)
            {
                Volatile.Write(ref _n16StopCommitted, 1);
#if EVIDENCE_PRIVATE_N16
                ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone.StopWriteCommitted);
#endif
            }
#endif
                if (claim is not null)
                {
                    stage = LinuxControlFailureStage.ReplyCommit;
                    _sequence.CompleteWrite(claim, true);
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
                if (claim.Operation == EvidenceControlOperation.Wait && claim.Positive && Volatile.Read(ref _n16WorkClaimed) == 1)
                {
                    Volatile.Write(ref _n16WaitCommitted, 1);
#if EVIDENCE_PRIVATE_N16
                    ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone.WaitWriteCommitted);
#endif
                }
#endif
                    var readyCommitted = claim.Operation == EvidenceControlOperation.Ready;
                    if (readyCommitted)
                    {
                        Interlocked.Exchange(ref _negativeReadyCommitted, 1);
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
                        _cancellationReady.TrySetResult(); // Only after original write/release/owner/CompleteWrite.
#endif
                    }
                    committedExit = claim.Operation == EvidenceControlOperation.Exit;
                    claim = null;
#if EVIDENCE_PRIVATE_N04
                    if (readyCommitted && _n04 is not null)
                        await _n04.ReadyCommittedAsync(io.Token).ConfigureAwait(false);
#endif
#if EVIDENCE_PRIVATE_N07
                    if (readyCommitted && _n07 is not null)
                        await _n07.ReadyCommittedAsync(io.Token).ConfigureAwait(false);
#endif
                }
            }
            catch (Exception error) when (Recoverable(error))
            {
                _failures.Capture(stage, operation, error,
                    stage == LinuxControlFailureStage.ListenerClose ? _listener.FirstFailure : null);
                if (claim is not null)
                {
                    try { _sequence.CompleteWrite(claim, false); }
                    catch (EvidenceAdmissionException commitError) { _failures.Capture(LinuxControlFailureStage.HandlerFailureCommit, operation, commitError); /* Failed write remains consumed and latched. */ }
                }
                _sequence.RecordFailure();
            }
            finally
            {
                if (!released)
                {
                    stage = LinuxControlFailureStage.ConnectionRelease;
                    try { await _listener.ReleaseAsync(connection).ConfigureAwait(false); }
                    catch (Exception error) when (Recoverable(error))
                    { _failures.Capture(stage, operation, error); Interlocked.Exchange(ref _connectionCloseFailed, 1); _sequence.RecordFailure(); }
                }
                stage = LinuxControlFailureStage.ReplyGateRelease;
                if (heldReplyOrder) _replyOrder.Release();
                stage = LinuxControlFailureStage.ControlRelease;
                control.Dispose();
                stage = LinuxControlFailureStage.CleanupRegistrationClose;
                cleanupCancellation.Dispose();
                stage = LinuxControlFailureStage.ExitCommit;
                if (committedExit)
                {
                    _exitCommitted.TrySetResult();
#if EVIDENCE_PRIVATE_N16
                if (Volatile.Read(ref _n16WorkClaimed) == 1)
                    ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone.ExitCommitted);
#endif
                }
            }
        }
        catch (Exception error) when (Recoverable(error))
        {
            _failures.Capture(stage, operation, error,
                stage == LinuxControlFailureStage.ListenerClose ? _listener.FirstFailure : null);
            throw;
        }
    }

    private async Task<SupervisionControlJoinFacts> StopEmptyDescendantsAsync(CancellationToken token)
    {
        if (_application is not null)
        {
            // Dispatch both containment operations before awaiting either original task.
            var application = _application.StopAndJoinAsync(token);
            var producer = _coverage?.StopAndJoinAsync(token) ?? Task.FromResult(new SupervisionControlJoinFacts(true, true, true));
            await Task.WhenAll(application, producer).ConfigureAwait(false);
            return await producer.ConfigureAwait(false);
        }
        if (_coverage is not null) return await _coverage.StopAndJoinAsync(token).ConfigureAwait(false);
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
        if (EvidenceNativeQualification.AcceptedBlockedWorkEnabled)
        {
            if (!_ledger.IsWorkloadAdmissionClosed) throw Rejected();
            return await StopAndJoinN16WorkAsync(token).ConfigureAwait(false);
        }
#endif
        _owner.RequireControlIdentity(token);
        if (!_ledger.IsWorkloadAdmissionClosed || _ledger.ActiveWorkloads != 0) throw Rejected();
        // This private server has no work-dispatch path or external workload registration API.
        // These are observations of the genuine empty descendant set, not worker exit assertions.
        return new SupervisionControlJoinFacts(true, true, true);
    }


    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxControlListener _listener;
    private readonly LinuxWorkerProcess _worker;
    private readonly EvidenceProtectedLaunchInput _input;
    private readonly LinuxRunAccounts _accounts;
    private readonly LinuxRunWorkspace _workspace;
    private LinuxCoverageControlOperations? _coverage;
    private LinuxApplicationProcess? _application;
#if EVIDENCE_PRIVATE_N04
    private LinuxN04CheckpointOwner? _n04;
#endif
#if EVIDENCE_PRIVATE_N07
    private LinuxN07CheckpointOwner? _n07;
    private int _n07OriginalTasksCompleted;
    private int _n07LocalCloseAttemptCompleted;
#endif
    private readonly SupervisionWorkRegistry _ledger = new();
    private readonly SupervisionControlSequence _sequence;
    private readonly SupervisionSingleAttempt _run = new();
    private readonly SemaphoreSlim _replyOrder = new(1, 1);
    private readonly TaskCompletionSource _exitCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _negativeReadyCommitted; // A past authenticated write event, not whole-protocol success.
    private readonly TaskCompletionSource _exitIntent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _executionGate = new();
    private Task? _execution;
    private int _ioJoined;
    private int _localOwnersClosed;
    private int _connectionCloseFailed;
    private readonly LinuxControlFailureLatch _failures = new();
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
    private readonly TaskCompletionSource _cancellationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
#endif
    private readonly TaskCompletionSource _cancellationFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _cancellationSignal;
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
    private int _signalClaimed;
#endif
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
    private readonly object _n16Gate = new();
    private readonly TaskCompletionSource _n16BodyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _n16Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _n16StopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _n16WaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _n16Work;
    private int _n16WorkClaimed;
    private int _n16AcceptedCommitted;
    private int _n16ResponseCommitted;
    private int _n16StopCommitted;
    private int _n16WaitCommitted;
    private int _n16ControlsOverlapped;
#if EVIDENCE_PRIVATE_N16
    private LinuxN16ProgressMark? _n16LastProgress;
#endif
#endif

#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
    /// <summary>Whether the original registered signal task joined successfully; not token delivery.</summary>
    internal bool CancellationSignalJoined => _cancellationSignal?.IsCompletedSuccessfully == true;

#endif
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
#if EVIDENCE_PRIVATE_N16
    /// <summary>Projects private N16 past write/task events only after original server and native holder joins.</summary>
    /// <remarks>
    /// Requires the original custody owners, all accepted-body/control joins, authenticated response
    /// writes and the final committed EXIT. This bounded record is failure-only data; it grants no completion, admission,
    /// account deletion, product acceptance or authority to a reader.
    /// </remarks>
    internal byte[] CaptureAcceptedBlockedWork(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxWorkerProcess worker, CancellationToken token)
    {
        RequireCustodyOwner(input, owner, accounts, workspace, worker, token);
        if (!_sequence.ExitAcknowledged || !_exitCommitted.Task.IsCompletedSuccessfully) throw Rejected();
        lock (_n16Gate)
            if (_n16Work?.IsCompletedSuccessfully != true || Volatile.Read(ref _n16WorkClaimed) != 1
                || Volatile.Read(ref _n16AcceptedCommitted) != 1 || Volatile.Read(ref _n16ResponseCommitted) != 1
                || Volatile.Read(ref _n16StopCommitted) != 1 || Volatile.Read(ref _n16WaitCommitted) != 1
                || Volatile.Read(ref _n16ControlsOverlapped) != 1) throw Rejected();
        var bytes = EvidenceCanonicalJson.Serialize(new
        {
            schema = "issue779-accepted-blocked-work-v1", generation = owner.RunId.ToString("N"),
            @case = "N16", accepted_write_committed = true, request_blocked_during_control_overlap = true,
            original_body_joined = true, original_request_response_committed = true,
            stop_write_committed = true, positive_wait_write_committed = true,
            exit_write_committed = true,
            handlers_joined = true, active_workloads = 0, active_controls = 0,
            native_authority = false, native_acceptance = false
        });
        token.ThrowIfCancellationRequested();
        if (bytes.Length is 0 or > 4096) throw Rejected();
        RequireCustodyOwner(input, owner, accounts, workspace, worker, token);
        return bytes;
    }
#endif
#endif

#if EVIDENCE_PRIVATE_N16
    /// <summary>Copies only existing N16 server flags and the age of its last observed original milestone.</summary>
    /// <remarks>This method performs no owner, admission, custody, wait, token, or kernel operation.</remarks>
    internal LinuxN16ProgressDiagnostic.Snapshot CaptureN16ProgressDiagnostic()
    {
        var mark = Volatile.Read(ref _n16LastProgress);
        long? elapsed = mark is null ? null : LinuxN16ProgressDiagnostic.ElapsedMilliseconds(
            mark.Timestamp, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        return new(
            mark?.Milestone ?? LinuxN16ProgressDiagnostic.Milestone.Unknown,
            elapsed,
            Volatile.Read(ref _n16WorkClaimed) == 1,
            _n16BodyEntered.Task.IsCompletedSuccessfully,
            Volatile.Read(ref _n16AcceptedCommitted) == 1,
            Volatile.Read(ref _n16ResponseCommitted) == 1,
            _n16StopStarted.Task.IsCompletedSuccessfully,
            _n16WaitStarted.Task.IsCompletedSuccessfully,
            Volatile.Read(ref _n16ControlsOverlapped) == 1,
            Volatile.Read(ref _n16StopCommitted) == 1,
            Volatile.Read(ref _n16WaitCommitted) == 1,
            _exitCommitted.Task.IsCompletedSuccessfully,
            null);
    }

    private void ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone milestone) =>
        Volatile.Write(ref _n16LastProgress, new(milestone, Stopwatch.GetTimestamp()));

    private sealed record LinuxN16ProgressMark(LinuxN16ProgressDiagnostic.Milestone Milestone, long Timestamp);
#endif

#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
    /// <summary>Projects the original signal task only after the existing server/holder joins.</summary>
    /// <remarks>Successful syscall and READY observations still cannot establish caller-token delivery.</remarks>
    internal string CaptureCancellationSignal(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxWorkerProcess worker, CancellationToken token)
    {
        RequireCustodyOwner(input, owner, accounts, workspace, worker, token);
        if (!CancellationSignalJoined || Volatile.Read(ref _signalClaimed) != 1
            || !_worker.CancellationPhaseObserved.IsCompletedSuccessfully || Volatile.Read(ref _negativeReadyCommitted) != 1)
            throw Rejected();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = "issue779-cancellation-signal-v1",
            generation = _owner.RunId.ToString("N"),
            @case = EvidenceOriginalCancellationCheckpoint.Case,
            signal = 2,
            syscall_exit = 0,
            original_signal_task_joined = true,
            ready_committed = true,
            phase_observed = true,
            native_authority = false
        });
    }

#endif
#if EVIDENCE_PRIVATE_N09
    /// <summary>Rechecks and closes the actual N09 allocation holder after original worker joins.</summary>
    /// <remarks>The bounded metadata record is consistency data only and cannot establish native acceptance.</remarks>
    internal byte[]? CaptureAndCloseN09AllocationSlot(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxWorkerProcess worker, CancellationToken token)
    {
        if (!workspace.HasN09AllocationSlot) return null;
        try
        {
            RequireCustodyOwner(input, owner, accounts, workspace, worker, token);
            worker.RequireCustodyOwner(input, owner, accounts, workspace, this, token);
            if (!CancellationSignalJoined || Volatile.Read(ref _signalClaimed) != 1
                || !_worker.CancellationPhaseObserved.IsCompletedSuccessfully
                || Volatile.Read(ref _negativeReadyCommitted) != 1) throw Rejected();
            return workspace.CloseAndCaptureN09AllocationSlot(token);
        }
        catch (Exception error) when (Recoverable(error))
        {
            try { workspace.CloseN09AllocationSlotWithoutCapture(); }
            catch (Exception closeError) when (Recoverable(closeError)) { /* Preserve the original failure; workspace remains quarantined. */ }
            throw;
        }
    }
#endif

#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
    private async Task SignalCancellationOwnedAsync(Task dispatch, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        try
        {
            await _cancellationReady.Task.WaitAsync(token).ConfigureAwait(false);
            await _worker.CancellationPhaseObserved.WaitAsync(token).ConfigureAwait(false);
            _worker.RequireServerOwner(_input, _owner, _accounts, _workspace, _listener, token);
            var identity = _worker.RequireWorker(token);
#if EVIDENCE_PRIVATE_N09
            // Retain the real allocated output slot after the authenticated phase/READY and
            // immediately before the original, identity-bound SIGINT.
            _workspace.RetainN09AllocationSlot(token);
#endif
            if (Interlocked.Exchange(ref _signalClaimed, 1) != 0) throw Rejected();
            LinuxOriginalCancellationSignal.Send(_worker, identity, _owner, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (Recoverable(error))
        {
            _cancellationFailed.TrySetException(error);
            _ = _cancellationFailed.Task.Exception;
            throw;
        }
    }

#endif
    /// <summary>Gets first caught server-fault data; this establishes no protocol or native outcome.</summary>
    internal LinuxControlFailure? FirstFailure => _failures.First;

    private LinuxEmptyObservationControlServer(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxControlListener listener, LinuxWorkerProcess worker,
        LinuxCoverageControlOperations? coverage)
    {
        _input = input; _owner = owner; _listener = listener; _worker = worker;
        _accounts = accounts; _workspace = workspace;
        _coverage = coverage;
        _sequence = new(_ledger, StopEmptyDescendantsAsync);
    }

    /// <summary>Binds the actual owners and resolves the protected empty or coverage targeted policy before dispatch.</summary>
    /// <remarks>This cannot activate the reserved supervisor entry or qualify an application/consumer.</remarks>
    internal static LinuxEmptyObservationControlServer Create(EvidenceProtectedLaunchInput input,
        LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxRunWorkspace workspace,
        LinuxControlListener listener, LinuxWorkerProcess worker, CancellationToken token,
        LinuxProducerWorkspace? producerWorkspace = null, SupervisionRunOutputBudget? output = null,
        LinuxApplicationWorkspace? applicationWorkspace = null, EvidenceApplicationSelection? applicationSelection = null)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts); ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(listener); ArgumentNullException.ThrowIfNull(worker);
        worker.ClaimControlServer(input, owner, accounts, workspace, listener, token);
        var plan = input.Request.ObservationProducerIds.Count == 0
            ? EvidenceEmptyObservationPlan.FromInput(input, token) : input.Request.ApplicationId is null
                ? EvidenceCoverageObservationPlan.FromInput(input, token) : EvidenceApplicationSelection.ResolvePlan(input, token);
        // Ownership references may be retained before startup. RunCoreAsync still requires the
        // actual joined startup and rechecks live kernel identity before any accepted request.
        worker.RequireServerOwner(input, owner, accounts, workspace, listener, token);
        var server = new LinuxEmptyObservationControlServer(input, owner, accounts, workspace, listener, worker, null);
        if (plan.Profile.Producers.Count != 0)
        {
            if (producerWorkspace is null || output is null) throw Rejected();
            server._coverage = LinuxCoverageControlOperations.Create(input, owner, accounts, workspace,
                producerWorkspace, server._ledger, output, token);
        }
        if (applicationWorkspace is not null || applicationSelection is not null)
        {
            if (applicationWorkspace is null || applicationSelection is null || producerWorkspace is null || output is null) throw Rejected();
            server._application = LinuxApplicationProcess.Create(input, owner, accounts, workspace, producerWorkspace,
                applicationWorkspace, applicationSelection, server._ledger, output, token);
        }
        return server;
    }

    /// <summary>Registers and joins every original accept, handler, response write and accepted connection close.</summary>
    /// <param name="token">Independent root/control lifetime; a worker's per-operation cancellation never owns this token.</param>
    /// <remarks>
    /// One pending accept and at most 32 handlers are retained. Per-request I/O is capped by the protected
    /// admission bound and original job remainder. Cleanup has one cumulative stop/cleanup bound; later
    /// requests cannot reset it. The reply semaphore orders ACK commits, including a client which sends
    /// its next request immediately after receiving LF while the prior server continuation still runs.
    /// No handler calls this drain or the worker's final stop/join. Caller cancellation closes actual I/O;
    /// every original operation remains joined before return, including ignored cancellation.
    /// A valid EXIT claim signals intent, forbids further accept dispatch and joins listening admission before
    /// ACK bytes. Intent grants no successful commit. Current handlers and connections remain retained through
    /// writes, release and final owner checks, then full listener disposal closes the native named handles.
    /// </remarks>
    internal Task RunAsync(CancellationToken token)
    {
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task execution;
        lock (_executionGate)
        {
            _run.Claim();
            execution = RunOwnedAsync(dispatch.Task, token);
            _execution = execution;
        }
        dispatch.SetResult();
        return execution;
    }

    /// <summary>Requires actual completed server I/O and original native references before filesystem custody.</summary>
    /// <remarks>Protocol failure may remain; this guard grants neither successful execution nor account deletion.</remarks>
    internal void RequireCustodyOwner(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxWorkerProcess worker, CancellationToken token)
    {
        if (!ReferenceEquals(input, _input) || !ReferenceEquals(owner, _owner)
            || !ReferenceEquals(accounts, _accounts) || !ReferenceEquals(workspace, _workspace)
            || !ReferenceEquals(worker, _worker)) throw Rejected();
        owner.RequireControlIdentity(token);
        lock (_executionGate)
            if (_execution?.IsCompleted != true || Volatile.Read(ref _ioJoined) == 0
                || Volatile.Read(ref _localOwnersClosed) == 0 || Volatile.Read(ref _connectionCloseFailed) != 0
                || !_ledger.IsControlAdmissionClosed || _ledger.ActiveControls != 0
                || !_ledger.IsWorkloadAdmissionClosed || _ledger.ActiveWorkloads != 0) throw Rejected();
        owner.RequireControlIdentity(token);
    }

    /// <summary>Gets descriptor data only after an actual peer-bound READY write committed and handlers joined.</summary>
    /// <param name="input">Reference-equal original protected input.</param>
    /// <param name="owner">Original actual owner.</param>
    /// <param name="accounts">Original actual accounts.</param>
    /// <param name="workspace">Original retained workspace and immutable sealed descriptor.</param>
    /// <param name="worker">Original selected worker, never caller PID data.</param>
    /// <param name="token">Original cleanup token, not a new deadline.</param>
    /// <returns>Descriptor SHA256 data; it authenticates neither an external record nor a subsequent action.</returns>
    /// <remarks>
    /// Unlike successful-sequence ReadyAcknowledged, the past event remains observable after a later
    /// negative outcome. It is set only after existing authenticated response I/O, connection release,
    /// owner checks and CompleteWrite succeed. Missing events reject; no failure flag is cleared.
    /// </remarks>
    internal string RequireNegativeReadyDescriptor(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxWorkerProcess worker, CancellationToken token)
    {
        RequireCustodyOwner(input, owner, accounts, workspace, worker, token);
        if (Volatile.Read(ref _negativeReadyCommitted) != 1 || _workspace.DescriptorSha256 is not { } descriptor)
            throw Rejected();
        return descriptor;
    }

    /// <summary>Obtains the actual drained listener's original named-inode comparison data for native custody.</summary>
    /// <remarks>Does not seal an inode, clear any failure or release accounts.</remarks>
    internal LinuxProtectedMetadata RequireCustodySocket(CancellationToken token)
    {
        RequireCustodyOwner(_input, _owner, _accounts, _workspace, _worker, token);
        return _listener.RequireCustodySocket(_owner, _accounts, _workspace, token);
    }

#if EVIDENCE_PRIVATE_N07
    internal string RequireN07FailedReadyDescriptor(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxWorkerProcess worker, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(input, _input) || !ReferenceEquals(owner, _owner)
            || !ReferenceEquals(accounts, _accounts) || !ReferenceEquals(workspace, _workspace)
            || !ReferenceEquals(worker, _worker)) throw Rejected();
        owner.RequireCleanupLaunchInput(input, token);
        lock (_executionGate)
            if (_execution?.IsCompleted != true || Volatile.Read(ref _n07OriginalTasksCompleted) != 1
                || Volatile.Read(ref _n07LocalCloseAttemptCompleted) != 1 || _n07?.JoinedAttempt != true
                || Volatile.Read(ref _negativeReadyCommitted) != 1 || _ledger.ActiveControls != 0) throw Rejected();
        var descriptor = _workspace.DescriptorSha256 ?? throw Rejected();
        owner.RequireCleanupLaunchInput(input, token);
        return descriptor;
    }

    internal TaskStatus N07ExecutionStatus
    {
        get { lock (_executionGate) return _execution is { IsCompleted: true } task ? task.Status : throw Rejected(); }
    }
#endif

    /// <summary>Requires the original successful server task and all committed protocol steps before final-file claims.</summary>
    /// <remarks>Physical custody after a failed protocol remains possible, but cannot pass this success guard.</remarks>
    internal void RequireSuccessfulCompletion(CancellationToken token)
    {
        RequireCustodyOwner(_input, _owner, _accounts, _workspace, _worker, token);
        lock (_executionGate)
            if (_execution?.IsCompletedSuccessfully != true || !_sequence.ExitAcknowledged
                || !_ledger.IsSettled) throw Rejected();
        _owner.RequireControlIdentity(token);
    }

#if EVIDENCE_PRIVATE_ACCEPTED_WORK
    private async Task<SupervisionControlJoinFacts> StopAndJoinN16WorkAsync(CancellationToken token)
    {
        Task? n16Work;
        lock (_n16Gate) n16Work = _n16Work;
        if (n16Work is null)
        {
            if (_ledger.ActiveWorkloads != 0) throw Rejected();
            _owner.RequireControlIdentity(token);
            return new SupervisionControlJoinFacts(true, true, true);
        }
        if (_ledger.ActiveWorkloads != 1) throw Rejected();
        // Release the fixed accepted body, then join its original task even if the caller's
        // cleanup token is canceled. The body has no external work and completes on this signal.
        try { await _n16WaitStarted.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            // Even expiry or a missing WAIT must release and join the original body. Neither
            // canceled handler waits nor failed replies can erase its workload registration.
            _n16Release.TrySetResult();
            await n16Work.ConfigureAwait(false);
        }
        _owner.RequireControlIdentity(token);
        if (_ledger.ActiveWorkloads != 0) throw Rejected();
        return new SupervisionControlJoinFacts(true, true, true);
    }

    private async Task RunN16BlockedWorkAsync(SupervisionWorkRegistry.Workload registration)
    {
        _n16BodyEntered.TrySetResult();
#if EVIDENCE_PRIVATE_N16
        ObserveN16Progress(LinuxN16ProgressDiagnostic.Milestone.BodyEntered);
#endif
        try { await _n16Release.Task.ConfigureAwait(false); }
        finally { registration.Complete(); }
    }
#endif

    private CancellationToken CleanupToken()
    {
        _owner.BeginRootTeardown();
        return _owner.RootTeardownToken;
    }

    private void RequireCleanupBound()
    {
        var token = CleanupToken();
        token.ThrowIfCancellationRequested();
        _owner.RequireControlIdentity(default);
        token.ThrowIfCancellationRequested();
    }

    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The protected empty Observation control operation was rejected.");
}
