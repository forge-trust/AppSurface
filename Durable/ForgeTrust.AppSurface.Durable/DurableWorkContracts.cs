namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Declares how the runtime may recover after an external provider outcome becomes unknown.
/// </summary>
public enum DurableProviderSafety
{
    /// <summary>
    /// Repeating the provider operation is safe by the provider contract.
    /// </summary>
    Idempotent = 0,

    /// <summary>
    /// Repeating the provider operation is safe only with the immutable activity-derived provider key.
    /// </summary>
    ProviderKeyed = 1,

    /// <summary>
    /// A side-effect-free reconciler must establish provider state before any retry.
    /// </summary>
    ReconcileBeforeRetry = 2,

    /// <summary>
    /// An unknown provider outcome always requires an authorized manual resolution.
    /// </summary>
    ManualResolution = 3,
}

/// <summary>
/// Versioned retry and lease snapshot stored when durable work is accepted.
/// </summary>
public sealed record DurableWorkRetryPolicy
{
    /// <summary>
    /// Gets the safe default policy for ordinary durable work.
    /// </summary>
    public static DurableWorkRetryPolicy Default { get; } = new(
        maximumAttempts: 8,
        maximumElapsedTime: TimeSpan.FromHours(24),
        initialRetryDelay: TimeSpan.FromSeconds(1),
        maximumRetryDelay: TimeSpan.FromMinutes(15),
        leaseDuration: TimeSpan.FromMinutes(2),
        renewalCadence: TimeSpan.FromSeconds(30),
        maximumLeaseLifetime: TimeSpan.FromMinutes(10),
        backoffAlgorithm: "exponential-v1");

    /// <summary>
    /// Initializes a retry and lease policy.
    /// </summary>
    public DurableWorkRetryPolicy(
        int maximumAttempts,
        TimeSpan maximumElapsedTime,
        TimeSpan initialRetryDelay,
        TimeSpan maximumRetryDelay,
        TimeSpan leaseDuration,
        TimeSpan renewalCadence,
        TimeSpan maximumLeaseLifetime,
        string backoffAlgorithm)
    {
        if (maximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        RequirePositive(maximumElapsedTime, nameof(maximumElapsedTime));
        RequirePositive(initialRetryDelay, nameof(initialRetryDelay));
        RequirePositive(maximumRetryDelay, nameof(maximumRetryDelay));
        RequirePositive(leaseDuration, nameof(leaseDuration));
        RequirePositive(renewalCadence, nameof(renewalCadence));
        RequirePositive(maximumLeaseLifetime, nameof(maximumLeaseLifetime));
        if (initialRetryDelay > maximumRetryDelay)
        {
            throw new ArgumentException("Initial retry delay must not exceed maximum retry delay.", nameof(initialRetryDelay));
        }

        if (renewalCadence >= leaseDuration)
        {
            throw new ArgumentException("Lease renewal cadence must be shorter than the lease duration.", nameof(renewalCadence));
        }

        if (leaseDuration > maximumLeaseLifetime)
        {
            throw new ArgumentException("Lease duration must not exceed maximum lease lifetime.", nameof(leaseDuration));
        }

        MaximumAttempts = maximumAttempts;
        MaximumElapsedTime = maximumElapsedTime;
        InitialRetryDelay = initialRetryDelay;
        MaximumRetryDelay = maximumRetryDelay;
        LeaseDuration = leaseDuration;
        RenewalCadence = renewalCadence;
        MaximumLeaseLifetime = maximumLeaseLifetime;
        BackoffAlgorithm = DurableIdentifier.Require(backoffAlgorithm, nameof(backoffAlgorithm), 100);
    }

    /// <summary>Gets the maximum number of execution attempts.</summary>
    public int MaximumAttempts { get; }

    /// <summary>Gets the maximum time from acceptance through retry exhaustion.</summary>
    public TimeSpan MaximumElapsedTime { get; }

    /// <summary>Gets the first retry delay.</summary>
    public TimeSpan InitialRetryDelay { get; }

    /// <summary>Gets the retry delay cap.</summary>
    public TimeSpan MaximumRetryDelay { get; }

    /// <summary>Gets the bounded claim lease duration.</summary>
    public TimeSpan LeaseDuration { get; }

    /// <summary>Gets the recommended lease renewal cadence.</summary>
    public TimeSpan RenewalCadence { get; }

    /// <summary>Gets the maximum lifetime of one claim generation.</summary>
    public TimeSpan MaximumLeaseLifetime { get; }

    /// <summary>Gets the versioned backoff algorithm identifier.</summary>
    public string BackoffAlgorithm { get; }

    private static void RequirePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>
/// Represents the authoritative lifecycle state of a durable work aggregate.
/// </summary>
public enum DurableWorkState
{
    /// <summary>Accepted in the caller or runtime transaction.</summary>
    Accepted = 0,
    /// <summary>Eligible for claim.</summary>
    Ready = 1,
    /// <summary>Claimed under a bounded lease generation.</summary>
    Claimed = 2,
    /// <summary>Cancellation was requested before terminal truth was known.</summary>
    CancelRequested = 3,
    /// <summary>Cancellation was requested after an external effect permit.</summary>
    CancelPending = 4,
    /// <summary>Completed successfully.</summary>
    Succeeded = 5,
    /// <summary>Completed successfully after cancellation was requested.</summary>
    SucceededAfterCancelRequested = 6,
    /// <summary>Exhausted or encountered a terminal technical failure.</summary>
    FailedTerminal = 7,
    /// <summary>Canceled before an external effect permit was recorded.</summary>
    CanceledBeforeEffect = 8,
    /// <summary>Stopped pending reconciliation, operator action, or compatibility repair.</summary>
    Suspended = 9,
}

/// <summary>
/// Describes one idempotent durable work submission.
/// </summary>
public sealed record DurableWorkRequest
{
    /// <summary>
    /// Initializes a durable work request.
    /// </summary>
    public DurableWorkRequest(
        DurableScopeId scopeId,
        DurableCommandId commandId,
        string idempotencyKey,
        string workName,
        string workVersion,
        DurableEncodedPayload payload,
        DurableProviderSafety providerSafety,
        DurableWorkRetryPolicy? retryPolicy = null,
        DateTimeOffset? dueAtUtc = null)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        if (!Enum.IsDefined(providerSafety))
        {
            throw new ArgumentOutOfRangeException(nameof(providerSafety));
        }

        ScopeId = scopeId;
        CommandId = commandId;
        IdempotencyKey = DurableIdentifier.Require(idempotencyKey, nameof(idempotencyKey), 200);
        WorkName = DurableIdentifier.Require(workName, nameof(workName), 200);
        WorkVersion = DurableIdentifier.Require(workVersion, nameof(workVersion), 100);
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        ProviderSafety = providerSafety;
        RetryPolicy = retryPolicy ?? DurableWorkRetryPolicy.Default;
        DueAtUtc = dueAtUtc?.ToUniversalTime();
        Fingerprint = DurableCommandFingerprints.Create(
            "appsurface.durable.work.enqueue.v1",
            ScopeId.Value,
            WorkName,
            WorkVersion,
            Payload,
            ProviderSafety,
            RetryPolicy,
            DueAtUtc);
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the idempotent command identifier.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets the caller retry key, scoped to the owning scope.</summary>
    public string IdempotencyKey { get; }

    /// <summary>Gets the registered work name.</summary>
    public string WorkName { get; }

    /// <summary>Gets the registered work contract version.</summary>
    public string WorkVersion { get; }

    /// <summary>Gets the immutable encoded work payload.</summary>
    public DurableEncodedPayload Payload { get; }

    /// <summary>Gets the provider ambiguity policy snapshot.</summary>
    public DurableProviderSafety ProviderSafety { get; }

    /// <summary>Gets the retry and lease policy snapshot.</summary>
    public DurableWorkRetryPolicy RetryPolicy { get; }

    /// <summary>Gets the first UTC eligibility time, or immediate eligibility when absent.</summary>
    public DateTimeOffset? DueAtUtc { get; }

    /// <summary>Gets the computed versioned fingerprint of mutation-affecting semantic fields.</summary>
    public DurableCommandFingerprint Fingerprint { get; }
}

/// <summary>
/// Indicates whether a durable acceptance was newly committed or deduplicated.
/// </summary>
public enum DurableWorkAcceptanceKind
{
    /// <summary>A new work aggregate was accepted.</summary>
    Accepted = 0,
    /// <summary>The exact prior request and outcome were returned.</summary>
    Duplicate = 1,
}

/// <summary>
/// Records the stable outcome of accepting durable work.
/// </summary>
public sealed record DurableWorkAcceptance
{
    /// <summary>
    /// Initializes an acceptance result.
    /// </summary>
    public DurableWorkAcceptance(
        DurableWorkId workId,
        DurableCommandId commandId,
        DurableWorkAcceptanceKind kind,
        long revision,
        DateTimeOffset acceptedAtUtc)
    {
        DurableIdentifier.Require(workId.Value, nameof(workId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        WorkId = workId;
        CommandId = commandId;
        Kind = kind;
        Revision = revision;
        AcceptedAtUtc = acceptedAtUtc.ToUniversalTime();
    }

    /// <summary>Gets the immutable durable work identifier.</summary>
    public DurableWorkId WorkId { get; }

    /// <summary>Gets the accepted command identifier.</summary>
    public DurableCommandId CommandId { get; }

    /// <summary>Gets whether the request was new or deduplicated.</summary>
    public DurableWorkAcceptanceKind Kind { get; }

    /// <summary>Gets the aggregate revision produced by acceptance.</summary>
    public long Revision { get; }

    /// <summary>Gets the authoritative store acceptance time in UTC.</summary>
    public DateTimeOffset AcceptedAtUtc { get; }
}

/// <summary>
/// Accepts durable work outside an existing caller-owned transaction.
/// </summary>
public interface IDurableWorkClient
{
    /// <summary>
    /// Accepts work atomically in a runtime-owned authoritative-store transaction.
    /// </summary>
    ValueTask<DurableOperationResult<DurableWorkAcceptance>> EnqueueAsync(
        DurableWorkRequest request,
        CancellationToken cancellationToken = default);
}
