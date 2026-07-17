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
    /// <param name="activityId">Stable activity identity; this also becomes the immutable provider key.</param>
    /// <param name="leaseGeneration">Positive provider-authoritative lease generation.</param>
    /// <param name="scopeGeneration">Positive provider-authoritative scope generation.</param>
    /// <param name="runtimeEpoch">Non-empty opaque runtime recovery epoch. Epoch values are not ordered.</param>
    /// <returns>An identity whose attempt number is one.</returns>
    /// <exception cref="ArgumentException">Thrown when required text is empty or invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a generation is not positive.</exception>
    public static DurableWorkerExecutionIdentity CreateInitial(
        string activityId,
        long leaseGeneration,
        long scopeGeneration,
        string runtimeEpoch) =>
        Create(activityId, 1, leaseGeneration, scopeGeneration, runtimeEpoch);

    /// <summary>Creates a fenced identity from provider-authoritative attempt and generation values.</summary>
    /// <param name="activityId">Stable activity identity; this also becomes the immutable provider key.</param>
    /// <param name="attemptNumber">Positive provider-authoritative attempt number.</param>
    /// <param name="leaseGeneration">Positive provider-authoritative lease generation.</param>
    /// <param name="scopeGeneration">Positive provider-authoritative scope generation.</param>
    /// <param name="runtimeEpoch">Non-empty opaque runtime recovery epoch. Epoch values are not ordered.</param>
    /// <returns>A validated execution identity.</returns>
    /// <exception cref="ArgumentException">Thrown when required text is empty or invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when an attempt or generation is not positive.</exception>
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
    /// <param name="attemptNumber">Attempt number, which must not be lower than the current value.</param>
    /// <param name="leaseGeneration">Lease generation, which must not be lower than the current value.</param>
    /// <param name="scopeGeneration">Scope generation, which must not be lower than the current value.</param>
    /// <param name="runtimeEpoch">Non-empty opaque runtime recovery epoch. Epoch values are not ordered.</param>
    /// <returns>A new identity that preserves <see cref="ActivityId"/> and <see cref="ProviderKey"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when no field changes or required text is empty or invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when an attempt or generation regresses.</exception>
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
            throw new ArgumentException("An advanced execution identity must change an attempt, fence generation, or runtime epoch.");
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
