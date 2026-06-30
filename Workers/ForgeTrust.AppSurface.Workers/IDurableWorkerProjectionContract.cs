namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Records durable worker execution facts and repairs visible projections from those facts.
/// </summary>
/// <typeparam name="TWork">Type of work claimed by the chain.</typeparam>
/// <typeparam name="TResult">Type of terminal execution result.</typeparam>
/// <typeparam name="TProjection">Type of visible projection repaired from durable facts.</typeparam>
/// <remarks>
/// Implementations are app-owned and persistence-specific. The contract separates side-effect execution from
/// projection repair: <see cref="TryClaimAsync"/> decides whether executor activity may run,
/// <see cref="CompleteAsync"/> records a terminal fact, and reconciliation methods update projections without
/// re-running executor activity.
/// </remarks>
public interface IDurableWorkerProjectionContract<TWork, TResult, TProjection>
{
    /// <summary>
    /// Attempts to claim a work item before executor activity is scheduled.
    /// </summary>
    /// <param name="work">Typed work payload.</param>
    /// <param name="correlation">Correlation identifiers for the claim.</param>
    /// <param name="cancellationToken">Token that cancels claim evaluation.</param>
    /// <returns>
    /// A claim envelope. <see cref="DurableWorkerProjectionOutcome.Claimed"/> means executor activity may be scheduled;
    /// duplicate, stale, conflict, and unrecoverable outcomes must not schedule executor activity.
    /// </returns>
    ValueTask<DurableWorkerEnvelope<TWork>> TryClaimAsync(
        TWork work,
        DurableWorkerCorrelation correlation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a terminal execution fact after executor activity completes.
    /// </summary>
    /// <param name="work">Typed work payload.</param>
    /// <param name="result">Terminal result produced by executor activity.</param>
    /// <param name="correlation">Correlation identifiers for the completion fact.</param>
    /// <param name="cancellationToken">Token that cancels completion recording.</param>
    /// <returns>
    /// A completion envelope. Successful completion should lead to projection repair; duplicate completions should not
    /// schedule executor activity.
    /// </returns>
    ValueTask<DurableWorkerEnvelope<TResult>> CompleteAsync(
        TWork work,
        TResult result,
        DurableWorkerCorrelation correlation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Repairs the visible projection for one terminal execution fact.
    /// </summary>
    /// <param name="work">Typed work payload associated with the terminal fact.</param>
    /// <param name="result">Terminal result used to derive the visible projection.</param>
    /// <param name="correlation">Correlation identifiers for the repair attempt.</param>
    /// <param name="cancellationToken">Token that cancels projection repair.</param>
    /// <returns>A projection envelope describing repaired, noop, stale, conflict, or unrecoverable repair behavior.</returns>
    ValueTask<DurableWorkerEnvelope<TProjection>> ReconcileProjectionAsync(
        TWork work,
        TResult result,
        DurableWorkerCorrelation correlation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Repairs a bounded batch of stale or missing projections from durable terminal facts.
    /// </summary>
    /// <param name="request">Bounded projection repair request.</param>
    /// <param name="cancellationToken">Token that cancels projection repair enumeration.</param>
    /// <returns>Projection repair envelopes for the bounded repair pass.</returns>
    IAsyncEnumerable<DurableWorkerEnvelope<TProjection>> ReconcilePendingProjectionsAsync(
        DurableWorkerProjectionRepairRequest request,
        CancellationToken cancellationToken = default);
}
