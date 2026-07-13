namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Configures PostgreSQL-backed durable execution without granting schema-migration authority.
/// </summary>
/// <remarks>
/// These settings are process-local execution policy. Durable work, Flow, and schedule policy is snapshotted in the
/// database when commands are accepted. Changing these settings therefore does not rewrite authoritative history.
/// </remarks>
public sealed class AppSurfaceDurablePostgreSqlOptions
{
    private string _workerId = CreateDefaultWorkerId();

    /// <summary>
    /// Gets or sets the privacy-safe identity recorded on claim leases.
    /// </summary>
    /// <remarks>
    /// The value distinguishes concurrently running hosts; it is not an authorization credential and must not contain
    /// a connection string, access token, user-provided text, or other secret. The default combines a bounded machine
    /// name with the current process identifier.
    /// </remarks>
    public string WorkerId
    {
        get => _workerId;
        set => _workerId = value;
    }

    /// <summary>
    /// Gets or sets whether accepted commands publish metadata-only PostgreSQL wake hints.
    /// </summary>
    /// <remarks>
    /// Wake hints reduce latency but are never a correctness dependency. Polling and due-state discovery remain
    /// authoritative, so disabling notifications does not change durable semantics.
    /// </remarks>
    public bool SendWakeNotifications { get; set; } = true;

    /// <summary>Gets or sets the maximum aggregate count processed by one hosted pump pass.</summary>
    public int MaximumItemsPerPass { get; set; } = 32;

    /// <summary>Gets or sets the budget for discovering and beginning additional items in one hosted pump pass.</summary>
    public TimeSpan TimeBudgetPerPass { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the durable surfaces processed by the opt-in hosted worker.</summary>
    public DurableRuntimeSurface HostedSurfaces { get; set; } = DurableRuntimeSurface.All;

    /// <summary>
    /// Gets or sets the longest delay between authoritative discovery passes when no immediate work remains.
    /// </summary>
    /// <remarks>
    /// This interval bounds recovery from lost wake hints. It does not replace a continuous worker or an external
    /// activator; a process that is scaled to zero cannot advance schedules by itself.
    /// </remarks>
    public TimeSpan IdlePollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the delay after a transient PostgreSQL or timeout failure in the hosted loop.</summary>
    public TimeSpan TransientFailureDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets how old the last heartbeat may become before health reports <c>ASDUR404</c>.
    /// </summary>
    /// <remarks>
    /// Keep this comfortably above <see cref="IdlePollingInterval"/> and the expected external-activator cadence. A
    /// crashed process may not relinquish a reused worker id until this bound expires; use a unique worker id per live
    /// replica instead of shortening the bound to permit concurrent reuse.
    /// </remarks>
    public TimeSpan HeartbeatStaleAfter { get; set; } = TimeSpan.FromSeconds(15);

    internal AppSurfaceDurablePostgreSqlOptions SnapshotAndValidate()
    {
        var workerId = RequireWorkerId(WorkerId);
        _ = new DurableRuntimePumpRequest(MaximumItemsPerPass, TimeBudgetPerPass, HostedSurfaces);
        RequireDelay(IdlePollingInterval, nameof(IdlePollingInterval), TimeSpan.FromMinutes(5));
        RequireDelay(TransientFailureDelay, nameof(TransientFailureDelay), TimeSpan.FromMinutes(5));
        RequireDelay(HeartbeatStaleAfter, nameof(HeartbeatStaleAfter), TimeSpan.FromHours(1));
        if (HeartbeatStaleAfter < TimeSpan.FromSeconds(1)
            || HeartbeatStaleAfter <= IdlePollingInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatStaleAfter),
                HeartbeatStaleAfter,
                "The heartbeat stale bound must be at least one second and longer than the idle polling interval.");
        }

        return new AppSurfaceDurablePostgreSqlOptions
        {
            WorkerId = workerId,
            SendWakeNotifications = SendWakeNotifications,
            MaximumItemsPerPass = MaximumItemsPerPass,
            TimeBudgetPerPass = TimeBudgetPerPass,
            HostedSurfaces = HostedSurfaces,
            IdlePollingInterval = IdlePollingInterval,
            TransientFailureDelay = TransientFailureDelay,
            HeartbeatStaleAfter = HeartbeatStaleAfter,
        };
    }

    private static string CreateDefaultWorkerId()
    {
        var machineName = string.IsNullOrWhiteSpace(Environment.MachineName)
            ? "host"
            : Environment.MachineName;
        var maximumMachineLength = 160;
        if (machineName.Length > maximumMachineLength)
        {
            machineName = machineName[..maximumMachineLength];
        }

        machineName = string.Concat(machineName.Select(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':'
                ? character
                : '-'));

        return $"{machineName}:{Environment.ProcessId}";
    }

    private static string RequireWorkerId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 200
            || value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException(
                "WorkerId must use 1 to 200 ASCII letters, digits, '-', '_', '.', or ':' and must not contain user data or secrets.",
                nameof(WorkerId));
        }

        return value;
    }

    private static void RequireDelay(TimeSpan value, string parameterName, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Delay must be greater than zero and no longer than {maximum}.");
        }
    }
}
