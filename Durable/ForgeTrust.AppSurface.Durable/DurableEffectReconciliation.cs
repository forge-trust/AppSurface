using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Identifies what a side-effect-free provider reconciliation established.
/// </summary>
public enum DurableEffectReconciliationKind
{
    /// <summary>The original provider effect is proven applied and has a typed terminal result.</summary>
    Applied = 0,
    /// <summary>The original provider effect is proven not applied, so a new permitted retry may be safe.</summary>
    NotApplied = 1,
    /// <summary>Provider state remains ambiguous and work must stay suspended.</summary>
    Unknown = 2,
}

/// <summary>
/// Strongly typed result from a provider reconciliation read.
/// </summary>
/// <typeparam name="TResult">Registered terminal result type.</typeparam>
public sealed record DurableEffectReconciliation<TResult>
{
    private DurableEffectReconciliation(DurableEffectReconciliationKind kind, TResult? result)
    {
        Kind = kind;
        Result = result;
    }

    /// <summary>Gets the proven provider state.</summary>
    public DurableEffectReconciliationKind Kind { get; }

    /// <summary>Gets the terminal result when <see cref="Kind"/> is <see cref="DurableEffectReconciliationKind.Applied"/>.</summary>
    public TResult? Result { get; }

    /// <summary>Creates an applied result with terminal provider truth.</summary>
    public static DurableEffectReconciliation<TResult> Applied(TResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(DurableEffectReconciliationKind.Applied, result);
    }

    /// <summary>Creates a result proving that the external effect did not occur.</summary>
    public static DurableEffectReconciliation<TResult> NotApplied() =>
        new(DurableEffectReconciliationKind.NotApplied, default);

    /// <summary>Creates a result that leaves the external effect ambiguous.</summary>
    public static DurableEffectReconciliation<TResult> Unknown() =>
        new(DurableEffectReconciliationKind.Unknown, default);
}

/// <summary>
/// Performs a side-effect-free provider read after an allowed effect has an unknown outcome.
/// </summary>
/// <typeparam name="TWork">Registered work type.</typeparam>
/// <typeparam name="TResult">Registered terminal result type.</typeparam>
/// <remarks>
/// Reconciliation must never repeat the provider mutation. It queries provider state using the immutable provider key
/// and returns what can be proven. Returning <see cref="DurableEffectReconciliationKind.Unknown"/> keeps work suspended.
/// </remarks>
public interface IDurableEffectReconciler<TWork, TResult>
{
    /// <summary>
    /// Reads provider state for the original activity-derived provider key.
    /// </summary>
    ValueTask<DurableEffectReconciliation<TResult>> ReconcileAsync(
        DurableWorkerEnvelope<TWork> work,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Encoded reconciliation result used by the runtime store boundary.
/// </summary>
public sealed record DurableEncodedEffectReconciliation
{
    /// <summary>
    /// Initializes an encoded reconciliation result.
    /// </summary>
    public DurableEncodedEffectReconciliation(
        DurableEffectReconciliationKind kind,
        DurableEncodedPayload? result)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if ((kind == DurableEffectReconciliationKind.Applied) != (result is not null))
        {
            throw new ArgumentException("Only an applied reconciliation carries a terminal result.", nameof(result));
        }

        Kind = kind;
        Result = result;
    }

    /// <summary>Gets the proven provider state.</summary>
    public DurableEffectReconciliationKind Kind { get; }

    /// <summary>Gets the encoded terminal result for an applied effect.</summary>
    public DurableEncodedPayload? Result { get; }
}
