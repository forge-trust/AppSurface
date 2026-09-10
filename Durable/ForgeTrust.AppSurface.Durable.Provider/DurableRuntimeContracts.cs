namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>
/// Selects which durable surfaces a bounded pump pass may process.
/// </summary>
[Flags]
public enum DurableRuntimeSurface
{
    /// <summary>No durable surface.</summary>
    None = 0,
    /// <summary>Direct and Flow activity work.</summary>
    Work = 1,
    /// <summary>Flow commands, timers, and external-event continuations.</summary>
    Flow = 2,
    /// <summary>Schedule cursors and occurrences.</summary>
    Schedule = 4,
    /// <summary>All durable surfaces.</summary>
    All = Work | Flow | Schedule,
}

/// <summary>
/// Bounds one runtime pump pass for hosted and externally activated execution.
/// </summary>
public sealed record DurableRuntimePumpRequest
{
    /// <summary>
    /// Initializes a bounded pump request.
    /// </summary>
    /// <param name="maximumItems">Total item bound from 1 through 10,000; defaults to 32.</param>
    /// <param name="timeBudget">Positive pass budget of at most five minutes; defaults to ten seconds.</param>
    /// <param name="surfaces">Nonempty defined set of durable surfaces; defaults to all surfaces.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a bound, budget, or surface set is invalid.</exception>
    public DurableRuntimePumpRequest(
        int maximumItems = 32,
        TimeSpan? timeBudget = null,
        DurableRuntimeSurface surfaces = DurableRuntimeSurface.All)
    {
        if (maximumItems is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }

        var resolvedBudget = timeBudget ?? TimeSpan.FromSeconds(10);
        if (resolvedBudget <= TimeSpan.Zero || resolvedBudget > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeBudget));
        }

        if (surfaces == DurableRuntimeSurface.None || (surfaces & ~DurableRuntimeSurface.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(surfaces));
        }

        MaximumItems = maximumItems;
        TimeBudget = resolvedBudget;
        Surfaces = surfaces;
    }

    /// <summary>Gets the total item bound.</summary>
    public int MaximumItems { get; }

    /// <summary>
    /// Gets the wall-clock budget for discovering and beginning additional items.
    /// </summary>
    /// <remarks>
    /// An already-started provider call or authoritative-store transaction may finish past this budget. The runtime does
    /// not manufacture an ambiguous external outcome merely to enforce a hard stopwatch deadline.
    /// </remarks>
    public TimeSpan TimeBudget { get; }

    /// <summary>Gets the selected durable surfaces.</summary>
    public DurableRuntimeSurface Surfaces { get; }
}

/// <summary>
/// Summarizes one bounded runtime pump pass without high-cardinality identifiers.
/// </summary>
public sealed record DurableRuntimePumpResult
{
    /// <summary>
    /// Initializes a pump result.
    /// </summary>
    public DurableRuntimePumpResult(
        int discovered,
        int claimed,
        int processed,
        int deferred,
        int failed,
        bool hasMore,
        DateTimeOffset? nextDueAtUtc,
        TimeSpan elapsed)
    {
        RequireNonNegative(discovered, nameof(discovered));
        RequireNonNegative(claimed, nameof(claimed));
        RequireNonNegative(processed, nameof(processed));
        RequireNonNegative(deferred, nameof(deferred));
        RequireNonNegative(failed, nameof(failed));

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        Discovered = discovered;
        Claimed = claimed;
        Processed = processed;
        Deferred = deferred;
        Failed = failed;
        HasMore = hasMore;
        NextDueAtUtc = nextDueAtUtc?.ToUniversalTime();
        Elapsed = elapsed;
    }

    /// <summary>Gets discovered candidate count.</summary>
    public int Discovered { get; }

    /// <summary>Gets successfully claimed count.</summary>
    public int Claimed { get; }

    /// <summary>Gets successfully processed count.</summary>
    public int Processed { get; }

    /// <summary>Gets policy-deferred count.</summary>
    public int Deferred { get; }

    /// <summary>Gets safely failed or suspended count.</summary>
    public int Failed { get; }

    /// <summary>Gets whether immediately eligible work may remain.</summary>
    public bool HasMore { get; }

    /// <summary>Gets the earliest known future due time.</summary>
    public DateTimeOffset? NextDueAtUtc { get; }

    /// <summary>Gets elapsed duration of the bounded pass.</summary>
    public TimeSpan Elapsed { get; }

    private static void RequireNonNegative(int value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Pump counts must not be negative.");
        }
    }
}

/// <summary>
/// Identifies the outcome of one admission-aware durable runtime pump attempt.
/// </summary>
public enum DurableRuntimePumpAttemptKind
{
    /// <summary>Application execution returned and terminal provider bookkeeping completed.</summary>
    Completed = 0,

    /// <summary>The provider refused admission before application execution began.</summary>
    Refused = 1,

    /// <summary>The provider could not observe the authoritative store before application execution began.</summary>
    Unavailable = 2,

    /// <summary>The observed schema or runtime epoch did not authorize application execution.</summary>
    Incompatible = 3,
}

/// <summary>
/// Reports whether one admission-aware pump invocation completed or stopped before application execution.
/// </summary>
/// <remarks>
/// <see cref="DurableRuntimePumpAttemptKind.Refused"/>, <see cref="DurableRuntimePumpAttemptKind.Unavailable"/>, and
/// <see cref="DurableRuntimePumpAttemptKind.Incompatible"/> certify only that this invocation did not enter
/// application execution. They do not establish the status of an earlier invocation whose response was lost,
/// another process, or item-level external effects.
/// </remarks>
public sealed record DurableRuntimePumpAttempt
{
    /// <summary>Initializes a closed pump-attempt outcome.</summary>
    /// <param name="kind">Defined attempt outcome.</param>
    /// <param name="result">Completed pump result; required only for <see cref="DurableRuntimePumpAttemptKind.Completed"/>.</param>
    /// <param name="problemCode">
    /// Provider-neutral problem code; required for unavailable and incompatible outcomes and absent otherwise.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="kind"/> is undefined.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the result or problem code contradicts <paramref name="kind"/>.
    /// </exception>
    public DurableRuntimePumpAttempt(
        DurableRuntimePumpAttemptKind kind,
        DurableRuntimePumpResult? result,
        string? problemCode)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        switch (kind)
        {
            case DurableRuntimePumpAttemptKind.Completed:
                if (result is null)
                {
                    throw new ArgumentException("A completed pump attempt requires a result.", nameof(result));
                }

                if (problemCode is not null)
                {
                    throw new ArgumentException("A completed pump attempt cannot have a problem code.", nameof(problemCode));
                }

                break;

            case DurableRuntimePumpAttemptKind.Refused:
                if (result is not null)
                {
                    throw new ArgumentException("A refused pump attempt cannot have a result.", nameof(result));
                }

                if (problemCode is not null)
                {
                    throw new ArgumentException("A refused pump attempt cannot have a problem code.", nameof(problemCode));
                }

                break;

            case DurableRuntimePumpAttemptKind.Unavailable:
                RequireNoResult(result, kind);
                if (!string.Equals(problemCode, DurableProblemCodes.StoreUnavailable, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"An unavailable pump attempt requires {DurableProblemCodes.StoreUnavailable}.",
                        nameof(problemCode));
                }

                break;

            case DurableRuntimePumpAttemptKind.Incompatible:
                RequireNoResult(result, kind);
                if (!IsCompatibilityProblemCode(problemCode))
                {
                    throw new ArgumentException(
                        "An incompatible pump attempt requires ASDUR108 or a code from ASDUR400 through ASDUR403.",
                        nameof(problemCode));
                }

                break;
        }

        Kind = kind;
        Result = result;
        ProblemCode = problemCode;
    }

    /// <summary>Gets the closed attempt outcome.</summary>
    public DurableRuntimePumpAttemptKind Kind { get; }

    /// <summary>Gets the completed pump result, or null when execution did not begin.</summary>
    public DurableRuntimePumpResult? Result { get; }

    /// <summary>Gets the provider-neutral incompatibility or unavailability code, when applicable.</summary>
    public string? ProblemCode { get; }

    private static void RequireNoResult(DurableRuntimePumpResult? result, DurableRuntimePumpAttemptKind kind)
    {
        if (result is not null)
        {
            throw new ArgumentException($"A {kind} pump attempt cannot have a result.", nameof(result));
        }
    }

    private static bool IsCompatibilityProblemCode(string? problemCode) =>
        string.Equals(problemCode, DurableProblemCodes.RecoveryEpochRequired, StringComparison.Ordinal)
        || string.Equals(problemCode, DurableProblemCodes.SchemaMissing, StringComparison.Ordinal)
        || string.Equals(problemCode, DurableProblemCodes.SchemaUpgradeRequired, StringComparison.Ordinal)
        || string.Equals(problemCode, DurableProblemCodes.SchemaVersionUnsupported, StringComparison.Ordinal)
        || string.Equals(problemCode, DurableProblemCodes.SchemaInconsistent, StringComparison.Ordinal);
}

/// <summary>
/// Executes one bounded pass of the authoritative durable runtime.
/// </summary>
/// <remarks>
/// Hosted loops and external activators must call this same primitive. A notification, queue, or HTTP wake-up may
/// accelerate a pass but cannot become a correctness dependency.
/// </remarks>
public interface IDurableRuntimePump
{
    /// <summary>
    /// Executes one bounded processing pass.
    /// </summary>
    ValueTask<DurableRuntimePumpResult> RunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Attempts authoritative admission and, when admitted, executes one bounded durable runtime pump pass.
/// </summary>
/// <remarks>
/// The <c>Try</c> contract applies only to expected pre-execution admission outcomes. Caller cancellation,
/// application-execution failures, terminal-bookkeeping failures, malformed provider state, and unclassified
/// exceptions propagate. A caller may make a new policy-controlled attempt after a returned pre-execution outcome,
/// but a returned outcome does not prove the status of an earlier invocation whose response was lost.
/// </remarks>
public interface IDurableRuntimePumpAdmission
{
    /// <summary>Attempts authoritative admission and executes one bounded pass when admitted.</summary>
    /// <param name="request">Bounded pump-pass limits and selected durable surfaces.</param>
    /// <param name="cancellationToken">Caller cancellation for admission, execution, and finalization.</param>
    /// <returns>
    /// A closed attempt that distinguishes completion, refusal, provider unavailability, and incompatibility.
    /// </returns>
    /// <exception cref="OperationCanceledException">Thrown when the caller cancels the attempt.</exception>
    /// <exception cref="Exception">
    /// Propagates application-execution, finalization, malformed-provider-state, and unclassified failures unchanged.
    /// </exception>
    ValueTask<DurableRuntimePumpAttempt> TryRunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default);
}
