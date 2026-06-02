namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Default resume authorizer that denies every external resume event.
/// </summary>
/// <remarks>
/// Hosts must replace this service with an application-specific implementation before exposing resume endpoints or
/// queue consumers. Durable instance ids and event names are not sufficient authorization by themselves.
/// </remarks>
public sealed class DenyAllFlowResumeAuthorizer : IFlowResumeAuthorizer
{
    /// <inheritdoc />
    public ValueTask<FlowResumeAuthorizationResult> AuthorizeAsync(
        FlowResumeAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(FlowResumeAuthorizationResult.Deny("flow.resume-denied", "No resume authorizer has been registered."));
    }
}
