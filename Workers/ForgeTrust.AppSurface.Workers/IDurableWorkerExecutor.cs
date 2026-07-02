namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Executes the side-effecting portion of a durable worker chain.
/// </summary>
/// <typeparam name="TWork">Type of work claimed by the chain.</typeparam>
/// <typeparam name="TResult">Type of terminal result produced by executor activity.</typeparam>
/// <remarks>
/// Executors are expected to run inside host-owned durable activities, queue handlers, or equivalent runtime work. They
/// should not be invoked by projection repair paths. Projection repair uses durable completion facts through
/// <see cref="IDurableWorkerProjectionContract{TWork,TResult,TProjection}"/> instead.
/// </remarks>
public interface IDurableWorkerExecutor<TWork, TResult>
{
    /// <summary>
    /// Runs the side-effecting worker activity for a claimed work item.
    /// </summary>
    /// <param name="work">Claimed work envelope that authorized executor scheduling.</param>
    /// <param name="cancellationToken">Token that cancels executor work.</param>
    /// <returns>The terminal result that should be recorded with the projection contract.</returns>
    ValueTask<TResult> ExecuteAsync(
        DurableWorkerEnvelope<TWork> work,
        CancellationToken cancellationToken = default);
}
