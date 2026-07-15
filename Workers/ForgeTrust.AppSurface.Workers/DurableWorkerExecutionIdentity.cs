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
    private DurableWorkerExecutionIdentity(
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

    /// <summary>Creates the first fenced execution identity for one logical provider operation.</summary>
    public static DurableWorkerExecutionIdentity CreateInitial(
        string activityId,
        long leaseGeneration,
        long scopeGeneration,
        string runtimeEpoch) =>
        Create(activityId, 1, leaseGeneration, scopeGeneration, runtimeEpoch);

    /// <summary>Creates a fenced identity from provider-authoritative attempt and generation values.</summary>
    public static DurableWorkerExecutionIdentity Create(
        string activityId,
        int attemptNumber,
        long leaseGeneration,
        long scopeGeneration,
        string runtimeEpoch)
    {
        var validatedActivityId = DurableWorkerCorrelation.RequireText(activityId, nameof(activityId));
        return new(
            validatedActivityId,
            attemptNumber,
            leaseGeneration,
            scopeGeneration,
            runtimeEpoch,
            providerKey: validatedActivityId);
    }

    /// <summary>
    /// Advances retry and fencing generations while retaining the immutable provider-operation identity.
    /// </summary>
    public DurableWorkerExecutionIdentity Advance(
        int attemptNumber,
        long leaseGeneration,
        long scopeGeneration,
        string runtimeEpoch)
    {
        if (attemptNumber < AttemptNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        }

        if (leaseGeneration < LeaseGeneration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseGeneration));
        }

        if (scopeGeneration < ScopeGeneration)
        {
            throw new ArgumentOutOfRangeException(nameof(scopeGeneration));
        }

        if (attemptNumber == AttemptNumber
            && leaseGeneration == LeaseGeneration
            && scopeGeneration == ScopeGeneration
            && string.Equals(runtimeEpoch, RuntimeEpoch, StringComparison.Ordinal))
        {
            throw new ArgumentException("An advanced execution identity must change an attempt or fence generation.");
        }

        return new(
            ActivityId,
            attemptNumber,
            leaseGeneration,
            scopeGeneration,
            runtimeEpoch,
            ProviderKey);
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
