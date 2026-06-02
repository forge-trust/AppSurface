namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Represents an invalid flow definition or an attempt to execute a node transition that the definition does not allow.
/// </summary>
/// <remarks>
/// Definition errors are deterministic authoring mistakes. They are surfaced as exceptions so tests and startup
/// validation fail loudly before a durable instance is started.
/// </remarks>
public sealed class FlowDefinitionException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowDefinitionException"/> class.
    /// </summary>
    /// <param name="message">Diagnostic message.</param>
    public FlowDefinitionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FlowDefinitionException"/> class.
    /// </summary>
    /// <param name="message">Diagnostic message.</param>
    /// <param name="innerException">Exception that caused this flow definition failure.</param>
    public FlowDefinitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
