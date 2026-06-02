using ForgeTrust.AppSurface.Flow;

namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Authorizes external resume events before they are delivered to Durable Task.
/// </summary>
public interface IFlowResumeAuthorizer
{
    /// <summary>
    /// Authorizes a resume request.
    /// </summary>
    /// <param name="request">Authorization request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An authorization result.</returns>
    ValueTask<FlowResumeAuthorizationResult> AuthorizeAsync(
        FlowResumeAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resume-event authorization request.
/// </summary>
public sealed record FlowResumeAuthorizationRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowResumeAuthorizationRequest"/> class.
    /// </summary>
    /// <param name="flowId">Flow id.</param>
    /// <param name="version">Flow version.</param>
    /// <param name="instanceId">Durable instance id.</param>
    /// <param name="nodeId">Waiting node id.</param>
    /// <param name="eventName">External event name.</param>
    /// <param name="caller">Application-defined caller identifier.</param>
    /// <param name="metadata">Application-defined authorization metadata.</param>
    public FlowResumeAuthorizationRequest(
        string flowId,
        string version,
        string instanceId,
        string nodeId,
        string eventName,
        string caller,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        FlowId = FlowDefinition<object>.RequireText(flowId, nameof(flowId));
        Version = FlowDefinition<object>.RequireText(version, nameof(version));
        InstanceId = FlowDefinition<object>.RequireText(instanceId, nameof(instanceId));
        NodeId = FlowDefinition<object>.RequireText(nodeId, nameof(nodeId));
        EventName = FlowDefinition<object>.RequireText(eventName, nameof(eventName));
        Caller = FlowDefinition<object>.RequireText(caller, nameof(caller));
        Metadata = metadata ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the flow id.
    /// </summary>
    public string FlowId { get; }

    /// <summary>
    /// Gets the flow version.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the durable instance id.
    /// </summary>
    public string InstanceId { get; }

    /// <summary>
    /// Gets the waiting node id.
    /// </summary>
    public string NodeId { get; }

    /// <summary>
    /// Gets the external event name.
    /// </summary>
    public string EventName { get; }

    /// <summary>
    /// Gets the application-defined caller identifier.
    /// </summary>
    public string Caller { get; }

    /// <summary>
    /// Gets application-defined authorization metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}

/// <summary>
/// Authorization result for a resume event.
/// </summary>
public sealed record FlowResumeAuthorizationResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowResumeAuthorizationResult"/> class.
    /// </summary>
    /// <param name="allowed">Whether the resume event is allowed.</param>
    /// <param name="code">Stable machine-readable result code.</param>
    /// <param name="message">Human-readable diagnostic message.</param>
    public FlowResumeAuthorizationResult(bool allowed, string code, string message)
    {
        Allowed = allowed;
        Code = FlowDefinition<object>.RequireText(code, nameof(code));
        Message = FlowDefinition<object>.RequireText(message, nameof(message));
    }

    /// <summary>
    /// Gets a value indicating whether the resume event is allowed.
    /// </summary>
    public bool Allowed { get; }

    /// <summary>
    /// Gets the stable machine-readable result code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the human-readable diagnostic message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Creates an allow result.
    /// </summary>
    public static FlowResumeAuthorizationResult Allow(string code = "flow.resume-allowed", string message = "Resume event allowed.") =>
        new(true, code, message);

    /// <summary>
    /// Creates a deny result.
    /// </summary>
    public static FlowResumeAuthorizationResult Deny(string code, string message) => new(false, code, message);
}
