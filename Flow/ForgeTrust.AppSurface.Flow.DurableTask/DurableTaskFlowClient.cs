namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Authorizes external resume requests before a host raises a Durable Task external event.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public interface IDurableTaskFlowClient<TContext>
{
    /// <summary>
    /// Authorizes a resume event.
    /// </summary>
    /// <param name="request">Resume authorization request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authorization result.</returns>
    ValueTask<FlowResumeAuthorizationResult> AuthorizeResumeAsync(
        FlowResumeAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IDurableTaskFlowClient{TContext}"/>.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed class DurableTaskFlowClient<TContext> : IDurableTaskFlowClient<TContext>
{
    private readonly IFlowResumeAuthorizer _authorizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableTaskFlowClient{TContext}"/> class.
    /// </summary>
    /// <param name="authorizer">Resume-event authorizer.</param>
    public DurableTaskFlowClient(IFlowResumeAuthorizer authorizer)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
    }

    /// <inheritdoc />
    public ValueTask<FlowResumeAuthorizationResult> AuthorizeResumeAsync(
        FlowResumeAuthorizationRequest request,
        CancellationToken cancellationToken = default) =>
        _authorizer.AuthorizeAsync(request, cancellationToken);
}
