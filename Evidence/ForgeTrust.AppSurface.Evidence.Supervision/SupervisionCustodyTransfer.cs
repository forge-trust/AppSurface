using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed first-failure data for a custody procedure, never a native custody assertion.</summary>
internal enum SupervisionCustodyFailure
{
    /// <summary>No failure or cancellation has been observed.</summary>
    None,
    /// <summary>The original token or an actual procedure was canceled.</summary>
    Cancelled,
    /// <summary>Actual settlement validation failed or returned no task.</summary>
    SettlementValidationFailed,
    /// <summary>The complete node preflight failed or returned no task.</summary>
    PreflightFailed,
    /// <summary>Mutation and its immediate recheck failed or returned no task.</summary>
    MutationFailed,
    /// <summary>Local owner closure failed or returned no task.</summary>
    LocalCloseFailed,
    /// <summary>The final native recheck failed or returned no task.</summary>
    FinalNativeRecheckFailed,
}

/// <summary>Owns one preflight/mutation/close/recheck procedure without issuing custody or admission.</summary>
/// <remarks>
/// The real native owner supplies closed callbacks that retain and inspect its actual resources. This
/// bookkeeping seam cannot construct that owner, validate metadata as authority, or create a capability.
/// The whole task is retained before dispatch. Every callback runs outside locks, and its original task
/// is joined directly. Ignored cancellation or a stalled callback keeps this procedure pending until it
/// really finishes; the independently armed native lifetime remains responsible for containment.
/// No timer, linked token, cancellation proxy, retry, or deadline renewal is introduced here.
/// </remarks>
internal sealed class SupervisionCustodyTransfer
{
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _insideProcedure = new();
    private Task? _run;
    private CancellationToken _originalToken;
    private SupervisionCustodyFailure _firstFailure;

    /// <summary>Gets the first closed failure classification; later cleanup cannot replace or clear it.</summary>
    internal SupervisionCustodyFailure FirstFailure
    {
        get
        {
            lock (_gate)
            {
                ObserveCancellationLocked(_originalToken);
                return _firstFailure;
            }
        }
    }

    /// <summary>Gets sticky procedure failure, including cancellation of the retained original token.</summary>
    internal bool Failed => FirstFailure != SupervisionCustodyFailure.None;

    /// <summary>Gets whether the entire original task completed successfully and its token remains uncanceled.</summary>
    /// <remarks>This is revocable procedure data, never proof of native custody, physical exit, or acceptance.</remarks>
    internal bool SuccessfulCompleted
    {
        get
        {
            lock (_gate)
            {
                ObserveCancellationLocked(_originalToken);
                return _run?.IsCompletedSuccessfully == true && _firstFailure == SupervisionCustodyFailure.None;
            }
        }
    }

    /// <summary>Reserves the only attempt before dispatching any actual validation or mutation.</summary>
    /// <param name="validateSettled">Checks actual preceding joins and settlement before node preflight.</param>
    /// <param name="preflightAllNodes">Preflights every selected node before any mutation is permitted.</param>
    /// <param name="mutateAndRecheck">Mutates only the preflighted nodes and rechecks their actual custody.</param>
    /// <param name="closeLocalOwners">Always attempted, including before-dispatch cancellation and earlier failure.</param>
    /// <param name="finalNativeRecheck">Always attempted after local closure, even when closure failed.</param>
    /// <param name="token">The original stop/cleanup deadline; callbacks retain this same token through their closures.</param>
    /// <returns>The retained whole procedure task, completed only after all dispatched original tasks join.</returns>
    /// <remarks>
    /// Fault/cancellation skips remaining validation, preflight, and mutation. Both cleanup callbacks
    /// still run once and are joined, even if they ignore cancellation. They must independently enforce
    /// the original native deadline and must not await this procedure or invoke RunAsync on this owner.
    /// Callbacks cannot return null tasks. Missing arguments reject without consuming an attempt.
    /// Replay, callback reentry, and completed failure expose only fixed ASEVD410, without raw exceptions.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A callback is absent.</exception>
    /// <exception cref="EvidenceAdmissionException">The attempt is repeated, reentrant, or fails.</exception>
    internal Task RunAsync(Func<Task> validateSettled, Func<Task> preflightAllNodes,
        Func<Task> mutateAndRecheck, Func<Task> closeLocalOwners, Func<Task> finalNativeRecheck,
        CancellationToken token)
    {
        if (_insideProcedure.Value) throw Rejected();
        ArgumentNullException.ThrowIfNull(validateSettled);
        ArgumentNullException.ThrowIfNull(preflightAllNodes);
        ArgumentNullException.ThrowIfNull(mutateAndRecheck);
        ArgumentNullException.ThrowIfNull(closeLocalOwners);
        ArgumentNullException.ThrowIfNull(finalNativeRecheck);
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task retained;
        lock (_gate)
        {
            if (_run is not null) throw Rejected();
            _originalToken = token;
            retained = RunCoreAsync(dispatch.Task, validateSettled, preflightAllNodes, mutateAndRecheck,
                closeLocalOwners, finalNativeRecheck, token);
            _run = retained;
        }
        dispatch.SetResult();
        return retained;
    }

    private async Task RunCoreAsync(Task dispatch, Func<Task> validateSettled, Func<Task> preflightAllNodes,
        Func<Task> mutateAndRecheck, Func<Task> closeLocalOwners, Func<Task> finalNativeRecheck,
        CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        await AttemptBeforeMutationAsync(validateSettled, SupervisionCustodyFailure.SettlementValidationFailed,
            token).ConfigureAwait(false);
        await AttemptBeforeMutationAsync(preflightAllNodes, SupervisionCustodyFailure.PreflightFailed,
            token).ConfigureAwait(false);
        await AttemptBeforeMutationAsync(mutateAndRecheck, SupervisionCustodyFailure.MutationFailed,
            token).ConfigureAwait(false);
        // Cancellation must not skip either cleanup attempt or detach either original task.
        await AttemptAsync(closeLocalOwners, SupervisionCustodyFailure.LocalCloseFailed).ConfigureAwait(false);
        lock (_gate) ObserveCancellationLocked(token);
        await AttemptAsync(finalNativeRecheck, SupervisionCustodyFailure.FinalNativeRecheckFailed)
            .ConfigureAwait(false);
        lock (_gate)
        {
            ObserveCancellationLocked(token);
            if (_firstFailure != SupervisionCustodyFailure.None) throw Rejected();
        }
    }

    private async Task AttemptBeforeMutationAsync(Func<Task> procedure, SupervisionCustodyFailure failure,
        CancellationToken token)
    {
        lock (_gate)
        {
            ObserveCancellationLocked(token);
            if (_firstFailure != SupervisionCustodyFailure.None) return;
        }
        await AttemptAsync(procedure, failure).ConfigureAwait(false);
        lock (_gate) ObserveCancellationLocked(token);
    }

    private async Task AttemptAsync(Func<Task> procedure, SupervisionCustodyFailure failure)
    {
        var previous = _insideProcedure.Value;
        _insideProcedure.Value = true;
        try
        {
            var actual = procedure() ?? throw Rejected();
            await actual.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { RecordFailure(SupervisionCustodyFailure.Cancelled); }
        catch (Exception) { RecordFailure(failure); }
        finally { _insideProcedure.Value = previous; }
    }

    private void RecordFailure(SupervisionCustodyFailure failure)
    {
        lock (_gate)
            if (_firstFailure == SupervisionCustodyFailure.None) _firstFailure = failure;
    }

    // Called only under the bookkeeping lock; inspecting the token dispatches no callback or I/O.
    private void ObserveCancellationLocked(CancellationToken token)
    {
        if (token.IsCancellationRequested && _firstFailure == SupervisionCustodyFailure.None)
            _firstFailure = SupervisionCustodyFailure.Cancelled;
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The owned custody transfer procedure was rejected.");
}
