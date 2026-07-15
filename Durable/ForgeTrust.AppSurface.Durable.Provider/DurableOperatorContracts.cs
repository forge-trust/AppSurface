using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>
/// Requests a privacy-bounded snapshot of one work aggregate in an already authorized scope.
/// </summary>
public sealed record DurableWorkGetRequest
{
    /// <summary>Initializes a scoped work query.</summary>
    public DurableWorkGetRequest(DurableScopeId scopeId, DurableWorkId workId)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(workId, nameof(workId));
        ScopeId = scopeId;
        WorkId = workId;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the opaque work identifier.</summary>
    public DurableWorkId WorkId { get; }
}

/// <summary>
/// Reports authoritative work state without returning the original work payload or provider diagnostics.
/// </summary>
public sealed record DurableWorkSnapshot
{
    /// <summary>Initializes an immutable work snapshot.</summary>
    public DurableWorkSnapshot(
        DurableScopeId scopeId,
        DurableWorkId workId,
        string activityId,
        string workName,
        string workVersion,
        DurableWorkState state,
        DurableProviderSafety providerSafety,
        string providerKey,
        int attemptNumber,
        long revision,
        DateTimeOffset acceptedAtUtc,
        DateTimeOffset dueAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? terminalAtUtc,
        string? terminalCode,
        DurableEncodedPayload? result)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(workId, nameof(workId));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (!Enum.IsDefined(providerSafety))
        {
            throw new ArgumentOutOfRangeException(nameof(providerSafety));
        }

        if (attemptNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        ScopeId = scopeId;
        WorkId = workId;
        ActivityId = ProviderContractValidation.Require(activityId, nameof(activityId), 200);
        WorkName = ProviderContractValidation.Require(workName, nameof(workName), 200);
        WorkVersion = ProviderContractValidation.Require(workVersion, nameof(workVersion), 100);
        State = state;
        ProviderSafety = providerSafety;
        ProviderKey = ProviderContractValidation.Require(providerKey, nameof(providerKey), 512);
        AttemptNumber = attemptNumber;
        Revision = revision;
        AcceptedAtUtc = acceptedAtUtc.ToUniversalTime();
        DueAtUtc = dueAtUtc.ToUniversalTime();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        TerminalAtUtc = terminalAtUtc?.ToUniversalTime();
        TerminalCode = terminalCode;
        Result = result;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the work aggregate identity.</summary>
    public DurableWorkId WorkId { get; }

    /// <summary>Gets the immutable effect activity identity.</summary>
    public string ActivityId { get; }

    /// <summary>Gets the registered work name.</summary>
    public string WorkName { get; }

    /// <summary>Gets the registered work version.</summary>
    public string WorkVersion { get; }

    /// <summary>Gets the current authoritative state.</summary>
    public DurableWorkState State { get; }

    /// <summary>Gets the accepted provider ambiguity policy.</summary>
    public DurableProviderSafety ProviderSafety { get; }

    /// <summary>Gets the immutable provider-safe idempotency key.</summary>
    public string ProviderKey { get; }

    /// <summary>Gets the number of claims that reached this aggregate.</summary>
    public int AttemptNumber { get; }

    /// <summary>Gets the optimistic-concurrency revision.</summary>
    public long Revision { get; }

    /// <summary>Gets the authoritative store acceptance timestamp.</summary>
    public DateTimeOffset AcceptedAtUtc { get; }

    /// <summary>Gets the current eligibility timestamp.</summary>
    public DateTimeOffset DueAtUtc { get; }

    /// <summary>Gets the most recent authoritative update timestamp.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Gets the terminal timestamp when terminal.</summary>
    public DateTimeOffset? TerminalAtUtc { get; }

    /// <summary>Gets the safe terminal or suspension code when one is present.</summary>
    public string? TerminalCode { get; }

    /// <summary>Gets the encoded terminal business result when work succeeded.</summary>
    /// <remarks>The original work payload and stale provider observations are intentionally not returned.</remarks>
    public DurableEncodedPayload? Result { get; }
}

/// <summary>
/// Requests an audited cancellation under optimistic concurrency.
/// </summary>
/// <remarks>
/// The application must authorize the actor and scope before calling this API. Actor and reason values must be
/// privacy-safe identifiers, not free-form user content.
/// </remarks>
public sealed record DurableWorkCancelRequest
{
    /// <summary>Initializes an audited cancellation command.</summary>
    public DurableWorkCancelRequest(
        DurableScopeId scopeId,
        DurableWorkId workId,
        string actorId,
        string reasonCode,
        long expectedRevision)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(workId, nameof(workId));
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        ScopeId = scopeId;
        WorkId = workId;
        ActorId = ProviderContractValidation.Require(actorId, nameof(actorId), 200);
        ReasonCode = ProviderContractValidation.Require(reasonCode, nameof(reasonCode), 120);
        ExpectedRevision = expectedRevision;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the work aggregate identity.</summary>
    public DurableWorkId WorkId { get; }

    /// <summary>Gets the privacy-safe authorized actor identity written to history.</summary>
    public string ActorId { get; }

    /// <summary>Gets the privacy-safe reason code written to history.</summary>
    public string ReasonCode { get; }

    /// <summary>Gets the required current revision.</summary>
    public long ExpectedRevision { get; }
}

/// <summary>Identifies an accepted work cancellation outcome.</summary>
public enum DurableWorkCancelOutcome
{
    /// <summary>The cancellation changed authoritative state.</summary>
    Applied = 0,
    /// <summary>The aggregate was already terminal and no effect was repeated.</summary>
    AlreadyTerminal = 1,
}

/// <summary>Reports a successful work cancellation command.</summary>
public sealed record DurableWorkCancelResult(
    DurableWorkId WorkId,
    DurableWorkCancelOutcome Outcome,
    DurableWorkState State,
    long Revision);

/// <summary>Requests one bounded, ordered, payload-free page of Work operations in an authorized scope.</summary>
public sealed record DurableWorkListRequest
{
    /// <summary>Initializes a scoped Work inventory request.</summary>
    public DurableWorkListRequest(
        DurableScopeId scopeId,
        DurableWorkState? state = null,
        bool requiresRecoveryReleaseOnly = false,
        int pageSize = 100,
        string? continuationToken = null)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        if (state is { } requestedState && !Enum.IsDefined(requestedState))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (pageSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        ScopeId = scopeId;
        State = state;
        RequiresRecoveryReleaseOnly = requiresRecoveryReleaseOnly;
        PageSize = pageSize;
        ContinuationToken = continuationToken is null
            ? null
            : ProviderContractValidation.Require(continuationToken, nameof(continuationToken), 200);
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the optional public Work state filter.</summary>
    public DurableWorkState? State { get; }

    /// <summary>Gets whether only nonterminal rows owned by an older runtime epoch should be returned.</summary>
    public bool RequiresRecoveryReleaseOnly { get; }

    /// <summary>Gets the maximum page size.</summary>
    public int PageSize { get; }

    /// <summary>Gets the prior page's opaque continuation token.</summary>
    public string? ContinuationToken { get; }
}

/// <summary>Reports one payload-free Work inventory item for recovery and operations.</summary>
public sealed record DurableWorkListItem
{
    /// <summary>Initializes a Work inventory item.</summary>
    public DurableWorkListItem(
        DurableWorkId workId,
        string activityId,
        string workName,
        string workVersion,
        DurableWorkState state,
        DurableProviderSafety providerSafety,
        int attemptNumber,
        long revision,
        DateTimeOffset acceptedAtUtc,
        DateTimeOffset dueAtUtc,
        DateTimeOffset updatedAtUtc,
        string? terminalCode,
        bool cancellationRequested,
        bool requiresRecoveryRelease)
    {
        ProviderContractValidation.Require(workId, nameof(workId));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (!Enum.IsDefined(providerSafety))
        {
            throw new ArgumentOutOfRangeException(nameof(providerSafety));
        }

        if (attemptNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        WorkId = workId;
        ActivityId = ProviderContractValidation.Require(activityId, nameof(activityId), 200);
        WorkName = ProviderContractValidation.Require(workName, nameof(workName), 200);
        WorkVersion = ProviderContractValidation.Require(workVersion, nameof(workVersion), 100);
        State = state;
        ProviderSafety = providerSafety;
        AttemptNumber = attemptNumber;
        Revision = revision;
        AcceptedAtUtc = acceptedAtUtc.ToUniversalTime();
        DueAtUtc = dueAtUtc.ToUniversalTime();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        TerminalCode = terminalCode;
        CancellationRequested = cancellationRequested;
        RequiresRecoveryRelease = requiresRecoveryRelease;
    }

    /// <summary>Gets the opaque Work identity.</summary>
    public DurableWorkId WorkId { get; }
    /// <summary>Gets the immutable activity identity used for cross-surface correlation.</summary>
    public string ActivityId { get; }
    /// <summary>Gets the registered Work name.</summary>
    public string WorkName { get; }
    /// <summary>Gets the immutable Work version.</summary>
    public string WorkVersion { get; }
    /// <summary>Gets the public authoritative state.</summary>
    public DurableWorkState State { get; }
    /// <summary>Gets the immutable provider-safety class.</summary>
    public DurableProviderSafety ProviderSafety { get; }
    /// <summary>Gets the current attempt number.</summary>
    public int AttemptNumber { get; }
    /// <summary>Gets the current aggregate revision required by operator commands.</summary>
    public long Revision { get; }
    /// <summary>Gets the authoritative store acceptance time.</summary>
    public DateTimeOffset AcceptedAtUtc { get; }
    /// <summary>Gets the current eligibility time.</summary>
    public DateTimeOffset DueAtUtc { get; }
    /// <summary>Gets the last authoritative mutation time.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }
    /// <summary>Gets the safe terminal or suspension code, when present.</summary>
    public string? TerminalCode { get; }
    /// <summary>Gets whether cancellation intent has been recorded.</summary>
    public bool CancellationRequested { get; }
    /// <summary>Gets whether this nonterminal Work row belongs to an older runtime epoch.</summary>
    public bool RequiresRecoveryRelease { get; }
}

/// <summary>Reports one bounded Work inventory page.</summary>
public sealed record DurableWorkListResult
{
    /// <summary>Initializes a Work inventory page.</summary>
    public DurableWorkListResult(IReadOnlyList<DurableWorkListItem> items, string? continuationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToArray();
        ContinuationToken = continuationToken;
    }

    /// <summary>Gets the ordered payload-free items.</summary>
    public IReadOnlyList<DurableWorkListItem> Items { get; }
    /// <summary>Gets the opaque token for the next page, or <see langword="null"/> when complete.</summary>
    public string? ContinuationToken { get; }
}

/// <summary>
/// Provides application-authorized query and cancellation operations for work aggregates.
/// </summary>
public interface IDurableWorkControlClient
{
    /// <summary>Reads one scoped work snapshot.</summary>
    ValueTask<DurableOperationResult<DurableWorkSnapshot>> GetAsync(
        DurableWorkGetRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Lists one bounded payload-free Work inventory page in an already authorized scope.</summary>
    ValueTask<DurableOperationResult<DurableWorkListResult>> ListAsync(
        DurableWorkListRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Requests audited cancellation under optimistic concurrency.</summary>
    ValueTask<DurableOperationResult<DurableWorkCancelResult>> CancelAsync(
        DurableWorkCancelRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Requests disabling an owning durable scope and fencing every prior scope generation.</summary>
public sealed record DurableScopeDisableRequest
{
    /// <summary>Initializes an audited scope disable command.</summary>
    public DurableScopeDisableRequest(
        DurableScopeId scopeId,
        string actorId,
        string reasonCode,
        long expectedGeneration)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        if (expectedGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        }

        ScopeId = scopeId;
        ActorId = ProviderContractValidation.Require(actorId, nameof(actorId), 200);
        ReasonCode = ProviderContractValidation.Require(reasonCode, nameof(reasonCode), 120);
        ExpectedGeneration = expectedGeneration;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the privacy-safe authorized actor identity.</summary>
    public string ActorId { get; }

    /// <summary>Gets the privacy-safe reason code.</summary>
    public string ReasonCode { get; }

    /// <summary>Gets the required active generation.</summary>
    public long ExpectedGeneration { get; }
}

/// <summary>Identifies an accepted scope disable outcome.</summary>
public enum DurableScopeDisableOutcome
{
    /// <summary>The scope generation advanced and the scope became disabled.</summary>
    Applied = 0,
    /// <summary>The scope was already disabled.</summary>
    AlreadyDisabled = 1,
}

/// <summary>Reports a successful scope disable command.</summary>
public sealed record DurableScopeDisableResult(
    DurableScopeId ScopeId,
    DurableScopeDisableOutcome Outcome,
    long Generation);

/// <summary>Provides application-authorized scope lifecycle fencing.</summary>
public interface IDurableScopeControlClient
{
    /// <summary>Disables a scope and increments its generation atomically.</summary>
    ValueTask<DurableOperationResult<DurableScopeDisableResult>> DisableAsync(
        DurableScopeDisableRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Identifies an idempotent operator mutation outcome.</summary>
public enum DurableWorkOperatorOutcome
{
    /// <summary>The command changed or authoritatively resolved the work aggregate.</summary>
    Applied = 0,
    /// <summary>The exact completed operator command was returned without repeating provider reconciliation.</summary>
    Duplicate = 1,
}

/// <summary>Identifies the proof supplied by an authorized manual-resolution command.</summary>
public enum DurableManualResolutionKind
{
    /// <summary>The original external effect is proven applied and the registered terminal result is supplied.</summary>
    Applied = 0,
    /// <summary>The original external effect is proven not applied, so retry or cancellation is safe.</summary>
    ProvenNotApplied = 1,
}

/// <summary>Reports the authoritative result of a work operator command.</summary>
public sealed record DurableWorkOperatorResult(
    DurableWorkId WorkId,
    DurableWorkOperatorOutcome Outcome,
    DurableWorkState State,
    long Revision);

/// <summary>Requests side-effect-free reconciliation for suspended ReconcileBeforeRetry work.</summary>
public sealed record DurableWorkReconcileRequest
{
    /// <summary>Initializes an audited reconciliation command.</summary>
    public DurableWorkReconcileRequest(
        DurableScopeId scopeId,
        DurableWorkId workId,
        DurableCommandId commandId,
        string actorId,
        string reasonCode,
        long expectedRevision)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(workId, nameof(workId));
        ProviderContractValidation.Require(commandId, nameof(commandId));
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        ScopeId = scopeId;
        WorkId = workId;
        CommandId = commandId;
        ActorId = ProviderContractValidation.Require(actorId, nameof(actorId), 200);
        ReasonCode = ProviderContractValidation.Require(reasonCode, nameof(reasonCode), 120);
        ExpectedRevision = expectedRevision;
        Fingerprint = ProviderCommandFingerprints.Create(
            "appsurface.durable.work.reconcile.v1",
            ScopeId,
            WorkId,
            ActorId,
            ReasonCode,
            ExpectedRevision);
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }
    /// <summary>Gets the suspended work aggregate.</summary>
    public DurableWorkId WorkId { get; }
    /// <summary>Gets the idempotent operator command identity.</summary>
    public DurableCommandId CommandId { get; }
    /// <summary>Gets the authorized privacy-safe actor identity.</summary>
    public string ActorId { get; }
    /// <summary>Gets the privacy-safe reason code.</summary>
    public string ReasonCode { get; }
    /// <summary>Gets the expected work revision.</summary>
    public long ExpectedRevision { get; }
    /// <summary>Gets the computed semantic command fingerprint.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>Requests an audited resolution for ManualResolution work.</summary>
public sealed record DurableWorkManualResolutionRequest
{
    /// <summary>Initializes a manual-resolution command.</summary>
    public DurableWorkManualResolutionRequest(
        DurableScopeId scopeId,
        DurableWorkId workId,
        DurableCommandId commandId,
        string actorId,
        string reasonCode,
        long expectedRevision,
        DurableManualResolutionKind resolution,
        DurableEncodedPayload? result = null)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(workId, nameof(workId));
        ProviderContractValidation.Require(commandId, nameof(commandId));
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        if (!Enum.IsDefined(resolution))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }

        if ((resolution == DurableManualResolutionKind.Applied) != (result is not null))
        {
            throw new ArgumentException("Only an applied manual resolution carries a registered terminal result.", nameof(result));
        }

        ScopeId = scopeId;
        WorkId = workId;
        CommandId = commandId;
        ActorId = ProviderContractValidation.Require(actorId, nameof(actorId), 200);
        ReasonCode = ProviderContractValidation.Require(reasonCode, nameof(reasonCode), 120);
        ExpectedRevision = expectedRevision;
        Resolution = resolution;
        Result = result;
        Fingerprint = ProviderCommandFingerprints.Create(
            "appsurface.durable.work.manual-resolution.v1",
            ScopeId,
            WorkId,
            ActorId,
            ReasonCode,
            ExpectedRevision,
            Resolution,
            Result);
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }
    /// <summary>Gets the suspended work aggregate.</summary>
    public DurableWorkId WorkId { get; }
    /// <summary>Gets the idempotent operator command identity.</summary>
    public DurableCommandId CommandId { get; }
    /// <summary>Gets the authorized privacy-safe actor identity.</summary>
    public string ActorId { get; }
    /// <summary>Gets the privacy-safe reason code.</summary>
    public string ReasonCode { get; }
    /// <summary>Gets the expected work revision.</summary>
    public long ExpectedRevision { get; }
    /// <summary>Gets the provider proof supplied by the operator.</summary>
    public DurableManualResolutionKind Resolution { get; }
    /// <summary>Gets the exact registered result when the effect is proven applied.</summary>
    public DurableEncodedPayload? Result { get; }
    /// <summary>Gets the computed semantic command fingerprint.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>Requests release of suspended work only when its effect policy proves a retry is safe.</summary>
public sealed record DurableWorkRetrySafeRequest
{
    /// <summary>Initializes an audited safe-retry release.</summary>
    public DurableWorkRetrySafeRequest(
        DurableScopeId scopeId,
        DurableWorkId workId,
        DurableCommandId commandId,
        string actorId,
        string reasonCode,
        long expectedRevision)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(workId, nameof(workId));
        ProviderContractValidation.Require(commandId, nameof(commandId));
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        ScopeId = scopeId;
        WorkId = workId;
        CommandId = commandId;
        ActorId = ProviderContractValidation.Require(actorId, nameof(actorId), 200);
        ReasonCode = ProviderContractValidation.Require(reasonCode, nameof(reasonCode), 120);
        ExpectedRevision = expectedRevision;
        Fingerprint = ProviderCommandFingerprints.Create(
            "appsurface.durable.work.retry-safe.v1",
            ScopeId,
            WorkId,
            ActorId,
            ReasonCode,
            ExpectedRevision);
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }
    /// <summary>Gets the suspended work aggregate.</summary>
    public DurableWorkId WorkId { get; }
    /// <summary>Gets the idempotent operator command identity.</summary>
    public DurableCommandId CommandId { get; }
    /// <summary>Gets the authorized privacy-safe actor identity.</summary>
    public string ActorId { get; }
    /// <summary>Gets the privacy-safe reason code.</summary>
    public string ReasonCode { get; }
    /// <summary>Gets the expected work revision.</summary>
    public long ExpectedRevision { get; }
    /// <summary>Gets the computed semantic command fingerprint.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>Requests release of nonterminal work fenced by a rotated restore epoch.</summary>
public sealed record DurableWorkRecoveryReleaseRequest
{
    /// <summary>Initializes an audited recovery release.</summary>
    public DurableWorkRecoveryReleaseRequest(
        DurableScopeId scopeId,
        DurableWorkId workId,
        DurableCommandId commandId,
        string actorId,
        string reasonCode,
        long expectedRevision)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(workId, nameof(workId));
        ProviderContractValidation.Require(commandId, nameof(commandId));
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        ScopeId = scopeId;
        WorkId = workId;
        CommandId = commandId;
        ActorId = ProviderContractValidation.Require(actorId, nameof(actorId), 200);
        ReasonCode = ProviderContractValidation.Require(reasonCode, nameof(reasonCode), 120);
        ExpectedRevision = expectedRevision;
        Fingerprint = ProviderCommandFingerprints.Create(
            "appsurface.durable.work.recovery-release.v1",
            ScopeId,
            WorkId,
            ActorId,
            ReasonCode,
            ExpectedRevision);
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }
    /// <summary>Gets the old-epoch nonterminal work aggregate.</summary>
    public DurableWorkId WorkId { get; }
    /// <summary>Gets the idempotent operator command identity.</summary>
    public DurableCommandId CommandId { get; }
    /// <summary>Gets the authorized privacy-safe actor identity.</summary>
    public string ActorId { get; }
    /// <summary>Gets the privacy-safe reason code.</summary>
    public string ReasonCode { get; }
    /// <summary>Gets the expected work revision.</summary>
    public long ExpectedRevision { get; }
    /// <summary>Gets the computed semantic command fingerprint.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>
/// Provides audited recovery operations for suspended durable work.
/// </summary>
/// <remarks>
/// Applications must authorize scope and actor before calling this surface. Reconciliation is a provider read and
/// must never repeat the mutation. Manual resolution accepts only an exact registered result or proof of no effect.
/// Safe retry and recovery release fail closed when a prior permit remains ambiguous.
/// </remarks>
public interface IDurableWorkOperatorClient
{
    /// <summary>Runs the registered side-effect-free reconciler and commits its proof.</summary>
    ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ReconcileAsync(
        DurableWorkReconcileRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits authorized applied or proven-not-applied provider truth for manual work or canceled replay-safe
    /// ambiguity.
    /// </summary>
    /// <remarks>
    /// Idempotent and provider-keyed work is eligible only while suspended with an ambiguous permit and a preserved
    /// cancellation request. Applied proof becomes succeeded-after-cancel; proven-not-applied proof becomes canceled
    /// before effect. This lets an operator honor cancellation without authorizing replay.
    /// </remarks>
    ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ResolveAsync(
        DurableWorkManualResolutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Releases suspended work only when replay is safe under its immutable provider policy.</summary>
    /// <remarks>
    /// This audited command explicitly overrides and consumes a preserved cancellation request before making work
    /// eligible again. Use <see cref="ResolveAsync"/> instead when provider proof can honor cancellation without replay.
    /// </remarks>
    ValueTask<DurableOperationResult<DurableWorkOperatorResult>> RetrySafeAsync(
        DurableWorkRetrySafeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-fences old-epoch nonterminal work to the configured runtime epoch without erasing due time or effect evidence.
    /// </summary>
    ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ReleaseAfterRecoveryAsync(
        DurableWorkRecoveryReleaseRequest request,
        CancellationToken cancellationToken = default);
}
