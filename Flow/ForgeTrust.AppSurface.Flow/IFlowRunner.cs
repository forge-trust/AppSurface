namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Runs AppSurface Flow definitions until the next external-event wait, typed activity, or terminal outcome.
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

    /// <summary>
    /// Resumes a node that is waiting for one typed activity result.
    /// </summary>
    /// <param name="definition">Flow definition to run.</param>
    /// <param name="nodeId">Node id that requested the activity.</param>
    /// <param name="context">Context persisted with the activity request.</param>
    /// <param name="activityResult">Decoded typed activity result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result after execution pauses or ends.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown by the default implementation when the runner has not implemented activity resumption.
    /// </exception>
    /// <remarks>
    /// The default implementation preserves compatibility for custom v1 runners and reports that activity resumption
    /// is unsupported. Runners that surface <see cref="FlowRunStatus.ActivityPending"/> must override this method.
    /// </remarks>
    ValueTask<FlowRunResult<TContext>> ResumeActivityAsync(
        FlowDefinition<TContext> definition,
        string nodeId,
        TContext context,
        FlowActivityWorkResult activityResult,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<FlowRunResult<TContext>>(
            new NotSupportedException($"Flow runner '{GetType().FullName}' does not support activity resumption."));
}
