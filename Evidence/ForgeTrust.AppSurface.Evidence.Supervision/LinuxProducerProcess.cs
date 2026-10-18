using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Retains and settles one authentic nonroot subject test unit and its paired output pumps.</summary>
/// <remarks>
/// Construction binds the exact input, owner, account holder, producer workspace, shared output ledger and
/// workload registry by reference before any process I/O. RUN claims the single workload before creating
/// pipes or connecting to systemd. The original AddRef connection, pending start, actual process identity,
/// monitor, cgroup observations and both pumps remain held through join. A normal nonzero test exit is
/// returned as test-result data; only abnormal exit, process/custody failure, pump loss, quota or failed
/// settlement prevents report readiness. See <see href="producer-execution.md"/>.
/// </remarks>
internal sealed class LinuxProducerProcess
{
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(25);
    private const int RetainedPrefixBytesPerStream = 1024 * 1024;

    private readonly object _gate = new();
    private readonly EvidenceProtectedLaunchInput _input;
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxRunAccounts _accounts;
    private readonly LinuxRunWorkspace _workerWorkspace;
    private readonly LinuxProducerWorkspace _workspace;
    private readonly SupervisionWorkRegistry _ledger;
    private readonly SupervisionRunOutputBudget _outputBudget;
    private readonly SupervisionPendingStart _pending;
    private LinuxOutputPipes? _pipes;
    private Task<SupervisionOutputReceipt>? _collect;
    private LinuxSystemdBackend? _backend;
    private LinuxSystemdBackend? _observations;
    private LinuxProcessIdentity? _process;
    private LinuxProducerUnit? _recipe;
    private LinuxUnitProperties? _normalTerminal;
    private LinuxUnitProperties? _stoppedTerminal;
    private LinuxUnitProperties? _lastStopObservation;
    private SupervisionOutputReceipt? _output;
    private Task<EvidenceRestrictedProcessResult>? _run;
    private Task? _registrationJoin;
    private SupervisionWorkRegistry.Workload? _workload;
    private string? _resultsToken;
    private int _dispatchAttempted;
    private int _runClaimed;
    private int _closed;
    private int _failed;
    private int _cleanupFailed;
    private int _groupEmptyTwice;
    private int _pumpJoined;
    private int _physicallySettled;

    private LinuxProducerProcess(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workerWorkspace, LinuxProducerWorkspace workspace,
        SupervisionWorkRegistry ledger, SupervisionRunOutputBudget outputBudget)
    {
        _input = input;
        _owner = owner;
        _accounts = accounts;
        _workerWorkspace = workerWorkspace;
        _workspace = workspace;
        _ledger = ledger;
        _outputBudget = outputBudget;
        _pending = new(LinuxUnitName.Create(LinuxUnitRole.Producer, owner.RunId));
    }

    /// <summary>Gets the generated selected producer unit name as data only.</summary>
    internal LinuxUnitName Unit => _pending.Unit;

    /// <summary>Gets physical group, monitor, pending-start and pump settlement, including a test failure exit.</summary>
    internal bool PhysicallySettled => Volatile.Read(ref _physicallySettled) != 0;

    /// <summary>Gets the private root-retained output receipt only after physical settlement.</summary>
    internal SupervisionOutputReceipt? Output => PhysicallySettled
        ? _output : throw LinuxSystemdBackend.InvalidControl();

    /// <summary>Constructs the holder from the exact genuine native objects and retains it before start or pipe I/O.</summary>
    internal static LinuxProducerProcess Create(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workerWorkspace, LinuxProducerWorkspace workspace,
        SupervisionWorkRegistry ledger, SupervisionRunOutputBudget outputBudget, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(workerWorkspace);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(outputBudget);
        input.Recheck(token);
        owner.RequireLaunchInput(input, token);
        accounts.RequireOwnedBy(owner, token);
        workerWorkspace.RequireOwnedBy(owner, accounts, token);
        workspace.RequireOwnedBy(owner, accounts, token);
        var producer = new LinuxProducerProcess(input, owner, accounts, workerWorkspace, workspace, ledger, outputBudget);
        workspace.RetainProducer(owner, accounts, producer);
        return producer;
    }

    /// <summary>Consumes and registers the one producer workload before any pipe, backend or start I/O.</summary>
    /// <param name="resultsToken">The same closed token selected by the worker's validated fixed argv.</param>
    /// <param name="token">The original run request/deadline token; no replacement deadline is created.</param>
    /// <returns>Normal process exit status, empty bounded wire strings, exact received bytes and actual pair truncation.</returns>
    internal Task<EvidenceRestrictedProcessResult> RunAsync(string resultsToken, CancellationToken token)
    {
        LinuxCoverageControlOperations.RequireToken(resultsToken);
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EvidenceRestrictedProcessResult> run;
        lock (_gate)
        {
            if (Volatile.Read(ref _closed) != 0 || Interlocked.Exchange(ref _runClaimed, 1) != 0)
                throw LinuxSystemdBackend.InvalidControl();
            try { _workload = _ledger.BeginWorkload(); }
            catch { Interlocked.Exchange(ref _failed, 1); throw LinuxSystemdBackend.InvalidControl(); }
            _resultsToken = resultsToken;
            _run = run = RunOwnedAsync(resultsToken, token, dispatch.Task);
            _registrationJoin = CompleteWorkloadAfterRunAsync(run, _workload);
        }
        dispatch.TrySetResult();
        return run;
    }

    /// <summary>Closes producer admission, stops before/after an ambiguous start and joins the original run task.</summary>
    /// <remarks>This never starts root teardown and never joins the caller's control handler through a callback.</remarks>
    internal async Task StopAndJoinAsync(CancellationToken cleanupToken)
    {
        Interlocked.Exchange(ref _closed, 1);
        try { await _pending.StopAsync(StopSelectedUnitAsync, cleanupToken).ConfigureAwait(false); }
        catch { Interlocked.Exchange(ref _cleanupFailed, 1); }

        Task<EvidenceRestrictedProcessResult>? run;
        Task? registration;
        lock (_gate) { run = _run; registration = _registrationJoin; }
        if (run is not null)
        {
            try { await run.ConfigureAwait(false); }
            catch { Interlocked.Exchange(ref _failed, 1); }
        }
        if (registration is not null)
        {
            try { await registration.ConfigureAwait(false); }
            catch { Interlocked.Exchange(ref _failed, 1); }
        }
        if (run is null)
        {
            var pending = _pending.Snapshot;
            if (!pending.StopJoined || pending.FirstFailure is SupervisionPendingStartFailure.StopFailed or SupervisionPendingStartFailure.StopCancelled) Interlocked.Exchange(ref _cleanupFailed, 1);
            else Interlocked.Exchange(ref _physicallySettled, 1); // No start, process, pipe or monitor was acquired.
        }
    }

    /// <summary>Requires successful normal process/pump settlement before report data may be opened.</summary>
    /// <remarks>Exit status may be nonzero for a normal failed-test result; transport/process failures never pass.</remarks>
    internal void RequireReportsReady(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        LinuxProducerWorkspace workspace, CancellationToken token)
    {
        RequireReferences(owner, accounts, workspace);
        owner.RequireLaunchInput(_input, token);
        accounts.RequireOwnedBy(owner, token);
        workspace.RequireOwnedBy(owner, accounts, token);
        if (_run is not { IsCompletedSuccessfully: true } || !PhysicallySettled
            || _normalTerminal is null || _stoppedTerminal is null || _output is not { Successful: true }
            || _output.DiscardedBytes != 0 || _outputBudget.QuotaExceeded
            || Volatile.Read(ref _failed) != 0 || Volatile.Read(ref _cleanupFailed) != 0
            || Volatile.Read(ref _groupEmptyTwice) == 0 || Volatile.Read(ref _pumpJoined) == 0)
            throw LinuxSystemdBackend.InvalidControl();
    }

    /// <summary>Requires retained original references and successful native cleanup joins, but not zero test exit.</summary>
    /// <remarks>This cleanup guard is valid after owner work admission stops and never calls RequireActive.</remarks>
    internal void RequirePhysicalSettlement(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        LinuxProducerWorkspace workspace, CancellationToken token)
    {
        RequireReferences(owner, accounts, workspace);
        owner.RequireControlIdentity(token);
        accounts.RequireControlOwnedBy(owner, token);
        workspace.RequireControlOwnedBy(owner, accounts, token);
        var pending = _pending.Snapshot;
        var startJoined = !pending.StartReserved || pending.StartJoined;
        var noProcessAcquired = Volatile.Read(ref _dispatchAttempted) == 0;
        if (!PhysicallySettled || !pending.Closed || !pending.StopJoined || pending.FirstFailure is SupervisionPendingStartFailure.StopFailed or SupervisionPendingStartFailure.StopCancelled
            || !startJoined || Volatile.Read(ref _cleanupFailed) != 0
            || !noProcessAcquired && (_stoppedTerminal is null || Volatile.Read(ref _groupEmptyTwice) == 0
                || Volatile.Read(ref _pumpJoined) == 0 || _output is not { Drained: true }
                || _collect is { IsCompleted: false }))
            throw LinuxSystemdBackend.InvalidControl();
    }

    private async Task<EvidenceRestrictedProcessResult> RunOwnedAsync(string resultsToken,
        CancellationToken token, Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        int? normalExitCode = null;
        var diagnosticStage = "binding";
        string? failedStage = null;
        CancellationTokenSource? outputDeadline = null;
        try
        {
            RequireLiveBindings(token);
            diagnosticStage = "results";
            var preparedPath = _workspace.PrepareResultsToken(resultsToken, token);
            var selected = _workspace.RequireResultsToken(resultsToken, token);
            var expectedPath = _workspace.ResultsRoot.TrimEnd('/') + "/" + resultsToken;
            if (!StringComparer.Ordinal.Equals(preparedPath, selected.Path)
                || !StringComparer.Ordinal.Equals(selected.Path, expectedPath)) throw LinuxSystemdBackend.InvalidControl();

            var stop = TimeSpan.FromSeconds(_input.Request.StoppingSeconds);
            var cleanupReserve = _owner.CleanupAllowance;
            var collection = TimeSpan.FromSeconds(_input.Request.CollectionSeconds);
            var availableRuntime = _owner.Remaining - stop - cleanupReserve - collection;
            var runtime = availableRuntime > TimeSpan.FromSeconds(LinuxProducerUnit.MaximumRuntimeSeconds)
                ? TimeSpan.FromSeconds(LinuxProducerUnit.MaximumRuntimeSeconds) : availableRuntime;
            if (runtime <= TimeSpan.Zero) throw LinuxSystemdBackend.InvalidControl();
            var unitAllowance = runtime + stop;

            diagnosticStage = "pipes";
            outputDeadline = LinuxOutputPipes.CreateDrainDeadline(_owner.TeardownCancellation, _input.Remaining);
            _pipes = LinuxOutputPipes.Create();
            _collect = _pipes.BeginCollectAsync(outputDeadline.Token,
                receivedByteLimit: EvidenceRunBudgetLimits.MaximumProcessOutputBytes,
                prefixByteLimit: RetainedPrefixBytesPerStream, runBudget: _outputBudget);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.Remaining.Ticks,
                TimeSpan.FromSeconds(_input.Request.StartSeconds).Ticks)));
            diagnosticStage = "backend";
            _backend = await LinuxSystemdBackend.ConnectAsync(startup.Token).ConfigureAwait(false);
            _recipe = LinuxProducerUnit.Create(Unit, _owner.Unit, _input.Request.RuntimeHost,
                _input.Request.RuntimeRoot, _input.Request.ToolRoot, _input.Request.SubjectRoot,
                _workerWorkspace.RawResultsRoot, _workerWorkspace.ControlSocket, _workerWorkspace.OutputParent,
                _workspace.SubjectRoot, _workspace.SolutionPath, _workspace.ResultsRoot, selected.Path,
                _workspace.ScratchRoot, _workspace.PackageRoot, _accounts.SubjectUid, _accounts.SubjectGid,
                unitAllowance, stop, _pipes.StandardOutput, _pipes.StandardError);

            try
            {
                diagnosticStage = "start";
                await _pending.StartAsync(startToken =>
                {
                    RequireLiveBindings(startToken);
                    Interlocked.Exchange(ref _dispatchAttempted, 1);
                    return _backend.StartProducerAsync(_recipe, startToken);
                }, startup.Token).ConfigureAwait(false);
            }
            finally { _pipes.CloseWriteCopies(); }

            diagnosticStage = "running";
            _observations = await LinuxSystemdBackend.ConnectAsync(startup.Token).ConfigureAwait(false);
            var running = await ObserveRunningAsync(startup.Token).ConfigureAwait(false);
            diagnosticStage = "identity";
            _process = LinuxProcessIdentity.CaptureProducer(running.MainPid, _accounts.SubjectUid,
                _accounts.SubjectGid, Unit, startup.Token);
            _process.Recheck(startup.Token);
            diagnosticStage = "normal-exit";
            var terminal = await ObserveNormalExitAsync(token).ConfigureAwait(false);
            normalExitCode = terminal.ExecMainStatus;
            _normalTerminal = terminal;
        }
        catch
        {
            failedStage = diagnosticStage;
            Interlocked.Exchange(ref _failed, 1);
        }

        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(_owner.TeardownCancellation);
        cleanup.CancelAfter(TimeSpan.FromTicks(Math.Min(_owner.CleanupRemaining.Ticks,
            TimeSpan.FromSeconds(_input.Request.CleanupSeconds).Ticks)));
        var cleanupToken = cleanup.Token;
        try
        {
            await _pending.StopAsync(StopSelectedUnitAsync, cleanupToken).ConfigureAwait(false);
        }
        catch { Interlocked.Exchange(ref _cleanupFailed, 1); }

        if (Volatile.Read(ref _dispatchAttempted) != 0)
        {
            try { await ObserveStoppedAndEmptyTwiceAsync(cleanupToken).ConfigureAwait(false); }
            catch { Interlocked.Exchange(ref _cleanupFailed, 1); }
        }

        if (_pipes is not null)
        {
            try { _pipes.CloseWriteCopies(); }
            catch { Interlocked.Exchange(ref _cleanupFailed, 1); }
            if (_collect is not null)
            {
                try
                {
                    _output = await _pipes.JoinAsync().ConfigureAwait(false);
                    Interlocked.Exchange(ref _pumpJoined, 1);
                    if (!_output.Successful || _outputBudget.QuotaExceeded) Interlocked.Exchange(ref _failed, 1);
                }
                catch { Interlocked.Exchange(ref _cleanupFailed, 1); }
            }
            try { await _pipes.DisposeAsync().ConfigureAwait(false); }
            catch { Interlocked.Exchange(ref _cleanupFailed, 1); }
        }

        try { _process?.Dispose(); }
        catch { Interlocked.Exchange(ref _cleanupFailed, 1); }
        try { _observations?.Dispose(); }
        catch { Interlocked.Exchange(ref _cleanupFailed, 1); }
        try { _backend?.Dispose(); }
        catch { Interlocked.Exchange(ref _cleanupFailed, 1); }
        try { outputDeadline?.Dispose(); }
        catch { Interlocked.Exchange(ref _cleanupFailed, 1); }

        var pending = _pending.Snapshot;
        if (pending.FirstFailure is SupervisionPendingStartFailure.StopFailed or SupervisionPendingStartFailure.StopCancelled || !pending.StopJoined || (pending.StartReserved && !pending.StartJoined))
            Interlocked.Exchange(ref _cleanupFailed, 1);
        if (Volatile.Read(ref _dispatchAttempted) == 0
            || Volatile.Read(ref _cleanupFailed) == 0 && Volatile.Read(ref _groupEmptyTwice) != 0
                && Volatile.Read(ref _pumpJoined) != 0 && _output is { Drained: true } && _stoppedTerminal is not null)
            Interlocked.Exchange(ref _physicallySettled, 1);

        var failed = Volatile.Read(ref _failed) != 0 || Volatile.Read(ref _cleanupFailed) != 0
            || _output is not { Successful: true } || _outputBudget.QuotaExceeded || normalExitCode is null;
        if ((failed || normalExitCode != 0) && _collect is { IsCompletedSuccessfully: true } && _output is not null)
        {
            try { LinuxProducerFailureOutput.Retain(_input, _owner, _output,
                failedStage ?? "settlement", _normalTerminal, _stoppedTerminal, _pending.Snapshot, cleanupToken,
                physicallySettled: PhysicallySettled, cleanupFailed: Volatile.Read(ref _cleanupFailed) != 0,
                groupEmptyTwice: Volatile.Read(ref _groupEmptyTwice) != 0,
                pumpJoined: Volatile.Read(ref _pumpJoined) != 0,
                capturedPid: _process?.Pid, lastStop: _lastStopObservation); }
            catch (Exception diagnosticError) when (diagnosticError is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException) { }
        }
        if (failed)
        {
            _ledger.RecordFailure(SupervisionWorkFailure.WorkloadFailed);
            throw LinuxSystemdBackend.InvalidControl();
        }

        var completedOutput = _output ?? throw LinuxSystemdBackend.InvalidControl();
        return new EvidenceRestrictedProcessResult(normalExitCode ?? throw LinuxSystemdBackend.InvalidControl(), string.Empty, string.Empty,
            completedOutput.DiscardedBytes != 0, completedOutput.ReceivedBytes);
    }

    private async Task<LinuxUnitProperties> ObserveRunningAsync(CancellationToken token)
    {
        while (true)
        {
            RequirePollingBindings(token);
            var facts = await _observations!.ReadUnitAsync(Unit, token).ConfigureAwait(false);
            if (_recipe!.HasRunningMain(facts)) return facts;
            if (_recipe.HasFinished(facts) || _recipe.HasStopped(facts)) throw LinuxSystemdBackend.InvalidControl();
            RequireOutputHealthy();
            await Task.Delay(ObservationInterval, token).ConfigureAwait(false);
        }
    }

    private async Task<LinuxUnitProperties> ObserveNormalExitAsync(CancellationToken token)
    {
        var process = _process ?? throw LinuxSystemdBackend.InvalidControl();
        while (true)
        {
            _owner.RequireControlIdentity(token);
            _input.Deadline.Check(token);
            RequireOutputHealthy();
            var facts = await _observations!.ReadUnitAsync(Unit, token).ConfigureAwait(false);
            if (_recipe!.HasFinished(facts))
            {
                if (facts.ExecMainPid != process.Pid || facts.ExecMainCode != 1)
                    throw LinuxSystemdBackend.InvalidControl();
                // Final acceptance rehashes the complete retained deployment once, rather than
                // hashing an SDK-sized tree on every poll. The actual owner/deadline stayed live.
                _owner.RequireCleanupLaunchInput(_input, token);
                return facts;
            }
            if ((!_recipe.HasRunningMain(facts) || facts.MainPid != process.Pid)
                && !_recipe.IsNormalExitTransition(facts, process.Pid))
                throw LinuxSystemdBackend.InvalidControl();
            // The authenticated manager retains this captured execution PID through natural exit.
            // A live proc recheck after a running sample would reject a legitimate concurrent exit.
            // Initial startup acquired the actual UID/GID/start-time/cgroup identity; subsequent
            // terminal selection and physical group/pump checks remain independently required.
            await Task.Delay(ObservationInterval, token).ConfigureAwait(false);
        }
    }

    private async Task ObserveStoppedAndEmptyTwiceAsync(CancellationToken token)
    {
        LinuxUnitProperties? final = null;
        while (true)
        {
            _owner.RequireCleanup(token);
            var unit = await _backend!.ReadUnitAsync(Unit, token).ConfigureAwait(false);
            _lastStopObservation = unit;
            var group = LinuxCgroupProbe.Read(Unit, token);
            if (_recipe!.HasStopped(unit)
                && (_process is null || unit.ExecMainPid == _process.Pid)
                && LinuxAccountUtility.GroupEmpty(group))
            {
                final = unit;
                break;
            }
            await Task.Delay(ObservationInterval, token).ConfigureAwait(false);
        }
        await Task.Delay(ObservationInterval, token).ConfigureAwait(false);
        _owner.RequireCleanup(token);
        var second = await _backend!.ReadUnitAsync(Unit, token).ConfigureAwait(false);
        _lastStopObservation = second;
        var secondGroup = LinuxCgroupProbe.Read(Unit, token);
        if (!_recipe!.HasStopped(second) || final.ExecMainPid != second.ExecMainPid
            || final.ExecMainCode != second.ExecMainCode || final.ExecMainStatus != second.ExecMainStatus
            || (_process is not null && second.ExecMainPid != _process.Pid)
            || !LinuxAccountUtility.GroupEmpty(secondGroup)) throw LinuxSystemdBackend.InvalidControl();
        _stoppedTerminal = second;
        Interlocked.Exchange(ref _groupEmptyTwice, 1);
    }

    private async Task StopSelectedUnitAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _dispatchAttempted) == 0) return;
        _owner.RequireCleanup(token);
        // systemd's TimeoutStopUSec equals the worker's entire stopping allowance. Waiting for
        // that automatic KILL races the worker's own stop deadline. Spend only half of the existing
        // reserve on TERM, then force the same selected group and retain time for physical checks.
        var grace = TimeSpan.FromTicks(Math.Min(_owner.CleanupRemaining.Ticks,
            TimeSpan.FromSeconds(_input.Request.StoppingSeconds).Ticks) / 2);
        await SupervisionProducerStop.RunAsync(grace, async ct =>
        {
            using var stop = await LinuxSystemdBackend.ConnectAsync(ct).ConfigureAwait(false);
            await stop.StopUnitAsync(Unit, ct).ConfigureAwait(false);
        }, async ct =>
        {
            using var observations = await LinuxSystemdBackend.ConnectAsync(ct).ConfigureAwait(false);
            while (true)
            {
                _owner.RequireCleanup(ct);
                var sample = await observations.ReadUnitAsync(Unit, ct).ConfigureAwait(false);
                _lastStopObservation = sample;
                if (_recipe!.HasStopped(sample)
                    && (_process is null || sample.ExecMainPid == _process.Pid)
                    && LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(Unit, ct))) return;
                await Task.Delay(ObservationInterval, ct).ConfigureAwait(false);
            }
        }, async ct =>
        {
            // A grace cancellation can abort its connection. Authenticate a fresh containment
            // connection under the original cleanup token rather than retrying canceled I/O.
            using var containment = await LinuxSystemdBackend.ConnectAsync(ct).ConfigureAwait(false);
            await containment.StopUnitAsync(Unit, ct).ConfigureAwait(false);
            await containment.KillUnitAsync(Unit, ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        _owner.RequireCleanup(token);
    }

    private void RequirePollingBindings(CancellationToken token)
    {
        _owner.RequireActive(token);
        _input.Deadline.Check(token);
        _accounts.RequireOwnedBy(_owner, token);
        _workspace.RequireOwnedBy(_owner, _accounts, token);
    }

    private void RequireLiveBindings(CancellationToken token)
    {
        _owner.RequireLaunchInput(_input, token);
        _accounts.RequireOwnedBy(_owner, token);
        _workspace.RequireOwnedBy(_owner, _accounts, token);
    }

    private void RequireOutputHealthy()
    {
        if (_outputBudget.QuotaExceeded || _collect?.IsFaulted == true || _collect?.IsCanceled == true)
            throw LinuxSystemdBackend.InvalidControl();
    }

    private async Task CompleteWorkloadAfterRunAsync(Task<EvidenceRestrictedProcessResult> run,
        SupervisionWorkRegistry.Workload workload)
    {
        try { await run.ConfigureAwait(false); }
        catch { /* The original run task has failed; the registration remains until this join. */ }
        if (!workload.Complete()) Interlocked.Exchange(ref _failed, 1);
    }

    private void RequireReferences(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        LinuxProducerWorkspace workspace)
    {
        if (!ReferenceEquals(owner, _owner) || !ReferenceEquals(accounts, _accounts)
            || !ReferenceEquals(workspace, _workspace)) throw LinuxSystemdBackend.InvalidControl();
    }
}
