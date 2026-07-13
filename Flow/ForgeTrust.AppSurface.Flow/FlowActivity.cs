using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Describes a typed, stable activity callsite in a Flow definition.
/// </summary>
/// <typeparam name="TWork">Serializable work contract sent to the activity executor.</typeparam>
/// <typeparam name="TResult">Serializable result contract returned to the waiting Flow node.</typeparam>
/// <remarks>
/// A callsite id and its contract versions are durable identifiers. Do not change them while nonterminal Flow
/// instances can reference the callsite. Expected business failures belong in <typeparamref name="TResult"/>;
/// exhausted technical failures and ambiguous provider outcomes are host-level suspension concerns.
/// </remarks>
public sealed class FlowActivityCallsite<TWork, TResult>
{
    /// <summary>
    /// Initializes a new activity callsite.
    /// </summary>
    /// <param name="callsiteId">Stable callsite identifier within the Flow definition.</param>
    /// <param name="workContractVersion">Positive serialized work contract version.</param>
    /// <param name="resultContractVersion">Positive serialized result contract version.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="callsiteId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when either contract version is less than one.
    /// </exception>
    public FlowActivityCallsite(
        string callsiteId,
        int workContractVersion = 1,
        int resultContractVersion = 1)
    {
        CallsiteId = FlowDefinition<object>.RequireText(callsiteId, nameof(callsiteId));
        WorkContractVersion = RequirePositiveVersion(workContractVersion, nameof(workContractVersion));
        ResultContractVersion = RequirePositiveVersion(resultContractVersion, nameof(resultContractVersion));
    }

    /// <summary>
    /// Gets the stable callsite identifier.
    /// </summary>
    public string CallsiteId { get; }

    /// <summary>
    /// Gets the serialized work contract version.
    /// </summary>
    public int WorkContractVersion { get; }

    /// <summary>
    /// Gets the serialized result contract version.
    /// </summary>
    public int ResultContractVersion { get; }

    /// <summary>
    /// Creates the typed result used to resume this callsite's Flow node.
    /// </summary>
    /// <param name="result">Decoded activity result.</param>
    /// <returns>A result carrying this callsite's identity and result contract version.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result"/> is null.</exception>
    public FlowActivityWorkResult<TResult> CreateResult(TResult result) =>
        new(CallsiteId, ResultContractVersion, result);

    /// <summary>
    /// Attempts to read a resumed activity result for this exact callsite and result contract.
    /// </summary>
    /// <param name="workResult">Activity result supplied by the host, or null for initial node evaluation.</param>
    /// <param name="result">The typed result when the identity and contract match.</param>
    /// <returns><see langword="true"/> when the result belongs to this callsite; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// A mismatched result is not coerced. Durable hosts should normally prevent mismatches by validating the persisted
    /// wait registration before evaluating the node. Nodes can use this method to distinguish initial execution from
    /// activity resumption without casting or reflection.
    /// </remarks>
    public bool TryGetResult(
        FlowActivityWorkResult? workResult,
        [MaybeNullWhen(false)] out TResult result)
    {
        if (workResult is FlowActivityWorkResult<TResult> typed &&
            string.Equals(typed.CallsiteId, CallsiteId, StringComparison.Ordinal) &&
            typed.ResultContractVersion == ResultContractVersion)
        {
            result = typed.Value;
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Reads a resumed activity result for this exact callsite and result contract.
    /// </summary>
    /// <param name="workResult">Activity result supplied by the host.</param>
    /// <returns>The typed activity result.</returns>
    /// <exception cref="FlowDefinitionException">
    /// Thrown when the result is absent or belongs to a different callsite, CLR type, or contract version.
    /// </exception>
    public TResult GetResult(FlowActivityWorkResult? workResult)
    {
        if (TryGetResult(workResult, out var result))
        {
            return result;
        }

        var actual = workResult is null
            ? "no activity result"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"callsite '{workResult.CallsiteId}', result type '{workResult.ResultType.FullName}', version '{workResult.ResultContractVersion}'");
        throw new FlowDefinitionException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Activity callsite '{CallsiteId}' expected result type '{typeof(TResult).FullName}' version '{ResultContractVersion}', but received {actual}."));
    }

    private static int RequirePositiveVersion(int version, string parameterName)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, version, "Contract versions must be at least 1.");
        }

        return version;
    }
}

/// <summary>
/// Provides non-generic activity request metadata to durable Flow hosts.
/// </summary>
/// <typeparam name="TContext">Serializable context persisted while the activity runs.</typeparam>
/// <remarks>
/// The metadata is intentionally exposed without reflection so a host can select registered codecs from the declared
/// CLR types and contract versions. The interface does not authorize execution or serialize values itself.
/// </remarks>
public interface IFlowActivityRequest<TContext>
{
    /// <summary>
    /// Gets the stable callsite identifier.
    /// </summary>
    string CallsiteId { get; }

    /// <summary>
    /// Gets the declared work CLR type.
    /// </summary>
    Type WorkType { get; }

    /// <summary>
    /// Gets the serialized work contract version.
    /// </summary>
    int WorkContractVersion { get; }

    /// <summary>
    /// Gets the declared result CLR type.
    /// </summary>
    Type ResultType { get; }

    /// <summary>
    /// Gets the serialized result contract version.
    /// </summary>
    int ResultContractVersion { get; }

    /// <summary>
    /// Gets the typed work value through a non-generic host boundary.
    /// </summary>
    object Work { get; }

    /// <summary>
    /// Gets the Flow context to persist atomically with the activity command.
    /// </summary>
    TContext Context { get; }

    /// <summary>
    /// Wraps a decoded result in this request's typed callsite contract.
    /// </summary>
    /// <param name="result">Decoded result whose runtime value must implement <see cref="ResultType"/>.</param>
    /// <returns>A typed work result suitable for resuming the Flow node.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="result"/> has the wrong CLR type.</exception>
    /// <remarks>
    /// This method lets a host bridge a registered non-generic codec back into the typed Flow result without reflection.
    /// </remarks>
    FlowActivityWorkResult CreateResult(object result);
}

/// <summary>
/// Base contract for a decoded activity result supplied when a waiting node resumes.
/// </summary>
/// <remarks>
/// Results contain safe application contracts, not provider response bodies or credentials. Durable hosts validate the
/// callsite, CLR type, and result contract version against the persisted activity wait before node evaluation.
/// </remarks>
public abstract record FlowActivityWorkResult
{
    private protected FlowActivityWorkResult(
        string callsiteId,
        Type resultType,
        int resultContractVersion,
        object result)
    {
        CallsiteId = FlowDefinition<object>.RequireText(callsiteId, nameof(callsiteId));
        ResultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        if (resultContractVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resultContractVersion),
                resultContractVersion,
                "Contract versions must be at least 1.");
        }

        ResultContractVersion = resultContractVersion;
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>
    /// Gets the activity callsite that produced the result.
    /// </summary>
    public string CallsiteId { get; }

    /// <summary>
    /// Gets the declared result CLR type.
    /// </summary>
    public Type ResultType { get; }

    /// <summary>
    /// Gets the serialized result contract version.
    /// </summary>
    public int ResultContractVersion { get; }

    /// <summary>
    /// Gets the decoded result through a non-generic host boundary.
    /// </summary>
    public object Result { get; }
}

/// <summary>
/// Carries a decoded, typed activity result for node resumption.
/// </summary>
/// <typeparam name="TResult">Serializable result contract returned by the activity executor.</typeparam>
public sealed record FlowActivityWorkResult<TResult> : FlowActivityWorkResult
{
    internal FlowActivityWorkResult(
        string callsiteId,
        int resultContractVersion,
        TResult value)
        : base(
            callsiteId,
            typeof(TResult),
            resultContractVersion,
            FlowNodeOutcome<TResult>.RequireContext(value)!)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the typed decoded result.
    /// </summary>
    public TResult Value { get; }
}
