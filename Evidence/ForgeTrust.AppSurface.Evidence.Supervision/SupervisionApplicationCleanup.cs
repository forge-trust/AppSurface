namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Runs the application owner's ordered stop/join and cleanup callbacks.</summary>
/// <remarks>This procedure owns ordering and the original broad failure catches only. It does not
/// create an application owner, admit work, establish deadlines, or infer terminal/group/pump facts.
/// Callbacks must be borrowed from the one existing application owner.</remarks>
internal static class SupervisionApplicationCleanup
{
    /// <summary>Runs one application cleanup pass and returns its sticky procedure-failure state.</summary>
    /// <param name="actions">Bindings borrowed from one private application; no native ownership is transferred.</param>
    /// <returns>True when cancel, lifetime join, body cleanup or final work completion failed.</returns>
    /// <remarks>Begin-stop and active-work completion retain their original outside-catch behavior.
    /// Monitor failures retain their independent ledger callback and do not by themselves skip body cleanup.</remarks>
    internal static async Task<bool> RunAsync(SupervisionApplicationCleanupActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(actions.BeginStop);
        ArgumentNullException.ThrowIfNull(actions.CancelMonitor);
        ArgumentNullException.ThrowIfNull(actions.StopAndJoinLifetime);
        ArgumentNullException.ThrowIfNull(actions.MonitorCancellationRequested);
        ArgumentNullException.ThrowIfNull(actions.RecordMonitorFailure);
        ArgumentNullException.ThrowIfNull(actions.CloseBody);
        ArgumentNullException.ThrowIfNull(actions.CompleteActiveWork);

        // BeginStop is intentionally outside the catch boundary, matching the native owner.
        actions.BeginStop();
        var failed = false;
        try { actions.CancelMonitor(); } catch { failed = true; }
        try { await actions.StopAndJoinLifetime().ConfigureAwait(false); }
        catch { failed = true; }
        try
        {
            var monitor = actions.Monitor();
            if (monitor is not null)
            {
                try { await monitor.ConfigureAwait(false); }
                catch (OperationCanceledException) when (actions.MonitorCancellationRequested()) { }
                catch { actions.RecordMonitorFailure(); }
            }
            await actions.CloseBody(failed).ConfigureAwait(false);
        }
        catch { failed = true; }
        finally
        {
            if (!actions.CompleteActiveWork()) failed = true;
        }

        return failed;
    }
}

/// <summary>Borrowed operations and monitor state from one application owner; no ownership is transferred.</summary>
/// <param name="BeginStop">Original monitor-state transition, invoked before any catch boundary.</param>
/// <param name="CancelMonitor">Original stop-source cancellation; failure is sticky but lifetime join still runs.</param>
/// <param name="StopAndJoinLifetime">Original lifetime stop and operation join, awaited without a proxy.</param>
/// <param name="Monitor">Reads the current original monitor only after lifetime join, including late startup publication.</param>
/// <param name="MonitorCancellationRequested">Reads that monitor's original cancellation source when its task cancels.</param>
/// <param name="RecordMonitorFailure">Original ledger sink for unexpected monitor failure; body cleanup still follows.</param>
/// <param name="CloseBody">Original terminal/group/output checks, closes and publication, receiving prior sticky failure.</param>
/// <param name="CompleteActiveWork">Original final registration completion; false marks failure and exceptions propagate.</param>
internal sealed record SupervisionApplicationCleanupActions(
    Action BeginStop,
    Action CancelMonitor,
    Func<Task> StopAndJoinLifetime,
    Func<Task?> Monitor,
    Func<bool> MonitorCancellationRequested,
    Action RecordMonitorFailure,
    Func<bool, Task> CloseBody,
    Func<bool> CompleteActiveWork);
