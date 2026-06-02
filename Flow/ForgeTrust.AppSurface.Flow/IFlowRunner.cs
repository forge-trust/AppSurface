namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Runs AppSurface Flow definitions until the next wait or terminal outcome.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public interface IFlowRunner<TContext>
{
    /// <summary>
    /// Starts a new flow instance at the definition's start node.
    /// </summary>
    /// <param name="definition">Flow definition to run.</param>
    /// <param name="initialContext">Initial context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result after execution pauses or ends.</returns>
    ValueTask<FlowRunResult<TContext>> RunAsync(
        FlowDefinition<TContext> definition,
        TContext initialContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a waiting node with an external event or timeout.
    /// </summary>
    /// <param name="definition">Flow definition to run.</param>
    /// <param name="nodeId">Node id that was waiting.</param>
    /// <param name="context">Persisted context.</param>
    /// <param name="resumeEvent">Event that resumed the node.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result after execution pauses or ends.</returns>
    ValueTask<FlowRunResult<TContext>> ResumeAsync(
        FlowDefinition<TContext> definition,
        string nodeId,
        TContext context,
        FlowResumeEvent resumeEvent,
        CancellationToken cancellationToken = default);
}
