namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>Bounds one provider-neutral external wake independently of pump discovery limits.</summary>
/// <remarks>
/// A request budget signals cooperative cancellation; provider execution and terminal bookkeeping may finish later.
/// Hosts own authorization, empty wake bodies, transport deadlines, and response policy. See
/// <c>Durable/external-activation-v1.md</c> for the complete phase and recovery contract.
/// </remarks>
public sealed record DurableExternalActivationRequest
{
    /// <summary>Initializes an explicitly budgeted activation request.</summary>
    /// <param name="pumpRequest">Non-null provider pump limits, preserved by reference.</param>
    /// <param name="requestBudget">
    /// Positive cooperative budget including setup and health, at most <c>uint.MaxValue - 1</c> milliseconds.
    /// No default is supplied; this need not equal the pump discovery budget.
    /// </param>
    /// <exception cref="ArgumentNullException">The pump request is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The budget exceeds the .NET timer range or is not positive.</exception>
    public DurableExternalActivationRequest(DurableRuntimePumpRequest pumpRequest, TimeSpan requestBudget)
    {
        ArgumentNullException.ThrowIfNull(pumpRequest);
        if (requestBudget <= TimeSpan.Zero || requestBudget > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(requestBudget));
        }

        PumpRequest = pumpRequest;
        RequestBudget = requestBudget;
    }

    /// <summary>Gets the exact host-configured discovery limits and surface selection.</summary>
    public DurableRuntimePumpRequest PumpRequest { get; }

    /// <summary>Gets the cooperative monotonic budget from activation entry, including health and setup.</summary>
    public TimeSpan RequestBudget { get; }
}

/// <summary>Identifies the closed outcome of one externally requested activation.</summary>
public enum DurableExternalActivationOutcomeKind
{
    /// <summary>The health read could not observe the store; no pump was invoked.</summary>
    Unavailable = 0,
    /// <summary>Observed compatibility prevents activation; no pump was invoked.</summary>
    Incompatible = 1,
    /// <summary>The compatible worker is draining; no pump was invoked.</summary>
    Draining = 2,
    /// <summary>Authoritative admission refused the pass before application execution.</summary>
    Busy = 3,
    /// <summary>Caller cancellation won before the admission interface was invoked.</summary>
    CanceledBeforeAdmission = 4,
    /// <summary>The service budget expired before the admission interface was invoked.</summary>
    RequestBudgetExceeded = 5,
    /// <summary>The pass and terminal provider bookkeeping completed, including any failed items.</summary>
    Completed = 6,
    /// <summary>An unexpected nonfatal setup or health failure prevented pump invocation.</summary>
    ActivationFailed = 7,
    /// <summary>Cancellation was observed after invoking admission; inspect persisted effects before retrying.</summary>
    PumpCanceled = 8,
    /// <summary>Admission, execution, or terminal bookkeeping failed; inspect persisted effects before retrying.</summary>
    PumpFailed = 9,
}

/// <summary>Contains only validated activation facts and the original completed aggregate.</summary>
/// <remarks>
/// A health observation is advisory. This result does not identify Work processed by a generic wake or establish
/// external-effect certainty. Codes use exact ordinal spelling; stale codes are retained only for observed Stale.
/// </remarks>
public sealed record DurableExternalActivationResult
{
    /// <summary>Initializes a closed, internally consistent result.</summary>
    /// <param name="kind">One of the ten defined outcomes.</param>
    /// <param name="observedHealthState">Defined state after a successful health read; otherwise null.</param>
    /// <param name="problemCode">Canonical code permitted for this outcome and observed state, or null.</param>
    /// <param name="pumpResult">Exact non-null aggregate for Completed only; all other outcomes require null.</param>
    /// <exception cref="ArgumentOutOfRangeException">An outcome or observed state is undefined.</exception>
    /// <exception cref="ArgumentException">The state, code, or aggregate contradicts the closed outcome matrix.</exception>
    public DurableExternalActivationResult(
        DurableExternalActivationOutcomeKind kind,
        DurableRuntimeHealthState? observedHealthState,
        string? problemCode,
        DurableRuntimePumpResult? pumpResult)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (observedHealthState is { } state && !Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(observedHealthState));
        }

        if ((kind == DurableExternalActivationOutcomeKind.Completed) != (pumpResult is not null))
        {
            throw new ArgumentException("Only Completed requires and permits a pump result.", nameof(pumpResult));
        }

        var eligible = observedHealthState is DurableRuntimeHealthState.NotStarted
            or DurableRuntimeHealthState.Healthy or DurableRuntimeHealthState.Stale;
        var observationCode = problemCode is null
            || (observedHealthState == DurableRuntimeHealthState.Stale && ExternalActivationValidation.IsStaleCode(problemCode));
        var valid = kind switch
        {
            DurableExternalActivationOutcomeKind.Unavailable => observedHealthState == DurableRuntimeHealthState.Unavailable
                && problemCode == DurableProblemCodes.StoreUnavailable,
            DurableExternalActivationOutcomeKind.Incompatible => observedHealthState is not null
                && observedHealthState != DurableRuntimeHealthState.Unavailable
                && ExternalActivationValidation.IsCompatibilityCode(problemCode),
            DurableExternalActivationOutcomeKind.Draining => observedHealthState == DurableRuntimeHealthState.Draining
                && problemCode is null,
            DurableExternalActivationOutcomeKind.CanceledBeforeAdmission or DurableExternalActivationOutcomeKind.RequestBudgetExceeded
                => observationCode,
            DurableExternalActivationOutcomeKind.Busy or DurableExternalActivationOutcomeKind.Completed
                or DurableExternalActivationOutcomeKind.PumpCanceled => eligible && observationCode,
            DurableExternalActivationOutcomeKind.ActivationFailed => problemCode == DurableProblemCodes.ExternalActivationFailed,
            DurableExternalActivationOutcomeKind.PumpFailed => eligible && (problemCode == DurableProblemCodes.StoreUnavailable
                || ExternalActivationValidation.IsCompatibilityCode(problemCode)
                || problemCode == DurableProblemCodes.ExternalActivationFailed),
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("The observed state and problem code contradict the activation outcome.", nameof(problemCode));
        }

        Kind = kind;
        ObservedHealthState = observedHealthState;
        ProblemCode = problemCode;
        PumpResult = pumpResult;
    }

    /// <summary>Gets the closed operational outcome.</summary>
    public DurableExternalActivationOutcomeKind Kind { get; }

    /// <summary>Gets the state observed by this invocation before attempting admission, or null.</summary>
    public DurableRuntimeHealthState? ObservedHealthState { get; }

    /// <summary>Gets the bounded final diagnostic code, or null when no diagnostic applies.</summary>
    public string? ProblemCode { get; }

    /// <summary>Gets the unchanged aggregate for Completed, including failed item counts; otherwise null.</summary>
    public DurableRuntimePumpResult? PumpResult { get; }
}

/// <summary>Observes health and attempts exactly one provider-admitted pass for an eligible wake.</summary>
/// <remarks>
/// Implementations must support concurrent calls. There is no queue, retry, legacy fallback, or cached admission gate.
/// Before invocation caller cancellation wins over budget expiration. After invocation an authoritative returned attempt
/// wins over late cancellation; cancellation exceptions use phase and source signals, not exception-token identity.
/// Hosts retain route, authentication, response, deployment, and application preparation/cleanup ownership.
/// </remarks>
public interface IDurableExternalActivationService
{
    /// <summary>Runs one cooperative activation lifecycle without detaching unfinished provider work.</summary>
    /// <param name="request">Explicit service deadline and provider discovery limits.</param>
    /// <param name="callerCancellation">Caller or transport cancellation, separate from the request budget.</param>
    /// <returns>A safe validated result; Completed preserves the provider aggregate by reference.</returns>
    /// <exception cref="ArgumentNullException">The request is null, before instrumentation starts.</exception>
    /// <exception cref="OutOfMemoryException">Fatal process failures propagate instead of becoming operational results.</exception>
    /// <exception cref="AccessViolationException">Fatal process failures propagate instead of becoming operational results.</exception>
    /// <exception cref="StackOverflowException">Fatal process failures propagate instead of becoming operational results.</exception>
    ValueTask<DurableExternalActivationResult> ActivateAsync(
        DurableExternalActivationRequest request,
        CancellationToken callerCancellation = default);
}

/// <summary>Centralizes canonical evidence checks without creating another provider admission protocol.</summary>
internal static class ExternalActivationValidation
{
    /// <summary>Recognizes only the closed provider compatibility diagnostics using ordinal spelling.</summary>
    internal static bool IsCompatibilityCode(string? code) => code is DurableProblemCodes.RecoveryEpochRequired
        or DurableProblemCodes.SchemaMissing or DurableProblemCodes.SchemaUpgradeRequired
        or DurableProblemCodes.SchemaVersionUnsupported or DurableProblemCodes.SchemaInconsistent;

    /// <summary>Recognizes the two advisory stale diagnostics; these are not item execution failures.</summary>
    internal static bool IsStaleCode(string? code) => code is DurableProblemCodes.ActivatorStale or DurableProblemCodes.WorkerIdentityConflict;

    /// <summary>Excludes fatal process exceptions from safe operational and observation failure handling.</summary>
    internal static bool IsNonfatal(Exception exception) => exception is not StackOverflowException
        and not OutOfMemoryException and not AccessViolationException;

    /// <summary>
    /// Rejects snapshot codes that cannot safely form a closed result. NotStarted/404 is the provider's valid
    /// first-heartbeat assessment; it is not retained as a stale activation-result diagnostic.
    /// </summary>
    internal static void ValidateHealth(DurableRuntimeHealthSnapshot health)
    {
        ArgumentNullException.ThrowIfNull(health);
        var valid = health.State == DurableRuntimeHealthState.Unavailable
            ? health.ProblemCode == DurableProblemCodes.StoreUnavailable && !health.SchemaCompatible && !health.EpochCompatible
            : !health.CanEnableActivation
                ? IsCompatibilityCode(health.ProblemCode)
                : health.State switch
                {
                    DurableRuntimeHealthState.NotStarted => !health.IsDraining
                        && health.ProblemCode is null or DurableProblemCodes.ActivatorStale,
                    DurableRuntimeHealthState.Healthy => !health.IsDraining && health.ProblemCode is null,
                    DurableRuntimeHealthState.Draining => health.IsDraining && health.ProblemCode is null,
                    DurableRuntimeHealthState.Stale => health.ProblemCode is null || IsStaleCode(health.ProblemCode),
                    _ => false,
                };
        if (!valid)
        {
            throw new InvalidDataException("The health observation cannot form a safe activation result.");
        }
    }
}
