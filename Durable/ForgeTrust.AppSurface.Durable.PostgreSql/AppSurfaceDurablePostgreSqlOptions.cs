using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Configures process-local PostgreSQL durable runtime behavior.
/// </summary>
/// <remarks>
/// These options control activation only. They do not apply migrations, alter durable protocol policy, or replace
/// PostgreSQL leases, recovery epochs, and history as the source of truth. Registering PostgreSQL storage remains
/// passive until <see cref="AppSurfaceDurablePostgreSqlBuilder.AddWorkerHost"/> is called.
/// </remarks>
public sealed class AppSurfaceDurablePostgreSqlOptions
{
    private string _workerId = CreateDefaultWorkerId();

    /// <summary>Gets or sets the privacy-safe identity written on short-lived claims and runtime heartbeats.</summary>
    /// <remarks>
    /// Use a unique value for every concurrently live replica. It is not an authorization credential and must not
    /// contain connection details, user input, or other secrets.
    /// </remarks>
    public string WorkerId
    {
        get => _workerId;
        set => _workerId = value;
    }

    /// <summary>Gets or sets whether accepted commands emit metadata-only PostgreSQL wake hints.</summary>
    /// <remarks>Polling remains authoritative when hints are disabled, lost, duplicated, delayed, or unavailable.</remarks>
    public bool SendWakeNotifications { get; set; } = true;

    /// <summary>Gets or sets the maximum completed or committed Turns in one hosted pass.</summary>
    public int MaximumItemsPerPass { get; set; } = 32;

    /// <summary>Gets or sets the budget for discovering and starting additional Turns in one hosted pass.</summary>
    public TimeSpan TimeBudgetPerPass { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the durable surfaces activated by this worker instance.</summary>
    public DurableRuntimeSurface HostedSurfaces { get; set; } = DurableRuntimeSurface.All;

    /// <summary>Gets or sets the maximum delay between authoritative polling passes when no work is immediately due.</summary>
    public TimeSpan IdlePollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the bounded delay before retrying a transient store or listener failure.</summary>
    public TimeSpan TransientFailureDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets how old a heartbeat may become before health reports the worker as stale.</summary>
    public TimeSpan HeartbeatStaleAfter { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets or sets host shutdown time reserved for drain persistence and runtime cleanup.</summary>
    /// <remarks>
    /// Hosted startup validates this reserve against <c>HostOptions.ShutdownTimeout</c>. A pass admitted by the host
    /// receives only the remaining time, while an externally activated pass retains its own caller-supplied budget.
    /// </remarks>
    public TimeSpan ShutdownReserve { get; set; } = TimeSpan.FromSeconds(5);

    internal AppSurfaceDurablePostgreSqlOptions SnapshotAndValidate()
    {
        var workerId = RequireWorkerId(WorkerId);
        _ = new DurableRuntimePumpRequest(MaximumItemsPerPass, TimeBudgetPerPass, HostedSurfaces);
        RequirePositiveBounded(IdlePollingInterval, nameof(IdlePollingInterval), TimeSpan.FromMinutes(5));
        RequirePositiveBounded(TransientFailureDelay, nameof(TransientFailureDelay), TimeSpan.FromMinutes(5));
        RequirePositiveBounded(HeartbeatStaleAfter, nameof(HeartbeatStaleAfter), TimeSpan.FromHours(1));
        RequirePositiveBounded(ShutdownReserve, nameof(ShutdownReserve), TimeSpan.FromMinutes(5));
        if (HeartbeatStaleAfter < TimeSpan.FromSeconds(1) || HeartbeatStaleAfter <= IdlePollingInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatStaleAfter),
                HeartbeatStaleAfter,
                "HeartbeatStaleAfter must be at least one second and longer than IdlePollingInterval.");
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
            ShutdownReserve = ShutdownReserve,
        };
    }

    private static string CreateDefaultWorkerId()
    {
        var machineName = string.IsNullOrWhiteSpace(Environment.MachineName) ? "host" : Environment.MachineName;
        if (machineName.Length > 160)
        {
            machineName = machineName[..160];
        }

        machineName = string.Concat(machineName.Select(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' ? character : '-'));
        return $"{machineName}:{Environment.ProcessId}";
    }

    private static string RequireWorkerId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 200
            || value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException(
                "WorkerId must contain 1 to 200 ASCII letters, digits, '-', '_', '.', or ':' and must not contain secrets or user data.",
                nameof(WorkerId));
        }

        return value;
    }

    private static void RequirePositiveBounded(TimeSpan value, string parameterName, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be positive and no longer than {maximum}.");
        }
    }
}
