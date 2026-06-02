namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Describes an external event or timeout that resumed a waiting flow node.
/// </summary>
public sealed record FlowResumeEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowResumeEvent"/> class.
    /// </summary>
    /// <param name="eventName">Event name.</param>
    /// <param name="payload">Optional event payload.</param>
    /// <param name="isTimeout">Whether this event represents a timeout.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="eventName"/> is empty.</exception>
    public FlowResumeEvent(string eventName, object? payload = null, bool isTimeout = false)
    {
        EventName = FlowDefinition<object>.RequireText(eventName, nameof(eventName));
        Payload = payload;
        IsTimeout = isTimeout;
    }

    /// <summary>
    /// Gets the event name.
    /// </summary>
    public string EventName { get; }

    /// <summary>
    /// Gets the optional event payload.
    /// </summary>
    public object? Payload { get; }

    /// <summary>
    /// Gets a value indicating whether this event represents a timeout.
    /// </summary>
    public bool IsTimeout { get; }

    /// <summary>
    /// Creates a timeout resume event for the supplied wait event.
    /// </summary>
    /// <param name="eventName">Wait event whose timeout expired.</param>
    /// <returns>A timeout resume event.</returns>
    public static FlowResumeEvent Timeout(string eventName) => new(eventName, isTimeout: true);
}
