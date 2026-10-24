namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Orders the producer's already-owned cleanup procedures and reports their failures.</summary>
/// <remarks>
/// This is an internal procedure seam, not a producer, owner, lease, deadline, native-handle, or
/// admission factory. The private producer supplies callbacks bound to its authentic cleanup token,
/// tasks, pipes, and owners. The callback set contains no replacement state or success result. The
/// caller retains all pending-state and native-fact projection after this procedure returns.
/// </remarks>
internal static class SupervisionProducerCleanup
{
    /// <summary>Attempts the fixed producer cleanup sequence, preserving the original catch boundaries.</summary>
    /// <param name="actions">Callbacks borrowed from that one private producer instance.</param>
    /// <param name="recordFailure">Nonthrowing sink for the caller's existing cleanup-failure latch.</param>
    /// <remarks>
    /// Pending stop is always attempted. Stopped observation is attempted only after dispatch. Pipe
    /// write-copy closure and pipe disposal are attempted whenever the pipe owner was acquired; pump
    /// joining is attempted only when the collector task was acquired. Every procedure failure is
    /// reported immediately and later cleanup continues. The retained-owner close is intentionally
    /// outside these catch boundaries, matching the caller's existing direct CloseAll invocation.
    /// </remarks>
    internal static async Task RunAsync(SupervisionProducerCleanupActions actions,
        Action<SupervisionProducerCleanupStage> recordFailure)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(recordFailure);

        await AttemptAsync(actions.StopPending, SupervisionProducerCleanupStage.StopPending).ConfigureAwait(false);
        if (actions.DispatchAttempted())
            await AttemptAsync(actions.ObserveStopped, SupervisionProducerCleanupStage.ObserveStopped).ConfigureAwait(false);

        if (actions.Pipes is { } pipes)
        {
            Attempt(pipes.CloseWriteCopies, SupervisionProducerCleanupStage.CloseWriteCopies);
            if (pipes.JoinPumps is not null)
                await AttemptAsync(pipes.JoinPumps, SupervisionProducerCleanupStage.JoinPumps).ConfigureAwait(false);
            await AttemptValueTaskAsync(pipes.DisposeAsync, SupervisionProducerCleanupStage.DisposePipes).ConfigureAwait(false);
        }

        // The existing CloseAll call remains outside this procedure's catch boundaries by design.
        actions.CloseRetainedOwners();

        async Task AttemptAsync(Func<Task> operation, SupervisionProducerCleanupStage stage)
        {
            try { await operation().ConfigureAwait(false); }
            catch { recordFailure(stage); }
        }

        async Task AttemptValueTaskAsync(Func<ValueTask> operation, SupervisionProducerCleanupStage stage)
        {
            try { await operation().ConfigureAwait(false); }
            catch { recordFailure(stage); }
        }

        void Attempt(Action operation, SupervisionProducerCleanupStage stage)
        {
            try { operation(); }
            catch { recordFailure(stage); }
        }
    }
}

/// <summary>Callbacks correlated to one already-acquired producer's cleanup resources.</summary>
/// <remarks>
/// Required callbacks are the private producer's original pending and retained-owner procedures.
/// <see cref="SupervisionProducerCleanupPipes.JoinPumps"/> is null only when collection was never
/// acquired. A null pipes binding means no pipe owner was acquired. These records carry operations,
/// not native resources or authority, and must not be used to create an alternative producer.
/// </remarks>
internal sealed record SupervisionProducerCleanupActions(
    Func<Task> StopPending,
    Func<bool> DispatchAttempted,
    Func<Task> ObserveStopped,
    SupervisionProducerCleanupPipes? Pipes,
    Action CloseRetainedOwners);

/// <summary>Correlated operations for one producer pipe owner.</summary>
internal sealed record SupervisionProducerCleanupPipes(
    Action CloseWriteCopies,
    Func<Task>? JoinPumps,
    Func<ValueTask> DisposeAsync);

/// <summary>Procedure stages reported to the producer's existing cleanup-failure latch.</summary>
internal enum SupervisionProducerCleanupStage
{
    StopPending,
    ObserveStopped,
    CloseWriteCopies,
    JoinPumps,
    DisposePipes
}
