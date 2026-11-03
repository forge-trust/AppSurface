namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Joins the original control I/O after the accept loop ends, without authenticating a native owner.</summary>
/// <remarks>
/// The server supplies only fixed operations on its privately constructed owners. This procedure cannot
/// create a listener, connection, worker, workload, cleanup deadline, acknowledgement or custody capability.
/// Its result describes joined I/O only; the server still validates protocol completion, native settlement
/// and the original deadline. Every supplied task is awaited directly, including after cancellation.
/// </remarks>
internal static class SupervisionControlDrain
{
    /// <summary>Attempts the complete ordered drain, preserving failures while still joining later operations.</summary>
    /// <param name="exitIntent">Whether the actual server observed EXIT intent; intent is not a committed ACK.</param>
    /// <param name="pendingAccept">The original pending accept, if any, still owned by the listener.</param>
    /// <param name="handlers">The retained original handlers after accept dispatch has stopped.</param>
    /// <param name="actions">Fixed native-owner operations; none may recursively await this procedure.</param>
    /// <param name="recordFailure">Nonthrowing sink that captures the original stage and latches protocol failure.</param>
    /// <returns>Whether all I/O joins succeeded. Descendant stop failure is separately latched by the sink.</returns>
    /// <remarks>
    /// Without EXIT, containment starts before handlers are joined, because their work may require stop
    /// to finish. With EXIT, only accept admission closes first; accepted connections remain live through
    /// handler writes. Both paths then close the listener, join the original stop, and join control records.
    /// No caller wait, timeout, retry, cancellation proxy or replacement task supplies completion here.
    /// Expected cancellation of the retained signal is ignored only after its own lifetime is canceled.
    /// A pending accept fault is observed but the listener drain remains responsible for classifying it.
    /// Recoverable failures never skip the subsequent cleanup steps. Fatal runtime failures propagate.
    /// </remarks>
    internal static async Task<bool> RunAsync(bool exitIntent, Task? pendingAccept,
        IReadOnlyList<Task> handlers, SupervisionControlDrainActions actions,
        Action<LinuxControlFailureStage, Exception> recordFailure)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(recordFailure);
        var ioJoined = true;
        Task? failedDrain = null;
        if (!exitIntent)
        {
            try { failedDrain = actions.BeginFailedDrain(); }
            catch (Exception error) when (Recoverable(error))
            { recordFailure(LinuxControlFailureStage.DescendantsStop, error); }
        }
        // Match the original stage when canceling the added signal task: prior accept-loop stage on
        // EXIT, DescendantsStop otherwise. The native caller captures the former before entering.
        var signalStage = exitIntent ? actions.ExitSignalStage : LinuxControlFailureStage.DescendantsStop;
        try { actions.CancelSignal(); }
        catch (Exception error) when (Recoverable(error))
        { recordFailure(signalStage, error); ioJoined = false; }
        try { await actions.Signal.ConfigureAwait(false); }
        catch (OperationCanceledException) when (actions.SignalLifetime.IsCancellationRequested) { }
        catch (Exception error) when (Recoverable(error))
        { recordFailure(signalStage, error); ioJoined = false; }
        try { actions.CancelAccept(); }
        catch (Exception error) when (Recoverable(error))
        { recordFailure(LinuxControlFailureStage.AcceptCancel, error); }
        ioJoined = await actions.CloseCheckpoints(ioJoined).ConfigureAwait(false);
        await AttemptAsync(exitIntent ? actions.CloseAcceptAdmission : actions.CloseListener,
            LinuxControlFailureStage.ListenerClose, affectsIo: true).ConfigureAwait(false);
        if (pendingAccept is not null)
        {
            try { await pendingAccept.ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error)) { /* The listener independently latches unexpected accept faults. */ }
        }
        await AttemptAsync(() => Task.WhenAll(handlers), LinuxControlFailureStage.HandlersJoin,
            affectsIo: true).ConfigureAwait(false);
        if (exitIntent)
            await AttemptAsync(actions.CloseListener, LinuxControlFailureStage.ListenerClose,
                affectsIo: true).ConfigureAwait(false);
        await AttemptAsync(() => failedDrain ?? actions.StopDescendants(),
            LinuxControlFailureStage.DescendantsStop, affectsIo: false).ConfigureAwait(false);
        await AttemptAsync(actions.JoinControls, LinuxControlFailureStage.ControlsJoin,
            affectsIo: true).ConfigureAwait(false);
        return ioJoined;

        async Task AttemptAsync(Func<Task> operation, LinuxControlFailureStage stage, bool affectsIo)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error))
            {
                recordFailure(stage, error);
                if (affectsIo) ioJoined = false;
            }
        }
    }

    private static bool Recoverable(Exception error) => SupervisionExceptionPolicy.IsRecoverable(error);
}

/// <summary>Fixed operation bindings borrowed for one server drain; no operation grants native authority.</summary>
/// <remarks>
/// Only the native server binds this shape in production. The separate procedure seam permits ordinary
/// task/barrier controls without constructing that server or supplying a transport to it. Delegates and
/// original tasks must be nonnull; they remain owned by the caller, which also owns their actual deadline.
/// </remarks>
internal sealed record SupervisionControlDrainActions(
    Func<Task> BeginFailedDrain,
    Action CancelSignal,
    Task Signal,
    CancellationToken SignalLifetime,
    Action CancelAccept,
    Func<bool, Task<bool>> CloseCheckpoints,
    Func<Task> CloseAcceptAdmission,
    Func<Task> CloseListener,
    Func<Task> StopDescendants,
    Func<Task> JoinControls,
    LinuxControlFailureStage ExitSignalStage);
