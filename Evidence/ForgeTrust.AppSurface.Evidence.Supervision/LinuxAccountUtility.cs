namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Executes fixed root account commands under the actual single-use owner's original lifetime.</summary>
/// <remarks>
/// No process, argument, path, environment or transport callback is supplied by request JSON. Pending
/// ownership and both pumps precede the start call. A canceled start remains owned through an independent
/// authenticated stop, actual start join, second stop, kernel group inspection and both pump joins.
/// The original starting connection retains AddRef until completion. Neither a job reply, an exited
/// main PID nor absent cgroup data alone can complete the operation. Output is private bounded data.
/// A synchronous kernel/NSS/client stall remains contained by the independent OS owner lifetime.
/// </remarks>
internal sealed class LinuxAccountUtility
{
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxRunAccountCommand _command;
    private readonly bool _cleanup;
    private readonly LinuxAccountFailureLatch _failures = new();
    private int _attempted;
    private int _physicallySettled;

    private LinuxAccountUtility(LinuxOwnerActivation owner, LinuxRunAccountCommand command, bool cleanup)
    { _owner = owner; _command = command; _cleanup = cleanup; }

    /// <summary>Gets whether this actual attempt has joined pending dispatch, native group and all owned pipes.</summary>
    /// <remarks>Failure can remain latched; this permits strict account rollback, never an admission or passed result.</remarks>
    internal bool PhysicallySettled => Volatile.Read(ref _physicallySettled) != 0;

    /// <summary>Gets detached first execution/cleanup fault, captured before normalization or teardown.</summary>
    internal LinuxAccountFailure? FirstFailure => _failures.First;

    /// <summary>Creates retained ownership before execution; requires an actual owner, not metadata.</summary>
    internal static LinuxAccountUtility Create(LinuxOwnerActivation owner, LinuxRunAccountCommand command, bool cleanup)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(command);
        RequireOwner(owner, cleanup, default);
        return new(owner, command, cleanup);
    }

    /// <summary>Claims the single execution attempt synchronously, before any native dispatch.</summary>
    internal Task ExecuteAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _attempted, 1) != 0) throw LinuxSystemdBackend.InvalidControl();
        return ExecuteCoreAsync(token);
    }
    /// <summary>Runs one closed utility and returns only after successful execution and physical/output joins.</summary>
    /// <param name="token">Caller cancellation within the original owner lifetime.</param>
    private async Task ExecuteCoreAsync(CancellationToken token)
    {
        var owner = _owner;
        var command = _command;
        var cleanup = _cleanup;
        var stopping = owner.UtilityStopping;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);

        LinuxOutputPipes? pipes = null;
        LinuxSystemdBackend? backend = null;
        var pending = new SupervisionPendingStart(LinuxUnitName.Create(LinuxUnitRole.AccountUtility, Guid.NewGuid()));
        var failure = false;
        var cancelled = false;
        var started = false;
        var nativeJoined = false;
        var pipesClosed = false;
        var stage = LinuxAccountUtilityStage.OwnerCheck;
        int? observedCode = null;
        int? observedStatus = null;
        void Capture(Exception error) => _failures.Capture(cleanup ? LinuxAccountPreparationStage.CleanupUtility
            : LinuxAccountPreparationStage.UtilityExecute, stage, command.Operation, error,
            observedCode, observedStatus, backend?.FirstStartFailure?.DBusCategory);
        try
        {
            // Even a pre-dispatch rejection passes through the ownership finalizer: no OS start
            // occurred, so safe rollback need not quarantine earlier successfully created identities.
            RequireOwner(owner, cleanup, token);
            var remaining = cleanup ? owner.CleanupRemaining : owner.Remaining;
            if (remaining <= stopping) throw LinuxSystemdBackend.InvalidControl();
            deadline.CancelAfter(TimeSpan.FromTicks(Math.Min(remaining.Ticks,
                TimeSpan.FromSeconds(10).Ticks + stopping.Ticks)));
            stage = LinuxAccountUtilityStage.Pipes;
            pipes = LinuxOutputPipes.Create();
            _ = pipes.BeginCollectAsync(deadline.Token, receivedByteLimit: 128 * 1024, prefixByteLimit: 4096);
            stage = LinuxAccountUtilityStage.BackendConnect;
            backend = await LinuxSystemdBackend.ConnectAsync(deadline.Token).ConfigureAwait(false);
            RequireOwner(owner, cleanup, deadline.Token);
            // Connection/acquisition time consumes the original remainder before OS properties are selected.
            stage = LinuxAccountUtilityStage.Recipe;
            var recipe = LinuxAccountUnit.Create(pending.Unit, owner.Unit, command,
                cleanup ? owner.CleanupRemaining : owner.Remaining, stopping,
                pipes.StandardOutput, pipes.StandardError);
            stage = LinuxAccountUtilityStage.Start;
            started = true; // The pending coordinator reserves before it can invoke StartTransientUnit.
            await pending.StartAsync(ct => backend.StartAccountUtilityAsync(recipe, ct), deadline.Token).ConfigureAwait(false);
            stage = LinuxAccountUtilityStage.CloseWrites;
            pipes.CloseWriteCopies(); // Actual start task, including FD transfer, has joined.
            while (true)
            {
                stage = LinuxAccountUtilityStage.OwnerCheck;
                RequireOwner(owner, cleanup, deadline.Token);
                stage = LinuxAccountUtilityStage.UnitRead;
                var observed = await backend.ReadUnitAsync(pending.Unit, deadline.Token).ConfigureAwait(false);
                (observedCode, observedStatus) = ObserveTerminalData(observed, pending.Unit);
                stage = LinuxAccountUtilityStage.TerminalCheck;
                if (recipe.HasFinished(observed)) break;
                stage = LinuxAccountUtilityStage.ObservationDelay;
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) { Capture(error); failure = true; cancelled = token.IsCancellationRequested; }
        catch (Exception error) { Capture(error); failure = true; }
        finally
        {
            // Only failed/cancelled forward preparation begins whole-run rollback. Successful
            // utility settlement remains a local stop and must leave future work admission open.
            stage = LinuxAccountUtilityStage.BeginTeardown;
            try
            {
                if (failure || token.IsCancellationRequested)
                {
                    CaptureCallerCancellationBeforeTeardown(_failures, cleanup, command.Operation, token,
                        observedCode, observedStatus, backend?.FirstStartFailure?.DBusCategory);
                    owner.BeginRootTeardown();
                }
            }
            catch (Exception error) { Capture(error); failure = true; }
            // This timer is bounded by the original run plus its protected cleanup reserve. It does not
            // reopen new work or replace either actual start/pump task with a timed-out wait wrapper.
            try
            {
                stage = LinuxAccountUtilityStage.Stop;
                using var stopDeadline = cleanup || failure || token.IsCancellationRequested
                    ? CancellationTokenSource.CreateLinkedTokenSource(owner.RootTeardownToken)
                    : new CancellationTokenSource();
                stopDeadline.CancelAfter(owner.CleanupAllowance);
                if (started)
                {
                    await pending.StopAsync(async ct =>
                    {
                        owner.RequireCleanup(ct);
                        using var stop = await LinuxSystemdBackend.ConnectAsync(ct).ConfigureAwait(false);
                        await stop.StopUnitAsync(pending.Unit, ct).ConfigureAwait(false);
                        owner.RequireCleanup(ct);
                    }, stopDeadline.Token).ConfigureAwait(false);
                }
                stage = LinuxAccountUtilityStage.CloseWrites;
                if (pipes is not null) pipes.CloseWriteCopies();
                if (started)
                {
                    while (true)
                    {
                        stage = LinuxAccountUtilityStage.GroupRead;
                        owner.RequireCleanup(stopDeadline.Token);
                        if (GroupEmpty(LinuxCgroupProbe.Read(pending.Unit, stopDeadline.Token))) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(25), stopDeadline.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error) { Capture(error); failure = true; }

            // A failed stop must not detach pumps or convert closing a reader into EOF. If an external
            // writer survives, the independent owner deadline terminates this blocked physical join.
            if (pipes is not null)
            {
                stage = LinuxAccountUtilityStage.CloseWrites;
                try { pipes.CloseWriteCopies(); }
                catch (Exception error) { Capture(error); failure = true; }
                try
                {
                    stage = LinuxAccountUtilityStage.OutputJoin;
                    var output = await pipes.JoinAsync().ConfigureAwait(false);
                    if (!output.Successful) { Capture(LinuxSystemdBackend.InvalidControl()); failure = true; }
                    stage = LinuxAccountUtilityStage.GroupRead;
                    nativeJoined = (!started || pending.Snapshot is { StartJoined: true, StopJoined: true })
                        && (!started || GroupEmpty(LinuxCgroupProbe.Read(pending.Unit, default)));
                    if (!nativeJoined) { Capture(LinuxSystemdBackend.InvalidControl()); failure = true; }
                    owner.RequireCleanup(default);
                }
                catch (Exception error) { Capture(error); failure = true; }
                stage = LinuxAccountUtilityStage.PipeDispose;
                try { await pipes.DisposeAsync().ConfigureAwait(false); pipesClosed = true; }
                catch (Exception error) { Capture(error); failure = true; }
            }
            else if (!started) { nativeJoined = true; pipesClosed = true; }
            stage = LinuxAccountUtilityStage.BackendDispose;
            try { backend?.Dispose(); }
            catch (Exception error) { Capture(error); failure = true; nativeJoined = false; }
            // Publish only from this actual executor, after every real task/FD join. If custody is
            // uncertain, the account owner must quarantine rather than delete or reuse identities.
            if (nativeJoined && pipesClosed)
            {
                stage = LinuxAccountUtilityStage.PhysicalSettlement;
                try { owner.RequireCleanup(default); Interlocked.Exchange(ref _physicallySettled, 1); }
                catch (Exception error) { Capture(error); failure = true; }
            }
        }
        stage = LinuxAccountUtilityStage.FinalOwnerCheck;
        if (cancelled) throw new OperationCanceledException(token);
        if (failure || token.IsCancellationRequested)
        {
            if (token.IsCancellationRequested) Capture(new OperationCanceledException(token));
            throw LinuxSystemdBackend.InvalidControl();
        }
        try { RequireOwner(owner, cleanup, token); }
        catch (Exception error) { Capture(error); throw; }
    }

    /// <summary>Retains observed caller cancellation before any teardown operation can fail.</summary>
    /// <param name="failures">The same attempt's sticky detached diagnostic latch.</param>
    /// <param name="cleanup">Whether this attempt performs account cleanup rather than preparation.</param>
    /// <param name="operation">The attempt's existing closed account operation.</param>
    /// <param name="token">The original caller token, observed without changing rejection priority.</param>
    /// <param name="observedCode">Previously selected terminal unit data, or null.</param>
    /// <param name="observedStatus">Previously selected terminal unit data, or null.</param>
    /// <param name="dbusCategory">Previously retained closed start-error data, or null.</param>
    /// <remarks>No token, timer, native action or outcome is changed; an earlier fault remains first.</remarks>
    internal static void CaptureCallerCancellationBeforeTeardown(LinuxAccountFailureLatch failures,
        bool cleanup, LinuxRunAccountOperation operation, CancellationToken token,
        int? observedCode = null, int? observedStatus = null, LinuxSystemdStartError? dbusCategory = null)
    {
        if (token.IsCancellationRequested)
            failures.Capture(cleanup ? LinuxAccountPreparationStage.CleanupUtility
                : LinuxAccountPreparationStage.UtilityExecute, LinuxAccountUtilityStage.BeginTeardown,
                operation, new OperationCanceledException(token), observedCode, observedStatus, dbusCategory);
    }

    /// <summary>Copies bounded numeric data only from a selected, initialized terminal unit sample.</summary>
    /// <remarks>This data predicate establishes neither successful utility execution nor physical exit.</remarks>
    internal static (int? Code, int? Status) ObserveTerminalData(LinuxUnitProperties? value, LinuxUnitName selected)
    {
        if (value is null || selected is null || value.Id != selected.Value || value.LoadState != "loaded"
            || value.MainPid != 0 || value.ExecMainPid == 0 || value.ExecMainCode is < 1 or > 6
            || value.ExecMainStatus is < 0 or > 255
            || !((value.ActiveState == "active" && value.SubState == "exited")
                || (value.ActiveState == "inactive" && value.SubState == "dead")
                || (value.ActiveState == "failed" && value.SubState == "failed"))) return (null, null);
        return (value.ExecMainCode, value.ExecMainStatus);
    }

    /// <summary>Validates detached empty-group sample shape; it cannot prove unit settlement or native exit.</summary>
    internal static bool GroupEmpty(LinuxCgroupSample value)
    {
        if (value is null) throw LinuxSystemdBackend.InvalidControl();
        if (!value.Exists)
        {
            if (value.Populated is not null || value.Frozen is not null || value.DeviceMajor is not null
                || value.DeviceMinor is not null || value.KernelInode is not null) throw LinuxSystemdBackend.InvalidControl();
            return true;
        }
        if (value.Populated is null || value.Frozen is null || value.DeviceMajor is null || value.DeviceMinor is null
            || value.KernelInode is null or 0) throw LinuxSystemdBackend.InvalidControl();
        return value.Populated == false && value.Frozen == false;
    }

    private static void RequireOwner(LinuxOwnerActivation owner, bool cleanup, CancellationToken token)
    {
        if (cleanup) owner.RequireCleanup(token);
        else owner.RequireActive(token);
    }
}
