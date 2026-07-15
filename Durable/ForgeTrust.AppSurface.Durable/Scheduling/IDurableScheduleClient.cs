namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Stable problem codes returned by durable schedule operations.
/// </summary>
public static class DurableScheduleProblemCodes
{
    /// <summary>The supplied shape, target, policy, time zone, or expression is invalid.</summary>
    public const string ScheduleInvalid = "ASDUR301";

    /// <summary>The persisted cron dialect or grammar is not supported by this runtime.</summary>
    public const string DialectUnsupported = "ASDUR302";

    /// <summary>The authorized scope does not contain the requested schedule.</summary>
    public const string ScheduleNotFound = "ASDUR303";

    /// <summary>The expected aggregate revision did not match authoritative state.</summary>
    public const string RevisionConflict = "ASDUR304";

    /// <summary>The command or idempotency key was reused with different content.</summary>
    public const string CommandConflict = "ASDUR305";

    /// <summary>The application authorization policy rejected the operation.</summary>
    public const string AccessDenied = "ASDUR306";

    /// <summary>The pinned cron evaluator, deterministic seed, or time-zone rules no longer match this runtime.</summary>
    public const string EvaluationChanged = "ASDUR307";
}

/// <summary>
/// Identifies the stable successful outcome of a schedule mutation.
/// </summary>
public enum DurableScheduleMutationCode
{
    /// <summary>A new schedule was durably accepted.</summary>
    Created = 0,

    /// <summary>The exact prior command and outcome were returned.</summary>
    Duplicate = 1,

    /// <summary>The definition or target was replaced and the generation advanced.</summary>
    Updated = 2,

    /// <summary>New occurrence generation and pending starts were paused.</summary>
    Paused = 3,

    /// <summary>A paused schedule became eligible to generate or start occurrences.</summary>
    Resumed = 4,

    /// <summary>The schedule was deleted and every not-yet-started occurrence was invalidated.</summary>
    Deleted = 5,

    /// <summary>The requested lifecycle state was already authoritative; the command was recorded as a no-op.</summary>
    Unchanged = 6,

    /// <summary>A restore-fenced schedule was re-bound to the active runtime epoch without losing its cursor.</summary>
    RecoveryReleased = 7,
}

/// <summary>
/// Represents the authoritative lifecycle state of a durable schedule.
/// </summary>
public enum DurableScheduleState
{
    /// <summary>Occurrences may be generated and started.</summary>
    Active = 0,

    /// <summary>New occurrences and pending starts are held until resume.</summary>
    Paused = 1,

    /// <summary>No not-yet-started occurrence may start.</summary>
    Deleted = 2,

    /// <summary>Execution is stopped pending restore reconciliation or a compatibility repair.</summary>
    Suspended = 3,
}

/// <summary>
/// Requests creation of one durable schedule.
/// </summary>
public sealed record DurableScheduleCreateRequest
{
    /// <summary>
    /// Initializes a create request.
    /// </summary>
    public DurableScheduleCreateRequest(
        DurableScopeId scopeId,
        DurableCommandId commandId,
        string idempotencyKey,
        DurableScheduleId scheduleId,
        DurableSchedule schedule,
        DurableScheduleTarget target,
        string? displayName = null)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        DurableIdentifier.Require(scheduleId.Value, nameof(scheduleId), 200);
        ScopeId = scopeId;
        CommandId = commandId;
        IdempotencyKey = DurableIdentifier.Require(idempotencyKey, nameof(idempotencyKey), 200);
        ScheduleId = scheduleId;
        Schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        DisplayName = displayName is null ? null : DurableIdentifier.RequireSafeLabel(displayName, nameof(displayName), 200);
        Fingerprint = DurableCommandFingerprints.Create(
            "appsurface.durable.schedule.create.v1",
            ScopeId.Value,
            ScheduleId.Value,
            Schedule,
            Target,
            DisplayName);
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the idempotent command identity.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the caller retry key, unique within the owning scope.</summary>
    public string IdempotencyKey { get; }

    /// <summary>Gets the caller-selected schedule identity used for deterministic <c>H</c> expansion.</summary>
    public DurableScheduleId ScheduleId { get; }

    /// <summary>Gets the immutable timing and policy definition.</summary>
    public DurableSchedule Schedule { get; }

    /// <summary>Gets the registered durable work or Flow target.</summary>
    public DurableScheduleTarget Target { get; }

    /// <summary>Gets the optional privacy-safe operator label.</summary>
    public string? DisplayName { get; }
    /// <summary>Gets the computed semantic command fingerprint.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>
/// Requests replacement of a schedule definition and target under optimistic concurrency.
/// </summary>
/// <remarks>
/// A successful update increments the schedule generation and invalidates undispatched occurrences from the prior
/// generation. An already-running prior-generation target may finish and continues to occupy its concurrency slot.
/// </remarks>
public sealed record DurableScheduleUpdateRequest
{
    /// <summary>Initializes an update request.</summary>
    public DurableScheduleUpdateRequest(
        DurableScopeId scopeId,
        DurableCommandId commandId,
        DurableScheduleId scheduleId,
        long expectedRevision,
        DurableSchedule schedule,
        DurableScheduleTarget target,
        string? displayName = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        DurableIdentifier.Require(scheduleId.Value, nameof(scheduleId), 200);
        ScopeId = scopeId;
        CommandId = commandId;
        ScheduleId = scheduleId;
        ExpectedRevision = expectedRevision;
        Schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        DisplayName = displayName is null ? null : DurableIdentifier.RequireSafeLabel(displayName, nameof(displayName), 200);
        Fingerprint = DurableCommandFingerprints.Create(
            "appsurface.durable.schedule.update.v1",
            ScopeId.Value,
            ScheduleId.Value,
            ExpectedRevision,
            Schedule,
            Target,
            DisplayName);
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the idempotent command identity.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the schedule identity.</summary>
    public DurableScheduleId ScheduleId { get; }

    /// <summary>Gets the required authoritative revision.</summary>
    public long ExpectedRevision { get; }

    /// <summary>Gets the replacement timing and policy definition.</summary>
    public DurableSchedule Schedule { get; }

    /// <summary>Gets the replacement registered target.</summary>
    public DurableScheduleTarget Target { get; }

    /// <summary>Gets the replacement optional operator label.</summary>
    public string? DisplayName { get; }
    /// <summary>Gets the computed semantic command fingerprint.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>Identifies the operation represented by a shared schedule lifecycle command.</summary>
public enum DurableScheduleCommandKind
{
    /// <summary>Pause occurrence generation and pending starts.</summary>
    Pause = 0,
    /// <summary>Resume a paused schedule.</summary>
    Resume = 1,
    /// <summary>Delete the schedule.</summary>
    Delete = 2,
    /// <summary>Release a restore-fenced schedule against the active runtime epoch.</summary>
    ReleaseAfterRecovery = 3,
}

/// <summary>
/// Requests a pause, resume, or delete under optimistic concurrency.
/// </summary>
public sealed record DurableScheduleCommand
{
    /// <summary>Initializes a lifecycle command.</summary>
    public DurableScheduleCommand(
        DurableScheduleCommandKind kind,
        DurableScopeId scopeId,
        DurableCommandId commandId,
        DurableScheduleId scheduleId,
        string actorId,
        string reasonCode,
        long expectedRevision)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        DurableIdentifier.Require(scheduleId.Value, nameof(scheduleId), 200);
        Kind = kind;
        ScopeId = scopeId;
        CommandId = commandId;
        ScheduleId = scheduleId;
        ActorId = DurableIdentifier.Require(actorId, nameof(actorId), 200);
        ReasonCode = DurableIdentifier.Require(reasonCode, nameof(reasonCode), 120);
        ExpectedRevision = expectedRevision;
        var operation = kind switch
        {
            DurableScheduleCommandKind.Pause => "pause",
            DurableScheduleCommandKind.Resume => "resume",
            DurableScheduleCommandKind.Delete => "delete",
            DurableScheduleCommandKind.ReleaseAfterRecovery => "recovery-release",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Fingerprint = DurableCommandFingerprints.Create(
            $"appsurface.durable.schedule.{operation}.v1",
            ScopeId.Value,
            ScheduleId.Value,
            ActorId,
            ReasonCode,
            ExpectedRevision);
    }

    /// <summary>Gets the operation whose schema is encoded in the fingerprint.</summary>
    public DurableScheduleCommandKind Kind { get; }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the idempotent command identity.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the schedule identity.</summary>
    public DurableScheduleId ScheduleId { get; }

    /// <summary>Gets the privacy-safe authorized actor identifier recorded in schedule history.</summary>
    public string ActorId { get; }

    /// <summary>Gets the privacy-safe reason code recorded in schedule history.</summary>
    public string ReasonCode { get; }

    /// <summary>Gets the required authoritative revision.</summary>
    public long ExpectedRevision { get; }
    /// <summary>Gets the computed operation-specific semantic command fingerprint.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>
/// Records the stable successful result of a schedule mutation.
/// </summary>
public sealed record DurableScheduleMutationResult
{
    /// <summary>Initializes a mutation result.</summary>
    public DurableScheduleMutationResult(
        DurableScheduleId scheduleId,
        DurableCommandId commandId,
        DurableScheduleMutationCode code,
        long generation,
        long revision,
        DateTimeOffset committedAtUtc)
    {
        DurableIdentifier.Require(scheduleId.Value, nameof(scheduleId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ScheduleId = scheduleId;
        CommandId = commandId;
        Code = code;
        Generation = generation;
        Revision = revision;
        CommittedAtUtc = committedAtUtc.ToUniversalTime();
    }

    /// <summary>Gets the schedule identity.</summary>
    public DurableScheduleId ScheduleId { get; }

    /// <summary>Gets the accepted command identity.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the stable successful outcome.</summary>
    public DurableScheduleMutationCode Code { get; }

    /// <summary>Gets the active definition generation.</summary>
    public long Generation { get; }

    /// <summary>Gets the authoritative aggregate revision.</summary>
    public long Revision { get; }

    /// <summary>Gets the authoritative store commit timestamp in UTC.</summary>
    public DateTimeOffset CommittedAtUtc { get; }
}

/// <summary>
/// Represents an authorized durable schedule query result.
/// </summary>
public sealed record DurableScheduleSnapshot
{
    /// <summary>Initializes a schedule snapshot.</summary>
    public DurableScheduleSnapshot(
        DurableScheduleId scheduleId,
        string? displayName,
        DurableScheduleState state,
        long generation,
        long revision,
        DurableSchedule schedule,
        DurableScheduleTargetSnapshot target,
        DateTimeOffset? nextOccurrenceUtc)
    {
        DurableIdentifier.Require(scheduleId.Value, nameof(scheduleId), 200);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ScheduleId = scheduleId;
        DisplayName = displayName is null ? null : DurableIdentifier.RequireSafeLabel(displayName, nameof(displayName), 200);
        State = state;
        Generation = generation;
        Revision = revision;
        Schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        NextOccurrenceUtc = nextOccurrenceUtc?.ToUniversalTime();
    }

    /// <summary>Gets the schedule identity.</summary>
    public DurableScheduleId ScheduleId { get; }

    /// <summary>Gets the optional privacy-safe operator label.</summary>
    public string? DisplayName { get; }

    /// <summary>Gets the authoritative lifecycle state.</summary>
    public DurableScheduleState State { get; }

    /// <summary>Gets the active definition generation.</summary>
    public long Generation { get; }

    /// <summary>Gets the aggregate revision.</summary>
    public long Revision { get; }

    /// <summary>Gets the immutable timing and policy definition.</summary>
    public DurableSchedule Schedule { get; }

    /// <summary>Gets the registered target.</summary>
    public DurableScheduleTargetSnapshot Target { get; }

    /// <summary>Gets the next materialized or evaluated UTC occurrence.</summary>
    public DateTimeOffset? NextOccurrenceUtc { get; }
}

/// <summary>
/// Describes the exact registered target and encoded input persisted for a schedule generation.
/// </summary>
/// <remarks>
/// Create and update use a typed <see cref="DurableScheduleTarget"/>. Queries return this encoded snapshot because a
/// durable schedule can be inspected without loading arbitrary CLR types; applications may resolve its registered codec
/// when they need to decode the approved input.
/// </remarks>
public sealed record DurableScheduleTargetSnapshot
{
    /// <summary>Initializes a persisted target snapshot.</summary>
    public DurableScheduleTargetSnapshot(
        DurableScheduleTargetKind kind,
        string registeredName,
        string registeredVersion,
        DurableEncodedPayload input,
        DurableProviderSafety? providerSafety = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        RegisteredName = DurableIdentifier.Require(registeredName, nameof(registeredName), 200);
        RegisteredVersion = DurableIdentifier.Require(registeredVersion, nameof(registeredVersion), 100);
        Input = input ?? throw new ArgumentNullException(nameof(input));
        if ((kind == DurableScheduleTargetKind.Work) != providerSafety.HasValue)
        {
            throw new ArgumentException(
                "Work targets require provider safety and Flow targets must not declare it.",
                nameof(providerSafety));
        }

        if (providerSafety.HasValue && !Enum.IsDefined(providerSafety.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(providerSafety));
        }

        ProviderSafety = providerSafety;
    }

    /// <summary>Gets whether this target starts registered durable work or a registered Flow.</summary>
    public DurableScheduleTargetKind Kind { get; }

    /// <summary>Gets the registered work name or Flow id.</summary>
    public string RegisteredName { get; }

    /// <summary>Gets the immutable work or Flow version.</summary>
    public string RegisteredVersion { get; }

    /// <summary>Gets the allowlisted encoded work input or initial Flow context.</summary>
    public DurableEncodedPayload Input { get; }

    /// <summary>Gets the snapshotted work-provider safety, or <see langword="null"/> for Flow targets.</summary>
    public DurableProviderSafety? ProviderSafety { get; }
}

/// <summary>
/// Requests a bounded authorized schedule listing.
/// </summary>
public sealed record DurableScheduleListRequest
{
    /// <summary>Initializes a list request.</summary>
    public DurableScheduleListRequest(
        DurableScopeId scopeId,
        int pageSize = 100,
        string? continuationToken = null,
        DurableScheduleState? state = null,
        bool? requiresRecoveryRelease = null)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        if (pageSize is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (state is { } requestedState && !Enum.IsDefined(requestedState))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ScopeId = scopeId;
        PageSize = pageSize;
        ContinuationToken = continuationToken is null
            ? null
            : DurableIdentifier.Require(continuationToken, nameof(continuationToken), 200);
        State = state;
        RequiresRecoveryRelease = requiresRecoveryRelease;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the maximum number of schedules to return.</summary>
    public int PageSize { get; }

    /// <summary>Gets the opaque continuation token, or <see langword="null"/> for the first page.</summary>
    public string? ContinuationToken { get; }

    /// <summary>Gets the optional authoritative lifecycle-state filter.</summary>
    public DurableScheduleState? State { get; }

    /// <summary>Gets the optional old-epoch nonterminal recovery filter.</summary>
    public bool? RequiresRecoveryRelease { get; }
}

/// <summary>Represents one payload-free schedule inventory item.</summary>
public sealed record DurableScheduleListItem
{
    /// <summary>Initializes a payload-free schedule inventory item.</summary>
    public DurableScheduleListItem(
        DurableScheduleId scheduleId,
        string? displayName,
        DurableScheduleState state,
        long generation,
        long revision,
        DurableScheduleKind scheduleKind,
        ScheduleOverlapPolicy overlapPolicy,
        ScheduleMisfirePolicy misfirePolicy,
        DurableScheduleTargetKind targetKind,
        string targetName,
        string targetVersion,
        DurableProviderSafety? targetProviderSafety,
        DateTimeOffset? nextOccurrenceUtc,
        bool requiresRecoveryRelease)
    {
        DurableIdentifier.Require(scheduleId.Value, nameof(scheduleId), 200);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (generation < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (!Enum.IsDefined(scheduleKind))
        {
            throw new ArgumentOutOfRangeException(nameof(scheduleKind));
        }

        if (!Enum.IsDefined(targetKind))
        {
            throw new ArgumentOutOfRangeException(nameof(targetKind));
        }

        ScheduleId = scheduleId;
        DisplayName = displayName is null
            ? null
            : DurableIdentifier.RequireSafeLabel(displayName, nameof(displayName), 200);
        State = state;
        Generation = generation;
        Revision = revision;
        ScheduleKind = scheduleKind;
        OverlapPolicy = overlapPolicy ?? throw new ArgumentNullException(nameof(overlapPolicy));
        MisfirePolicy = misfirePolicy ?? throw new ArgumentNullException(nameof(misfirePolicy));
        TargetKind = targetKind;
        TargetName = DurableIdentifier.Require(targetName, nameof(targetName), 200);
        TargetVersion = DurableIdentifier.Require(targetVersion, nameof(targetVersion), 100);
        if (targetProviderSafety is { } safety && !Enum.IsDefined(safety))
        {
            throw new ArgumentOutOfRangeException(nameof(targetProviderSafety));
        }

        if ((targetKind == DurableScheduleTargetKind.Work) != targetProviderSafety.HasValue)
        {
            throw new ArgumentException(
                "Work targets require provider safety and Flow targets must not declare it.",
                nameof(targetProviderSafety));
        }

        TargetProviderSafety = targetProviderSafety;
        NextOccurrenceUtc = nextOccurrenceUtc?.ToUniversalTime();
        RequiresRecoveryRelease = requiresRecoveryRelease;
    }

    /// <summary>Gets the opaque schedule identity.</summary>
    public DurableScheduleId ScheduleId { get; }
    /// <summary>Gets the optional privacy-safe display label.</summary>
    public string? DisplayName { get; }
    /// <summary>Gets the authoritative lifecycle state.</summary>
    public DurableScheduleState State { get; }
    /// <summary>Gets the active definition generation.</summary>
    public long Generation { get; }
    /// <summary>Gets the aggregate revision required by mutations.</summary>
    public long Revision { get; }
    /// <summary>Gets the timing definition kind without its expression or payload.</summary>
    public DurableScheduleKind ScheduleKind { get; }
    /// <summary>Gets the immutable overlap policy.</summary>
    public ScheduleOverlapPolicy OverlapPolicy { get; }
    /// <summary>Gets the immutable misfire policy.</summary>
    public ScheduleMisfirePolicy MisfirePolicy { get; }
    /// <summary>Gets the registered target surface.</summary>
    public DurableScheduleTargetKind TargetKind { get; }
    /// <summary>Gets the registered target name.</summary>
    public string TargetName { get; }
    /// <summary>Gets the registered target version.</summary>
    public string TargetVersion { get; }
    /// <summary>Gets Work provider safety, or <see langword="null"/> for a Flow target.</summary>
    public DurableProviderSafety? TargetProviderSafety { get; }
    /// <summary>Gets the next nominal occurrence time.</summary>
    public DateTimeOffset? NextOccurrenceUtc { get; }
    /// <summary>Gets whether this nonterminal schedule belongs to an older runtime epoch.</summary>
    public bool RequiresRecoveryRelease { get; }
}

/// <summary>
/// Represents one bounded page of authorized payload-free schedule inventory items.
/// </summary>
public sealed record DurableScheduleListResult
{
    /// <summary>Initializes a list result.</summary>
    public DurableScheduleListResult(IReadOnlyList<DurableScheduleListItem> schedules, string? continuationToken)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        Schedules = schedules.ToArray();
        ContinuationToken = continuationToken;
    }

    /// <summary>Gets the immutable page of payload-free schedule inventory items.</summary>
    public IReadOnlyList<DurableScheduleListItem> Schedules { get; }

    /// <summary>Gets the next opaque continuation token, or <see langword="null"/> when this is the last page.</summary>
    public string? ContinuationToken { get; }
}

/// <summary>
/// Requests a side-effect-free preview of upcoming occurrences before or after persistence.
/// </summary>
public sealed record DurableScheduleExplainRequest
{
    /// <summary>Initializes an explanation request.</summary>
    public DurableScheduleExplainRequest(
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        DurableSchedule schedule,
        DateTimeOffset anchorUtc,
        int occurrenceCount = 5)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(scheduleId.Value, nameof(scheduleId), 200);
        if (occurrenceCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrenceCount));
        }

        ScopeId = scopeId;
        ScheduleId = scheduleId;
        Schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        AnchorUtc = anchorUtc.ToUniversalTime();
        OccurrenceCount = occurrenceCount;
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the schedule id used for deterministic <c>H</c> expansion.</summary>
    public DurableScheduleId ScheduleId { get; }

    /// <summary>Gets the definition to explain.</summary>
    public DurableSchedule Schedule { get; }

    /// <summary>Gets the preview anchor and acceptance-time approximation.</summary>
    public DateTimeOffset AnchorUtc { get; }

    /// <summary>Gets the maximum number of upcoming occurrences to return.</summary>
    public int OccurrenceCount { get; }
}

/// <summary>
/// Describes a side-effect-free evaluated schedule in operationally useful terms.
/// </summary>
public sealed record DurableScheduleExplanation
{
    /// <summary>Initializes a schedule explanation.</summary>
    public DurableScheduleExplanation(
        DurableScheduleId scheduleId,
        DurableScheduleKind kind,
        ScheduleOverlapPolicy overlapPolicy,
        ScheduleMisfirePolicy misfirePolicy,
        IReadOnlyList<DateTimeOffset> nextOccurrencesUtc,
        CronDialect? cronDialect = null,
        CronGrammar? cronGrammar = null,
        string? ianaTimeZoneId = null,
        string? evaluatorVersion = null,
        int? jitterSeed = null,
        string? timeZoneRulesFingerprint = null,
        IReadOnlyList<string>? notes = null)
    {
        DurableIdentifier.Require(scheduleId.Value, nameof(scheduleId), 200);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (cronDialect is { } dialect && !Enum.IsDefined(dialect))
        {
            throw new ArgumentOutOfRangeException(nameof(cronDialect));
        }

        if (cronGrammar is { } grammar && !Enum.IsDefined(grammar))
        {
            throw new ArgumentOutOfRangeException(nameof(cronGrammar));
        }

        ScheduleId = scheduleId;
        Kind = kind;
        OverlapPolicy = overlapPolicy ?? throw new ArgumentNullException(nameof(overlapPolicy));
        MisfirePolicy = misfirePolicy ?? throw new ArgumentNullException(nameof(misfirePolicy));
        ArgumentNullException.ThrowIfNull(nextOccurrencesUtc);
        NextOccurrencesUtc = nextOccurrencesUtc.Select(value => value.ToUniversalTime()).ToArray();
        CronDialect = cronDialect;
        CronGrammar = cronGrammar;
        IanaTimeZoneId = ianaTimeZoneId;
        EvaluatorVersion = evaluatorVersion;
        JitterSeed = jitterSeed;
        TimeZoneRulesFingerprint = timeZoneRulesFingerprint;
        Notes = notes?.ToArray() ?? [];
    }

    /// <summary>Gets the schedule identity.</summary>
    public DurableScheduleId ScheduleId { get; }

    /// <summary>Gets the schedule shape.</summary>
    public DurableScheduleKind Kind { get; }

    /// <summary>Gets the effective overlap behavior.</summary>
    public ScheduleOverlapPolicy OverlapPolicy { get; }

    /// <summary>Gets the effective downtime behavior.</summary>
    public ScheduleMisfirePolicy MisfirePolicy { get; }

    /// <summary>Gets upcoming evaluated instants normalized to UTC.</summary>
    public IReadOnlyList<DateTimeOffset> NextOccurrencesUtc { get; }

    /// <summary>Gets the cron dialect when this is a cron schedule.</summary>
    public CronDialect? CronDialect { get; }

    /// <summary>Gets the cron grammar when this is a cron schedule.</summary>
    public CronGrammar? CronGrammar { get; }

    /// <summary>Gets the IANA time zone when this is a cron schedule.</summary>
    public string? IanaTimeZoneId { get; }

    /// <summary>Gets the pinned evaluator package version when applicable.</summary>
    public string? EvaluatorVersion { get; }

    /// <summary>Gets the deterministic <c>H</c> expansion seed when applicable.</summary>
    public int? JitterSeed { get; }

    /// <summary>Gets the fingerprint of the time-zone rules used for this calculation.</summary>
    public string? TimeZoneRulesFingerprint { get; }

    /// <summary>Gets safe explanatory notes, including DST or acceptance-anchor caveats.</summary>
    public IReadOnlyList<string> Notes { get; }
}

/// <summary>
/// Creates, inspects, explains, updates, pauses, resumes, and deletes durable schedules.
/// </summary>
/// <remarks>
/// The application must authorize the trusted <see cref="DurableScopeId"/> before invoking this client. Delete blocks
/// every not-yet-started occurrence but does not revoke a target that already started. Pause preserves same-generation
/// pending work but prevents it from starting until resume.
/// </remarks>
public interface IDurableScheduleClient
{
    /// <summary>Durably creates a schedule or returns the exact prior idempotent outcome.</summary>
    ValueTask<DurableOperationResult<DurableScheduleMutationResult>> CreateAsync(
        DurableScheduleCreateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the definition and target, incrementing the generation on success.</summary>
    ValueTask<DurableOperationResult<DurableScheduleMutationResult>> UpdateAsync(
        DurableScheduleUpdateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Pauses occurrence generation and pending starts without canceling an already-running target.</summary>
    ValueTask<DurableOperationResult<DurableScheduleMutationResult>> PauseAsync(
        DurableScheduleCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes a paused schedule and makes a preserved pending occurrence immediately eligible.</summary>
    ValueTask<DurableOperationResult<DurableScheduleMutationResult>> ResumeAsync(
        DurableScheduleCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a schedule and invalidates every target that has not started.</summary>
    ValueTask<DurableOperationResult<DurableScheduleMutationResult>> DeleteAsync(
        DurableScheduleCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebinds a restore-fenced schedule to the active runtime epoch while preserving pending and catch-up state.
    /// </summary>
    ValueTask<DurableOperationResult<DurableScheduleMutationResult>> ReleaseAfterRecoveryAsync(
        DurableScheduleCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one authorized schedule snapshot.</summary>
    ValueTask<DurableOperationResult<DurableScheduleSnapshot>> GetAsync(
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists one bounded authorized page of schedule snapshots.</summary>
    ValueTask<DurableOperationResult<DurableScheduleListResult>> ListAsync(
        DurableScheduleListRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Explains upcoming occurrences and effective policy without mutating runtime state.</summary>
    ValueTask<DurableOperationResult<DurableScheduleExplanation>> ExplainNextOccurrencesAsync(
        DurableScheduleExplainRequest request,
        CancellationToken cancellationToken = default);
}
