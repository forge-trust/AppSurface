namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Identifies one durable Flow instance independently of its current node or runtime claim.
/// </summary>
public readonly record struct DurableFlowInstanceId
{
    /// <summary>
    /// Initializes a durable Flow instance identifier.
    /// </summary>
    public DurableFlowInstanceId(string value)
    {
        Value = DurableIdentifier.Require(value, nameof(value), 200);
    }

    /// <summary>Gets the opaque identifier value.</summary>
    public string Value { get; }

    /// <summary>Creates a cryptographically random Flow instance identifier.</summary>
    public static DurableFlowInstanceId New() => new(Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Identifies a single-use external Flow event independently of transport retries.
/// </summary>
public readonly record struct DurableFlowEventId
{
    /// <summary>
    /// Initializes a durable Flow event identifier.
    /// </summary>
    public DurableFlowEventId(string value)
    {
        Value = DurableIdentifier.Require(value, nameof(value), 200);
    }

    /// <summary>Gets the opaque identifier value.</summary>
    public string Value { get; }

    /// <summary>Creates a cryptographically random event identifier.</summary>
    public static DurableFlowEventId New() => new(Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Represents the authoritative lifecycle state of a durable Flow instance.
/// </summary>
public enum DurableFlowState
{
    /// <summary>Accepted and eligible for one-node evaluation.</summary>
    Ready = 0,
    /// <summary>Waiting for an external event.</summary>
    WaitingForEvent = 1,
    /// <summary>Waiting for a durable timer.</summary>
    WaitingForTimer = 2,
    /// <summary>Waiting for one durable activity result.</summary>
    WaitingForActivity = 3,
    /// <summary>Cancellation has been requested while activity truth is unresolved.</summary>
    CancelPending = 4,
    /// <summary>Completed successfully.</summary>
    Completed = 5,
    /// <summary>Faulted with a process-level Flow failure.</summary>
    Faulted = 6,
    /// <summary>Canceled without erasing a known or possible external effect.</summary>
    Canceled = 7,
    /// <summary>Suspended pending compatibility, reconciliation, or operator repair.</summary>
    Suspended = 8,
}

/// <summary>
/// Describes one idempotent durable Flow start.
/// </summary>
public sealed record DurableFlowStartRequest
{
    /// <summary>
    /// Initializes a durable Flow start request.
    /// </summary>
    public DurableFlowStartRequest(
        DurableScopeId scopeId,
        DurableCommandId commandId,
        string idempotencyKey,
        DurableFlowInstanceId instanceId,
        string flowId,
        string flowVersion,
        DurableEncodedPayload context)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        DurableIdentifier.Require(instanceId.Value, nameof(instanceId), 200);
        ScopeId = scopeId;
        CommandId = commandId;
        IdempotencyKey = DurableIdentifier.Require(idempotencyKey, nameof(idempotencyKey), 200);
        InstanceId = instanceId;
        FlowId = DurableIdentifier.Require(flowId, nameof(flowId), 200);
        FlowVersion = DurableIdentifier.Require(flowVersion, nameof(flowVersion), 100);
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the idempotent command identifier.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the caller retry key.</summary>
    public string IdempotencyKey { get; }

    /// <summary>Gets the requested Flow instance identifier.</summary>
    public DurableFlowInstanceId InstanceId { get; }

    /// <summary>Gets the registered Flow definition identifier.</summary>
    public string FlowId { get; }

    /// <summary>Gets the immutable Flow definition version.</summary>
    public string FlowVersion { get; }

    /// <summary>Gets the encoded initial context.</summary>
    public DurableEncodedPayload Context { get; }
}

/// <summary>
/// Delivers one authorized external event only to an active matching wait.
/// </summary>
public sealed record DurableFlowEventRequest
{
    /// <summary>
    /// Initializes an external event request.
    /// </summary>
    public DurableFlowEventRequest(
        DurableScopeId scopeId,
        DurableCommandId commandId,
        DurableFlowEventId eventId,
        DurableFlowInstanceId instanceId,
        string eventName,
        DurableEncodedPayload? payload = null,
        long? expectedRevision = null)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        DurableIdentifier.Require(eventId.Value, nameof(eventId), 200);
        DurableIdentifier.Require(instanceId.Value, nameof(instanceId), 200);
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        ScopeId = scopeId;
        CommandId = commandId;
        EventId = eventId;
        InstanceId = instanceId;
        EventName = DurableIdentifier.Require(eventName, nameof(eventName), 200);
        Payload = payload;
        ExpectedRevision = expectedRevision;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the command identifier used for transport deduplication.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the single-use event identifier.</summary>
    public DurableFlowEventId EventId { get; }

    /// <summary>Gets the target Flow instance.</summary>
    public DurableFlowInstanceId InstanceId { get; }

    /// <summary>Gets the exact case-sensitive wait event name.</summary>
    public string EventName { get; }

    /// <summary>Gets the optional allowlisted event payload.</summary>
    public DurableEncodedPayload? Payload { get; }

    /// <summary>Gets an optional optimistic Flow revision.</summary>
    public long? ExpectedRevision { get; }
}

/// <summary>
/// Requests cooperative cancellation of a durable Flow instance.
/// </summary>
public sealed record DurableFlowCancelRequest
{
    /// <summary>
    /// Initializes a Flow cancellation request.
    /// </summary>
    /// <param name="scopeId">Trusted owning scope.</param>
    /// <param name="commandId">Idempotent cancellation command identifier.</param>
    /// <param name="instanceId">Target Flow instance.</param>
    /// <param name="actorId">Privacy-safe identifier for the authorized actor requesting cancellation.</param>
    /// <param name="reasonCode">Privacy-safe machine-readable cancellation reason.</param>
    /// <param name="expectedRevision">Required optimistic Flow revision.</param>
    public DurableFlowCancelRequest(
        DurableScopeId scopeId,
        DurableCommandId commandId,
        DurableFlowInstanceId instanceId,
        string actorId,
        string reasonCode,
        long expectedRevision)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        DurableIdentifier.Require(instanceId.Value, nameof(instanceId), 200);
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        ScopeId = scopeId;
        CommandId = commandId;
        InstanceId = instanceId;
        ActorId = DurableIdentifier.Require(actorId, nameof(actorId), 200);
        ReasonCode = DurableIdentifier.Require(reasonCode, nameof(reasonCode), 120);
        ExpectedRevision = expectedRevision;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the command identifier.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the target Flow instance.</summary>
    public DurableFlowInstanceId InstanceId { get; }

    /// <summary>Gets the privacy-safe authorized actor identifier recorded for audit.</summary>
    public string ActorId { get; }

    /// <summary>Gets the privacy-safe cancellation reason code.</summary>
    public string ReasonCode { get; }

    /// <summary>Gets the required optimistic Flow revision.</summary>
    public long ExpectedRevision { get; }
}

/// <summary>
/// Requests an audited release of a recoverably suspended Flow, or direct adoption of an exact-revision dormant Flow
/// from an older runtime epoch, after an operator has verified runtime compatibility or resolved ambiguous child-work
/// truth.
/// </summary>
/// <remarks>
/// Direct epoch adoption retains the instance's prior nonterminal state and active wait shape. It does not adopt an
/// evaluating, terminal, or current-epoch non-suspended instance, and it never bypasses manifest compatibility.
/// </remarks>
public sealed record DurableFlowReleaseRequest
{
    /// <summary>Initializes an audited Flow recovery release request.</summary>
    /// <param name="scopeId">Trusted owning scope.</param>
    /// <param name="commandId">Idempotent operator command identifier.</param>
    /// <param name="instanceId">Target Flow instance.</param>
    /// <param name="actorId">Privacy-safe identifier for the authorized operator.</param>
    /// <param name="reasonCode">Privacy-safe machine-readable release reason.</param>
    /// <param name="expectedRevision">Required Flow revision observed by the operator.</param>
    public DurableFlowReleaseRequest(
        DurableScopeId scopeId,
        DurableCommandId commandId,
        DurableFlowInstanceId instanceId,
        string actorId,
        string reasonCode,
        long expectedRevision)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        DurableIdentifier.Require(instanceId.Value, nameof(instanceId), 200);
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        ScopeId = scopeId;
        CommandId = commandId;
        InstanceId = instanceId;
        ActorId = DurableIdentifier.Require(actorId, nameof(actorId), 200);
        ReasonCode = DurableIdentifier.Require(reasonCode, nameof(reasonCode), 120);
        ExpectedRevision = expectedRevision;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the idempotent operator command identifier.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the target Flow instance.</summary>
    public DurableFlowInstanceId InstanceId { get; }

    /// <summary>Gets the privacy-safe authorized operator identifier recorded for audit.</summary>
    public string ActorId { get; }

    /// <summary>Gets the privacy-safe release reason recorded for audit.</summary>
    public string ReasonCode { get; }

    /// <summary>Gets the required Flow revision.</summary>
    public long ExpectedRevision { get; }
}

/// <summary>Requests a payload-free snapshot of one Flow in an application-authorized scope.</summary>
public sealed record DurableFlowGetRequest
{
    /// <summary>Initializes a scoped Flow lookup.</summary>
    /// <param name="scopeId">Trusted owning scope.</param>
    /// <param name="instanceId">Opaque Flow instance identifier.</param>
    public DurableFlowGetRequest(DurableScopeId scopeId, DurableFlowInstanceId instanceId)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(instanceId.Value, nameof(instanceId), 200);
        ScopeId = scopeId;
        InstanceId = instanceId;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the target Flow instance.</summary>
    public DurableFlowInstanceId InstanceId { get; }
}

/// <summary>Requests one bounded, authorized page of payload-free Flow snapshots.</summary>
/// <remarks>
/// The optional recovery filter identifies dormant nonterminal instances whose persisted runtime epoch differs from
/// the active client epoch. It is an inventory aid, not proof that manifest, state-shape, or external-effect recovery
/// checks will permit release.
/// </remarks>
public sealed record DurableFlowListRequest
{
    /// <summary>Initializes a scoped Flow list request.</summary>
    /// <param name="scopeId">Trusted owning scope.</param>
    /// <param name="state">Optional authoritative lifecycle-state filter.</param>
    /// <param name="requiresRecoveryRelease">
    /// Optional old-epoch recovery filter; <see langword="null"/> returns both matching and nonmatching instances.
    /// </param>
    /// <param name="pageSize">Maximum snapshots to return, from 1 through 1,000.</param>
    /// <param name="continuationToken">Opaque token returned by the prior page, or <see langword="null"/>.</param>
    public DurableFlowListRequest(
        DurableScopeId scopeId,
        DurableFlowState? state = null,
        bool? requiresRecoveryRelease = null,
        int pageSize = 100,
        string? continuationToken = null)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        if (state.HasValue && !Enum.IsDefined(state.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (pageSize is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        ScopeId = scopeId;
        State = state;
        RequiresRecoveryRelease = requiresRecoveryRelease;
        PageSize = pageSize;
        ContinuationToken = continuationToken is null
            ? null
            : DurableIdentifier.Require(continuationToken, nameof(continuationToken), 200);
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the optional lifecycle-state filter.</summary>
    public DurableFlowState? State { get; }

    /// <summary>Gets the optional old-epoch recovery filter.</summary>
    public bool? RequiresRecoveryRelease { get; }

    /// <summary>Gets the maximum snapshots returned by this page.</summary>
    public int PageSize { get; }

    /// <summary>Gets the opaque continuation token, or <see langword="null"/> for the first page.</summary>
    public string? ContinuationToken { get; }
}

/// <summary>Represents one bounded page of payload-free Flow snapshots.</summary>
public sealed record DurableFlowListResult
{
    /// <summary>Initializes a Flow list result.</summary>
    public DurableFlowListResult(IReadOnlyList<DurableFlowSnapshot> flows, string? continuationToken)
    {
        ArgumentNullException.ThrowIfNull(flows);
        Flows = flows.ToArray();
        ContinuationToken = continuationToken;
    }

    /// <summary>Gets the immutable page of Flow snapshots.</summary>
    public IReadOnlyList<DurableFlowSnapshot> Flows { get; }

    /// <summary>Gets the next opaque continuation token, or <see langword="null"/> on the final page.</summary>
    public string? ContinuationToken { get; }
}

/// <summary>
/// Provides payload-free durable Flow state suitable for status pages, optimistic commands, and operator recovery.
/// </summary>
public sealed record DurableFlowSnapshot
{
    /// <summary>Initializes a payload-free Flow snapshot.</summary>
    public DurableFlowSnapshot(
        DurableFlowInstanceId instanceId,
        string flowId,
        string flowVersion,
        DurableFlowState state,
        string currentNodeId,
        long revision,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? cancellationRequestedAtUtc,
        DateTimeOffset? terminalAtUtc,
        string? terminalCode,
        bool requiresRecoveryRelease = false)
    {
        DurableIdentifier.Require(instanceId.Value, nameof(instanceId), 200);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        InstanceId = instanceId;
        FlowId = DurableIdentifier.Require(flowId, nameof(flowId), 200);
        FlowVersion = DurableIdentifier.Require(flowVersion, nameof(flowVersion), 100);
        State = state;
        CurrentNodeId = DurableIdentifier.Require(currentNodeId, nameof(currentNodeId), 200);
        Revision = revision;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        CancellationRequestedAtUtc = cancellationRequestedAtUtc?.ToUniversalTime();
        TerminalAtUtc = terminalAtUtc?.ToUniversalTime();
        TerminalCode = terminalCode is null
            ? null
            : DurableIdentifier.Require(terminalCode, nameof(terminalCode), 120);
        RequiresRecoveryRelease = requiresRecoveryRelease;
    }

    /// <summary>Gets the Flow instance identifier.</summary>
    public DurableFlowInstanceId InstanceId { get; }

    /// <summary>Gets the immutable Flow definition identifier.</summary>
    public string FlowId { get; }

    /// <summary>Gets the immutable Flow definition version.</summary>
    public string FlowVersion { get; }

    /// <summary>Gets the authoritative lifecycle state.</summary>
    public DurableFlowState State { get; }

    /// <summary>Gets the current stable node identifier.</summary>
    public string CurrentNodeId { get; }

    /// <summary>Gets the aggregate revision required by optimistic commands.</summary>
    public long Revision { get; }

    /// <summary>Gets when the Flow was accepted.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets when the Flow aggregate last changed.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Gets when cancellation was first requested, if applicable.</summary>
    public DateTimeOffset? CancellationRequestedAtUtc { get; }

    /// <summary>Gets when a terminal state was reached, if applicable.</summary>
    public DateTimeOffset? TerminalAtUtc { get; }

    /// <summary>Gets the privacy-safe terminal or suspension code, if present.</summary>
    public string? TerminalCode { get; }

    /// <summary>
    /// Gets whether this dormant nonterminal instance belongs to an older runtime epoch and requires an audited
    /// recovery release before the active runtime may continue it.
    /// </summary>
    /// <remarks>
    /// This inventory flag does not prove manifest compatibility, wait-shape validity, or resolved external-effect
    /// truth. Evaluating and terminal rows are false because they are not directly releasable dormant instances.
    /// </remarks>
    public bool RequiresRecoveryRelease { get; }
}

/// <summary>
/// Describes the result of a durable Flow command.
/// </summary>
public enum DurableFlowCommandOutcome
{
    /// <summary>A new command was accepted.</summary>
    Accepted = 0,
    /// <summary>The exact prior command outcome was returned.</summary>
    Duplicate = 1,
    /// <summary>The event arrived before its matching wait and its event id remains reusable.</summary>
    NotWaitingYet = 2,
    /// <summary>A timer, event, cancellation, or terminal transition won the same revision race.</summary>
    RaceLost = 3,
    /// <summary>The instance was already terminal and did not change.</summary>
    AlreadyTerminal = 4,
}

/// <summary>
/// Records the stable outcome of a durable Flow command.
/// </summary>
public sealed record DurableFlowCommandResult
{
    /// <summary>
    /// Initializes a Flow command result.
    /// </summary>
    public DurableFlowCommandResult(
        DurableFlowInstanceId instanceId,
        DurableFlowCommandOutcome outcome,
        DurableFlowState state,
        long revision)
    {
        DurableIdentifier.Require(instanceId.Value, nameof(instanceId), 200);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        InstanceId = instanceId;
        Outcome = outcome;
        State = state;
        Revision = revision;
    }

    /// <summary>Gets the Flow instance.</summary>
    public DurableFlowInstanceId InstanceId { get; }

    /// <summary>Gets the command outcome.</summary>
    public DurableFlowCommandOutcome Outcome { get; }

    /// <summary>Gets the resulting authoritative state.</summary>
    public DurableFlowState State { get; }

    /// <summary>Gets the resulting aggregate revision.</summary>
    public long Revision { get; }
}

/// <summary>
/// Starts, resumes, and cancels durable Flow instances through application-authorized calls.
/// </summary>
public interface IDurableFlowClient
{
    /// <summary>Reads a payload-free snapshot from the authorized scope.</summary>
    ValueTask<DurableOperationResult<DurableFlowSnapshot>> GetAsync(
        DurableFlowGetRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists one bounded, authorized page of payload-free Flow snapshots, optionally filtered by lifecycle state and
    /// old-epoch recovery requirement.
    /// </summary>
    ValueTask<DurableOperationResult<DurableFlowListResult>> ListAsync(
        DurableFlowListRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Accepts an idempotent Flow start.</summary>
    ValueTask<DurableOperationResult<DurableFlowCommandResult>> StartAsync(
        DurableFlowStartRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers an authorized event to an active matching wait.
    /// </summary>
    /// <remarks>
    /// V1 does not buffer early events. <see cref="DurableFlowCommandOutcome.NotWaitingYet"/> means the event id was not
    /// consumed and the caller may retry after observing the wait.
    /// </remarks>
    ValueTask<DurableOperationResult<DurableFlowCommandResult>> RaiseEventAsync(
        DurableFlowEventRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Requests cooperative cancellation at an expected revision.</summary>
    ValueTask<DurableOperationResult<DurableFlowCommandResult>> CancelAsync(
        DurableFlowCancelRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a recoverable suspension or directly adopts a dormant nonterminal Flow from an older runtime epoch,
    /// only after exact runtime-manifest, optimistic-revision, and persisted-state-shape validation. Authorization
    /// remains application-owned.
    /// </summary>
    /// <remarks>
    /// A direct epoch release preserves the prior ready, event-wait, timer-wait, activity-wait, or cancel-pending
    /// state. A terminal result is reported without mutation; incompatible manifests and unsafe state shapes fail.
    /// </remarks>
    ValueTask<DurableOperationResult<DurableFlowCommandResult>> ReleaseSuspensionAsync(
        DurableFlowReleaseRequest request,
        CancellationToken cancellationToken = default);
}
