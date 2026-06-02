namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Configures the Durable Task adapter boundary for AppSurface Flow.
/// </summary>
/// <remarks>
/// The default configuration is intentionally conservative: resume events are denied until a host registers an
/// <see cref="IFlowResumeAuthorizer"/> implementation, and context serialization is validated before durable execution.
/// </remarks>
public sealed class AppSurfaceFlowDurableTaskOptions
{
    /// <summary>
    /// Gets or sets whether the adapter validates flow context serialization before evaluating durable decisions.
    /// </summary>
    public bool ValidateContextSerialization { get; set; } = true;

    /// <summary>
    /// Gets or sets whether a host should ignore late or mismatched resume events instead of faulting the durable flow.
    /// </summary>
    /// <remarks>
    /// The default value is <see langword="true"/> because durable orchestrations can receive delayed external events
    /// after a timer has won the race. Ignoring the stale signal is usually safer than failing a completed timeout path.
    /// </remarks>
    public bool IgnoreLateResumeEvents { get; set; } = true;

    /// <summary>
    /// Gets or sets the retry policy a Durable Task host should apply when scheduling flow node work.
    /// </summary>
    /// <remarks>
    /// The default value is <see langword="null"/>, which means the adapter does not request retries and the host's
    /// normal scheduling behavior applies. Set this when every node in a flow should share one durable retry policy.
    /// </remarks>
    public FlowRetryPolicy? NodeRetryPolicy { get; set; }
}
