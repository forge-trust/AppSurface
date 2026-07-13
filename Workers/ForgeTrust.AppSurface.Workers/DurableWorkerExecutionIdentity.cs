namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Separates immutable provider-operation identity from retry and lease generations.
/// </summary>
/// <remarks>
/// The provider key is derived from <see cref="ActivityId"/>, never from an attempt or lease. A new attempt or lease
/// therefore cannot silently create a second logical provider operation.
/// </remarks>
public sealed record DurableWorkerExecutionIdentity
{
    /// <summary>
    /// Initializes a durable execution identity.
    /// </summary>
    public DurableWorkerExecutionIdentity(
        string activityId,
        int attemptNumber,
        long leaseGeneration,
        long scopeGeneration,
        string runtimeEpoch,
        string providerKey)
    {
        if (attemptNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        }

        if (leaseGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseGeneration));
        }

        if (scopeGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(scopeGeneration));
        }

        ActivityId = DurableWorkerCorrelation.RequireText(activityId, nameof(activityId));
        AttemptNumber = attemptNumber;
        LeaseGeneration = leaseGeneration;
        ScopeGeneration = scopeGeneration;
        RuntimeEpoch = DurableWorkerCorrelation.RequireText(runtimeEpoch, nameof(runtimeEpoch));
        ProviderKey = DurableWorkerCorrelation.RequireText(providerKey, nameof(providerKey));
    }

    /// <summary>Gets the immutable activity identity.</summary>
    public string ActivityId { get; }

    /// <summary>Gets the monotonically increasing execution attempt number.</summary>
    public int AttemptNumber { get; }

    /// <summary>Gets the exact lease generation that authorized execution.</summary>
    public long LeaseGeneration { get; }

    /// <summary>Gets the owning scope lifecycle generation.</summary>
    public long ScopeGeneration { get; }

    /// <summary>Gets the out-of-band runtime recovery epoch.</summary>
    public string RuntimeEpoch { get; }

    /// <summary>Gets the immutable activity-derived provider idempotency key.</summary>
    public string ProviderKey { get; }
}
