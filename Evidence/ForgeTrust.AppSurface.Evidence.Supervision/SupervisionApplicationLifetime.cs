using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Coordinates one application start, resource readiness operations, and one joined stop sequence.</summary>
/// <remarks>
/// This data-only coordinator registers its start and readiness operations in the supplied
/// <see cref="SupervisionWorkRegistry"/> and completes each registration only after the callback's original
/// task returns. It never closes or joins the shared registry: a root owner may retain separate workload
/// registrations for native units, controls, or other cleanup that must outlive this coordinator. It creates
/// no process, lease, admission authority, physical-exit evidence, or deadline. The caller supplies the
/// original cleanup token and remains responsible for native checks and the final registry drain.
/// </remarks>
internal sealed class SupervisionApplicationLifetime
{
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _insideProcedure = new();
    private readonly AsyncLocal<bool> _insideDisposer = new();
    private readonly SupervisionWorkRegistry _ledger;
    private readonly CancellationTokenSource _stopCancellation = new();
    private readonly List<Operation> _operations = [];
    private readonly Dictionary<string, Operation> _resources = new(StringComparer.Ordinal);
    private readonly List<Func<CancellationToken, Task>> _disposers = [];
    private readonly List<CancellationToken> _inputTokens = [];
    private Task? _startTask;
    private Task? _stopTask;
    private CancellationToken _cleanupToken;
    private bool _startAttempted;
    private bool _startSucceeded;
    private bool _closed;
    private bool _failed;
    private bool _cleanupFailed;
    private bool _startPendingAtClose;
    private bool _cleanupPhaseReady;
    private bool _joined;
    private int _stopCallsReturned;
    private int _disposersReturned;

    /// <summary>Creates a coordinator whose operations register in the owner's shared workload ledger.</summary>
    /// <param name="ledger">The registry used to reserve each start and readiness operation.</param>
    /// <exception cref="ArgumentNullException">The ledger is null.</exception>
    internal SupervisionApplicationLifetime(SupervisionWorkRegistry ledger) =>
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <summary>Gets whether the stop callback sequence and every owned operation have actually returned.</summary>
    /// <remarks>This reports managed task completion only; it does not prove native process or lease settlement.</remarks>
    internal bool Joined { get { lock (_gate) return _joined; } }

    /// <summary>Gets whether any operation, callback, or caller token has failed or been canceled.</summary>
    /// <remarks>Failure is sticky and local to this coordinator; it is never recorded in the shared ledger.</remarks>
    internal bool Failed
    {
        get
        {
            lock (_gate)
            {
                if (_inputTokens.Any(token => token.IsCancellationRequested))
                {
                    _failed = true;
                    _closed = true;
                }
                if (_cleanupToken.IsCancellationRequested)
                {
                    _failed = true;
                    _cleanupFailed = true;
                    _closed = true;
                }
                return _failed;
            }
        }
    }

    /// <summary>Registers one local disposer, to be invoked after start and readiness tasks have joined.</summary>
    /// <remarks>
    /// Register disposers before the first start attempt. A disposer may call
    /// <see cref="RequireJoinedCleanupPhase"/> from its own callback. It must not await this coordinator's
    /// stop task; reentrant lifecycle operations are rejected to prevent self-join.
    /// </remarks>
    /// <param name="disposer">The cleanup callback, which receives the original stop caller's token.</param>
    /// <exception cref="ArgumentNullException">The disposer is null.</exception>
    /// <exception cref="EvidenceAdmissionException">Startup or cleanup has already closed registration.</exception>
    internal void RegisterDisposer(Func<CancellationToken, Task> disposer)
    {
        ArgumentNullException.ThrowIfNull(disposer);
        RequireExternalCaller();
        lock (_gate)
        {
            ObserveInputCancellationLocked();
            if (_closed || _startAttempted || _stopTask is not null)
                throw Rejected();
            _disposers.Add(disposer);
        }
    }

    /// <summary>Consumes the single start attempt, reserves it in the ledger, then dispatches the callback.</summary>
    /// <remarks>
    /// Reservation precedes callback invocation and any asynchronous I/O. The callback receives a token linked
    /// to its original caller token and this coordinator's irreversible stop signal. A returned task remains
    /// owned through its actual completion even when it ignores cancellation.
    /// </remarks>
    /// <typeparam name="T">The callback's start result.</typeparam>
    /// <param name="start">The single start procedure.</param>
    /// <param name="token">The original start cancellation token.</param>
    /// <returns>The task representing the full original callback lifetime.</returns>
    /// <exception cref="EvidenceAdmissionException">A start was already attempted, or admission has closed.</exception>
    internal Task<T> StartAsync<T>(Func<CancellationToken, Task<T>> start, CancellationToken token)
    {
        RequireExternalCaller();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Operation? operation = null;
        CancellationTokenSource? linkedCancellation = null;
        SupervisionWorkRegistry.Workload? workload = null;
        lock (_gate)
        {
            if (_startAttempted)
                throw Rejected();

            _startAttempted = true;
            ObserveInputCancellationLocked();
            if (_closed)
                throw Rejected();

            try
            {
                workload = _ledger.BeginWorkload();
                linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _stopCancellation.Token);
                operation = new Operation(workload, linkedCancellation, completion.Task);
                _startTask = completion.Task;
                _operations.Add(operation);
                RegisterInputTokenLocked(token);
            }
            catch (Exception)
            {
                workload?.Complete();
                linkedCancellation?.Dispose();
                _failed = true;
                _closed = true;
                _startTask = completion.Task;
                completion.TrySetException(Rejected());
                return completion.Task;
            }
        }

        _ = RunOperationAsync(start, linkedCancellation!.Token, operation!, completion, isStart: true);
        return completion.Task;
    }

    /// <summary>Starts one readiness wait after successful startup, with one wait allowed for each resource ID.</summary>
    /// <remarks>
    /// Resource IDs compare ordinally. The complete callback task is retained and joined by stop. A readiness
    /// callback cannot begin before the start callback has returned successfully, and callback errors or token
    /// cancellation latch <see cref="Failed"/> without poisoning the shared workload ledger.
    /// </remarks>
    /// <typeparam name="T">The callback's readiness result.</typeparam>
    /// <param name="resourceId">A nonempty identity unique within this application lifetime.</param>
    /// <param name="wait">The resource readiness procedure.</param>
    /// <param name="token">The original readiness cancellation token.</param>
    /// <returns>The task representing the full original callback lifetime.</returns>
    /// <exception cref="EvidenceAdmissionException">Startup has not succeeded, admission is closed, or this ID was used.</exception>
    internal Task<T> WaitResourceAsync<T>(string resourceId, Func<CancellationToken, Task<T>> wait,
        CancellationToken token)
    {
        RequireExternalCaller();
        if (string.IsNullOrWhiteSpace(resourceId))
            throw Rejected();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Operation? operation = null;
        CancellationTokenSource? linkedCancellation = null;
        SupervisionWorkRegistry.Workload? workload = null;
        lock (_gate)
        {
            ObserveInputCancellationLocked();
            if (_closed || !_startSucceeded || _resources.ContainsKey(resourceId))
                throw Rejected();

            try
            {
                workload = _ledger.BeginWorkload();
                linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _stopCancellation.Token);
                operation = new Operation(workload, linkedCancellation, completion.Task);
                _resources.Add(resourceId, operation);
                _operations.Add(operation);
                RegisterInputTokenLocked(token);
            }
            catch (Exception)
            {
                workload?.Complete();
                linkedCancellation?.Dispose();
                _failed = true;
                _closed = true;
                completion.TrySetException(Rejected());
                return completion.Task;
            }
        }

        _ = RunOperationAsync(wait, linkedCancellation!.Token, operation!, completion, isStart: false);
        return completion.Task;
    }

    /// <summary>Closes this coordinator, shares one stop task, and joins its start and readiness operations.</summary>
    /// <remarks>
    /// The first caller's callback and cleanup token define the one irreversible cleanup attempt; replays return
    /// that exact task. Stop runs before joining owned start/readiness tasks. If startup was still pending when
    /// admission closed, stop runs once more after startup and readiness tasks return to contain a resource that
    /// startup may have created late. Registered disposers run after those joins. Cleanup cancellation never
    /// abandons an original task, starts a background replacement, or resets a deadline.
    /// </remarks>
    /// <param name="stop">The owner-supplied stop callback; it is invoked a second time for a pending start.</param>
    /// <param name="cleanupToken">The original cleanup token passed to stop and registered disposers.</param>
    /// <returns>The shared task for the complete stop, join, and disposal sequence.</returns>
    internal Task StopAndJoinAsync(Func<CancellationToken, Task> stop, CancellationToken cleanupToken)
    {
        RequireExternalCaller();
        TaskCompletionSource completion;
        lock (_gate)
        {
            ObserveInputCancellationLocked();
            _closed = true;
            if (_stopTask is not null)
                return _stopTask;

            _cleanupToken = cleanupToken;
            RegisterInputTokenLocked(cleanupToken);
            _startPendingAtClose = _startTask is { IsCompleted: false };
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }

        _ = RunStopAsync(stop, cleanupToken, completion);
        return completion.Task;
    }

    /// <summary>Requires all start/readiness joins before local disposal may proceed.</summary>
    /// <remarks>
    /// This is an ordering assertion, not a native-exit check. It is valid inside a registered disposer after
    /// pre-disposer joins, including that disposer task itself, and after the stop sequence has completed.
    /// It deliberately does not require the current disposer to have stopped itself.
    /// </remarks>
    /// <exception cref="EvidenceAdmissionException">The stop sequence has not reached its joined cleanup phase.</exception>
    internal void RequireJoinedCleanupPhase()
    {
        if (_insideProcedure.Value && !_insideDisposer.Value)
            throw Rejected();
        lock (_gate)
        {
            ObserveInputCancellationLocked();
            if (!_cleanupPhaseReady)
                throw Rejected();
        }
    }

    private async Task RunOperationAsync<T>(Func<CancellationToken, Task<T>> procedure,
        CancellationToken operationToken, Operation operation, TaskCompletionSource<T> completion, bool isStart)
    {
        var previousProcedure = _insideProcedure.Value;
        _insideProcedure.Value = true;
        var succeeded = false;
        var canceled = false;
        T? result = default;
        try
        {
            operationToken.ThrowIfCancellationRequested();
            var callbackTask = procedure(operationToken) ?? throw Rejected();
            lock (_gate) operation.OriginalTask = callbackTask;
            result = await callbackTask.ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            MarkOperationFailed();
        }
        catch (Exception)
        {
            MarkOperationFailed();
        }

        if (!operation.Workload.Complete())
        {
            succeeded = false;
            canceled = false;
            MarkCleanupFailed();
        }
        try { operation.LinkedCancellation.Dispose(); }
        catch (Exception)
        {
            succeeded = false;
            canceled = false;
            MarkCleanupFailed();
        }
        lock (_gate)
        {
            if (isStart && succeeded)
                _startSucceeded = true;
        }
        _insideProcedure.Value = previousProcedure;

        if (succeeded)
            completion.TrySetResult(result!);
        else if (canceled)
            completion.TrySetCanceled(operationToken);
        else
            completion.TrySetException(Rejected());
    }

    private async Task RunStopAsync(Func<CancellationToken, Task> stop, CancellationToken cleanupToken,
        TaskCompletionSource completion)
    {
        try
        {
            try { _stopCancellation.Cancel(); }
            catch (Exception) { MarkCleanupFailed(); }

            await InvokeStopAsync(stop, cleanupToken).ConfigureAwait(false);
            await JoinOperationsAsync().ConfigureAwait(false);
            if (_startPendingAtClose)
                await InvokeStopAsync(stop, cleanupToken).ConfigureAwait(false);

            Func<CancellationToken, Task>[] disposers;
            lock (_gate)
            {
                _cleanupPhaseReady = true;
                disposers = _disposers.ToArray();
            }

            foreach (var disposer in disposers)
                await InvokeDisposerAsync(disposer, cleanupToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            MarkCleanupFailed();
            // Every user callback is isolated above; this catch keeps an unexpected bookkeeping failure sticky.
        }
        finally
        {
            bool failed;
            lock (_gate)
            {
                ObserveInputCancellationLocked();
                var joined = _stopCallsReturned == (_startPendingAtClose ? 2 : 1)
                    && _operations.All(operation => operation.CompletionTask.IsCompleted)
                    && _disposersReturned == _disposers.Count;
                _joined = joined;
                if (!joined)
                {
                    _failed = true;
                    _cleanupFailed = true;
                }
                failed = _cleanupFailed;
            }

            _stopCancellation.Dispose();

            if (failed)
                completion.TrySetException(Rejected());
            else
                completion.TrySetResult();
        }
    }

    private async Task InvokeStopAsync(Func<CancellationToken, Task> stop, CancellationToken cleanupToken)
    {
        var previousProcedure = _insideProcedure.Value;
        var callbackInvoked = false;
        _insideProcedure.Value = true;
        try
        {
            if (stop is null)
                throw Rejected();
            callbackInvoked = true;
            var callbackTask = stop(cleanupToken) ?? throw Rejected();
            await callbackTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            MarkCleanupFailed();
        }
        finally
        {
            if (callbackInvoked)
            {
                lock (_gate) _stopCallsReturned++;
            }
            _insideProcedure.Value = previousProcedure;
        }
    }

    private async Task JoinOperationsAsync()
    {
        Task[] operations;
        lock (_gate)
            operations = _operations.Select(operation => operation.CompletionTask).ToArray();

        foreach (var operation in operations)
        {
            try { await operation.ConfigureAwait(false); }
            catch (Exception) { MarkOperationFailed(); }
        }
    }

    private async Task InvokeDisposerAsync(Func<CancellationToken, Task> disposer, CancellationToken cleanupToken)
    {
        var previousProcedure = _insideProcedure.Value;
        var previousDisposer = _insideDisposer.Value;
        _insideProcedure.Value = true;
        _insideDisposer.Value = true;
        try
        {
            var callbackTask = disposer(cleanupToken) ?? throw Rejected();
            await callbackTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            MarkCleanupFailed();
        }
        finally
        {
            lock (_gate) _disposersReturned++;
            _insideDisposer.Value = previousDisposer;
            _insideProcedure.Value = previousProcedure;
        }
    }

    private void RegisterInputTokenLocked(CancellationToken token) => _inputTokens.Add(token);

    private void ObserveInputCancellationLocked()
    {
        if (_inputTokens.Any(token => token.IsCancellationRequested))
        {
            _failed = true;
            _closed = true;
        }
        if (_cleanupToken.IsCancellationRequested)
        {
            _failed = true;
            _cleanupFailed = true;
            _closed = true;
        }
    }

    private void MarkOperationFailed()
    {
        lock (_gate)
        {
            _failed = true;
            _closed = true;
        }
    }

    private void MarkCleanupFailed()
    {
        lock (_gate)
        {
            _failed = true;
            _cleanupFailed = true;
            _closed = true;
        }
    }

    private void RequireExternalCaller()
    {
        if (_insideProcedure.Value)
            throw Rejected();
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The application lifetime operation was rejected.");

    private sealed class Operation(SupervisionWorkRegistry.Workload workload,
        CancellationTokenSource linkedCancellation, Task completionTask)
    {
        internal SupervisionWorkRegistry.Workload Workload { get; } = workload;
        internal CancellationTokenSource LinkedCancellation { get; } = linkedCancellation;
        internal Task CompletionTask { get; } = completionTask;
        internal Task? OriginalTask { get; set; }
    }

}
