namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed failure reasons for the ownership ledger; none describes physical OS exit.</summary>
internal enum SupervisionWorkFailure
{
    /// <summary>No failure has been recorded.</summary>
    None,
    /// <summary>An owned workload procedure failed.</summary>
    WorkloadFailed,
    /// <summary>An owned control handler failed.</summary>
    ControlFailed,
    /// <summary>A reserved workload dispatch failed or remained ambiguous.</summary>
    DispatchFailed,
}

/// <summary>Tracks pending workloads independently from active control handlers.</summary>
/// <remarks>
/// Register before dispatch, including before asynchronous OS start I/O. Closing a gate is irreversible.
/// A registration remains owned until its explicit completion, even after a canceled join. This ledger
/// creates no admission, process lease, or physical-exit receipt. The root owner must stop actual OS work,
/// settle pending dispatches and output pumps, and only then complete the corresponding registration.
/// Workload joining excludes control handlers so a stop/wait handler can await it without awaiting itself.
/// </remarks>
internal sealed class SupervisionWorkRegistry
{
    /// <summary>Protected upper bound for simultaneously owned workload registrations.</summary>
    internal const int MaximumActiveWorkloads = 64;

    /// <summary>Protected upper bound for simultaneously owned control handlers.</summary>
    internal const int MaximumActiveControls = 32;

    private readonly object _gate = new();
    private readonly int _maximumWorkloads;
    private readonly int _maximumControls;
    private readonly HashSet<Workload> _workloads = [];
    private readonly HashSet<Control> _controls = [];
    private TaskCompletionSource _workloadsDrained = CompletedSource();
    private TaskCompletionSource _controlsDrained = CompletedSource();
    private bool _workloadsClosed;
    private bool _controlsClosed;
    private SupervisionWorkFailure _failure;

    /// <summary>Creates an empty ledger with protected caps, optionally reduced by the owning host.</summary>
    /// <param name="maximumActiveWorkloads">Positive workload cap, at most <see cref="MaximumActiveWorkloads"/>.</param>
    /// <param name="maximumActiveControls">Positive control cap, at most <see cref="MaximumActiveControls"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A cap is nonpositive or raises a protected maximum.</exception>
    internal SupervisionWorkRegistry(
        int maximumActiveWorkloads = MaximumActiveWorkloads,
        int maximumActiveControls = MaximumActiveControls)
    {
        if (maximumActiveWorkloads is < 1 or > MaximumActiveWorkloads)
            throw new ArgumentOutOfRangeException(nameof(maximumActiveWorkloads));
        if (maximumActiveControls is < 1 or > MaximumActiveControls)
            throw new ArgumentOutOfRangeException(nameof(maximumActiveControls));
        _maximumWorkloads = maximumActiveWorkloads;
        _maximumControls = maximumActiveControls;
    }

    /// <summary>Gets whether further workload registration is permanently rejected.</summary>
    internal bool IsWorkloadAdmissionClosed { get { lock (_gate) return _workloadsClosed; } }

    /// <summary>Gets whether further control-handler registration is permanently rejected.</summary>
    internal bool IsControlAdmissionClosed { get { lock (_gate) return _controlsClosed; } }

    /// <summary>Gets the pending workload count, including starts whose OS outcome is not yet known.</summary>
    internal int ActiveWorkloads { get { lock (_gate) return _workloads.Count; } }

    /// <summary>Gets the active control-handler count; it is excluded from workload joining.</summary>
    internal int ActiveControls { get { lock (_gate) return _controls.Count; } }

    /// <summary>Gets the first recorded failure; later failures cannot replace it.</summary>
    internal SupervisionWorkFailure FirstFailure { get { lock (_gate) return _failure; } }

    /// <summary>Gets whether any failure has been latched, independent of registration completion.</summary>
    internal bool IsFailed { get { lock (_gate) return _failure != SupervisionWorkFailure.None; } }

    /// <summary>Gets whether both gates are closed, both sets are empty, and no failure is latched.</summary>
    /// <remarks>
    /// This is current ledger data, not an immutable receipt. A later failure revokes it. The root owner
    /// must independently recheck physical exit, custody and its deadline before final publication.
    /// </remarks>
    internal bool IsSettled
    {
        get
        {
            lock (_gate)
                return _workloadsClosed && _controlsClosed && _workloads.Count == 0
                    && _controls.Count == 0 && _failure == SupervisionWorkFailure.None;
        }
    }

    /// <summary>Registers workload ownership before the caller dispatches any work.</summary>
    /// <returns>A handle that can be explicitly completed exactly once.</returns>
    /// <exception cref="InvalidOperationException">Workload admission is closed or its active cap is reached.</exception>
    /// <remarks>
    /// This method does not dispatch. A pending start remains owned across gate closure; the dispatcher
    /// must cooperate with the owner's stop path rather than treating cancellation as failed OS start.
    /// Rejection does not erase existing registrations or latch an unrelated execution failure.
    /// </remarks>
    internal Workload BeginWorkload()
    {
        lock (_gate)
        {
            if (_workloadsClosed || _workloads.Count >= _maximumWorkloads)
                throw new InvalidOperationException("Workload registration is closed or at its protected limit.");
            if (_workloads.Count == 0)
                _workloadsDrained = PendingSource();
            var workload = new Workload(this);
            _workloads.Add(workload);
            return workload;
        }
    }

    /// <summary>Registers a control handler, including stop/wait handlers while workloads are closing.</summary>
    /// <returns>A handler scope whose completion/disposal releases only control bookkeeping.</returns>
    /// <exception cref="InvalidOperationException">Control admission is closed or its active cap is reached.</exception>
    internal Control BeginControl()
    {
        lock (_gate)
        {
            if (_controlsClosed || _controls.Count >= _maximumControls)
                throw new InvalidOperationException("Control registration is closed or at its protected limit.");
            if (_controls.Count == 0)
                _controlsDrained = PendingSource();
            var control = new Control(this);
            _controls.Add(control);
            return control;
        }
    }

    /// <summary>Records the first failure and closes workload admission irreversibly.</summary>
    /// <remarks>Control admission stays open for stop/wait handlers. Completing handles never clears failure.</remarks>
    /// <param name="failure">One defined non-None reason; no arbitrary exception text is retained.</param>
    /// <returns>True only for the first failure accepted by this ledger.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The reason is undefined or None.</exception>
    internal bool RecordFailure(SupervisionWorkFailure failure)
    {
        if (!Enum.IsDefined(failure) || failure == SupervisionWorkFailure.None)
            throw new ArgumentOutOfRangeException(nameof(failure));
        lock (_gate)
        {
            if (_failure != SupervisionWorkFailure.None)
                return false;
            _failure = failure;
            _workloadsClosed = true;
            return true;
        }
    }

    /// <summary>Closes workload admission and waits only for explicitly completed workloads.</summary>
    /// <param name="cancellationToken">Cancels this wait, without releasing ownership or reopening admission.</param>
    /// <returns>True if the workloads drained and no failure was latched at the final check.</returns>
    /// <remarks>
    /// Control admission remains open. Repeated callers observe the same closed set and may retry after
    /// cancellation. The caller's own control registration is deliberately excluded. No OS stop is issued.
    /// </remarks>
    internal async Task<bool> CloseAndJoinWorkloadsAsync(CancellationToken cancellationToken = default)
    {
        Task drained;
        lock (_gate)
        {
            _workloadsClosed = true;
            drained = _workloadsDrained.Task;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return _failure == SupervisionWorkFailure.None;
    }

    /// <summary>Closes control admission and waits for all control-handler scopes to complete.</summary>
    /// <param name="cancellationToken">Cancels this wait only; registered handlers remain owned.</param>
    /// <returns>True if controls drained and no failure was latched at the final check.</returns>
    /// <remarks>
    /// The server owner calls this after it stops accepting handlers. An active handler must complete its
    /// own scope before awaiting this method; unlike workload join, this includes every active control.
    /// It does not close workload admission or constitute whole-run completion by itself.
    /// </remarks>
    internal async Task<bool> CloseAndJoinControlsAsync(CancellationToken cancellationToken = default)
    {
        Task drained;
        lock (_gate)
        {
            _controlsClosed = true;
            drained = _controlsDrained.Task;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return _failure == SupervisionWorkFailure.None;
    }

    private bool CompleteWorkload(Workload workload)
    {
        lock (_gate)
        {
            if (!_workloads.Remove(workload))
                return false;
            if (_workloads.Count == 0)
                _workloadsDrained.TrySetResult();
            return true;
        }
    }

    private bool CompleteControl(Control control)
    {
        lock (_gate)
        {
            if (!_controls.Remove(control))
                return false;
            if (_controls.Count == 0)
                _controlsDrained.TrySetResult();
            return true;
        }
    }

    private static TaskCompletionSource PendingSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSource()
    {
        var source = PendingSource();
        source.SetResult();
        return source;
    }

    /// <summary>One pending workload registration; intentionally not IDisposable.</summary>
    /// <remarks>Explicit completion is a bookkeeping assertion by the owner, not an OS-exit proof.</remarks>
    internal sealed class Workload
    {
        private readonly SupervisionWorkRegistry _owner;

        /// <summary>Creates bookkeeping for the registry's registration path.</summary>
        /// <param name="owner">The owning registry; constructing a handle alone registers no work.</param>
        internal Workload(SupervisionWorkRegistry owner) => _owner = owner;

        /// <summary>Explicitly completes this registration without changing either admission gate.</summary>
        /// <returns>True once, then false for every replay; another registration is never released.</returns>
        internal bool Complete() => _owner.CompleteWorkload(this);
    }

    /// <summary>One control-handler scope; disposal releases handler bookkeeping only.</summary>
    internal sealed class Control : IDisposable
    {
        private readonly SupervisionWorkRegistry _owner;

        /// <summary>Creates bookkeeping for the registry's control registration path.</summary>
        /// <param name="owner">The owning registry; constructing a scope alone registers no handler.</param>
        internal Control(SupervisionWorkRegistry owner) => _owner = owner;

        /// <summary>Completes this handler without asserting that any workload or OS process exited.</summary>
        /// <returns>True once, then false on replay.</returns>
        internal bool Complete() => _owner.CompleteControl(this);

        /// <summary>Ends this handler scope idempotently; does not close admission or complete workloads.</summary>
        public void Dispose() => Complete();
    }
}
