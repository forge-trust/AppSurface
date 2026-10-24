namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Orders the original worker cleanup operations while preserving every cleanup failure.</summary>
/// <remarks>
/// This procedure creates no owner, unit, task, handle, deadline or settlement capability. The private
/// native caller binds its original operations after startup joins and retains every native observation
/// and final publication check. Cancellation is not completion: all supplied operations are awaited
/// directly, without a canceled wait, retry or replacement task. The original monitor join retains its
/// own exception policy; an exception escaping that join propagates instead of being reclassified here.
/// </remarks>
internal static class SupervisionWorkerCleanup
{
    /// <summary>Attempts the fixed cleanup sequence, reporting a failure before the next operation begins.</summary>
    /// <param name="actions">Original native-owner bindings; optional monitor/pipes were never acquired.</param>
    /// <param name="recordFailure">Nonthrowing sink for the caller's sticky failure and stage-specific invalidation.</param>
    /// <remarks>
    /// Unit/group inspection, each pipe operation, observation close, final inspection and final start
    /// connection close retain the native caller's original catch-all policy. They continue after any
    /// thrown exception, including cancellation. The monitor binding runs outside those catch blocks.
    /// The final connection close is still attempted when the preceding inspection or close failed.
    /// This method reports procedure failures only; returning cannot establish physical exit or custody.
    /// </remarks>
    internal static async Task RunAsync(SupervisionWorkerCleanupActions actions,
        Action<SupervisionWorkerCleanupStage> recordFailure)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(recordFailure);
        await AttemptAsync(actions.JoinUnitAndGroup, SupervisionWorkerCleanupStage.UnitAndGroup).ConfigureAwait(false);
        if (actions.JoinMonitor is not null)
            await actions.JoinMonitor().ConfigureAwait(false);
        if (actions.Pipes is { } pipes)
        {
            Attempt(pipes.CloseWriteCopies, SupervisionWorkerCleanupStage.PipeWriteCopies);
            await AttemptAsync(pipes.JoinPumps, SupervisionWorkerCleanupStage.Pumps).ConfigureAwait(false);
            try { await pipes.DisposeAsync().ConfigureAwait(false); }
            catch { recordFailure(SupervisionWorkerCleanupStage.PipeClose); }
        }
        Attempt(actions.CloseObservation, SupervisionWorkerCleanupStage.ObservationClose);
        await AttemptAsync(actions.InspectAndClose, SupervisionWorkerCleanupStage.FinalInspection).ConfigureAwait(false);
        Attempt(actions.CloseStartConnection, SupervisionWorkerCleanupStage.StartConnectionClose);

        async Task AttemptAsync(Func<Task> operation, SupervisionWorkerCleanupStage stage)
        {
            try { await operation().ConfigureAwait(false); }
            catch { recordFailure(stage); }
        }

        void Attempt(Action operation, SupervisionWorkerCleanupStage stage)
        {
            try { operation(); }
            catch { recordFailure(stage); }
        }
    }
}

/// <summary>Fixed operations borrowed from one private worker; none is an alternative native transport.</summary>
/// <remarks>
/// Required delegates are nonnull original-owner operations. The native caller alone supplies this
/// shape in production. Optional bindings denote resources never acquired, not permission to skip an
/// acquired resource. Bindings must not recursively await this cleanup procedure.
/// </remarks>
internal sealed record SupervisionWorkerCleanupActions(
    Func<Task> JoinUnitAndGroup,
    Func<Task>? JoinMonitor,
    SupervisionWorkerPipeCleanup? Pipes,
    Action CloseObservation,
    Func<Task> InspectAndClose,
    Action CloseStartConnection);

/// <summary>Correlated original pipe owners: close transferred write copies, join pumps, then close readers.</summary>
internal sealed record SupervisionWorkerPipeCleanup(Action CloseWriteCopies, Func<Task> JoinPumps,
    Func<ValueTask> DisposeAsync);

/// <summary>Closed procedure stages used only to invalidate the native caller's local cleanup facts.</summary>
internal enum SupervisionWorkerCleanupStage
{
    UnitAndGroup,
    PipeWriteCopies,
    Pumps,
    PipeClose,
    ObservationClose,
    FinalInspection,
    StartConnectionClose
}
