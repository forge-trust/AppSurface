using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>Owns one call's clock, cancellation, phase, and observation resources without owning admission.</summary>
/// <remarks>
/// Setup is explicit so partial acquisition can be cleaned after a setup fault. The monotonic entry clock never
/// resets. Callback delivery is advisory: synchronous elapsed checks prevent overdue admission even with a delayed
/// timer. Only the sequential invocation path changes phase; callbacks merely signal cancellation.
/// </remarks>
/// <param name="clock">Monotonic clock used to enforce the original service-entry deadline.</param>
/// <param name="budget">Explicit cooperative request budget.</param>
/// <param name="caller">Independent transport or caller cancellation.</param>
/// <param name="started">Timestamp captured before allocation, or null only when that capture failed.</param>
internal sealed class DurableExternalActivationInvocation(TimeProvider clock, TimeSpan budget, CancellationToken caller, long? started)
{
    /// <summary>Canonical operation emitted through the process-shared source, without SDK dependencies.</summary>
    internal const string OperationName = "appsurface.durable.runtime.activation";

    private const string TagPrefix = "appsurface.durable.activation.";
    private CancellationTokenSource? _budgetSource;
    private CancellationTokenSource? _linkedSource;
    private ITimer? _timer;
    private Activity? _activity;

    /// <summary>Gets the sequential observation boundary; invocation does not certify admission or effects.</summary>
    internal ActivationPhase Phase { get; private set; }

    /// <summary>Gets defined health after a successful read, even if later validation fails.</summary>
    internal DurableRuntimeHealthState? ObservedHealthState { get; set; }

    /// <summary>Gets the observation's allowed stale code, retained only for a Stale state.</summary>
    internal string? ObservationCode { get; set; }

    /// <summary>Gets linked caller and budget cancellation after setup.</summary>
    internal CancellationToken Token => _linkedSource!.Token;

    /// <summary>Gets the independent caller signal; it wins during pre-invocation classification.</summary>
    internal bool CallerCanceled => caller.IsCancellationRequested;

    /// <summary>Gets whether the original monotonic deadline elapsed or the timer signaled it.</summary>
    internal bool BudgetCanceled => _budgetSource?.IsCancellationRequested == true
        || (started is { } timestamp && clock.GetElapsedTime(timestamp) >= budget);

    /// <summary>Enables observation and arms only the budget remaining from service-entry timestamp capture.</summary>
    internal void Start()
    {
        _budgetSource = new CancellationTokenSource();
        _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(caller, _budgetSource.Token);
        _activity = AppSurfaceActivitySources.Instance.StartActivity(OperationName, ActivityKind.Internal);
        var remaining = budget - clock.GetElapsedTime(started!.Value);
        if (remaining <= TimeSpan.Zero)
        {
            SignalBudget();
        }
        else
        {
            _timer = clock.CreateTimer(static state => ((DurableExternalActivationInvocation)state!).SignalBudget(),
                this, remaining, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Signals budget cancellation without letting nonfatal consumer callbacks fault a timer thread.</summary>
    private void SignalBudget()
    {
        try
        {
            _budgetSource!.Cancel();
        }
        catch (Exception exception) when (ExternalActivationValidation.IsNonfatal(exception))
        {
            // The signal remains set. A callback failure never manufactures another operational outcome.
        }
    }

    /// <summary>Classifies cancellation just before an operational pre-admission return.</summary>
    internal DurableExternalActivationResult? PreAdmissionCancellation() => CallerCanceled
        ? Result(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, ObservationCode)
        : BudgetCanceled
            ? Result(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, ObservationCode)
            : null;

    /// <summary>Marks the synchronous invocation boundary immediately before the sole admission call.</summary>
    internal void MarkPumpInvoked() => Phase = ActivationPhase.PumpInvoked;

    /// <summary>Marks only an authoritative Completed return, after terminal provider bookkeeping.</summary>
    internal void MarkCompleted() => Phase = ActivationPhase.Completed;

    /// <summary>Constructs every exit through the same public closed validation matrix.</summary>
    internal DurableExternalActivationResult Result(
        DurableExternalActivationOutcomeKind kind,
        string? code,
        DurableRuntimePumpResult? pumpResult = null) => new(kind, ObservedHealthState, code, pumpResult);

    /// <summary>
    /// Observes the selected result or last fatal boundary, then independently attempts disposal of all owned resources.
    /// Nonfatal observation and cleanup faults cannot replace a selected result or primary exception. The shared source
    /// is never disposed, exceptions are never recorded, and this path performs no asynchronous export or retry.
    /// </summary>
    internal void Finish(DurableExternalActivationResult? result, ILogger logger, Exception? primaryException = null)
    {
        Exception? fatalObservation = null;
        Attempt(() =>
        {
            _activity?.SetTag(TagPrefix + "contract_version", 1);
            _activity?.SetTag(TagPrefix + "phase", PhaseName);
            if (ObservedHealthState is { } state)
            {
                _activity?.SetTag(TagPrefix + "health_state", state.ToString());
            }

            if (result is not null)
            {
                _activity?.SetTag(TagPrefix + "outcome", result.Kind.ToString());
                if (result.ProblemCode is not null)
                {
                    _activity?.SetTag(TagPrefix + "problem_code", result.ProblemCode);
                }

                if (result.Kind is DurableExternalActivationOutcomeKind.ActivationFailed or DurableExternalActivationOutcomeKind.PumpFailed)
                {
                    _activity?.SetStatus(ActivityStatusCode.Error);
                }
            }
        });
        if (result is not null)
        {
            Attempt(() => logger.LogInformation(
                "Durable activation phase {Phase} outcome {Outcome} observed state {HealthState} code {ProblemCode}.",
                PhaseName, result.Kind.ToString(), result.ObservedHealthState?.ToString(), result.ProblemCode));
        }

        Attempt(() => _activity?.Dispose());
        Attempt(() => _timer?.Dispose());
        Attempt(() => _linkedSource?.Dispose());
        Attempt(() => _budgetSource?.Dispose());
        if (primaryException is null && fatalObservation is not null)
        {
            ExceptionDispatchInfo.Capture(fatalObservation).Throw();
        }

        // Preserve a primary exception and still attempt every independently owned cleanup, even if a later sink
        // throws a fatal exception. With no primary exception, fatal observation faults propagate after cleanup.
        void Attempt(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                if (!ExternalActivationValidation.IsNonfatal(exception))
                {
                    fatalObservation ??= exception;
                }
            }
        }
    }

    /// <summary>Gets the exact low-cardinality telemetry spelling for the last sequential boundary.</summary>
    private string PhaseName => Phase switch
    {
        ActivationPhase.PreAdmission => "pre_admission",
        ActivationPhase.PumpInvoked => "pump_invoked",
        _ => "completed",
    };

}

/// <summary>Defines sequential observation boundaries, not persisted execution or provider admission state.</summary>
internal enum ActivationPhase
{
    /// <summary>The admission interface has not been invoked.</summary>
    PreAdmission,
    /// <summary>The admission interface was invoked; admission/execution truth belongs to its result.</summary>
    PumpInvoked,
    /// <summary>The provider returned Completed after its bounded terminal bookkeeping.</summary>
    Completed,
}
