using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Testing;

/// <summary>Identifies the phase in which a durable host scenario stopped waiting.</summary>
public enum DurableScenarioPhase
{
    /// <summary>The non-mutating provider health assessment.</summary>
    Health = 0,
    /// <summary>The authoritative provider pump admission attempt.</summary>
    Pump = 1,
}

/// <summary>Identifies which scenario wait budget expired.</summary>
public enum DurableScenarioTimeoutReason
{
    /// <summary>The budget for one observation expired.</summary>
    Observation = 0,
    /// <summary>The shared budget for this scenario instance expired.</summary>
    Overall = 1,
}

/// <summary>A successfully published health assessment used by later pump calls.</summary>
/// <param name="Sequence">Publication order among successful assessments on this scenario.</param>
/// <param name="Snapshot">The exact production health snapshot returned by the provider.</param>
public sealed record DurableScenarioHealthAssessment(long Sequence, DurableRuntimeHealthSnapshot Snapshot);

/// <summary>
/// Identifies one provider admission call, including an eventual outcome after a scenario wait times out.
/// </summary>
/// <remarks>
/// The request and assessment are exact references. <see cref="Completion"/> is a repeat-awaitable task: a returned
/// attempt describes this invocation only, and a fault after execution starts can still require provider recovery.
/// The scenario retains handles until <see cref="DurableHostScenario.ClearCompletedPumpInvocations"/> removes them.
/// </remarks>
public sealed class DurableScenarioPumpInvocation
{
    private readonly TaskCompletionSource<DurableRuntimePumpAttempt> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _callerCancellationToken;

    internal DurableScenarioPumpInvocation(
        long sequence,
        DurableRuntimePumpRequest request,
        DurableScenarioHealthAssessment assessment,
        CancellationToken callerCancellationToken)
    {
        Sequence = sequence;
        Request = request;
        Assessment = assessment;
        _callerCancellationToken = callerCancellationToken;
    }

    /// <summary>Gets the invocation-start order within its scenario.</summary>
    public long Sequence { get; }

    /// <summary>Gets the exact bounded request passed to provider admission.</summary>
    public DurableRuntimePumpRequest Request { get; }

    /// <summary>Gets the assessment captured before this call; it is advisory, not an admission gate.</summary>
    public DurableScenarioHealthAssessment Assessment { get; }

    /// <summary>Gets the eventual provider attempt or its cancellation or exception.</summary>
    public Task<DurableRuntimePumpAttempt> Completion => _completion.Task;

    internal void SetResult(DurableRuntimePumpAttempt attempt) => _completion.TrySetResult(attempt);

    internal void SetException(Exception exception)
    {
        if (exception is OperationCanceledException cancellation &&
            _callerCancellationToken.IsCancellationRequested &&
            cancellation.CancellationToken == _callerCancellationToken)
        {
            _completion.TrySetCanceled(_callerCancellationToken);
        }
        else
        {
            _completion.TrySetException(exception);
        }
    }
}

/// <summary>Reports that a scenario stopped waiting without inventing a provider outcome.</summary>
/// <remarks>
/// A pump timeout after admission starts includes <see cref="Invocation"/>. Await its
/// <see cref="DurableScenarioPumpInvocation.Completion"/> before releasing provider resources. A health timeout or a
/// timeout before admission has no invocation handle. A timeout does not cancel the provider call.
/// </remarks>
public sealed class DurableScenarioTimeoutException : TimeoutException
{
    internal DurableScenarioTimeoutException(
        DurableScenarioPhase phase,
        DurableScenarioTimeoutReason reason,
        DurableScenarioPumpInvocation? invocation)
        : base($"{phase} scenario {reason.ToString().ToLowerInvariant()} deadline expired.")
    {
        Phase = phase;
        Reason = reason;
        Invocation = invocation;
    }

    /// <summary>Gets the phase whose wait expired.</summary>
    public DurableScenarioPhase Phase { get; }

    /// <summary>Gets the expired budget.</summary>
    public DurableScenarioTimeoutReason Reason { get; }

    /// <summary>Gets the started provider pump invocation, if any.</summary>
    public DurableScenarioPumpInvocation? Invocation { get; }

    /// <summary>Gets whether provider pump admission was invoked.</summary>
    public bool InvocationStarted => Invocation is not null;

    /// <summary>Gets whether provider execution status remains unknown until the invocation is observed.</summary>
    public bool ExecutionStatusUnknown => Invocation is not null;
}

/// <summary>
/// Composes a health observation and one or more authoritative pump admissions for deterministic host tests.
/// </summary>
/// <remarks>
/// The latest successfully published health snapshot is advisory. It never gates admission. One overall monotonic
/// budget begins with the first health assessment and never resets; construct a new scenario for a new budget.
/// The supplied <see cref="TimeProvider"/> must provide monotonic timestamps through
/// <see cref="TimeProvider.GetTimestamp"/>. The scenario wait deadline never cancels provider work: only the caller's
/// cancellation token reaches provider methods. A scenario retains pump handles and their shallow request references
/// until <see cref="ClearCompletedPumpInvocations"/> is called or the scenario is released. Await timed-out handles
/// before releasing provider or test resources. This helper does not implement provider persistence or retry policy.
/// </remarks>
public sealed class DurableHostScenario
{
    private readonly object _gate = new();
    private readonly IDurableRuntimeHealth _health;
    private readonly IDurableRuntimePumpAdmission _admission;
    private readonly DurableRuntimePumpRequest _request;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _observationTimeout;
    private readonly TimeSpan _overallTimeout;
    private readonly List<DurableScenarioPumpInvocation> _invocations = [];
    private bool _started;
    private long _overallStartedAt;
    private long _assessmentSequence;
    private long _pumpSequence;
    private DurableScenarioHealthAssessment? _latestAssessment;

    /// <summary>Creates a scenario with explicit production providers, request, and clock.</summary>
    /// <param name="health">The provider health observer.</param>
    /// <param name="admission">The authoritative admission-aware pump.</param>
    /// <param name="request">The bounded request passed unchanged to each pump invocation.</param>
    /// <param name="timeProvider">A clock with monotonic timestamps; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="observationTimeout">Positive budget for each health or pump observation; defaults to 30 seconds.</param>
    /// <param name="overallTimeout">Positive shared scenario budget; defaults to two minutes.</param>
    /// <exception cref="ArgumentOutOfRangeException">A supplied timeout is not positive.</exception>
    public DurableHostScenario(
        IDurableRuntimeHealth health,
        IDurableRuntimePumpAdmission admission,
        DurableRuntimePumpRequest request,
        TimeProvider? timeProvider = null,
        TimeSpan? observationTimeout = null,
        TimeSpan? overallTimeout = null)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observationTimeout = observationTimeout ?? TimeSpan.FromSeconds(30);
        _overallTimeout = overallTimeout ?? TimeSpan.FromMinutes(2);
        if (_observationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(observationTimeout));
        }
        if (_overallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(overallTimeout));
        }
    }

    /// <summary>Gets an atomic, start-ordered snapshot of retained provider pump invocations.</summary>
    public IReadOnlyList<DurableScenarioPumpInvocation> PumpInvocations
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_invocations.ToArray());
            }
        }
    }

    /// <summary>Gets the most recently published successful health assessment, if any.</summary>
    public DurableScenarioHealthAssessment? LatestAssessment
    {
        get
        {
            lock (_gate)
            {
                return _latestAssessment;
            }
        }
    }

    /// <summary>
    /// Reads and publishes one health snapshot if its provider result wins the method's terminal decision.
    /// </summary>
    /// <remarks>
    /// Failed, canceled, or timed-out reads do not replace the latest assessment. A late provider result after timeout
    /// is observed for faults but is never published. Concurrent successful reads publish in method terminal-decision
    /// order under the scenario lock, even when the later publication was started first. A provider task finishing
    /// does not by itself publish an assessment. A health timeout has no pump invocation handle.
    /// </remarks>
    /// <exception cref="DurableScenarioTimeoutException">An observation or overall budget expired.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the wait or the provider canceled.</exception>
    public async Task<DurableScenarioHealthAssessment> AssessHealthAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            if (!_started)
            {
                _started = true;
                _overallStartedAt = startedAt;
            }
        }

        CheckBeforeInvocation(startedAt, DurableScenarioPhase.Health, cancellationToken);
        Task<DurableRuntimeHealthSnapshot> task = _health.GetAsync(cancellationToken).AsTask();
        DurableRuntimeHealthSnapshot snapshot;
        try
        {
            snapshot = await WaitWithinBudgetAsync(
                task, startedAt, DurableScenarioPhase.Health, invocation: null, cancellationToken).ConfigureAwait(false);
        }
        catch (DurableScenarioTimeoutException)
        {
            ObserveLateFault(task);
            throw;
        }
        catch (OperationCanceledException)
        {
            ObserveLateFault(task);
            throw;
        }

        if (snapshot is null)
        {
            throw new InvalidOperationException("The health provider returned a null snapshot.");
        }

        lock (_gate)
        {
            var assessment = new DurableScenarioHealthAssessment(++_assessmentSequence, snapshot);
            _latestAssessment = assessment;
            return assessment;
        }
    }

    /// <summary>
    /// Invokes authoritative provider admission once after a completed assessment and returns its exact attempt.
    /// </summary>
    /// <remarks>
    /// <see cref="DurableRuntimeHealthSnapshot.CanAttemptPump"/> is advisory and never gates this method. A timeout
    /// or caller cancellation after invocation
    /// starts leaves a retained handle in <see cref="PumpInvocations"/>. Await its completion before releasing provider
    /// resources; do not infer a refusal or retry safety from a local timeout.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No successful health assessment has been published.</exception>
    /// <exception cref="DurableScenarioTimeoutException">An observation or overall budget expired.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the wait or the provider canceled.</exception>
    public async Task<DurableRuntimePumpAttempt> RunDirectPumpOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _timeProvider.GetTimestamp();
        DurableScenarioHealthAssessment assessment;
        lock (_gate)
        {
            assessment = _latestAssessment ?? throw new InvalidOperationException(
                "Call AssessHealthAsync successfully before running a direct pump pass.");
        }
        CheckBeforeInvocation(startedAt, DurableScenarioPhase.Pump, cancellationToken);

        DurableScenarioPumpInvocation invocation;
        lock (_gate)
        {
            invocation = new DurableScenarioPumpInvocation(++_pumpSequence, _request, assessment, cancellationToken);
            _invocations.Add(invocation);
        }

        Task<DurableRuntimePumpAttempt> providerTask;
        try
        {
            providerTask = _admission.TryRunOnceAsync(_request, cancellationToken).AsTask();
        }
        catch (Exception exception)
        {
            invocation.SetException(exception);
            throw;
        }

        _ = BridgeCompletionAsync(providerTask, invocation);
        return await WaitWithinBudgetAsync(
            invocation.Completion, startedAt, DurableScenarioPhase.Pump, invocation, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically removes terminal pump handles while keeping in-flight handles reachable.
    /// </summary>
    /// <returns>The number of completed handles removed.</returns>
    /// <remarks>
    /// The caller retains any handle already obtained from a timeout or prior snapshot. Call this after awaiting
    /// timed-out handles when reusing a scenario, so exact request references do not remain in its history.
    /// </remarks>
    public int ClearCompletedPumpInvocations()
    {
        lock (_gate)
        {
            return _invocations.RemoveAll(static invocation => invocation.Completion.IsCompleted);
        }
    }

    private void CheckBeforeInvocation(
        long observationStartedAt,
        DurableScenarioPhase phase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_timeProvider.GetElapsedTime(_overallStartedAt) >= _overallTimeout)
        {
            throw new DurableScenarioTimeoutException(phase, DurableScenarioTimeoutReason.Overall, null);
        }
        if (_timeProvider.GetElapsedTime(observationStartedAt) >= _observationTimeout)
        {
            throw new DurableScenarioTimeoutException(phase, DurableScenarioTimeoutReason.Observation, null);
        }
    }

    private async Task<T> WaitWithinBudgetAsync<T>(
        Task<T> task,
        long observationStartedAt,
        DurableScenarioPhase phase,
        DurableScenarioPumpInvocation? invocation,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            // This check is the terminal linearization point. A completed task wins even at the deadline.
            if (task.IsCompleted)
            {
                return await task.ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var overallRemaining = _overallTimeout - _timeProvider.GetElapsedTime(_overallStartedAt);
            if (overallRemaining <= TimeSpan.Zero)
            {
                throw new DurableScenarioTimeoutException(phase, DurableScenarioTimeoutReason.Overall, invocation);
            }
            var observationRemaining = _observationTimeout - _timeProvider.GetElapsedTime(observationStartedAt);
            if (observationRemaining <= TimeSpan.Zero)
            {
                throw new DurableScenarioTimeoutException(phase, DurableScenarioTimeoutReason.Observation, invocation);
            }

            var remaining = overallRemaining < observationRemaining ? overallRemaining : observationRemaining;
            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(remaining, _timeProvider, delayCancellation.Token);
            await Task.WhenAny(task, delay).ConfigureAwait(false);
            delayCancellation.Cancel();
        }
    }

    private static async Task BridgeCompletionAsync(
        Task<DurableRuntimePumpAttempt> providerTask,
        DurableScenarioPumpInvocation invocation)
    {
        try
        {
            var attempt = await providerTask.ConfigureAwait(false);
            invocation.SetResult(attempt ?? throw new InvalidOperationException(
                "The pump admission provider returned a null attempt."));
        }
        catch (Exception exception)
        {
            invocation.SetException(exception);
        }
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
