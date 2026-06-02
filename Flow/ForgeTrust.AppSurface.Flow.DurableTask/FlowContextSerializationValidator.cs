namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Validates that a flow context can survive a serializer round trip before durable execution starts.
/// </summary>
public sealed class FlowContextSerializationValidator
{
    private readonly IFlowContextSerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlowContextSerializationValidator"/> class.
    /// </summary>
    /// <param name="serializer">Context serializer.</param>
    public FlowContextSerializationValidator(IFlowContextSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <summary>
    /// Validates a context by serializing and deserializing it.
    /// </summary>
    /// <typeparam name="TContext">Context type.</typeparam>
    /// <param name="context">Context instance.</param>
    /// <returns>A success or failure result.</returns>
    public FlowContextSerializationResult Validate<TContext>(TContext context)
    {
        try
        {
            var payload = _serializer.Serialize(context);
            _ = _serializer.Deserialize<TContext>(payload);
            return FlowContextSerializationResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FlowContextSerializationResult.Failure(
                "Flow context must round-trip through the configured serializer before Durable Task execution.",
                ex);
        }
    }
}

/// <summary>
/// Result produced by <see cref="FlowContextSerializationValidator"/>.
/// </summary>
/// <param name="Succeeded">Whether validation succeeded.</param>
/// <param name="Message">Validation message.</param>
/// <param name="Exception">Exception captured from the serializer, if any.</param>
public sealed record FlowContextSerializationResult(bool Succeeded, string Message, Exception? Exception)
{
    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static FlowContextSerializationResult Success() => new(true, "Flow context serialization succeeded.", null);

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static FlowContextSerializationResult Failure(string message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(false, message, exception);
    }
}
