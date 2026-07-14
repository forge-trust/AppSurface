namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Describes a typed external-event wait without exposing CLR type names to durable storage.
/// </summary>
/// <remarks>
/// This interface describes manifest metadata only. A durable host validates an authorized event against its persisted
/// wait registration, selects an allowlisted codec through <see cref="ContractName"/> and
/// <see cref="ContractVersion"/>, validates the decoded value against <see cref="PayloadType"/>, and only then invokes
/// the Flow evaluator. The evaluator does not authorize, decode, or deduplicate external events.
/// </remarks>
public interface IFlowEventCallsite
{
    /// <summary>Gets the exact case-sensitive event name.</summary>
    string EventName { get; }

    /// <summary>Gets the expected event payload CLR type for allowlisted codec lookup.</summary>
    Type PayloadType { get; }

    /// <summary>Gets the exact durable payload contract name.</summary>
    string ContractName { get; }

    /// <summary>Gets the exact durable payload contract version.</summary>
    string ContractVersion { get; }
}

/// <summary>
/// Declares the exact typed payload contract accepted by one external Flow event wait.
/// </summary>
/// <typeparam name="TPayload">Allowlisted event payload type.</typeparam>
/// <remarks>
/// The event name and contract identity are durable manifest values. Change them only with a new Flow definition
/// version. Values are preserved exactly and compared ordinally by durable hosts; there is no trimming, case folding,
/// or normalization. <typeparamref name="TPayload"/> is runtime codec metadata rather than a durable wire identifier.
/// Use the string-based <c>Wait</c> overload when an event intentionally carries no payload.
/// </remarks>
public sealed class FlowEventCallsite<TPayload> : IFlowEventCallsite
{
    /// <summary>Initializes a typed external-event callsite.</summary>
    /// <param name="eventName">Exact case-sensitive event name.</param>
    /// <param name="contractName">Exact durable payload contract name.</param>
    /// <param name="contractVersion">Exact durable payload contract version.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="eventName"/>, <paramref name="contractName"/>, or
    /// <paramref name="contractVersion"/> is empty.
    /// </exception>
    public FlowEventCallsite(string eventName, string contractName, string contractVersion)
    {
        EventName = FlowDefinition<object>.RequireText(eventName, nameof(eventName));
        ContractName = FlowDefinition<object>.RequireText(contractName, nameof(contractName));
        ContractVersion = FlowDefinition<object>.RequireText(contractVersion, nameof(contractVersion));
    }

    /// <inheritdoc />
    public string EventName { get; }

    /// <inheritdoc />
    public Type PayloadType => typeof(TPayload);

    /// <inheritdoc />
    public string ContractName { get; }

    /// <inheritdoc />
    public string ContractVersion { get; }
}
