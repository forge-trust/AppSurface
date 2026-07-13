namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Describes the safe, low-cardinality liveness state of one configured durable runtime worker.
/// </summary>
public enum DurableRuntimeHealthState
{
    /// <summary>The store is compatible, the epoch matches, and the worker heartbeat is current.</summary>
    Healthy = 0,

    /// <summary>No successful pump heartbeat has yet been recorded for this worker instance.</summary>
    NotStarted = 1,

    /// <summary>The worker heartbeat is older than the configured liveness bound.</summary>
    Stale = 2,

    /// <summary>The worker is intentionally refusing new pump passes while already-started work drains.</summary>
    Draining = 3,

    /// <summary>The schema or store-wide recovery epoch does not authorize this runtime.</summary>
    Incompatible = 4,
}

/// <summary>
/// Reports compatibility, heartbeat, sweep, drain, and due-dispatch lag without exposing durable payloads.
/// </summary>
public sealed record DurableRuntimeHealthSnapshot
{
    /// <summary>Initializes a runtime health snapshot.</summary>
    public DurableRuntimeHealthSnapshot(
        DurableRuntimeHealthState state,
        string? problemCode,
        bool schemaCompatible,
        bool epochCompatible,
        int installedSchemaVersion,
        int requiredSchemaVersion,
        Guid configuredRuntimeEpoch,
        Guid? activeRuntimeEpoch,
        string workerId,
        Guid? workerInstanceId,
        DurableRuntimeSurface hostedSurfaces,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? lastHeartbeatAtUtc,
        DateTimeOffset? lastSuccessfulSweepAtUtc,
        bool isDraining,
        bool isPassActive,
        long dueDispatchCount,
        DateTimeOffset? oldestDueAtUtc,
        TimeSpan? oldestDueAge)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (installedSchemaVersion < 0 || requiredSchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(installedSchemaVersion));
        }

        if (configuredRuntimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The configured runtime epoch must not be empty.", nameof(configuredRuntimeEpoch));
        }

        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200)
        {
            throw new ArgumentException("Worker id must contain 1 to 200 characters.", nameof(workerId));
        }

        if (hostedSurfaces == DurableRuntimeSurface.None || (hostedSurfaces & ~DurableRuntimeSurface.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hostedSurfaces));
        }

        if (dueDispatchCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dueDispatchCount));
        }

        if (oldestDueAge is { } age && age < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(oldestDueAge));
        }

        if (problemCode is not null && (string.IsNullOrWhiteSpace(problemCode) || problemCode.Length > 120))
        {
            throw new ArgumentException("Problem code must contain at most 120 characters.", nameof(problemCode));
        }

        State = state;
        ProblemCode = problemCode;
        SchemaCompatible = schemaCompatible;
        EpochCompatible = epochCompatible;
        InstalledSchemaVersion = installedSchemaVersion;
        RequiredSchemaVersion = requiredSchemaVersion;
        ConfiguredRuntimeEpoch = configuredRuntimeEpoch;
        ActiveRuntimeEpoch = activeRuntimeEpoch;
        WorkerId = workerId;
        WorkerInstanceId = workerInstanceId;
        HostedSurfaces = hostedSurfaces;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        StartedAtUtc = startedAtUtc?.ToUniversalTime();
        LastHeartbeatAtUtc = lastHeartbeatAtUtc?.ToUniversalTime();
        LastSuccessfulSweepAtUtc = lastSuccessfulSweepAtUtc?.ToUniversalTime();
        IsDraining = isDraining;
        IsPassActive = isPassActive;
        DueDispatchCount = dueDispatchCount;
        OldestDueAtUtc = oldestDueAtUtc?.ToUniversalTime();
        OldestDueAge = oldestDueAge;
    }

    /// <summary>Gets the overall liveness verdict.</summary>
    public DurableRuntimeHealthState State { get; }

    /// <summary>Gets the stable diagnostic code for a non-healthy verdict, when one applies.</summary>
    public string? ProblemCode { get; }

    /// <summary>Gets whether installed reader and writer ranges include this package.</summary>
    public bool SchemaCompatible { get; }

    /// <summary>Gets whether the configured out-of-band runtime epoch matches the store.</summary>
    public bool EpochCompatible { get; }

    /// <summary>Gets the highest installed schema migration.</summary>
    public int InstalledSchemaVersion { get; }

    /// <summary>Gets the schema migration required by this package.</summary>
    public int RequiredSchemaVersion { get; }

    /// <summary>Gets the process-configured out-of-band recovery epoch.</summary>
    public Guid ConfiguredRuntimeEpoch { get; }

    /// <summary>Gets the store-wide active recovery epoch, when readable.</summary>
    public Guid? ActiveRuntimeEpoch { get; }

    /// <summary>Gets the privacy-safe configured worker identity.</summary>
    public string WorkerId { get; }

    /// <summary>Gets the process-instance correlation id currently owning the worker identity.</summary>
    public Guid? WorkerInstanceId { get; }

    /// <summary>Gets the surfaces this worker is configured to pump.</summary>
    public DurableRuntimeSurface HostedSurfaces { get; }

    /// <summary>Gets the PostgreSQL observation time.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Gets when the current worker instance first registered.</summary>
    public DateTimeOffset? StartedAtUtc { get; }

    /// <summary>Gets the most recent attempted pump heartbeat.</summary>
    public DateTimeOffset? LastHeartbeatAtUtc { get; }

    /// <summary>Gets the most recent successfully completed bounded sweep.</summary>
    public DateTimeOffset? LastSuccessfulSweepAtUtc { get; }

    /// <summary>Gets whether this worker is refusing new passes while already-started operations drain.</summary>
    public bool IsDraining { get; }

    /// <summary>Gets whether this worker instance has an in-flight bounded pump pass.</summary>
    public bool IsPassActive { get; }

    /// <summary>Gets the number of selected-surface dispatch rows currently overdue and available or reclaimable.</summary>
    public long DueDispatchCount { get; }

    /// <summary>Gets the oldest selected-surface available or reclaimable dispatch instant.</summary>
    public DateTimeOffset? OldestDueAtUtc { get; }

    /// <summary>Gets the database-observed age of the oldest overdue dispatch.</summary>
    public TimeSpan? OldestDueAge { get; }
}

/// <summary>
/// Reads the configured worker's PostgreSQL-backed compatibility and liveness snapshot.
/// </summary>
/// <remarks>
/// This API is safe to expose through an application-owned health endpoint. It contains no payloads or scope and
/// aggregate identifiers. Applications should alert on <see cref="DurableRuntimeHealthState.Stale"/>, an incompatible
/// schema or epoch, and oldest-due age outside their own service objective.
/// </remarks>
public interface IDurableRuntimeHealth
{
    /// <summary>Reads one non-mutating health snapshot.</summary>
    ValueTask<DurableRuntimeHealthSnapshot> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Controls process-local graceful drain for the configured worker identity.
/// </summary>
/// <remarks>
/// Beginning drain prevents future pump passes from claiming new aggregates. It does not revoke an effect permit,
/// cancel provider I/O already in progress, or wait for the caller's currently running pass. The host must wait for
/// its in-flight pass to return before shutting down. Resume is intended for a canceled deployment rollback, not for
/// bypassing a restore or compatibility fence.
/// </remarks>
public interface IDurableRuntimeDrainControl
{
    /// <summary>Marks the current worker instance as draining and refuses subsequent pump passes.</summary>
    ValueTask BeginDrainAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears process-local drain after compatibility and recovery preconditions remain satisfied.</summary>
    ValueTask ResumeAsync(CancellationToken cancellationToken = default);
}
