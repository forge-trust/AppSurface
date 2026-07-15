using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>
/// Carries one provider claim across the storage-to-executor boundary.
/// </summary>
/// <remarks>
/// Providers create this only after a scoped, fenced claim succeeds. The claim is not authorization by itself; the
/// provider must record the matching effect permit before invoking prepared work.
/// </remarks>
public sealed record DurableClaimedWork
{
    /// <summary>Initializes a validated provider claim.</summary>
    public DurableClaimedWork(
        DurableScopeId scopeId,
        DurableWorkId workId,
        string activityId,
        string workName,
        string workVersion,
        DurableEncodedPayload payload,
        DurableProviderSafety providerSafety,
        int attemptNumber,
        long leaseGeneration,
        long scopeGeneration,
        string runtimeEpoch)
    {
        _ = ProviderContractValidation.Require(scopeId.Value, nameof(scopeId), 200);
        _ = ProviderContractValidation.Require(workId.Value, nameof(workId), 200);
        if (!Enum.IsDefined(providerSafety))
        {
            throw new ArgumentOutOfRangeException(nameof(providerSafety));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseGeneration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scopeGeneration);

        ScopeId = scopeId;
        WorkId = workId;
        ActivityId = ProviderContractValidation.Require(activityId, nameof(activityId), 200);
        WorkName = ProviderContractValidation.Require(workName, nameof(workName), 200);
        WorkVersion = ProviderContractValidation.Require(workVersion, nameof(workVersion), 100);
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        ProviderSafety = providerSafety;
        AttemptNumber = attemptNumber;
        LeaseGeneration = leaseGeneration;
        ScopeGeneration = scopeGeneration;
        RuntimeEpoch = ProviderContractValidation.Require(runtimeEpoch, nameof(runtimeEpoch), 200);
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }
    /// <summary>Gets the immutable work aggregate identifier.</summary>
    public DurableWorkId WorkId { get; }
    /// <summary>Gets the immutable provider-operation activity identifier.</summary>
    public string ActivityId { get; }
    /// <summary>Gets the registered work name.</summary>
    public string WorkName { get; }
    /// <summary>Gets the registered work version.</summary>
    public string WorkVersion { get; }
    /// <summary>Gets the encoded work payload.</summary>
    public DurableEncodedPayload Payload { get; }
    /// <summary>Gets the provider ambiguity policy snapshot.</summary>
    public DurableProviderSafety ProviderSafety { get; }
    /// <summary>Gets the monotonically increasing attempt number.</summary>
    public int AttemptNumber { get; }
    /// <summary>Gets the claim lease generation.</summary>
    public long LeaseGeneration { get; }
    /// <summary>Gets the owning scope lifecycle generation.</summary>
    public long ScopeGeneration { get; }
    /// <summary>Gets the out-of-band recovery epoch.</summary>
    public string RuntimeEpoch { get; }

    /// <summary>Creates the validated application execution context for this provider claim.</summary>
    public DurableWorkExecutionContext ToExecutionContext() => new(
        ScopeId,
        WorkId,
        WorkName,
        WorkVersion,
        Payload,
        ProviderSafety,
        DurableWorkerExecutionIdentity.Create(
            ActivityId,
            AttemptNumber,
            LeaseGeneration,
            ScopeGeneration,
            RuntimeEpoch));
}

/// <summary>
/// Wraps already prepared application work for invocation after the provider commits its effect permit.
/// </summary>
public sealed class DurablePreparedWorkInvocation
{
    private readonly DurablePreparedWork _preparedWork;

    internal DurablePreparedWorkInvocation(DurablePreparedWork preparedWork)
    {
        _preparedWork = preparedWork ?? throw new ArgumentNullException(nameof(preparedWork));
    }

    /// <summary>Invokes the prepared application executor and returns its encoded terminal result.</summary>
    public ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default) =>
        _preparedWork.InvokeAsync(cancellationToken);
}

/// <summary>Adapts validated provider claims to adopter-owned work registrations.</summary>
public static class DurableProviderWorkAdapter
{
    /// <summary>Prepares an application executor without performing provider I/O.</summary>
    public static DurablePreparedWorkInvocation Prepare(
        DurableWorkRegistration registration,
        IServiceProvider services,
        DurableClaimedWork claim)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(claim);
        return new DurablePreparedWorkInvocation(registration.Prepare(services, claim.ToExecutionContext()));
    }

    /// <summary>Runs the adopter-owned side-effect-free reconciler for a validated provider claim.</summary>
    public static ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
        DurableWorkRegistration registration,
        IServiceProvider services,
        DurableClaimedWork claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(claim);
        return registration.ReconcileAsync(services, claim.ToExecutionContext(), cancellationToken);
    }
}
