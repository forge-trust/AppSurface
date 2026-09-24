using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Testing;

/// <summary>Builds production-validated runtime health snapshots for tests.</summary>
/// <remarks>Named states supply representative valid defaults. Overrides are applied before production validation.</remarks>
public sealed class DurableHealthSnapshotBuilder
{
    private DurableRuntimeHealthState _state = DurableRuntimeHealthState.Healthy;
    private string? _problemCode;
    private bool _hasProblemCodeOverride;
    private bool? _schemaCompatible;
    private bool? _epochCompatible;
    private int? _installedSchemaVersion;
    private int? _requiredSchemaVersion;
    private Guid? _configuredRuntimeEpoch;
    private Guid? _activeRuntimeEpoch;
    private bool _hasActiveRuntimeEpochOverride;
    private string? _workerId;
    private Guid? _workerInstanceId;
    private DurableRuntimeSurface? _hostedSurfaces;
    private DateTimeOffset? _observedAtUtc;
    private DateTimeOffset? _startedAtUtc;
    private bool _hasStartedAtUtcOverride;
    private DateTimeOffset? _lastHeartbeatAtUtc;
    private bool _hasLastHeartbeatAtUtcOverride;
    private DateTimeOffset? _lastSuccessfulSweepAtUtc;
    private bool _hasLastSuccessfulSweepAtUtcOverride;
    private bool? _isDraining;
    private bool? _isPassActive;
    private long? _dueDispatchCount;
    private DateTimeOffset? _oldestDueAtUtc;
    private TimeSpan? _oldestDueAge;

    /// <summary>Selects representative defaults for a defined health state.</summary>
    public DurableHealthSnapshotBuilder ForState(DurableRuntimeHealthState state) { _state = state; return this; }
    /// <summary>Overrides the optional privacy-safe problem code.</summary>
    public DurableHealthSnapshotBuilder WithProblemCode(string? value) { _problemCode = value; _hasProblemCodeOverride = true; return this; }
    /// <summary>Overrides schema compatibility.</summary>
    public DurableHealthSnapshotBuilder WithSchemaCompatible(bool value) { _schemaCompatible = value; return this; }
    /// <summary>Overrides epoch compatibility.</summary>
    public DurableHealthSnapshotBuilder WithEpochCompatible(bool value) { _epochCompatible = value; return this; }
    /// <summary>Overrides installed schema version.</summary>
    public DurableHealthSnapshotBuilder WithInstalledSchemaVersion(int value) { _installedSchemaVersion = value; return this; }
    /// <summary>Overrides required schema version.</summary>
    public DurableHealthSnapshotBuilder WithRequiredSchemaVersion(int value) { _requiredSchemaVersion = value; return this; }
    /// <summary>Overrides configured runtime epoch.</summary>
    public DurableHealthSnapshotBuilder WithConfiguredRuntimeEpoch(Guid value) { _configuredRuntimeEpoch = value; return this; }
    /// <summary>Overrides active runtime epoch.</summary>
    public DurableHealthSnapshotBuilder WithActiveRuntimeEpoch(Guid? value) { _activeRuntimeEpoch = value; _hasActiveRuntimeEpochOverride = true; return this; }
    /// <summary>Overrides worker identity.</summary>
    public DurableHealthSnapshotBuilder WithWorkerId(string value) { _workerId = value; return this; }
    /// <summary>Overrides worker instance identity.</summary>
    public DurableHealthSnapshotBuilder WithWorkerInstanceId(Guid? value) { _workerInstanceId = value; return this; }
    /// <summary>Overrides hosted surfaces.</summary>
    public DurableHealthSnapshotBuilder WithHostedSurfaces(DurableRuntimeSurface value) { _hostedSurfaces = value; return this; }
    /// <summary>Overrides observation time.</summary>
    public DurableHealthSnapshotBuilder WithObservedAtUtc(DateTimeOffset value) { _observedAtUtc = value; return this; }
    /// <summary>Overrides runtime start time.</summary>
    public DurableHealthSnapshotBuilder WithStartedAtUtc(DateTimeOffset? value) { _startedAtUtc = value; _hasStartedAtUtcOverride = true; return this; }
    /// <summary>Overrides heartbeat time.</summary>
    public DurableHealthSnapshotBuilder WithLastHeartbeatAtUtc(DateTimeOffset? value) { _lastHeartbeatAtUtc = value; _hasLastHeartbeatAtUtcOverride = true; return this; }
    /// <summary>Overrides last successful sweep time.</summary>
    public DurableHealthSnapshotBuilder WithLastSuccessfulSweepAtUtc(DateTimeOffset? value) { _lastSuccessfulSweepAtUtc = value; _hasLastSuccessfulSweepAtUtcOverride = true; return this; }
    /// <summary>Overrides drain state.</summary>
    public DurableHealthSnapshotBuilder WithIsDraining(bool value) { _isDraining = value; return this; }
    /// <summary>Overrides active-pass state.</summary>
    public DurableHealthSnapshotBuilder WithIsPassActive(bool value) { _isPassActive = value; return this; }
    /// <summary>Overrides overdue dispatch count.</summary>
    public DurableHealthSnapshotBuilder WithDueDispatchCount(long value) { _dueDispatchCount = value; return this; }
    /// <summary>Overrides oldest due instant.</summary>
    public DurableHealthSnapshotBuilder WithOldestDueAtUtc(DateTimeOffset? value) { _oldestDueAtUtc = value; return this; }
    /// <summary>Overrides oldest due age.</summary>
    public DurableHealthSnapshotBuilder WithOldestDueAge(TimeSpan? value) { _oldestDueAge = value; return this; }

    /// <summary>Builds the selected representative snapshot and delegates all validation to production.</summary>
    public DurableRuntimeHealthSnapshot Build() => BuildCore(contradictory: false);

    /// <summary>Builds a deliberately contradictory snapshot for tests of consumers handling inconsistent fields.</summary>
    public DurableRuntimeHealthSnapshot BuildContradictoryForTest() => BuildCore(contradictory: true);

    private DurableRuntimeHealthSnapshot BuildCore(bool contradictory)
    {
        var state = _state;
        var schema = _schemaCompatible ?? state is not (DurableRuntimeHealthState.Incompatible or DurableRuntimeHealthState.Unavailable);
        var epoch = _epochCompatible ?? state is not (DurableRuntimeHealthState.Incompatible or DurableRuntimeHealthState.Unavailable);
        var unavailable = state == DurableRuntimeHealthState.Unavailable;
        var draining = _isDraining ?? state == DurableRuntimeHealthState.Draining;
        var configuredEpoch = _configuredRuntimeEpoch ?? Guid.Parse("11111111-1111-1111-1111-111111111111");
        var activeEpoch = _hasActiveRuntimeEpochOverride ? _activeRuntimeEpoch : unavailable ? null : configuredEpoch;
        var started = _hasStartedAtUtcOverride ? _startedAtUtc : state == DurableRuntimeHealthState.NotStarted ? null : DateTimeOffset.UnixEpoch;
        var heartbeat = _hasLastHeartbeatAtUtcOverride ? _lastHeartbeatAtUtc : state is DurableRuntimeHealthState.NotStarted or DurableRuntimeHealthState.Unavailable ? null : DateTimeOffset.UnixEpoch;
        if (!contradictory)
        {
            var compatible = schema && epoch;
            var conflict = state switch
            {
                DurableRuntimeHealthState.Healthy when !compatible => "SchemaCompatible/EpochCompatible",
                DurableRuntimeHealthState.Healthy when draining => "IsDraining",
                DurableRuntimeHealthState.Healthy when started is null => "StartedAtUtc",
                DurableRuntimeHealthState.Healthy when heartbeat is null => "LastHeartbeatAtUtc",
                DurableRuntimeHealthState.NotStarted when !compatible => "SchemaCompatible/EpochCompatible",
                DurableRuntimeHealthState.NotStarted when draining => "IsDraining",
                DurableRuntimeHealthState.NotStarted when heartbeat is not null => "LastHeartbeatAtUtc",
                DurableRuntimeHealthState.Stale when !compatible => "SchemaCompatible/EpochCompatible",
                DurableRuntimeHealthState.Stale when draining => "IsDraining",
                DurableRuntimeHealthState.Stale when started is null => "StartedAtUtc",
                DurableRuntimeHealthState.Stale when heartbeat is null => "LastHeartbeatAtUtc",
                DurableRuntimeHealthState.Draining when !compatible => "SchemaCompatible/EpochCompatible",
                DurableRuntimeHealthState.Draining when !draining => "IsDraining",
                DurableRuntimeHealthState.Draining when started is null => "StartedAtUtc",
                DurableRuntimeHealthState.Incompatible when compatible => "SchemaCompatible/EpochCompatible",
                DurableRuntimeHealthState.Unavailable when schema || epoch => "SchemaCompatible/EpochCompatible",
                DurableRuntimeHealthState.Unavailable when activeEpoch is not null => "ActiveRuntimeEpoch",
                _ => null,
            };
            if (conflict is null && compatible && activeEpoch != configuredEpoch)
            {
                conflict = "ActiveRuntimeEpoch/ConfiguredRuntimeEpoch";
            }
            if (conflict is not null)
            {
                throw new ArgumentException($"{conflict} contradicts the selected {state} health state. Use {nameof(BuildContradictoryForTest)} only for deliberate contradiction tests.");
            }
        }

        return new(state, _hasProblemCodeOverride ? _problemCode : state == DurableRuntimeHealthState.Healthy ? null : $"test-{state.ToString().ToLowerInvariant()}", schema, epoch,
            _installedSchemaVersion ?? 1, _requiredSchemaVersion ?? 1, configuredEpoch,
            activeEpoch, _workerId ?? "test-worker", _workerInstanceId,
            _hostedSurfaces ?? DurableRuntimeSurface.All, _observedAtUtc ?? DateTimeOffset.UnixEpoch, started, heartbeat,
            _hasLastSuccessfulSweepAtUtcOverride ? _lastSuccessfulSweepAtUtc : heartbeat, draining, _isPassActive ?? false, _dueDispatchCount ?? 0,
            _oldestDueAtUtc, _oldestDueAge);
    }
}
