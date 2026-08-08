using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>Identifies the closed set of evidence-backed Flow repair assertions supported by the Provider API.</summary>
/// <remarks>
/// These values deliberately do not include a generic resume, release, or force-terminate operation. A host must
/// authorize the trusted scope before it calls this API; <c>ActorId</c> is immutable audit metadata, not proof of
/// authorization.
/// </remarks>
public enum DurableFlowRepairAction
{
    /// <summary>Asserts that the retained child Work effect completed with the referenced retained result.</summary>
    AssertChildEffectCompleted = 0,

    /// <summary>Asserts that a named manual-resolution command proved the child effect was not applied.</summary>
    AssertChildEffectNotApplied = 1,
}

/// <summary>Identifies the stable outcome of an idempotent Flow repair request.</summary>
public enum DurableFlowRepairOutcome
{
    /// <summary>The requested assertion was accepted and changed only the permitted Flow state.</summary>
    Applied = 0,

    /// <summary>The original terminal result was returned without repeating a state transition.</summary>
    Duplicate = 1,

    /// <summary>A competing Flow transition won after the request started.</summary>
    RaceLost = 2,

    /// <summary>The retained descriptor, state, or evidence does not admit the requested action.</summary>
    Refused = 3,

    /// <summary>The command identifier was already used with different semantic request content.</summary>
    Conflict = 4,
}

/// <summary>
/// References one retained child-Work evidence fact without disclosing a result payload, provider response, or
/// credentials.
/// </summary>
public sealed record DurableFlowRepairEvidenceReference
{
    private DurableFlowRepairEvidenceReference(
        DurableWorkId childWorkId,
        long expectedChildWorkRevision,
        long childWorkHistoryEventId,
        string? expectedChildResultSha256,
        DurableCommandId? requiredWorkOperatorCommandId)
    {
        ProviderContractValidation.Require(childWorkId, nameof(childWorkId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedChildWorkRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childWorkHistoryEventId);
        if ((expectedChildResultSha256 is null) == (requiredWorkOperatorCommandId is null))
        {
            throw new ArgumentException(
                "Flow repair evidence must identify exactly one completed-result digest or proven-not-applied operator command.");
        }

        ChildWorkId = childWorkId;
        ExpectedChildWorkRevision = expectedChildWorkRevision;
        ChildWorkHistoryEventId = childWorkHistoryEventId;
        ExpectedChildResultSha256 = expectedChildResultSha256 is null
            ? null
            : ProviderContractValidation.RequireSha256(expectedChildResultSha256, nameof(expectedChildResultSha256));
        RequiredWorkOperatorCommandId = requiredWorkOperatorCommandId;
        if (requiredWorkOperatorCommandId is { } commandId)
        {
            ProviderContractValidation.Require(commandId, nameof(requiredWorkOperatorCommandId));
        }
    }

    /// <summary>Creates evidence for a retained terminal child result.</summary>
    public static DurableFlowRepairEvidenceReference Completed(
        DurableWorkId childWorkId,
        long expectedChildWorkRevision,
        long childWorkHistoryEventId,
        string expectedChildResultSha256) =>
        new(childWorkId, expectedChildWorkRevision, childWorkHistoryEventId, expectedChildResultSha256, null);

    /// <summary>Creates evidence for a named completed manual resolution that proved the effect was not applied.</summary>
    public static DurableFlowRepairEvidenceReference ProvenNotApplied(
        DurableWorkId childWorkId,
        long expectedChildWorkRevision,
        long childWorkHistoryEventId,
        DurableCommandId requiredWorkOperatorCommandId) =>
        new(childWorkId, expectedChildWorkRevision, childWorkHistoryEventId, null, requiredWorkOperatorCommandId);

    internal static void RequireForAction(
        DurableFlowRepairAction action,
        DurableFlowRepairEvidenceReference evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (action == DurableFlowRepairAction.AssertChildEffectCompleted
            && (evidence.ExpectedChildResultSha256 is null || evidence.RequiredWorkOperatorCommandId is not null))
        {
            throw new ArgumentException("Completed-effect repair requires only a retained child-result digest.", nameof(evidence));
        }

        if (action == DurableFlowRepairAction.AssertChildEffectNotApplied
            && (evidence.ExpectedChildResultSha256 is not null || evidence.RequiredWorkOperatorCommandId is null))
        {
            throw new ArgumentException("No-effect repair requires only a completed proven-not-applied operator command.", nameof(evidence));
        }
    }

    /// <summary>Gets the child Work aggregate that supplied the retained evidence.</summary>
    public DurableWorkId ChildWorkId { get; }

    /// <summary>Gets the child Work revision that must still match under the repair transaction lock.</summary>
    public long ExpectedChildWorkRevision { get; }

    /// <summary>Gets the positive append-only child Work history event identity.</summary>
    public long ChildWorkHistoryEventId { get; }

    /// <summary>Gets the expected retained child-result digest for the completed-effect assertion, if applicable.</summary>
    public string? ExpectedChildResultSha256 { get; }

    /// <summary>Gets the required completed manual-resolution command for the no-effect assertion, if applicable.</summary>
    public DurableCommandId? RequiredWorkOperatorCommandId { get; }
}

/// <summary>Requests an audited, revision-fenced assertion about one suspended child-effect Flow.</summary>
public sealed record DurableFlowRepairRequest
{
    private DurableFlowRepairRequest(
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableCommandId commandId,
        long expectedFlowRevision,
        string expectedSuspensionDescriptorSha256,
        DurableFlowRepairAction action,
        DurableFlowRepairEvidenceReference evidence,
        string actorId,
        string reasonCode)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(instanceId, nameof(instanceId));
        ProviderContractValidation.Require(commandId, nameof(commandId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedFlowRevision);
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        DurableFlowRepairEvidenceReference.RequireForAction(action, evidence);

        ScopeId = scopeId;
        InstanceId = instanceId;
        CommandId = commandId;
        ExpectedFlowRevision = expectedFlowRevision;
        ExpectedSuspensionDescriptorSha256 = ProviderContractValidation.RequireSha256(
            expectedSuspensionDescriptorSha256,
            nameof(expectedSuspensionDescriptorSha256));
        Action = action;
        Evidence = evidence;
        ActorId = ProviderContractValidation.Require(actorId, nameof(actorId), 200);
        ReasonCode = ProviderContractValidation.Require(reasonCode, nameof(reasonCode), 120);
        Fingerprint = ProviderCommandFingerprints.CreateFlowRepair(
            ScopeId,
            InstanceId,
            ExpectedFlowRevision,
            ExpectedSuspensionDescriptorSha256,
            Action,
            Evidence,
            ActorId,
            ReasonCode);
    }

    /// <summary>Creates a completed-effect repair request from a retained child result digest.</summary>
    public static DurableFlowRepairRequest AssertChildEffectCompleted(
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableCommandId commandId,
        long expectedFlowRevision,
        string expectedSuspensionDescriptorSha256,
        DurableWorkId childWorkId,
        long expectedChildWorkRevision,
        long childWorkHistoryEventId,
        string expectedChildResultSha256,
        string actorId,
        string reasonCode) =>
        new(
            scopeId,
            instanceId,
            commandId,
            expectedFlowRevision,
            expectedSuspensionDescriptorSha256,
            DurableFlowRepairAction.AssertChildEffectCompleted,
            DurableFlowRepairEvidenceReference.Completed(
                childWorkId,
                expectedChildWorkRevision,
                childWorkHistoryEventId,
                expectedChildResultSha256),
            actorId,
            reasonCode);

    /// <summary>Creates a no-effect repair request from a named proven-not-applied manual resolution.</summary>
    public static DurableFlowRepairRequest AssertChildEffectNotApplied(
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableCommandId commandId,
        long expectedFlowRevision,
        string expectedSuspensionDescriptorSha256,
        DurableWorkId childWorkId,
        long expectedChildWorkRevision,
        long childWorkHistoryEventId,
        DurableCommandId requiredWorkOperatorCommandId,
        string actorId,
        string reasonCode) =>
        new(
            scopeId,
            instanceId,
            commandId,
            expectedFlowRevision,
            expectedSuspensionDescriptorSha256,
            DurableFlowRepairAction.AssertChildEffectNotApplied,
            DurableFlowRepairEvidenceReference.ProvenNotApplied(
                childWorkId,
                expectedChildWorkRevision,
                childWorkHistoryEventId,
                requiredWorkOperatorCommandId),
            actorId,
            reasonCode);

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the suspended Flow instance.</summary>
    public DurableFlowInstanceId InstanceId { get; }

    /// <summary>Gets the idempotent repair command identity.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the Flow revision that must match under lock.</summary>
    public long ExpectedFlowRevision { get; }

    /// <summary>Gets the expected V1 suspension descriptor digest.</summary>
    public string ExpectedSuspensionDescriptorSha256 { get; }

    /// <summary>Gets the closed repair assertion.</summary>
    public DurableFlowRepairAction Action { get; }

    /// <summary>Gets the bounded retained evidence reference.</summary>
    public DurableFlowRepairEvidenceReference Evidence { get; }

    /// <summary>Gets the privacy-safe audit actor identifier; it does not authorize the call.</summary>
    public string ActorId { get; }

    /// <summary>Gets the privacy-safe machine-readable repair reason.</summary>
    public string ReasonCode { get; }

    /// <summary>Gets the versioned semantic fingerprint used for replay and collision comparison.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>Provides an immutable, payload-free receipt for an accepted Flow repair assertion.</summary>
public sealed record DurableFlowRepairReceipt
{
    /// <summary>Initializes a canonical V1 repair receipt.</summary>
    public DurableFlowRepairReceipt(
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableCommandId commandId,
        DurableFlowRepairAction action,
        DurableCommandFingerprint requestFingerprint,
        string suspensionDescriptorSha256,
        DurableFlowRepairEvidenceReference evidence,
        string actorId,
        string reasonCode,
        DurableFlowState priorState,
        long priorRevision,
        DurableFlowState resultingState,
        long resultingRevision,
        long resultingFlowHistoryEventId,
        DateTimeOffset acceptedAtUtc)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(instanceId, nameof(instanceId));
        ProviderContractValidation.Require(commandId, nameof(commandId));
        ArgumentNullException.ThrowIfNull(requestFingerprint);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        if (!Enum.IsDefined(priorState))
        {
            throw new ArgumentOutOfRangeException(nameof(priorState));
        }

        if (!Enum.IsDefined(resultingState))
        {
            throw new ArgumentOutOfRangeException(nameof(resultingState));
        }

        DurableFlowRepairEvidenceReference.RequireForAction(action, evidence);
        if (!string.Equals(
                requestFingerprint.SchemaId,
                ProviderCommandFingerprints.GetFlowRepairSchemaId(action),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The receipt action must match the request fingerprint schema.", nameof(requestFingerprint));
        }

        if (priorState != DurableFlowState.Suspended)
        {
            throw new ArgumentException("A Flow repair receipt must begin from the suspended state.", nameof(priorState));
        }

        var expectedResultingState = action == DurableFlowRepairAction.AssertChildEffectCompleted
            ? DurableFlowState.Ready
            : DurableFlowState.WaitingForActivity;
        if (resultingState != expectedResultingState)
        {
            throw new ArgumentException("The Flow repair receipt state must match its evidence-backed action.", nameof(resultingState));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(priorRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultingRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultingFlowHistoryEventId);
        if (resultingRevision != checked(priorRevision + 1))
        {
            throw new ArgumentException("A Flow repair receipt must advance exactly one Flow revision.", nameof(resultingRevision));
        }
        ScopeId = scopeId;
        InstanceId = instanceId;
        CommandId = commandId;
        Action = action;
        RequestFingerprint = requestFingerprint;
        SuspensionDescriptorSha256 = ProviderContractValidation.RequireSha256(
            suspensionDescriptorSha256,
            nameof(suspensionDescriptorSha256));
        Evidence = evidence;
        ActorId = ProviderContractValidation.Require(actorId, nameof(actorId), 200);
        ReasonCode = ProviderContractValidation.Require(reasonCode, nameof(reasonCode), 120);
        PriorState = priorState;
        PriorRevision = priorRevision;
        ResultingState = resultingState;
        ResultingRevision = resultingRevision;
        ResultingFlowHistoryEventId = resultingFlowHistoryEventId;
        AcceptedAtUtc = NormalizeMicroseconds(acceptedAtUtc);
        ReceiptSha256 = ProviderCommandFingerprints.CreateFlowRepairReceipt(
            ScopeId,
            InstanceId,
            CommandId,
            Action,
            RequestFingerprint,
            SuspensionDescriptorSha256,
            Evidence,
            ActorId,
            ReasonCode,
            PriorState,
            PriorRevision,
            ResultingState,
            ResultingRevision,
            ResultingFlowHistoryEventId,
            AcceptedAtUtc);
    }

    /// <summary>Gets the trusted scope that owns every referenced durable record.</summary>
    public DurableScopeId ScopeId { get; }
    /// <summary>Gets the repaired Flow instance.</summary>
    public DurableFlowInstanceId InstanceId { get; }
    /// <summary>Gets the stable repair command and receipt identity.</summary>
    public DurableCommandId CommandId { get; }
    /// <summary>Gets the accepted repair assertion.</summary>
    public DurableFlowRepairAction Action { get; }
    /// <summary>Gets the original versioned request fingerprint.</summary>
    public DurableCommandFingerprint RequestFingerprint { get; }
    /// <summary>Gets the locked V1 suspension descriptor digest.</summary>
    public string SuspensionDescriptorSha256 { get; }
    /// <summary>Gets the retained evidence reference bound into the receipt digest.</summary>
    public DurableFlowRepairEvidenceReference Evidence { get; }
    /// <summary>Gets the privacy-safe audit actor identifier.</summary>
    public string ActorId { get; }
    /// <summary>Gets the privacy-safe repair reason code.</summary>
    public string ReasonCode { get; }
    /// <summary>Gets the Flow state before the repair mutation.</summary>
    public DurableFlowState PriorState { get; }
    /// <summary>Gets the Flow revision before the repair mutation.</summary>
    public long PriorRevision { get; }
    /// <summary>Gets the Flow state after the repair mutation.</summary>
    public DurableFlowState ResultingState { get; }
    /// <summary>Gets the Flow revision after the repair mutation.</summary>
    public long ResultingRevision { get; }
    /// <summary>Gets the append-only Flow history event written by the repair.</summary>
    public long ResultingFlowHistoryEventId { get; }
    /// <summary>Gets the accepted UTC instant normalized to PostgreSQL microsecond precision.</summary>
    public DateTimeOffset AcceptedAtUtc { get; }
    /// <summary>Gets the canonical V1 SHA-256 digest of this payload-free receipt.</summary>
    public string ReceiptSha256 { get; }

    private static DateTimeOffset NormalizeMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
}

/// <summary>Reports the terminal result of an audited Flow repair command.</summary>
public sealed record DurableFlowRepairResult
{
    /// <summary>Initializes a validated Flow repair result.</summary>
    public DurableFlowRepairResult(
        DurableFlowRepairOutcome outcome,
        DurableFlowRepairReceipt? receipt,
        DurableProblem? problem,
        DurableFlowState? observedFlowState,
        long? observedFlowRevision)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (observedFlowState.HasValue && !Enum.IsDefined(observedFlowState.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(observedFlowState));
        }

        if (observedFlowRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(observedFlowRevision));
        }

        if ((outcome is DurableFlowRepairOutcome.Applied or DurableFlowRepairOutcome.Duplicate) != (receipt is not null))
        {
            throw new ArgumentException("Only applied and duplicate Flow repair outcomes carry a receipt.", nameof(receipt));
        }

        if ((outcome is DurableFlowRepairOutcome.Applied or DurableFlowRepairOutcome.Duplicate) && problem is not null)
        {
            throw new ArgumentException("Accepted Flow repair outcomes cannot carry a problem.", nameof(problem));
        }

        if (outcome is DurableFlowRepairOutcome.RaceLost or DurableFlowRepairOutcome.Refused or DurableFlowRepairOutcome.Conflict
            && problem is null)
        {
            throw new ArgumentException("A rejected Flow repair outcome requires a stable problem.", nameof(problem));
        }

        Outcome = outcome;
        Receipt = receipt;
        Problem = problem;
        ObservedFlowState = observedFlowState;
        ObservedFlowRevision = observedFlowRevision;
    }

    /// <summary>Gets the idempotent repair outcome.</summary>
    public DurableFlowRepairOutcome Outcome { get; }
    /// <summary>Gets the immutable receipt for an applied or duplicate result, if any.</summary>
    public DurableFlowRepairReceipt? Receipt { get; }
    /// <summary>Gets the stable refusal, race, or conflict problem, if any.</summary>
    public DurableProblem? Problem { get; }
    /// <summary>Gets the safely observed Flow state when the Flow was found.</summary>
    public DurableFlowState? ObservedFlowState { get; }
    /// <summary>Gets the safely observed Flow revision when the Flow was found.</summary>
    public long? ObservedFlowRevision { get; }
}

/// <summary>Requests a payload-free repair assessment for one authorized Flow instance.</summary>
public sealed record DurableFlowRepairAssessmentRequest
{
    /// <summary>Initializes a scoped Flow repair assessment request.</summary>
    public DurableFlowRepairAssessmentRequest(DurableScopeId scopeId, DurableFlowInstanceId instanceId)
    {
        ProviderContractValidation.Require(scopeId, nameof(scopeId));
        ProviderContractValidation.Require(instanceId, nameof(instanceId));
        ScopeId = scopeId;
        InstanceId = instanceId;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }
    /// <summary>Gets the Flow instance to inspect.</summary>
    public DurableFlowInstanceId InstanceId { get; }
}

/// <summary>Describes one currently legal or specifically refused payload-free repair candidate.</summary>
public sealed record DurableFlowRepairCandidate
{
    /// <summary>Initializes a repair candidate.</summary>
    public DurableFlowRepairCandidate(
        DurableFlowRepairAction action,
        DurableFlowRepairEvidenceReference evidence,
        DurableProblem? refusalProblem = null)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        DurableFlowRepairEvidenceReference.RequireForAction(action, evidence);
        Action = action;
        Evidence = evidence;
        RefusalProblem = refusalProblem;
    }

    /// <summary>Gets the repair action this candidate describes.</summary>
    public DurableFlowRepairAction Action { get; }
    /// <summary>Gets the payload-free retained evidence needed to submit the action.</summary>
    public DurableFlowRepairEvidenceReference Evidence { get; }
    /// <summary>Gets an optional stable reason this action is presently refused.</summary>
    public DurableProblem? RefusalProblem { get; }
}

/// <summary>Provides a payload-free, advisory repair view that can become stale before submission.</summary>
public sealed record DurableFlowRepairAssessment
{
    /// <summary>Initializes a scoped Flow repair assessment.</summary>
    public DurableFlowRepairAssessment(
        DurableFlowInstanceId instanceId,
        DurableFlowState state,
        long revision,
        string? suspensionDescriptorSchema,
        string? suspensionDescriptorSha256,
        Guid? activityWaitId,
        DurableWorkId? childWorkId,
        long? childWorkRevision,
        IReadOnlyList<DurableFlowRepairCandidate> candidates)
    {
        ProviderContractValidation.Require(instanceId, nameof(instanceId));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        if ((suspensionDescriptorSchema is null) != (suspensionDescriptorSha256 is null))
        {
            throw new ArgumentException("A repair assessment exposes descriptor schema and digest together.");
        }

        if (suspensionDescriptorSchema is not null)
        {
            _ = ProviderContractValidation.Require(suspensionDescriptorSchema, nameof(suspensionDescriptorSchema), 200);
            _ = ProviderContractValidation.RequireSha256(suspensionDescriptorSha256!, nameof(suspensionDescriptorSha256));
        }

        if ((activityWaitId is null) != (childWorkId is null) || (childWorkId is null) != (childWorkRevision is null))
        {
            throw new ArgumentException("A repair assessment exposes activity-wait and child-Work identity together.");
        }

        if (childWorkId is { } workId)
        {
            ProviderContractValidation.Require(workId, nameof(childWorkId));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childWorkRevision!.Value);
        }

        ArgumentNullException.ThrowIfNull(candidates);
        InstanceId = instanceId;
        State = state;
        Revision = revision;
        SuspensionDescriptorSchema = suspensionDescriptorSchema;
        SuspensionDescriptorSha256 = suspensionDescriptorSha256;
        ActivityWaitId = activityWaitId;
        ChildWorkId = childWorkId;
        ChildWorkRevision = childWorkRevision;
        Candidates = Array.AsReadOnly(candidates.ToArray());
    }

    /// <summary>Gets the assessed Flow instance.</summary>
    public DurableFlowInstanceId InstanceId { get; }
    /// <summary>Gets the observed Flow state.</summary>
    public DurableFlowState State { get; }
    /// <summary>Gets the observed Flow revision.</summary>
    public long Revision { get; }
    /// <summary>Gets the V1 descriptor schema, when the persisted suspension is repairable.</summary>
    public string? SuspensionDescriptorSchema { get; }
    /// <summary>Gets the V1 descriptor digest, when the persisted suspension is repairable.</summary>
    public string? SuspensionDescriptorSha256 { get; }
    /// <summary>Gets the one activity wait identity, when applicable.</summary>
    public Guid? ActivityWaitId { get; }
    /// <summary>Gets the linked child Work identity, when applicable.</summary>
    public DurableWorkId? ChildWorkId { get; }
    /// <summary>Gets the observed child Work revision, when applicable.</summary>
    public long? ChildWorkRevision { get; }
    /// <summary>Gets the immutable payload-free candidate list.</summary>
    public IReadOnlyList<DurableFlowRepairCandidate> Candidates { get; }
}

/// <summary>Provides application-authorized, evidence-first repairs for child-effect Flow suspensions.</summary>
/// <remarks>
/// This preview surface does not authenticate callers, infer scope from untrusted input, return payloads, or execute
/// child Work. <see cref="GetAssessmentAsync"/> is advisory only; callers must submit a fresh revision-bound request.
/// Do not use <see cref="IDurableFlowClient.ReleaseSuspensionAsync"/> as a repair fallback for these suspensions.
/// </remarks>
public interface IFlowRepairOperatorClient
{
    /// <summary>Returns a payload-free repair assessment for a trusted scope and Flow instance.</summary>
    ValueTask<DurableOperationResult<DurableFlowRepairAssessment>> GetAssessmentAsync(
        DurableFlowRepairAssessmentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Commits one evidence-backed assertion or returns its stable terminal replay, refusal, race, or conflict.</summary>
    ValueTask<DurableOperationResult<DurableFlowRepairResult>> RepairAsync(
        DurableFlowRepairRequest request,
        CancellationToken cancellationToken = default);
}
