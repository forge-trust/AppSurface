using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Configures how a coverage run responds when an active operation produces no observable progress.
/// </summary>
internal enum CoverageRunWatchdogMode
{
    /// <summary>Disables no-progress classification while allowing heartbeats.</summary>
    Off,

    /// <summary>Reports each no-progress interval once and allows the run to continue.</summary>
    Warn,

    /// <summary>Allows the first confirmed no-progress operation to terminate the run.</summary>
    Fail,
}

/// <summary>
/// Contains validated timing and behavior settings for a coverage-run watchdog.
/// </summary>
/// <param name="Mode">Action taken for an operation that crosses the no-progress threshold.</param>
/// <param name="HeartbeatInterval">Periodic heartbeat interval, or zero to disable heartbeat rendering.</param>
/// <param name="NoProgressTimeout">Positive interval after which an active operation is classified as stale.</param>
internal sealed record CoverageRunWatchdogOptions(
    CoverageRunWatchdogMode Mode,
    TimeSpan HeartbeatInterval,
    TimeSpan NoProgressTimeout)
{
    /// <summary>Gets the default warning-only watchdog configuration.</summary>
    public static CoverageRunWatchdogOptions Default { get; } = new(
        CoverageRunWatchdogMode.Warn,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(10));

    /// <summary>Validates the option combination.</summary>
    /// <returns>This validated option instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An interval is outside the supported range.</exception>
    public CoverageRunWatchdogOptions Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        if (HeartbeatInterval < TimeSpan.Zero || HeartbeatInterval > CoverageRunWatchdogDuration.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval));
        }

        if (NoProgressTimeout <= TimeSpan.Zero || NoProgressTimeout > CoverageRunWatchdogDuration.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(NoProgressTimeout));
        }

        return this;
    }
}

/// <summary>
/// Parses the strict, culture-invariant duration syntax accepted by coverage-run watchdog options.
/// </summary>
internal static class CoverageRunWatchdogDuration
{
    /// <summary>Gets the largest accepted duration.</summary>
    public static TimeSpan Maximum { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Parses <paramref name="value"/> using the exact grammar <c>0|[1-9][0-9]*(ms|s|m|h)</c>.
    /// </summary>
    /// <param name="value">Untrimmed command-line value.</param>
    /// <param name="duration">Parsed duration when this method returns <see langword="true"/>.</param>
    /// <returns>Whether the value is syntactically valid and no greater than 30 days.</returns>
    public static bool TryParse(string? value, out TimeSpan duration)
    {
        duration = default;
        if (value is null)
        {
            return false;
        }

        if (value == "0")
        {
            return true;
        }

        var suffixLength = value.EndsWith("ms", StringComparison.Ordinal) ? 2 : 1;
        if (value.Length <= suffixLength)
        {
            return false;
        }

        var suffix = value[^suffixLength..];
        long multiplier;
        switch (suffix)
        {
            case "ms":
                multiplier = 1;
                break;
            case "s":
                multiplier = 1_000;
                break;
            case "m":
                multiplier = 60_000;
                break;
            case "h":
                multiplier = 3_600_000;
                break;
            default:
                return false;
        }

        var number = value.AsSpan(0, value.Length - suffixLength);
        if (number.IsEmpty || number[0] is < '1' or > '9')
        {
            return false;
        }

        foreach (var character in number[1..])
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude))
        {
            return false;
        }

        try
        {
            var milliseconds = checked(magnitude * multiplier);
            if (milliseconds > (long)Maximum.TotalMilliseconds)
            {
                return false;
            }

            duration = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

/// <summary>Identifies a supervised coverage-run operation.</summary>
internal enum CoverageRunWatchdogOperationKind
{
    /// <summary>Solution project discovery.</summary>
    Discovery,
    /// <summary>Optional solution build.</summary>
    Build,
    /// <summary>One scheduled test project.</summary>
    Project,
    /// <summary>Coverage report merge.</summary>
    Merge,
    /// <summary>Slow-test diagnostics.</summary>
    Diagnostics,
    /// <summary>Summary and timing artifact finalization.</summary>
    Artifacts,
}

/// <summary>Describes the externally visible lifecycle of a supervised operation.</summary>
internal enum CoverageRunWatchdogOperationState
{
    /// <summary>The operation is scheduled but has no active no-progress clock.</summary>
    Queued,
    /// <summary>The operation is active.</summary>
    Running,
    /// <summary>The operation is completing in-process bookkeeping.</summary>
    Finalizing,
    /// <summary>The operation has completed and has no active no-progress clock.</summary>
    Complete,
}

/// <summary>Defines shared identities used to connect workflow phases to their process observations.</summary>
internal static class CoverageRunWatchdogOperationIds
{
    /// <summary>Solution project discovery.</summary>
    public const string Discovery = "discovery";

    /// <summary>Optional build work.</summary>
    public const string Build = "build";

    /// <summary>Slow-test diagnostics.</summary>
    public const string Diagnostics = "diagnostics";

    /// <summary>Coverage report merge.</summary>
    public const string Merge = "merge";

    /// <summary>Summary and timing artifact finalization.</summary>
    public const string Artifacts = "artifacts";
}

/// <summary>
/// Describes an operation before it is registered with a run-scoped watchdog.
/// </summary>
/// <param name="Identity">Stable unique identity within the run.</param>
/// <param name="Kind">Operation kind.</param>
/// <param name="ExecutionIndex">Stable project schedule index, or zero for a shared phase.</param>
/// <param name="Project">Privacy-normalized solution-relative project path, when applicable.</param>
/// <param name="Log">Privacy-normalized output-relative log path, when applicable.</param>
internal sealed record CoverageRunWatchdogOperation(
    string Identity,
    CoverageRunWatchdogOperationKind Kind,
    int ExecutionIndex = 0,
    string? Project = null,
    string? Log = null);

/// <summary>
/// Captures one immutable view of a supervised operation.
/// </summary>
/// <param name="Identity">Stable operation identity.</param>
/// <param name="Kind">Operation kind.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="ExecutionIndex">Stable project execution index.</param>
/// <param name="Project">Privacy-normalized project path.</param>
/// <param name="Log">Privacy-normalized log path.</param>
/// <param name="Elapsed">Elapsed active time.</param>
/// <param name="NoProgress">Elapsed time since the most recent observable progress.</param>
/// <param name="LastProgressAtUtc">Display-only UTC time of the most recent progress.</param>
/// <param name="ProgressSequence">Sequence used to reject stale classification races.</param>
/// <param name="OutputBytes">Checked total of positive raw output-byte observations.</param>
/// <param name="WarningLatched">Whether warn mode already reported the current no-progress interval.</param>
internal sealed record CoverageRunWatchdogOperationSnapshot(
    string Identity,
    CoverageRunWatchdogOperationKind Kind,
    CoverageRunWatchdogOperationState State,
    int ExecutionIndex,
    string? Project,
    string? Log,
    TimeSpan Elapsed,
    TimeSpan NoProgress,
    DateTimeOffset? LastProgressAtUtc,
    long ProgressSequence,
    long OutputBytes,
    bool WarningLatched);

/// <summary>
/// Captures deterministic aggregate and operation state at one monotonic instant.
/// </summary>
/// <param name="RunElapsed">Elapsed time since supervisor construction.</param>
/// <param name="CapturedAtUtc">Wall-clock time paired with this immutable evaluation snapshot.</param>
/// <param name="Queued">Number of queued operations.</param>
/// <param name="Running">Number of running operations.</param>
/// <param name="Finalizing">Number of finalizing operations.</param>
/// <param name="Complete">Number of completed operations.</param>
/// <param name="Operations">Operations in stable workflow order.</param>
internal sealed record CoverageRunWatchdogSnapshot(
    TimeSpan RunElapsed,
    DateTimeOffset CapturedAtUtc,
    int Queued,
    int Running,
    int Finalizing,
    int Complete,
    IReadOnlyList<CoverageRunWatchdogOperationSnapshot> Operations);

/// <summary>
/// Describes work discovered during one watchdog evaluation.
/// </summary>
/// <param name="HeartbeatDue">Whether the caller should render a heartbeat.</param>
/// <param name="Snapshot">Current immutable supervisor state.</param>
/// <param name="NewlyStale">New warn incidents or fail-mode terminal candidates in stable order.</param>
internal sealed record CoverageRunWatchdogEvaluation(
    bool HeartbeatDue,
    CoverageRunWatchdogSnapshot Snapshot,
    IReadOnlyList<CoverageRunWatchdogOperationSnapshot> NewlyStale);

/// <summary>
/// Maintains concurrency-safe, run-scoped operation clocks for coverage heartbeats and no-progress classification.
/// </summary>
internal sealed class CoverageRunWatchdogSupervisor
{
    private static readonly TimeSpan MaximumMonitorDelay = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly CoverageRunWatchdogOptions _options;
    private readonly long _runStarted;
    private readonly Dictionary<string, MutableOperation> _operations = new(StringComparer.Ordinal);
    private long _lastHeartbeat;
    private CoverageRunWatchdogOperationSnapshot? _terminalCause;
    private bool _stopped;
    private bool _externalCancellationClaimed;

    /// <summary>Initializes a new supervisor using monotonic timestamps from <paramref name="timeProvider"/>.</summary>
    /// <param name="timeProvider">Clock used for elapsed decisions and display-only UTC timestamps.</param>
    /// <param name="options">Validated watchdog options.</param>
    public CoverageRunWatchdogSupervisor(TimeProvider timeProvider, CoverageRunWatchdogOptions options)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _runStarted = _timeProvider.GetTimestamp();
        _lastHeartbeat = _runStarted;
    }

    /// <summary>Gets the first operation that successfully claimed the fail-mode terminal gate.</summary>
    public CoverageRunWatchdogOperationSnapshot? TerminalCause
    {
        get
        {
            lock (_gate)
            {
                return _terminalCause;
            }
        }
    }

    /// <summary>Registers a unique operation in queued or active state.</summary>
    /// <param name="operation">Stable operation metadata.</param>
    /// <param name="initialState">Initial lifecycle state.</param>
    public void Register(CoverageRunWatchdogOperation operation, CoverageRunWatchdogOperationState initialState)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrWhiteSpace(operation.Identity))
        {
            throw new ArgumentException("An operation identity is required.", nameof(operation));
        }

        lock (_gate)
        {
            ThrowIfClosed();
            var now = _timeProvider.GetTimestamp();
            var utcNow = _timeProvider.GetUtcNow();
            if (!_operations.TryAdd(operation.Identity, new MutableOperation(operation, initialState, now, utcNow)))
            {
                throw new InvalidOperationException($"Coverage watchdog operation '{operation.Identity}' is already registered.");
            }
        }
    }

    /// <summary>Transitions an operation and records the transition as progress.</summary>
    /// <param name="identity">Registered operation identity.</param>
    /// <param name="state">New lifecycle state.</param>
    public void Transition(string identity, CoverageRunWatchdogOperationState state)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            var operation = GetOperation(identity);
            operation.Transition(state, _timeProvider.GetTimestamp(), _timeProvider.GetUtcNow());
        }
    }

    /// <summary>Records positive raw output bytes as progress for one active operation.</summary>
    /// <param name="identity">Registered operation identity.</param>
    /// <param name="byteCount">Positive raw byte count observed before decoding.</param>
    /// <returns>Whether progress rearmed a warn-latched operation and introduced a new deadline.</returns>
    public bool ObserveOutput(string identity, int byteCount)
    {
        if (byteCount <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            ThrowIfClosed();
            var operation = GetOperation(identity);
            return operation.ObserveOutput(byteCount, _timeProvider.GetTimestamp(), _timeProvider.GetUtcNow());
        }
    }

    /// <summary>Returns an immutable snapshot without changing heartbeat or warning latches.</summary>
    /// <returns>Current state in stable workflow order.</returns>
    public CoverageRunWatchdogSnapshot Snapshot()
    {
        lock (_gate)
        {
            return CreateSnapshot(_timeProvider.GetTimestamp());
        }
    }

    /// <summary>
    /// Gets the exact remaining time until the next actionable heartbeat or no-progress deadline,
    /// capped at 24 hours so supported durations remain safe for timer implementations.
    /// </summary>
    /// <returns>Zero when work is already due; otherwise a positive delay no greater than 24 hours.</returns>
    public TimeSpan GetNextDelay()
    {
        lock (_gate)
        {
            if (_stopped || _terminalCause is not null)
            {
                return MaximumMonitorDelay;
            }

            var now = _timeProvider.GetTimestamp();
            var next = MaximumMonitorDelay;
            if (_options.HeartbeatInterval > TimeSpan.Zero)
            {
                next = Minimum(next, Remaining(_lastHeartbeat, _options.HeartbeatInterval, now));
            }

            if (_options.Mode != CoverageRunWatchdogMode.Off)
            {
                foreach (var operation in _operations.Values)
                {
                    if (!operation.IsActive ||
                        (_options.Mode == CoverageRunWatchdogMode.Warn && operation.WarningLatched))
                    {
                        continue;
                    }

                    next = Minimum(next, Remaining(operation.LastProgress, _options.NoProgressTimeout, now));
                }
            }

            return next;
        }
    }

    /// <summary>
    /// Evaluates heartbeat and no-progress deadlines, atomically latching newly stale warn-mode operations.
    /// </summary>
    /// <returns>One immutable evaluation for rendering or fail-mode revalidation.</returns>
    public CoverageRunWatchdogEvaluation Evaluate()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            var heartbeatDue = !_stopped && _terminalCause is null && _options.HeartbeatInterval > TimeSpan.Zero &&
                _timeProvider.GetElapsedTime(_lastHeartbeat, now) >= _options.HeartbeatInterval;
            if (heartbeatDue)
            {
                _lastHeartbeat = now;
            }

            var newlyStale = new List<CoverageRunWatchdogOperationSnapshot>();
            if (!_stopped && _terminalCause is null && _options.Mode != CoverageRunWatchdogMode.Off)
            {
                foreach (var operation in OrderedOperations())
                {
                    if (!operation.IsActive || operation.WarningLatched ||
                        _timeProvider.GetElapsedTime(operation.LastProgress, now) < _options.NoProgressTimeout)
                    {
                        continue;
                    }

                    if (_options.Mode == CoverageRunWatchdogMode.Warn)
                    {
                        operation.WarningLatched = true;
                    }

                    newlyStale.Add(operation.Snapshot(_timeProvider, now));
                }
            }

            return new CoverageRunWatchdogEvaluation(heartbeatDue, CreateSnapshot(now), newlyStale);
        }
    }

    /// <summary>
    /// Atomically revalidates and claims one fail-mode candidate. The first successful claim closes the registry.
    /// </summary>
    /// <param name="candidate">Previously evaluated candidate.</param>
    /// <param name="terminalCause">Revalidated terminal snapshot on success.</param>
    /// <returns>Whether this candidate won the terminal gate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <see langword="null"/>.</exception>
    public bool TryClaimTerminal(
        CoverageRunWatchdogOperationSnapshot candidate,
        out CoverageRunWatchdogOperationSnapshot? terminalCause)
        => TryClaimTerminal(candidate, out terminalCause, out _);

    /// <summary>
    /// Atomically revalidates and claims one fail-mode candidate together with every operation
    /// that remains stale at the claim instant.
    /// </summary>
    /// <param name="candidate">Previously evaluated candidate.</param>
    /// <param name="terminalCause">Revalidated terminal snapshot on success.</param>
    /// <param name="terminalEvaluation">Fresh terminal evidence with the primary candidate first on success.</param>
    /// <returns>Whether this candidate won the terminal gate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <see langword="null"/>.</exception>
    public bool TryClaimTerminal(
        CoverageRunWatchdogOperationSnapshot candidate,
        out CoverageRunWatchdogOperationSnapshot? terminalCause,
        out CoverageRunWatchdogEvaluation? terminalEvaluation)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            terminalCause = _terminalCause;
            terminalEvaluation = null;
            if (_stopped || _options.Mode != CoverageRunWatchdogMode.Fail || _terminalCause is not null ||
                !_operations.TryGetValue(candidate.Identity, out var operation) || !operation.IsActive ||
                operation.ProgressSequence != candidate.ProgressSequence)
            {
                return false;
            }

            var now = _timeProvider.GetTimestamp();
            if (_timeProvider.GetElapsedTime(operation.LastProgress, now) < _options.NoProgressTimeout)
            {
                return false;
            }

            var stale = OrderedOperations()
                .Where(item => item.IsActive && _timeProvider.GetElapsedTime(item.LastProgress, now) >= _options.NoProgressTimeout)
                .Select(item => item.Snapshot(_timeProvider, now))
                .ToArray();
            _terminalCause = stale.Single(item => string.Equals(item.Identity, candidate.Identity, StringComparison.Ordinal));
            terminalCause = _terminalCause;
            terminalEvaluation = new CoverageRunWatchdogEvaluation(
                false,
                CreateSnapshot(now),
                [_terminalCause, .. stale.Where(item => !string.Equals(item.Identity, candidate.Identity, StringComparison.Ordinal))]);
            return true;
        }
    }

    /// <summary>Stops future evaluation and closes registration or progress mutation.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
        }
    }

    /// <summary>Atomically claims caller cancellation unless another terminal outcome already won.</summary>
    public void ClaimExternalCancellation()
    {
        lock (_gate)
        {
            if (_stopped || _terminalCause is not null)
            {
                return;
            }

            _externalCancellationClaimed = true;
            _stopped = true;
        }
    }

    /// <summary>
    /// Atomically closes normal or ordinary-failure completion unless fail mode already claimed a terminal cause.
    /// </summary>
    /// <param name="terminalCause">The already-claimed watchdog cause when completion lost the outcome gate.</param>
    /// <param name="externalCancellationClaimed">Whether caller cancellation won the outcome gate.</param>
    /// <returns><see langword="true" /> when the caller claimed non-watchdog completion.</returns>
    public bool TryComplete(
        out CoverageRunWatchdogOperationSnapshot? terminalCause,
        out bool externalCancellationClaimed)
    {
        lock (_gate)
        {
            terminalCause = _terminalCause;
            externalCancellationClaimed = _externalCancellationClaimed;
            if (_terminalCause is not null)
            {
                return false;
            }

            if (_externalCancellationClaimed)
            {
                return false;
            }

            _stopped = true;
            return true;
        }
    }

    private CoverageRunWatchdogSnapshot CreateSnapshot(long now)
    {
        var operations = OrderedOperations().Select(operation => operation.Snapshot(_timeProvider, now)).ToArray();
        return new CoverageRunWatchdogSnapshot(
            _timeProvider.GetElapsedTime(_runStarted, now),
            _timeProvider.GetUtcNow(),
            operations.Count(operation => operation.State == CoverageRunWatchdogOperationState.Queued),
            operations.Count(operation => operation.State == CoverageRunWatchdogOperationState.Running),
            operations.Count(operation => operation.State == CoverageRunWatchdogOperationState.Finalizing),
            operations.Count(operation => operation.State == CoverageRunWatchdogOperationState.Complete),
            operations);
    }

    private IEnumerable<MutableOperation> OrderedOperations()
        => _operations.Values
            .OrderBy(operation => operation.Metadata.Kind)
            .ThenBy(operation => operation.Metadata.Kind == CoverageRunWatchdogOperationKind.Project ? operation.Metadata.ExecutionIndex : 0)
            .ThenBy(operation => operation.Metadata.Identity, StringComparer.Ordinal);

    private MutableOperation GetOperation(string identity)
        => _operations.TryGetValue(identity, out var operation)
            ? operation
            : throw new KeyNotFoundException($"Coverage watchdog operation '{identity}' is not registered.");

    private TimeSpan Remaining(long started, TimeSpan interval, long now)
    {
        var elapsed = _timeProvider.GetElapsedTime(started, now);
        return elapsed >= interval ? TimeSpan.Zero : interval - elapsed;
    }

    private static TimeSpan Minimum(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private void ThrowIfClosed()
    {
        if (_stopped || _terminalCause is not null)
        {
            throw new InvalidOperationException("The coverage watchdog registry is closed.");
        }
    }

    private sealed class MutableOperation
    {
        public MutableOperation(
            CoverageRunWatchdogOperation metadata,
            CoverageRunWatchdogOperationState state,
            long now,
            DateTimeOffset utcNow)
        {
            Metadata = metadata;
            State = state;
            FirstActive = state is CoverageRunWatchdogOperationState.Running or CoverageRunWatchdogOperationState.Finalizing ? now : null;
            LastProgress = now;
            LastProgressAtUtc = FirstActive is null ? null : utcNow;
            ProgressSequence = FirstActive is null ? 0 : 1;
        }

        public CoverageRunWatchdogOperation Metadata { get; }
        public CoverageRunWatchdogOperationState State { get; private set; }
        public long? FirstActive { get; private set; }
        public long LastProgress { get; private set; }
        public DateTimeOffset? LastProgressAtUtc { get; private set; }
        public long ProgressSequence { get; private set; }
        public long OutputBytes { get; private set; }
        public bool WarningLatched { get; set; }
        public bool IsActive => State is CoverageRunWatchdogOperationState.Running or CoverageRunWatchdogOperationState.Finalizing;

        public void Transition(CoverageRunWatchdogOperationState state, long now, DateTimeOffset utcNow)
        {
            State = state;
            if (state is CoverageRunWatchdogOperationState.Running or CoverageRunWatchdogOperationState.Finalizing)
            {
                FirstActive ??= now;
                RecordProgress(now, utcNow);
            }
            else if (state == CoverageRunWatchdogOperationState.Complete)
            {
                ProgressSequence = checked(ProgressSequence + 1);
                WarningLatched = false;
            }
        }

        public bool ObserveOutput(int byteCount, long now, DateTimeOffset utcNow)
        {
            if (!IsActive)
            {
                return false;
            }

            var rearmedDeadline = WarningLatched;
            OutputBytes = checked(OutputBytes + byteCount);
            RecordProgress(now, utcNow);
            return rearmedDeadline;
        }

        public CoverageRunWatchdogOperationSnapshot Snapshot(TimeProvider timeProvider, long now)
            => new(
                Metadata.Identity,
                Metadata.Kind,
                State,
                Metadata.ExecutionIndex,
                Metadata.Project,
                Metadata.Log,
                FirstActive is long started ? timeProvider.GetElapsedTime(started, now) : TimeSpan.Zero,
                IsActive ? timeProvider.GetElapsedTime(LastProgress, now) : TimeSpan.Zero,
                LastProgressAtUtc,
                ProgressSequence,
                OutputBytes,
                WarningLatched);

        private void RecordProgress(long now, DateTimeOffset utcNow)
        {
            LastProgress = now;
            LastProgressAtUtc = utcNow;
            ProgressSequence = checked(ProgressSequence + 1);
            WarningLatched = false;
        }
    }
}

/// <summary>Contains privacy-normalized command metadata for a watchdog incident.</summary>
/// <param name="Executable">Logical executable name.</param>
/// <param name="Options">Command verb and switch names with all values omitted.</param>
internal sealed record CoverageRunWatchdogCommand(string Executable, IReadOnlyList<string> Options);

/// <summary>Contains one privacy-normalized operation in a watchdog incident.</summary>
/// <param name="Kind">Lowercase operation kind.</param>
/// <param name="Project">Normalized project path, when applicable.</param>
/// <param name="State">Lowercase lifecycle state.</param>
/// <param name="ElapsedMilliseconds">Elapsed active time.</param>
/// <param name="NoProgressMilliseconds">Time since observable progress.</param>
/// <param name="LastProgressAtUtc">UTC time of the last progress event.</param>
/// <param name="ProgressSequence">Sequence used to confirm classification.</param>
/// <param name="OutputBytes">Total positive raw output bytes.</param>
/// <param name="Log">Normalized log path, when applicable.</param>
/// <param name="Command">Privacy-normalized command metadata, when applicable.</param>
internal sealed record CoverageRunWatchdogIncidentOperation(
    CoverageRunWatchdogOperationKind Kind,
    string? Project,
    CoverageRunWatchdogOperationState State,
    long ElapsedMilliseconds,
    long NoProgressMilliseconds,
    DateTimeOffset? LastProgressAtUtc,
    long ProgressSequence,
    long OutputBytes,
    string? Log,
    CoverageRunWatchdogCommand? Command);

/// <summary>Contains bounded process-cleanup evidence for a watchdog incident.</summary>
/// <param name="Status">One of <c>not-requested</c>, <c>complete</c>, <c>failed</c>, or <c>deadline-exceeded</c>.</param>
/// <param name="Detail">Allowlisted cleanup detail, or <see langword="null"/>.</param>
internal sealed record CoverageRunWatchdogCleanup(string Status, string? Detail);

/// <summary>Defines the version-one watchdog artifact contract.</summary>
/// <param name="SchemaVersion">Artifact schema version; version one is currently supported.</param>
/// <param name="IncidentOrdinal">One-based incident number for this run.</param>
/// <param name="Outcome">Either <c>warning</c> or <c>terminated</c>.</param>
/// <param name="DiagnosticCode">Terminal diagnostic code, or <see langword="null"/> for warnings.</param>
/// <param name="WatchdogMode">Configured watchdog mode.</param>
/// <param name="HeartbeatIntervalMilliseconds">Configured heartbeat interval.</param>
/// <param name="NoProgressTimeoutMilliseconds">Configured no-progress interval.</param>
/// <param name="RunElapsedMilliseconds">Elapsed run time at classification.</param>
/// <param name="ClassifiedAtUtc">UTC classification time.</param>
/// <param name="Primary">Primary stale operation.</param>
/// <param name="ConcurrentlyStale">Additional stale operations in stable order.</param>
/// <param name="ConcurrentlyStaleOmitted">Number of trailing operations omitted for the size budget.</param>
/// <param name="Cleanup">Final cleanup status.</param>
internal sealed record CoverageRunWatchdogArtifact(
    int SchemaVersion,
    long IncidentOrdinal,
    string Outcome,
    string? DiagnosticCode,
    CoverageRunWatchdogMode WatchdogMode,
    long HeartbeatIntervalMilliseconds,
    long NoProgressTimeoutMilliseconds,
    long RunElapsedMilliseconds,
    DateTimeOffset ClassifiedAtUtc,
    CoverageRunWatchdogIncidentOperation Primary,
    IReadOnlyList<CoverageRunWatchdogIncidentOperation> ConcurrentlyStale,
    int ConcurrentlyStaleOmitted,
    CoverageRunWatchdogCleanup Cleanup);

/// <summary>
/// Validates and serializes watchdog artifacts using deterministic lowercase enum strings,
/// a 256-concurrent-operation pre-cap, and a strict size budget.
/// </summary>
internal static class CoverageRunWatchdogArtifactSerializer
{
    /// <summary>Gets the largest permitted UTF-8 artifact size, strictly below 64 KiB.</summary>
    public const int MaximumBytes = (64 * 1024) - 1;

    /// <summary>Gets the maximum number of concurrent operations considered before size fitting.</summary>
    public const int MaximumConcurrentOperations = 256;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Deserializes a bounded schema-version-one artifact for bootstrap promotion.</summary>
    /// <param name="bytes">UTF-8 JSON previously written by <see cref="Serialize"/>.</param>
    /// <returns>The validated watchdog artifact.</returns>
    /// <exception cref="JsonException">
    /// The payload is malformed, empty, oversized, or violates the schema-version-one field contract.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The artifact uses an unsupported schema version.</exception>
    public static CoverageRunWatchdogArtifact Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes)
        {
            throw new JsonException("Watchdog artifact payload exceeded the size budget.");
        }

        var artifact = JsonSerializer.Deserialize<CoverageRunWatchdogArtifact>(bytes, SerializerOptions)
            ?? throw new JsonException("Watchdog artifact payload was empty.");
        if (artifact.SchemaVersion != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Only watchdog artifact schema version 1 is supported.");
        }

        if (artifact.IncidentOrdinal <= 0 ||
            artifact.HeartbeatIntervalMilliseconds < 0 ||
            artifact.NoProgressTimeoutMilliseconds <= 0 ||
            artifact.RunElapsedMilliseconds < 0 ||
            artifact.ConcurrentlyStaleOmitted < 0 ||
            !Enum.IsDefined(artifact.WatchdogMode) ||
            artifact.Primary is null ||
            artifact.ConcurrentlyStale is null ||
            artifact.Cleanup is null ||
            artifact.ConcurrentlyStale.Count > MaximumConcurrentOperations ||
            artifact.ConcurrentlyStale.Any(operation => operation is null) ||
            !IsValidOperation(artifact.Primary) ||
            artifact.ConcurrentlyStale.Any(operation => !IsValidOperation(operation)) ||
            !IsValidOutcome(artifact))
        {
            throw new JsonException("Watchdog artifact payload did not satisfy the schema-version-one contract.");
        }

        return artifact;
    }

    /// <summary>
    /// Serializes an artifact, pre-capping concurrent operations at 256, then dropping
    /// concurrent command options, log pointers, and trailing records to fit the byte budget.
    /// </summary>
    /// <param name="artifact">Complete normalized incident.</param>
    /// <returns>UTF-8 JSON below 64 KiB.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The artifact uses an unsupported schema version.</exception>
    /// <exception cref="InvalidOperationException">Required primary and top-level fields alone exceed the budget.</exception>
    public static byte[] Serialize(CoverageRunWatchdogArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.SchemaVersion != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(artifact), "Only watchdog artifact schema version 1 is supported.");
        }

        var retained = artifact.ConcurrentlyStale.Take(MaximumConcurrentOperations).ToArray();
        var omitted = SaturatingAdd(
            artifact.ConcurrentlyStaleOmitted,
            artifact.ConcurrentlyStale.Count - retained.Length);
        var bounded = artifact with { ConcurrentlyStale = retained, ConcurrentlyStaleOmitted = omitted };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(bounded, SerializerOptions);
        if (bytes.Length <= MaximumBytes)
        {
            return bytes;
        }

        var concurrent = retained
            .Select(operation => operation with
            {
                Log = null,
                Command = operation.Command is null ? null : operation.Command with { Options = [] },
            })
            .ToList();
        bounded = artifact with { ConcurrentlyStale = concurrent, ConcurrentlyStaleOmitted = omitted };
        bytes = JsonSerializer.SerializeToUtf8Bytes(bounded, SerializerOptions);
        if (bytes.Length <= MaximumBytes)
        {
            return bytes;
        }

        var lower = 0;
        var upper = concurrent.Count;
        byte[]? fittedBytes = null;
        while (lower <= upper)
        {
            var candidateCount = lower + ((upper - lower) / 2);
            var candidate = concurrent.Take(candidateCount).ToArray();
            var candidateOmitted = SaturatingAdd(omitted, concurrent.Count - candidateCount);
            bounded = artifact with { ConcurrentlyStale = candidate, ConcurrentlyStaleOmitted = candidateOmitted };
            var candidateBytes = JsonSerializer.SerializeToUtf8Bytes(bounded, SerializerOptions);
            if (candidateBytes.Length <= MaximumBytes)
            {
                fittedBytes = candidateBytes;
                lower = candidateCount + 1;
            }
            else
            {
                upper = candidateCount - 1;
            }
        }

        return fittedBytes
            ?? throw new InvalidOperationException("Required watchdog artifact fields exceed the 64 KiB budget.");
    }

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Min(int.MaxValue, (long)left + right);

    private static bool IsValidOutcome(CoverageRunWatchdogArtifact artifact)
        => artifact.Outcome switch
        {
            "warning" => artifact.WatchdogMode == CoverageRunWatchdogMode.Warn &&
                artifact.DiagnosticCode is null &&
                artifact.Cleanup is { Status: "not-requested", Detail: null },
            "terminated" => artifact.WatchdogMode == CoverageRunWatchdogMode.Fail &&
                string.Equals(artifact.DiagnosticCode, "ASCOV121", StringComparison.Ordinal) &&
                IsValidTerminalCleanup(artifact.Cleanup),
            _ => false,
        };

    private static bool IsValidTerminalCleanup(CoverageRunWatchdogCleanup cleanup)
        => cleanup switch
        {
            { Status: "complete", Detail: null } => true,
            { Status: "failed", Detail: "kill-failed" } => true,
            { Status: "deadline-exceeded", Detail: "cleanup-incomplete" or "root-timeout" or "kill-failed" } => true,
            _ => false,
        };

    private static bool IsValidOperation(CoverageRunWatchdogIncidentOperation operation)
        => Enum.IsDefined(operation.Kind) &&
            operation.State is CoverageRunWatchdogOperationState.Running or CoverageRunWatchdogOperationState.Finalizing &&
            operation.ElapsedMilliseconds >= 0 &&
            operation.NoProgressMilliseconds >= 0 &&
            operation.ProgressSequence >= 0 &&
            operation.OutputBytes >= 0 &&
            (operation.Command is null ||
                (!string.IsNullOrWhiteSpace(operation.Command.Executable) && operation.Command.Options is not null));
}
