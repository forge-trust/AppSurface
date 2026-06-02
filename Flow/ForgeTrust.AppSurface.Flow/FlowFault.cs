namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Describes a fault returned by a flow node or adapter.
/// </summary>
/// <remarks>
/// Faults are flow results, not exceptions. Use them for expected process failures that should be visible to callers or
/// durable orchestration logs. Use exceptions for invalid definitions, host configuration errors, and programmer bugs.
/// </remarks>
public sealed record FlowFault
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowFault"/> class.
    /// </summary>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="message">Human-readable diagnostic message.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="code"/> or <paramref name="message"/> is empty.</exception>
    public FlowFault(string code, string message)
    {
        Code = FlowDefinition<object>.RequireText(code, nameof(code));
        Message = FlowDefinition<object>.RequireText(message, nameof(message));
    }

    /// <summary>
    /// Gets the stable machine-readable code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the human-readable diagnostic message.
    /// </summary>
    public string Message { get; }
}
