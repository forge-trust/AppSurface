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
    private int _attempted;
    private int _physicallySettled;

    private LinuxAccountUtility(LinuxOwnerActivation owner, LinuxRunAccountCommand command, bool cleanup)
    { _owner = owner; _command = command; _cleanup = cleanup; }

    /// <summary>Gets whether this actual attempt has joined pending dispatch, native group and all owned pipes.</summary>
    /// <remarks>Failure can remain latched; this permits strict account rollback, never an admission or passed result.</remarks>
    internal bool PhysicallySettled => Volatile.Read(ref _physicallySettled) != 0;

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
        try
        {
            // Even a pre-dispatch rejection passes through the ownership finalizer: no OS start
            // occurred, so safe rollback need not quarantine earlier successfully created identities.
            RequireOwner(owner, cleanup, token);
            var remaining = cleanup ? owner.CleanupRemaining : owner.Remaining;
            if (remaining <= stopping) throw LinuxSystemdBackend.InvalidControl();
            deadline.CancelAfter(TimeSpan.FromTicks(Math.Min(remaining.Ticks,
                TimeSpan.FromSeconds(10).Ticks + stopping.Ticks)));
            pipes = LinuxOutputPipes.Create();
            _ = pipes.BeginCollectAsync(deadline.Token, receivedByteLimit: 128 * 1024, prefixByteLimit: 4096);
            backend = await LinuxSystemdBackend.ConnectAsync(deadline.Token).ConfigureAwait(false);
            RequireOwner(owner, cleanup, deadline.Token);
            // Connection/acquisition time consumes the original remainder before OS properties are selected.
            var recipe = LinuxAccountUnit.Create(pending.Unit, owner.Unit, command,
                cleanup ? owner.CleanupRemaining : owner.Remaining, stopping,
                pipes.StandardOutput, pipes.StandardError);
            started = true; // The pending coordinator reserves before it can invoke StartTransientUnit.
            await pending.StartAsync(ct => backend.StartAccountUtilityAsync(recipe, ct), deadline.Token).ConfigureAwait(false);
            pipes.CloseWriteCopies(); // Actual start task, including FD transfer, has joined.
            while (true)
            {
                RequireOwner(owner, cleanup, deadline.Token);
                if (recipe.HasFinished(await backend.ReadUnitAsync(pending.Unit, deadline.Token).ConfigureAwait(false))) break;
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { failure = true; cancelled = token.IsCancellationRequested; }
        catch (Exception) { failure = true; }
        finally
        {
            // Only failed/cancelled forward preparation begins whole-run rollback. Successful
            // utility settlement remains a local stop and must leave future work admission open.
            try { if (failure || token.IsCancellationRequested) owner.BeginRootTeardown(); }
            catch (Exception) { failure = true; }
            // This timer is bounded by the original run plus its protected cleanup reserve. It does not
            // reopen new work or replace either actual start/pump task with a timed-out wait wrapper.
            try
            {
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
                if (pipes is not null) pipes.CloseWriteCopies();
                if (started)
                {
                    while (true)
                    {
                        owner.RequireCleanup(stopDeadline.Token);
                        if (GroupEmpty(LinuxCgroupProbe.Read(pending.Unit, stopDeadline.Token))) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(25), stopDeadline.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception) { failure = true; }

            // A failed stop must not detach pumps or convert closing a reader into EOF. If an external
            // writer survives, the independent owner deadline terminates this blocked physical join.
            if (pipes is not null)
            {
                try { pipes.CloseWriteCopies(); }
                catch (Exception) { failure = true; }
                try
                {
                    var output = await pipes.JoinAsync().ConfigureAwait(false);
                    if (!output.Successful) failure = true;
                    nativeJoined = (!started || pending.Snapshot is { StartJoined: true, StopJoined: true })
                        && (!started || GroupEmpty(LinuxCgroupProbe.Read(pending.Unit, default)));
                    if (!nativeJoined) failure = true;
                    owner.RequireCleanup(default);
                }
                catch (Exception) { failure = true; }
                try { await pipes.DisposeAsync().ConfigureAwait(false); pipesClosed = true; }
                catch (Exception) { failure = true; }
            }
            else if (!started) { nativeJoined = true; pipesClosed = true; }
            try { backend?.Dispose(); }
            catch (Exception) { failure = true; nativeJoined = false; }
            // Publish only from this actual executor, after every real task/FD join. If custody is
            // uncertain, the account owner must quarantine rather than delete or reuse identities.
            if (nativeJoined && pipesClosed)
            {
                try { owner.RequireCleanup(default); Interlocked.Exchange(ref _physicallySettled, 1); }
                catch (Exception) { failure = true; }
            }
        }
        if (cancelled) throw new OperationCanceledException(token);
        if (failure || token.IsCancellationRequested) throw LinuxSystemdBackend.InvalidControl();
        RequireOwner(owner, cleanup, token);
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
