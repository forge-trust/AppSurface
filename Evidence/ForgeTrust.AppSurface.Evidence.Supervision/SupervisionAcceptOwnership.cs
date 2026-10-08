using System.Runtime.CompilerServices;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Owns one actual pending accept and at most 32 live accepted results, as portable bookkeeping only.</summary>
/// <typeparam name="T">An actual accepted resource, compared by reference identity rather than user equality.</typeparam>
/// <remarks>
/// This intentional seam creates no Linux listener, authenticated connection, admission or physical-exit fact.
/// The native caller supplies the actual listener and resource close operations. Register the connection-handler
/// lifecycle before awaiting AcceptAsync, and join handler I/O separately: this class owns accepts and closes only.
/// The actual accept operation must close its own partial acquisitions before faulting; this owner cannot close
/// a resource that was never returned to it.
/// Every dispatch/close task is registered before its delegate can run. Delegates run outside locks; same-owner
/// AcceptAsync, ReleaseAsync, CloseAdmissionAsync and DisposeAsync reentrancy rejects before a shared-task wait. Cancellation closes
/// the listener and joins the original accept, including an implementation that ignores cancellation. A late result is closed and joined,
/// never published. No timer or cancellation proxy can detach these operations; the external root lifetime must
/// contain stalls. Ordinary shutdown is closed, not a failure; unexpected accept or any close fault is sticky.
/// Only SupervisionAcceptShutdownException identifies an intentionally interrupted actual accept, and only
/// after owner closure or original-token cancellation. Every other accept exception fails even after closure.
/// Successful releases free live capacity. Weak-key history preserves shared close completion without rooting
/// an unbounded list of previously released results. Closing this bookkeeping proves no handler or workload exit.
/// </remarks>
internal sealed class SupervisionAcceptOwnership<T> : IAsyncDisposable where T : class
{
    /// <summary>Maximum simultaneous retained resources, including any close still in progress or failed.</summary>
    internal const int MaximumLiveResults = 32;

    private static readonly AsyncLocal<ProcedureScope?> CurrentProcedure = new();

    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<T>> _acceptActual;
    private readonly Action _closeListenerActual;
    private readonly Func<T, ValueTask> _closeAcceptedActual;
    private readonly Dictionary<T, ResultEntry> _live = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<T, ResultEntry> _known = new();
    private PendingAccept? _pending;
    private Task? _listenerClose;
    private Task? _disposal;
    private Task? _admissionClose;
    private bool _closed;
    private bool _failed;

    /// <summary>Takes the actual accept and close operations; no operation runs during construction.</summary>
    /// <param name="acceptActual">Returns the original actual accept task; returning null or an old resource rejects.</param>
    /// <param name="closeListenerActual">Actually closes the listener once, to interrupt its pending accept.</param>
    /// <param name="closeAcceptedActual">Actually closes one result; its original ValueTask is joined exactly once.</param>
    internal SupervisionAcceptOwnership(Func<CancellationToken, Task<T>> acceptActual,
        Action closeListenerActual, Func<T, ValueTask> closeAcceptedActual)
    {
        ArgumentNullException.ThrowIfNull(acceptActual);
        ArgumentNullException.ThrowIfNull(closeListenerActual);
        ArgumentNullException.ThrowIfNull(closeAcceptedActual);
        _acceptActual = acceptActual;
        _closeListenerActual = closeListenerActual;
        _closeAcceptedActual = closeAcceptedActual;
    }

    /// <summary>Gets irreversible closure of accept admission; this is not listener or handler settlement.</summary>
    internal bool IsClosed { get { lock (_gate) return _closed; } }

    /// <summary>Gets sticky unexpected accept/close failure, without original exception or output.</summary>
    internal bool Failed { get { lock (_gate) return _failed; } }

    /// <summary>Registers one sequential accept before invoking the actual delegate.</summary>
    /// <param name="token">Original owner cancellation; no additional deadline or timer is created.</param>
    /// <returns>The registered task, completed only after actual accept and any required late close join.</returns>
    /// <remarks>
    /// Concurrent accepts, closed admission and full live capacity reject without dispatch or consuming capacity.
    /// A successful resource is retained before result publication. Cancellation permanently closes admission;
    /// it does not imply EOF, a stopped kernel unit or completed handler I/O. Rejection uses fixed ASEVD410.
    /// </remarks>
    internal Task<T> AcceptAsync(CancellationToken token)
    {
        var registered = RegisterAccept(token);
        registered.Dispatch();
        return registered.Task;
    }

    /// <summary>Reserves the original accept without dispatching its callback, for an enclosing admission gate.</summary>
    /// <remarks>
    /// Release the enclosing gate, then call Dispatch exactly once. Closure joins this reservation even
    /// before dispatch; a reservation forgotten by its caller stays owned rather than becoming detached.
    /// This portable procedure handle grants no native identity or authentication authority.
    /// </remarks>
    internal RegisteredAccept RegisterAccept(CancellationToken token)
    {
        RequireOutsideProcedure();
        var dispatch = Gate();
        PendingAccept pending;
        lock (_gate)
        {
            PruneClosed();
            if (_closed || _pending is not null || _live.Count >= MaximumLiveResults) throw Rejected();
            pending = new();
            _pending = pending;
            pending.Execution = AcceptCoreAsync(pending, dispatch.Task, token);
        }
        return new(pending.Completion.Task, dispatch);
    }

    /// <summary>Registers exactly one close for a known retained result, returning its shared actual completion.</summary>
    /// <param name="result">The same accepted object reference, never an equivalent decoded value.</param>
    /// <returns>The same task for concurrent/repeated release, including a permanently failed close.</returns>
    /// <remarks>Known releases remain joinable after shutdown; unknown/null results never invoke the close delegate.</remarks>
    internal Task ReleaseAsync(T result)
    {
        RequireOutsideProcedure();
        TaskCompletionSource? dispatch;
        Task close;
        lock (_gate)
        {
            if (result is null || !_known.TryGetValue(result, out var entry)) throw Rejected();
            close = RegisterClose(entry, out dispatch);
        }
        dispatch?.SetResult();
        return close;
    }

    /// <summary>Irreversibly closes accept admission and joins its original pending procedure, preserving published results.</summary>
    /// <returns>One shared original completion, including sticky listener/accept/late-close failure.</returns>
    /// <remarks>
    /// Registered before listener-close dispatch; no new accept can start after reservation. It closes the
    /// listening resource once and joins an ignored-cancellation accept and any unpublished late-result close.
    /// Already published results stay owned and usable until ReleaseAsync or full DisposeAsync. No caller
    /// token detaches this join, and same-owner callback reentrancy rejects before sharing its task.
    /// This is procedure bookkeeping, never handler settlement, native exit or an admission capability.
    /// </remarks>
    internal Task CloseAdmissionAsync()
    {
        var registered = RegisterAdmissionClose();
        registered.Dispatch();
        return registered.Task;
    }

    /// <summary>Reserves admission closure while deferring its actual callback until enclosing gates are released.</summary>
    /// <remarks>Repeated reservations share the original close task; each handle dispatches only its own gate.</remarks>
    internal RegisteredAdmissionClose RegisterAdmissionClose()
    {
        RequireOutsideProcedure();
        var dispatch = Gate();
        Task close;
        lock (_gate)
        {
            if (_admissionClose is not null) return new(_admissionClose, dispatch);
            _closed = true;
            close = CloseAdmissionCoreAsync(dispatch.Task, _pending);
            _admissionClose = close;
        }
        return new(close, dispatch);
    }

    /// <summary>A retained accept task and its single dispatch gate; detached procedure bookkeeping only.</summary>
    internal sealed class RegisteredAccept(Task<T> task, TaskCompletionSource dispatch)
    {
        private int _dispatched;
        /// <summary>Gets the original registered completion, including late-result closure.</summary>
        internal Task<T> Task { get; } = task;
        /// <summary>Releases callback dispatch once, after every enclosing admission lock is released.</summary>
        internal void Dispatch()
        {
            if (Interlocked.Exchange(ref _dispatched, 1) != 0) throw Rejected();
            dispatch.SetResult();
        }
    }

    /// <summary>A retained closure task and single dispatch gate, with no native settlement authority.</summary>
    internal sealed class RegisteredAdmissionClose(Task task, TaskCompletionSource dispatch)
    {
        private int _dispatched;
        /// <summary>Gets the original shared close completion.</summary>
        internal Task Task { get; } = task;
        /// <summary>Releases actual close dispatch once, after every enclosing admission lock is released.</summary>
        internal void Dispatch()
        {
            if (Interlocked.Exchange(ref _dispatched, 1) != 0) throw Rejected();
            dispatch.SetResult();
        }
    }

    private async Task CloseAdmissionCoreAsync(Task dispatch, PendingAccept? pending)
    {
        await dispatch.ConfigureAwait(false);
        try { await EnsureListenerClose().ConfigureAwait(false); }
        catch (Exception) { MarkFailed(); }
        if (pending is not null)
        {
            try { await pending.Completion.Task.ConfigureAwait(false); }
            catch (Exception) { } // Expected rejection is observed; unexpected cause is already sticky.
            await pending.Execution.ConfigureAwait(false);
        }
        lock (_gate) if (_failed) throw Rejected();
    }

    /// <summary>Closes the listener first, joins pending accept, then attempts and joins all retained closes.</summary>
    /// <remarks>
    /// Concurrent calls share one task, including failure. A pending accept that ignores listener close remains
    /// owned and awaited. Every result close is attempted even when another close faults. No caller token can
    /// cancel this join. The caller must separately stop/contain stalls and join registered connection handlers.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        RequireOutsideProcedure();
        var dispatch = Gate();
        Task disposal;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _closed = true;
            disposal = DisposeCoreAsync(dispatch.Task, _pending);
            _disposal = disposal;
        }
        dispatch.SetResult();
        return new(disposal);
    }

    private async Task AcceptCoreAsync(PendingAccept pending, Task dispatch, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        T? result = null;
        var rejected = false;
        try
        {
            await using (token.UnsafeRegister(static state => ((SupervisionAcceptOwnership<T>)state!).StopAccepting(), this))
            {
                bool closed;
                lock (_gate) closed = _closed;
                if (closed || token.IsCancellationRequested) rejected = true;
                else
                {
                    // The original task is awaited directly; synchronous delegate exceptions are caught too.
                    var previous = CurrentProcedure.Value;
                    CurrentProcedure.Value = new(this, previous);
                    try
                    {
                        var actual = _acceptActual(token);
                        if (actual is null) { MarkFailed(); rejected = true; }
                        else result = await actual.ConfigureAwait(false);
                    }
                    finally { CurrentProcedure.Value = previous; }
                    if (result is null) { MarkFailed(); rejected = true; }
                }
            } // Join cancellation callback bookkeeping before any result publication.
        }
        catch (SupervisionAcceptShutdownException)
        {
            lock (_gate)
            {
                if (!_closed && !token.IsCancellationRequested) _failed = true;
                _closed = true;
            }
            rejected = true;
        }
        catch (Exception)
        {
            MarkFailed();
            rejected = true;
        }

        ResultEntry? returned = null;
        lock (_gate)
        {
            if (result is not null)
            {
                if (_known.TryGetValue(result, out returned))
                {
                    _failed = true;
                    _closed = true;
                    rejected = true;
                }
                else
                {
                    returned = new(result);
                    _known.Add(result, returned);
                    _live.Add(result, returned);
                }
            }
            rejected |= _closed || token.IsCancellationRequested;
            if (!rejected)
            {
                _pending = null;
                pending.Completion.SetResult(result!); // RCAA prevents caller callbacks under this lock.
                return;
            }
            _closed = true;
        }

        try { await EnsureListenerClose().ConfigureAwait(false); }
        catch (Exception) { MarkFailed(); }
        if (returned is not null)
        {
            try { await ReleaseAsync(returned.Result).ConfigureAwait(false); }
            catch (Exception) { MarkFailed(); }
        }
        lock (_gate)
        {
            _pending = null;
            pending.Completion.SetException(Rejected());
        }
    }

    private void StopAccepting()
    {
        lock (_gate) _closed = true;
        _ = EnsureListenerClose(); // Registered and joined by pending accept and/or final disposal.
    }

    private void MarkFailed() { lock (_gate) { _failed = true; _closed = true; } }

    private Task EnsureListenerClose()
    {
        TaskCompletionSource? dispatch = null;
        Task task;
        lock (_gate)
        {
            _closed = true;
            if (_listenerClose is null)
            {
                dispatch = Gate();
                _listenerClose = CloseListenerCoreAsync(dispatch.Task);
            }
            task = _listenerClose;
        }
        dispatch?.SetResult();
        return task;
    }

    private async Task CloseListenerCoreAsync(Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        var previous = CurrentProcedure.Value;
        CurrentProcedure.Value = new(this, previous);
        try { _closeListenerActual(); }
        catch (Exception) { MarkFailed(); throw Rejected(); }
        finally { CurrentProcedure.Value = previous; }
    }

    // Called only under _gate; no supplied delegate can run until the returned dispatch gate is released.
    private Task RegisterClose(ResultEntry entry, out TaskCompletionSource? dispatch)
    {
        dispatch = null;
        if (entry.Close is null)
        {
            dispatch = Gate();
            entry.Close = CloseResultCoreAsync(entry, dispatch.Task);
        }
        return entry.Close;
    }

    private async Task CloseResultCoreAsync(ResultEntry entry, Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        var previous = CurrentProcedure.Value;
        CurrentProcedure.Value = new(this, previous);
        try { await _closeAcceptedActual(entry.Result).ConfigureAwait(false); }
        catch (Exception) { MarkFailed(); throw Rejected(); }
        finally { CurrentProcedure.Value = previous; }
        // The live entry is pruned only after THIS task completed, never while its final frame is still owned.
    }

    private async Task DisposeCoreAsync(Task dispatch, PendingAccept? pending)
    {
        await dispatch.ConfigureAwait(false);
        try { await CloseAdmissionAsync().ConfigureAwait(false); }
        catch (Exception) { MarkFailed(); }
        if (pending is not null)
        {
            // Observe the public rejection too, so a caller that forgot its task creates no detached fault.
            try { await pending.Completion.Task.ConfigureAwait(false); }
            catch (Exception) { }
            await pending.Execution.ConfigureAwait(false);
        }

        var gates = new List<TaskCompletionSource>();
        Task[] closes;
        lock (_gate)
        {
            closes = _live.Values.Select(entry =>
            {
                var close = RegisterClose(entry, out var start);
                if (start is not null) gates.Add(start);
                return close;
            }).ToArray();
        }
        foreach (var start in gates) start.SetResult();
        try { await Task.WhenAll(closes).ConfigureAwait(false); }
        catch (Exception) { MarkFailed(); }
        lock (_gate) if (_failed) throw Rejected();
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The protected accept ownership operation was rejected.");

    private void RequireOutsideProcedure()
    {
        for (var scope = CurrentProcedure.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Owner, this)) throw Rejected();
    }

    private sealed record ProcedureScope(SupervisionAcceptOwnership<T> Owner, ProcedureScope? Parent);

    // Called under _gate. Weak history retains shared release completion; only fully joined successes free slots.
    private void PruneClosed()
    {
        foreach (var result in _live.Where(static row => row.Value.Close?.IsCompletedSuccessfully == true)
            .Select(static row => row.Key).ToArray()) _live.Remove(result);
    }

    private sealed class PendingAccept
    {
        internal TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Execution { get; set; } = Task.CompletedTask;
    }

    private sealed class ResultEntry(T result)
    {
        internal T Result { get; } = result;
        internal Task? Close { get; set; }
    }
}

/// <summary>Sanitized procedure marker for an actual accept intentionally interrupted by its owner.</summary>
/// <remarks>
/// The native accept adapter may use this only for its known listener-close interruption or original-token
/// cancellation, after joining the actual operation and closing any unpublished accepted resource. It carries
/// no native, authentication, admission or settlement fact. The generic owner treats a premature marker as
/// failure; arbitrary I/O, identity and workspace faults must never be translated into this marker.
/// </remarks>
internal sealed class SupervisionAcceptShutdownException : Exception
{
    /// <summary>Creates the fixed shutdown marker without caller data, fields or an inner exception.</summary>
    internal SupervisionAcceptShutdownException()
        : base("The actual accept was intentionally interrupted by owner shutdown.") { }
}
