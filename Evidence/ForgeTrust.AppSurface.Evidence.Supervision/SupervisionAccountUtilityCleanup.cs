namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Orders one account utility's already-owned teardown callbacks.</summary>
/// <remarks>
/// This is an internal procedure seam, not an owner, unit, deadline, token, pipe, backend, cgroup,
/// custody or settlement factory. The caller supplies callbacks bound to the one actual utility and
/// retains all native observations, sticky failure state and final result projection. The first callback
/// is deliberately one compound stop/group procedure: a failure inside it is captured and skips the
/// remainder of that procedure, exactly as the original <c>try</c> body did. Later pipe, backend and
/// settlement procedures have their original independent catch boundaries and continue after failure.
/// </remarks>
internal static class SupervisionAccountUtilityCleanup
{
    /// <summary>Runs ordered teardown, capturing each fallible cleanup operation before continuing.</summary>
    /// <param name="actions">Callbacks borrowed from one private account utility instance.</param>
    /// <param name="captureFailure">The caller's existing sticky failure capture; it must not throw.</param>
    internal static async Task RunAsync(SupervisionAccountUtilityCleanupActions actions,
        Action<Exception> captureFailure)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(captureFailure);

        try { await actions.StopAndGroupAsync().ConfigureAwait(false); }
        catch (Exception error) { captureFailure(error); }

        if (actions.Pipes is { } pipes)
        {
            try { pipes.CloseWriteCopies(); }
            catch (Exception error) { captureFailure(error); }

            try { await pipes.JoinOutputAndCheckAsync().ConfigureAwait(false); }
            catch (Exception error) { captureFailure(error); }

            try { await pipes.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { captureFailure(error); }
        }
        else
        {
            if (actions.MarkNoPipesAndNotStarted is not null)
                actions.MarkNoPipesAndNotStarted();
        }

        try { actions.DisposeBackend(); }
        catch (Exception error) { captureFailure(error); }

        if (actions.CanPublishPhysicalSettlement())
        {
            try { actions.PublishPhysicalSettlement(); }
            catch (Exception error) { captureFailure(error); }
        }
    }
}

/// <summary>Private callbacks and caller-owned conditions for one already-acquired utility attempt.</summary>
/// <remarks>
/// <paramref name="StopAndGroupAsync"/> owns the original stop deadline, token, started condition,
/// first write-copy close and group-wait loop. It must preserve that compound failure boundary. The
/// later pipe close, output join/group check, pipe disposal, backend disposal and physical-settlement
/// callbacks retain their caller's native facts and exception projection. The boolean predicate is
/// evaluated only after backend disposal, so a cleanup failure cannot be hidden by an early snapshot.
/// <paramref name="MarkNoPipesAndNotStarted"/> and <paramref name="CanPublishPhysicalSettlement"/>
/// must be nonthrowing local-state operations, like the failure sink. The original caller performed
/// those assignments and boolean comparisons outside a catch boundary; a throwing replacement violates
/// this internal contract and is deliberately not normalized into a native cleanup outcome.
/// </remarks>
internal sealed record SupervisionAccountUtilityCleanupActions(
    Func<Task> StopAndGroupAsync,
    SupervisionAccountUtilityCleanupPipes? Pipes,
    Action? MarkNoPipesAndNotStarted,
    Action DisposeBackend,
    Func<bool> CanPublishPhysicalSettlement,
    Action PublishPhysicalSettlement);

/// <summary>Correlated callbacks for one actual output-pipe owner.</summary>
internal sealed record SupervisionAccountUtilityCleanupPipes(
    Action CloseWriteCopies,
    Func<Task> JoinOutputAndCheckAsync,
    Func<ValueTask> DisposeAsync);
