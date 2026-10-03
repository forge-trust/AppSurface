using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.AspNetCore.Authorization;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation;

/// <summary>Maps the four sample-owned liveness, Durable probe, and activation routes.</summary>
internal static class ActivationHttpEndpoints
{
    /// <summary>Gets the stable named authorization policy applied before private request handling.</summary>
    internal const string AuthorizationPolicy = "DurableActivation";

    /// <summary>Maps the sample routes, requiring authentication in addition to the host's named activation policy.</summary>
    /// <param name="app">Built web application.</param>
    /// <param name="settings">Validated host-owned pump and request budgets.</param>
    internal static void Map(WebApplication app, ActivationHostSettings settings)
    {
        app.MapGet("/live", static () => Results.Json(new LiveResponse("Live")));
        app.MapGet("/compatibility", (Func<HttpContext, Task<IResult>>)(context => ProbeAsync(context, readiness: false)));
        app.MapGet("/ready", (Func<HttpContext, Task<IResult>>)(context => ProbeAsync(context, readiness: true)));
        app.MapPost("/private/durable/activate", (Func<HttpContext, Task<IResult>>)(context => ActivateAsync(context, settings)))
            .RequireAuthorization(AuthorizationPolicy)
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser());
    }

    /// <summary>Reads and validates current health evidence, projecting only the safe probe response contract.</summary>
    /// <param name="context">Request context.</param>
    /// <param name="readiness">Selects readiness status mapping when true.</param>
    /// <remarks>
    /// Kept internal so request-abort mapping can be tested deterministically through the same handler without using
    /// reflection; the public probe routes invoke this method directly.
    /// </remarks>
    internal static async Task<IResult> ProbeAsync(HttpContext context, bool readiness)
    {
        try
        {
            var health = await context.RequestServices.GetRequiredService<IDurableRuntimeHealth>()
                .GetAsync(context.RequestAborted).ConfigureAwait(false);
            ValidateAssessment(health);
            var response = new ProbeResponse(
                "Assessment",
                health.State.ToString(),
                health.CanEnableActivation,
                health.IsReady,
                health.ProblemCode);
            var success = readiness ? health.IsReady : health.CanEnableActivation;
            return Results.Json(response, statusCode: success ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(
                new ProbeResponse("ProbeCanceled", null, null, null, null),
                statusCode: StatusCodes.Status408RequestTimeout);
        }
        catch (Exception exception) when (IsNonfatal(exception))
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("DurableExternalActivation.Probe")
                .LogWarning("Durable probe failed with outcome {Outcome} and problem code {ProblemCode}.", "ProbeFailed", DurableProblemCodes.ExternalActivationFailed);
            return Results.Json(
                new ProbeResponse("ProbeFailed", null, null, null, DurableProblemCodes.ExternalActivationFailed),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Enforces the payload-free wake contract before resolving the activation service.</summary>
    /// <param name="context">Authorized request context.</param>
    /// <param name="settings">Validated sample pump and request budgets.</param>
    /// <remarks>
    /// Kept internal so request-body cancellation can be exercised deterministically through the same handler without
    /// reflection; the authorized activation route invokes this method directly.
    /// </remarks>
    internal static async Task<IResult> ActivateAsync(HttpContext context, ActivationHostSettings settings)
    {
        try
        {
            if (context.Request.ContentLength is > 0)
            {
                return Results.Json(new ErrorResponse("WakeBodyMustBeEmpty"), statusCode: StatusCodes.Status400BadRequest);
            }

            if (context.Request.ContentLength is null)
            {
                var firstByte = new byte[1];
                var read = await context.Request.Body.ReadAsync(firstByte.AsMemory(), context.RequestAborted).ConfigureAwait(false);
                if (read != 0)
                {
                    return Results.Json(new ErrorResponse("WakeBodyMustBeEmpty"), statusCode: StatusCodes.Status400BadRequest);
                }
            }

            var service = context.RequestServices.GetRequiredService<IDurableExternalActivationService>();
            var request = new DurableExternalActivationRequest(
                new DurableRuntimePumpRequest(
                    settings.PumpMaximumItems,
                    settings.PumpDiscoveryBudget,
                    DurableRuntimeSurface.Work),
                settings.RequestBudget);
            var result = await service.ActivateAsync(request, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(ActivationResponse.From(result), statusCode: GetStatusCode(result));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new ErrorResponse("HostFailure"), statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (Exception exception) when (IsNonfatal(exception))
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("DurableExternalActivation.Host")
                .LogWarning("The activation host failed outside a returned service outcome with {FailureCode}.", DurableProblemCodes.ExternalActivationFailed);
            return Results.Json(new ErrorResponse("HostFailure"), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>Rejects a null, undefined, or contradictory provider health observation before it reaches the wire.</summary>
    /// <param name="health">Provider health snapshot to validate.</param>
    private static void ValidateAssessment(DurableRuntimeHealthSnapshot health)
    {
        ArgumentNullException.ThrowIfNull(health);
        var valid = health.State == DurableRuntimeHealthState.Unavailable
            ? health.ProblemCode == DurableProblemCodes.StoreUnavailable
                && !health.SchemaCompatible && !health.EpochCompatible
                && !health.CanEnableActivation && !health.IsReady
            : !health.CanEnableActivation
                ? IsCompatibilityCode(health.ProblemCode) && !health.IsReady
                : health.State switch
                {
                    DurableRuntimeHealthState.NotStarted => !health.IsDraining
                        && (health.ProblemCode is null or DurableProblemCodes.ActivatorStale)
                        && !health.IsReady,
                    DurableRuntimeHealthState.Healthy => !health.IsDraining
                        && health.ProblemCode is null && health.IsReady,
                    DurableRuntimeHealthState.Draining => health.IsDraining
                        && health.ProblemCode is null && !health.IsReady,
                    DurableRuntimeHealthState.Stale =>
                        (health.ProblemCode is null or DurableProblemCodes.ActivatorStale or DurableProblemCodes.WorkerIdentityConflict)
                        && !health.IsReady,
                    _ => false,
                };
        if (!valid)
        {
            throw new InvalidDataException("The provider health assessment is inconsistent with its safe classification.");
        }
    }

    /// <summary>Recognizes only bounded canonical schema and runtime-epoch compatibility diagnostics.</summary>
    /// <param name="code">Observed provider code.</param>
    private static bool IsCompatibilityCode(string? code) => code is
        DurableProblemCodes.RecoveryEpochRequired
        or DurableProblemCodes.SchemaMissing
        or DurableProblemCodes.SchemaUpgradeRequired
        or DurableProblemCodes.SchemaVersionUnsupported
        or DurableProblemCodes.SchemaInconsistent;

    /// <summary>Maps each closed service outcome to its approved HTTP status.</summary>
    /// <param name="result">Validated service result.</param>
    private static int GetStatusCode(DurableExternalActivationResult result) => result.Kind switch
    {
        DurableExternalActivationOutcomeKind.Unavailable or DurableExternalActivationOutcomeKind.Incompatible => StatusCodes.Status503ServiceUnavailable,
        DurableExternalActivationOutcomeKind.Draining => StatusCodes.Status503ServiceUnavailable,
        DurableExternalActivationOutcomeKind.Busy => StatusCodes.Status409Conflict,
        DurableExternalActivationOutcomeKind.CanceledBeforeAdmission or DurableExternalActivationOutcomeKind.PumpCanceled => StatusCodes.Status408RequestTimeout,
        DurableExternalActivationOutcomeKind.RequestBudgetExceeded => StatusCodes.Status504GatewayTimeout,
        DurableExternalActivationOutcomeKind.Completed => StatusCodes.Status200OK,
        DurableExternalActivationOutcomeKind.ActivationFailed => StatusCodes.Status500InternalServerError,
        DurableExternalActivationOutcomeKind.PumpFailed when result.ProblemCode == DurableProblemCodes.StoreUnavailable
            || IsCompatibilityCode(result.ProblemCode) => StatusCodes.Status503ServiceUnavailable,
        DurableExternalActivationOutcomeKind.PumpFailed => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>Excludes fatal process failures from safe sample-host error mapping.</summary>
    /// <param name="exception">Caught exception.</param>
    private static bool IsNonfatal(Exception exception) => exception is not
        StackOverflowException and not OutOfMemoryException and not AccessViolationException;
}

/// <summary>Contains validated low-cardinality host settings captured before the listener starts.</summary>
/// <param name="PumpMaximumItems">Maximum Work items to claim in one activation pass.</param>
/// <param name="PumpDiscoveryBudget">Maximum discovery duration for one activation pass.</param>
/// <param name="RequestBudget">Independent cooperative budget for the full activation service invocation.</param>
internal sealed record ActivationHostSettings(
    int PumpMaximumItems,
    TimeSpan PumpDiscoveryBudget,
    TimeSpan RequestBudget);

/// <summary>Serializes the exact sample-owned probe response fields.</summary>
/// <param name="Outcome">Assessment, ProbeFailed, or ProbeCanceled.</param>
/// <param name="ObservedHealthState">Defined state name, or null when no assessment was obtained.</param>
/// <param name="CanEnableActivation">Current compatibility/activation verdict, or null when unavailable.</param>
/// <param name="IsReady">Current Durable readiness verdict, or null when unavailable.</param>
/// <param name="ProblemCode">Approved bounded diagnostic code, or null.</param>
internal sealed record ProbeResponse(
    string Outcome,
    string? ObservedHealthState,
    bool? CanEnableActivation,
    bool? IsReady,
    string? ProblemCode);

/// <summary>Serializes the process liveness response.</summary>
/// <param name="Status">The fixed Live marker.</param>
internal sealed record LiveResponse(string Status);

/// <summary>Serializes a fixed sample error response without exception detail.</summary>
/// <param name="Error">A bounded sample-owned error label.</param>
internal sealed record ErrorResponse(string Error);
