namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Serializes flow contexts for Durable Task persistence validation.
/// </summary>
public interface IFlowContextSerializer
{
    /// <summary>
    /// Serializes a context to a string payload.
    /// </summary>
    /// <typeparam name="TContext">Context type.</typeparam>
    /// <param name="context">Context instance.</param>
    /// <returns>Serialized payload.</returns>
    string Serialize<TContext>(TContext context);

    /// <summary>
    /// Deserializes a context from a string payload.
    /// </summary>
    /// <typeparam name="TContext">Context type.</typeparam>
    /// <param name="payload">Serialized payload.</param>
    /// <returns>Deserialized context.</returns>
    TContext Deserialize<TContext>(string payload);
}
