using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Flow.DurableTask;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Workers.DurableTask;

/// <summary>
/// Maps durable worker projection contract results into Durable Task-facing decisions.
/// </summary>
/// <typeparam name="TWork">Type of work carried by the chain.</typeparam>
/// <typeparam name="TResult">Type of terminal execution result.</typeparam>
/// <typeparam name="TProjection">Type of visible projection repaired from terminal facts.</typeparam>
/// <remarks>
/// The runner is an adapter, not a hosted runtime. It calls the app-owned projection contract and returns decisions for
/// host-owned Durable Task orchestration code to interpret. It never invokes
/// <see cref="IDurableWorkerExecutor{TWork,TResult}"/> from projection repair methods. The expected stage order is
/// <see cref="IDurableTaskWorkerChainRunner{TWork,TResult,TProjection}.TryClaimAsync"/> before executor activity,
/// <see cref="IDurableTaskWorkerChainRunner{TWork,TResult,TProjection}.CompleteAsync"/> after terminal executor
/// activity, then <see cref="IDurableTaskWorkerChainRunner{TWork,TResult,TProjection}.ReconcileProjectionAsync"/> for
/// visible-state repair. Retry policies carried by decisions are advisory metadata; hosts translate them into Durable
/// Task retry options, timers, or activity scheduling behavior.
/// </remarks>
public interface IDurableTaskWorkerChainRunner<TWork, TResult, TProjection>
{
    /// <summary>
    /// Attempts to claim work and maps the claim result to a Durable Task-facing decision.
    /// </summary>
    /// <param name="contract">App-owned projection contract.</param>
    /// <param name="work">Typed work payload.</param>
    /// <param name="correlation">Correlation identifiers for the claim.</param>
    /// <param name="cancellationToken">Token that cancels the claim.</param>
    /// <returns>
    /// A durable decision. Valid claim outcomes are claimed, already-completed, noop, stale-fence, conflict, and
    /// unrecoverable. Claimed schedules executor activity; duplicate, noop, stale, and failure outcomes do not.
    /// </returns>
    ValueTask<DurableTaskWorkerDecision<TWork, TResult, TProjection>> TryClaimAsync(
        IDurableWorkerProjectionContract<TWork, TResult, TProjection> contract,
        TWork work,
        DurableWorkerCorrelation correlation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records completion and maps the terminal fact to projection repair or terminal decisions.
    /// </summary>
    /// <param name="contract">App-owned projection contract.</param>
    /// <param name="work">Typed work payload.</param>
    /// <param name="result">Terminal result produced by executor activity.</param>
    /// <param name="correlation">Correlation identifiers for the completion fact.</param>
    /// <param name="cancellationToken">Token that cancels completion recording.</param>
    /// <returns>
    /// A durable decision. Valid completion outcomes are completed, already-completed, noop, stale-fence, conflict, and
    /// unrecoverable. Fresh completion schedules projection repair; duplicate and noop completion do not.
    /// </returns>
    ValueTask<DurableTaskWorkerDecision<TWork, TResult, TProjection>> CompleteAsync(
        IDurableWorkerProjectionContract<TWork, TResult, TProjection> contract,
        TWork work,
        TResult result,
        DurableWorkerCorrelation correlation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Repairs projection state from a terminal fact and maps the result to a Durable Task-facing decision.
    /// </summary>
    /// <param name="contract">App-owned projection contract.</param>
    /// <param name="work">Typed work payload associated with the terminal fact.</param>
    /// <param name="result">Terminal result used to repair projection state.</param>
    /// <param name="correlation">Correlation identifiers for the repair attempt.</param>
    /// <param name="cancellationToken">Token that cancels projection repair.</param>
    /// <returns>
    /// A durable decision. Valid projection outcomes are reconciled, noop, stale-fence, conflict, and unrecoverable.
    /// Reconciled carries a projection payload; noop completes without a projection payload; projection repair never maps
    /// to executor scheduling.
    /// </returns>
    ValueTask<DurableTaskWorkerDecision<TWork, TResult, TProjection>> ReconcileProjectionAsync(
        IDurableWorkerProjectionContract<TWork, TResult, TProjection> contract,
        TWork work,
        TResult result,
        DurableWorkerCorrelation correlation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an external-event wait decision for host-owned Durable Task orchestration code.
    /// </summary>
    /// <param name="correlation">Correlation identifiers for the wait.</param>
    /// <param name="eventName">External event name expected by the host.</param>
    /// <param name="timeout">Optional durable timeout metadata.</param>
    /// <returns>A wait-for-external-event decision.</returns>
    DurableTaskWorkerDecision<TWork, TResult, TProjection> WaitForExternalEvent(
        DurableWorkerCorrelation correlation,
        string eventName,
        FlowTimeout? timeout = null);

    /// <summary>
    /// Creates a timeout decision for a wait branch that expired.
    /// </summary>
    /// <param name="correlation">Correlation identifiers for the timed-out wait.</param>
    /// <param name="eventName">External event name whose wait expired.</param>
    /// <param name="diagnostic">Optional safe diagnostic details.</param>
    /// <returns>A timed-out decision.</returns>
    DurableTaskWorkerDecision<TWork, TResult, TProjection> TimedOut(
        DurableWorkerCorrelation correlation,
        string eventName,
        DurableWorkerDiagnostic? diagnostic = null);

    /// <summary>
    /// Creates a stale or late signal decision without scheduling additional work.
    /// </summary>
    /// <param name="correlation">Correlation identifiers for the stale signal.</param>
    /// <param name="eventName">External event name associated with the stale signal.</param>
    /// <param name="diagnostic">Optional safe diagnostic details.</param>
    /// <returns>An ignore-late-signal decision.</returns>
    DurableTaskWorkerDecision<TWork, TResult, TProjection> IgnoreLateSignal(
        DurableWorkerCorrelation correlation,
        string eventName,
        DurableWorkerDiagnostic? diagnostic = null);
}

/// <summary>
/// Default implementation of <see cref="IDurableTaskWorkerChainRunner{TWork,TResult,TProjection}"/>.
/// </summary>
/// <typeparam name="TWork">Type of work carried by the chain.</typeparam>
/// <typeparam name="TResult">Type of terminal execution result.</typeparam>
/// <typeparam name="TProjection">Type of visible projection repaired from terminal facts.</typeparam>
public sealed class DurableTaskWorkerChainRunner<TWork, TResult, TProjection>
    : IDurableTaskWorkerChainRunner<TWork, TResult, TProjection>
{
    private readonly IOptions<AppSurfaceWorkersDurableTaskOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableTaskWorkerChainRunner{TWork,TResult,TProjection}"/> class.
    /// </summary>
    /// <param name="options">Adapter options that carry host retry intent.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    public DurableTaskWorkerChainRunner(IOptions<AppSurfaceWorkersDurableTaskOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async ValueTask<DurableTaskWorkerDecision<TWork, TResult, TProjection>> TryClaimAsync(
        IDurableWorkerProjectionContract<TWork, TResult, TProjection> contract,
        TWork work,
        DurableWorkerCorrelation correlation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(correlation);

        var envelope = await contract.TryClaimAsync(work, correlation, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(envelope);

        return MapClaim(envelope);
    }

    /// <inheritdoc />
    public async ValueTask<DurableTaskWorkerDecision<TWork, TResult, TProjection>> CompleteAsync(
        IDurableWorkerProjectionContract<TWork, TResult, TProjection> contract,
        TWork work,
        TResult result,
        DurableWorkerCorrelation correlation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(correlation);

        var envelope = await contract.CompleteAsync(work, result, correlation, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(envelope);

        return MapCompletion(envelope);
    }

    /// <inheritdoc />
    public async ValueTask<DurableTaskWorkerDecision<TWork, TResult, TProjection>> ReconcileProjectionAsync(
        IDurableWorkerProjectionContract<TWork, TResult, TProjection> contract,
        TWork work,
        TResult result,
        DurableWorkerCorrelation correlation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(correlation);

        var envelope = await contract.ReconcileProjectionAsync(work, result, correlation, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(envelope);

        return MapProjection(envelope);
    }

    /// <inheritdoc />
    public DurableTaskWorkerDecision<TWork, TResult, TProjection> WaitForExternalEvent(
        DurableWorkerCorrelation correlation,
        string eventName,
        FlowTimeout? timeout = null) =>
        DurableTaskWorkerDecision<TWork, TResult, TProjection>.WaitForExternalEvent(correlation, eventName, timeout);

    /// <inheritdoc />
    public DurableTaskWorkerDecision<TWork, TResult, TProjection> TimedOut(
        DurableWorkerCorrelation correlation,
        string eventName,
        DurableWorkerDiagnostic? diagnostic = null) =>
        DurableTaskWorkerDecision<TWork, TResult, TProjection>.TimedOut(correlation, eventName, diagnostic);

    /// <inheritdoc />
    public DurableTaskWorkerDecision<TWork, TResult, TProjection> IgnoreLateSignal(
        DurableWorkerCorrelation correlation,
        string eventName,
        DurableWorkerDiagnostic? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        var envelope = new DurableWorkerEnvelope<object>(
            DurableWorkerProjectionOutcome.StaleFence,
            "worker.signal-late",
            DurableWorkerRetryability.Terminal,
            correlation,
            diagnostic: diagnostic);

        return DurableTaskWorkerDecision<TWork, TResult, TProjection>.IgnoreLateSignal(envelope, eventName);
    }

    private DurableTaskWorkerDecision<TWork, TResult, TProjection> MapClaim(DurableWorkerEnvelope<TWork> envelope) =>
        envelope.Outcome switch
        {
            DurableWorkerProjectionOutcome.Claimed =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.ScheduleExecutor(
                    envelope,
                    _options.Value.ExecutorRetryPolicy),
            DurableWorkerProjectionOutcome.StaleFence =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.IgnoreLateSignal(envelope),
            DurableWorkerProjectionOutcome.Conflict when envelope.Retryability == DurableWorkerRetryability.Retryable =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.WaitForRetry(
                    envelope,
                    _options.Value.ExecutorRetryPolicy),
            DurableWorkerProjectionOutcome.Conflict or DurableWorkerProjectionOutcome.Unrecoverable =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.Fault(envelope),
            DurableWorkerProjectionOutcome.AlreadyCompleted or DurableWorkerProjectionOutcome.Noop =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.Complete(envelope),
            DurableWorkerProjectionOutcome.Completed or DurableWorkerProjectionOutcome.Reconciled =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.Fault(envelope),
            _ => DurableTaskWorkerDecision<TWork, TResult, TProjection>.Fault(envelope),
        };

    private DurableTaskWorkerDecision<TWork, TResult, TProjection> MapCompletion(
        DurableWorkerEnvelope<TResult> envelope) =>
        envelope.Outcome switch
        {
            DurableWorkerProjectionOutcome.Completed =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.RepairProjection(
                    envelope,
                    _options.Value.ProjectionRetryPolicy),
            DurableWorkerProjectionOutcome.StaleFence =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.IgnoreLateSignal(envelope),
            DurableWorkerProjectionOutcome.Conflict when envelope.Retryability == DurableWorkerRetryability.Retryable =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.WaitForRetry(
                    envelope,
                    _options.Value.ProjectionRetryPolicy),
            DurableWorkerProjectionOutcome.Conflict or DurableWorkerProjectionOutcome.Unrecoverable =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.Fault(envelope),
            DurableWorkerProjectionOutcome.AlreadyCompleted or DurableWorkerProjectionOutcome.Noop =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.Complete(envelope),
            DurableWorkerProjectionOutcome.Claimed or DurableWorkerProjectionOutcome.Reconciled =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.Fault(envelope),
            _ => DurableTaskWorkerDecision<TWork, TResult, TProjection>.Fault(envelope),
        };

    private DurableTaskWorkerDecision<TWork, TResult, TProjection> MapProjection(
        DurableWorkerEnvelope<TProjection> envelope) =>
        envelope.Outcome switch
        {
            DurableWorkerProjectionOutcome.StaleFence =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.IgnoreLateSignal(envelope),
            DurableWorkerProjectionOutcome.Conflict when envelope.Retryability == DurableWorkerRetryability.Retryable =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.WaitForRetry(
                    envelope,
                    _options.Value.ProjectionRetryPolicy),
            DurableWorkerProjectionOutcome.Conflict or DurableWorkerProjectionOutcome.Unrecoverable =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.Fault(envelope),
            DurableWorkerProjectionOutcome.Reconciled =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.CompleteProjection(envelope),
            DurableWorkerProjectionOutcome.Noop =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.Complete(envelope),
            DurableWorkerProjectionOutcome.Claimed
                or DurableWorkerProjectionOutcome.Completed
                or DurableWorkerProjectionOutcome.AlreadyCompleted =>
                DurableTaskWorkerDecision<TWork, TResult, TProjection>.Fault(envelope),
            _ => DurableTaskWorkerDecision<TWork, TResult, TProjection>.Fault(envelope),
        };
}
