using System.Globalization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AppSurfaceDurableWorker.Hosting;

/// <summary>Maps the fixed liveness, compatibility, readiness, and authorized payload-free activation routes.</summary>
internal static class ActivationEndpoints
{
    /// <summary>Maps the sample-owned routes to their application-owned closed projections.</summary>
    /// <param name="app">Built but not yet listening application.</param>
    /// <param name="settings">Validated low-cardinality activation and body-read limits.</param>
    internal static void Map(WebApplication app, ActivationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);

        app.MapGet("/live", static () => Results.Json(new LiveResponse("Live")));
        app.MapGet("/compatibility", (Func<HttpContext, Task<IResult>>)(
            context => ProbeAsync(context, readiness: false)));
        app.MapGet("/ready", (Func<HttpContext, Task<IResult>>)(
            context => ProbeAsync(context, readiness: true)));
        app.MapPost("/private/durable/activate", (Func<HttpContext, Task<IResult>>)(
            context => ActivateAsync(context, settings)))
            .RequireAuthorization(WorkerApplication.ActivationAuthorizationPolicy)
            .RequireAuthorization(static policy => policy.RequireAuthenticatedUser());
    }

    /// <summary>Returns an independent liveness marker without resolving or calling a Durable service.</summary>
    /// <param name="context">HTTP request context.</param>
    /// <param name="readiness">Selects the stricter Healthy-only verdict when true.</param>
    /// <returns>A bounded assessment projection and its route-specific status.</returns>
    internal static async Task<IResult> ProbeAsync(HttpContext context, bool readiness)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var health = await context.RequestServices.GetRequiredService<IDurableRuntimeHealth>()
                .GetAsync(context.RequestAborted)
                .ConfigureAwait(false);
            ValidateAssessment(health);
            var response = new ProbeResponse(
                "Assessment",
                health.State.ToString(),
                health.CanEnableActivation,
                health.IsReady,
                health.ProblemCode);
            var allowed = readiness ? health.IsReady : health.CanEnableActivation;
            return Results.Json(
                response,
                statusCode: allowed ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(
                new ProbeResponse("ProbeCanceled", null, null, null, null),
                statusCode: StatusCodes.Status408RequestTimeout);
        }
        catch (Exception exception) when (IsNonfatal(exception))
        {
            LogSafeFailure(context, "ProbeFailed");
            return Results.Json(
                new ProbeResponse("ProbeFailed", null, null, null, DurableProblemCodes.ExternalActivationFailed),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Enforces authentication-first, empty-body activation and projects all ten published service outcomes.</summary>
    /// <param name="context">Authorized HTTP request context.</param>
    /// <param name="settings">Validated pump, service, and body-read budgets.</param>
    /// <returns>The exact safe activation DTO or a bounded host-owned error.</returns>
    internal static async Task<IResult> ActivateAsync(HttpContext context, ActivationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            if (context.Request.ContentLength is > 0)
            {
                return Results.Json(
                    new ErrorResponse("WakeBodyMustBeEmpty"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (context.Request.ContentLength is null)
            {
                var firstByte = new byte[1];
                using var bodyBudget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                bodyBudget.CancelAfter(settings.BodyReadBudget);
                int bytesRead;
                try
                {
                    bytesRead = await context.Request.Body.ReadAsync(firstByte.AsMemory(), bodyBudget.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    return Results.Json(
                        new ErrorResponse("HostFailure"),
                        statusCode: StatusCodes.Status500InternalServerError);
                }
                catch (OperationCanceledException) when (bodyBudget.IsCancellationRequested)
                {
                    return Results.Json(
                        new ErrorResponse("WakeBodyReadTimeout"),
                        statusCode: StatusCodes.Status504GatewayTimeout);
                }

                if (bytesRead != 0)
                {
                    return Results.Json(
                        new ErrorResponse("WakeBodyMustBeEmpty"),
                        statusCode: StatusCodes.Status400BadRequest);
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
            return Results.Json(
                new ErrorResponse("HostFailure"),
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (Exception exception) when (IsNonfatal(exception))
        {
            LogSafeFailure(context, "HostFailure");
            return Results.Json(
                new ErrorResponse("HostFailure"),
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>Validates the provider snapshot before returning its bounded fields to a caller.</summary>
    /// <param name="health">Provider-owned health snapshot.</param>
    internal static void ValidateAssessment(DurableRuntimeHealthSnapshot health)
    {
        ArgumentNullException.ThrowIfNull(health);
        var valid = health.State == DurableRuntimeHealthState.Unavailable
            ? health.ProblemCode == DurableProblemCodes.StoreUnavailable
                && !health.SchemaCompatible
                && !health.EpochCompatible
                && !health.CanEnableActivation
                && !health.IsReady
            : !health.CanEnableActivation
                ? IsCompatibilityCode(health.ProblemCode) && !health.IsReady
                : health.State switch
                {
                    DurableRuntimeHealthState.NotStarted => !health.IsDraining
                        && (health.ProblemCode is null or DurableProblemCodes.ActivatorStale)
                        && !health.IsReady,
                    DurableRuntimeHealthState.Healthy => !health.IsDraining
                        && health.ProblemCode is null
                        && health.IsReady,
                    DurableRuntimeHealthState.Draining => health.IsDraining
                        && health.ProblemCode is null
                        && !health.IsReady,
                    DurableRuntimeHealthState.Stale =>
                        (health.ProblemCode is null
                            or DurableProblemCodes.ActivatorStale
                            or DurableProblemCodes.WorkerIdentityConflict)
                        && !health.IsReady,
                    _ => false,
                };
        if (!valid)
        {
            throw new InvalidDataException("The provider health assessment is inconsistent with its safe classification.");
        }
    }

    /// <summary>Maps the closed ten-outcome activation contract to stable HTTP statuses.</summary>
    /// <param name="result">Validated service outcome.</param>
    /// <returns>The published sample's status code.</returns>
    internal static int GetStatusCode(DurableExternalActivationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Kind switch
        {
            DurableExternalActivationOutcomeKind.Unavailable
                or DurableExternalActivationOutcomeKind.Incompatible
                or DurableExternalActivationOutcomeKind.Draining => StatusCodes.Status503ServiceUnavailable,
            DurableExternalActivationOutcomeKind.Busy => StatusCodes.Status409Conflict,
            DurableExternalActivationOutcomeKind.CanceledBeforeAdmission
                or DurableExternalActivationOutcomeKind.PumpCanceled => StatusCodes.Status408RequestTimeout,
            DurableExternalActivationOutcomeKind.RequestBudgetExceeded => StatusCodes.Status504GatewayTimeout,
            DurableExternalActivationOutcomeKind.Completed => StatusCodes.Status200OK,
            DurableExternalActivationOutcomeKind.ActivationFailed => StatusCodes.Status500InternalServerError,
            DurableExternalActivationOutcomeKind.PumpFailed
                when result.ProblemCode == DurableProblemCodes.StoreUnavailable
                    || IsCompatibilityCode(result.ProblemCode) => StatusCodes.Status503ServiceUnavailable,
            DurableExternalActivationOutcomeKind.PumpFailed => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status500InternalServerError,
        };
    }

    /// <summary>Recognizes only the canonical bounded schema and active-epoch compatibility codes.</summary>
    /// <param name="problemCode">Observed problem code.</param>
    /// <returns>True only for one of the allowed schema compatibility codes.</returns>
    private static bool IsCompatibilityCode(string? problemCode) => problemCode is
        DurableProblemCodes.RecoveryEpochRequired
        or DurableProblemCodes.SchemaMissing
        or DurableProblemCodes.SchemaUpgradeRequired
        or DurableProblemCodes.SchemaVersionUnsupported
        or DurableProblemCodes.SchemaInconsistent;

    /// <summary>Logs a stable host-owned failure category without request, payload, credential, or exception data.</summary>
    /// <param name="context">Request context used to resolve a logger.</param>
    /// <param name="failureCode">Fixed low-cardinality category.</param>
    private static void LogSafeFailure(HttpContext context, string failureCode) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("AppSurfaceDurableWorker.Activation")
            .LogWarning("The generated Durable HTTP host returned {FailureCode}.", failureCode);

    /// <summary>Excludes fatal process errors from fixed safe HTTP projections.</summary>
    /// <param name="exception">Observed exception.</param>
    /// <returns>False only for process-fatal exceptions.</returns>
    private static bool IsNonfatal(Exception exception) => exception is not
        StackOverflowException and not OutOfMemoryException and not AccessViolationException;
}

/// <summary>Represents the fixed process liveness marker.</summary>
/// <param name="Status">Always Live while the route is served.</param>
internal sealed record LiveResponse(string Status);

/// <summary>Represents one safe health assessment or fixed probe failure.</summary>
/// <param name="Outcome">Assessment, ProbeFailed, or ProbeCanceled.</param>
/// <param name="ObservedHealthState">Defined provider health state, or null without an assessment.</param>
/// <param name="CanEnableActivation">Provider compatibility verdict, or null without an assessment.</param>
/// <param name="IsReady">Canonical readiness verdict, or null without an assessment.</param>
/// <param name="ProblemCode">Approved bounded diagnostic code, or null.</param>
internal sealed record ProbeResponse(
    string Outcome,
    string? ObservedHealthState,
    bool? CanEnableActivation,
    bool? IsReady,
    string? ProblemCode);

/// <summary>Represents the exact safe activation response projection.</summary>
/// <param name="Outcome">One of the ten canonical activation outcome names.</param>
/// <param name="ObservedHealthState">Defined state observed by the activation service, or null.</param>
/// <param name="ProblemCode">Validated bounded code, or null.</param>
/// <param name="PumpResult">Lossless bounded aggregate projection for Completed, or null.</param>
internal sealed record ActivationResponse(
    string Outcome,
    string? ObservedHealthState,
    string? ProblemCode,
    PumpResultResponse? PumpResult)
{
    /// <summary>Projects only the fields accepted by the activation contract.</summary>
    /// <param name="result">Closed public activation result.</param>
    /// <returns>Safe wire DTO retaining all aggregate counts and elapsed ticks.</returns>
    internal static ActivationResponse From(DurableExternalActivationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var pump = result.PumpResult;
        return new ActivationResponse(
            result.Kind.ToString(),
            result.ObservedHealthState?.ToString(),
            result.ProblemCode,
            pump is null
                ? null
                : new PumpResultResponse(
                    pump.Discovered,
                    pump.Claimed,
                    pump.Processed,
                    pump.Deferred,
                    pump.Failed,
                    pump.HasMore,
                    pump.NextDueAtUtc?.ToUniversalTime().ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                        CultureInfo.InvariantCulture),
                    pump.Elapsed.Ticks));
    }
}

/// <summary>Contains the complete safe pump aggregate returned by one completed activation invocation.</summary>
/// <param name="Discovered">Candidate count.</param>
/// <param name="Claimed">Claim count.</param>
/// <param name="Processed">Successfully processed count.</param>
/// <param name="Deferred">Policy-deferred count.</param>
/// <param name="Failed">Failed or suspended count.</param>
/// <param name="HasMore">Whether eligible work may remain.</param>
/// <param name="NextDueAtUtc">Invariant seven-digit UTC timestamp or null.</param>
/// <param name="ElapsedTicks">Elapsed duration in 100-nanosecond ticks.</param>
internal sealed record PumpResultResponse(
    int Discovered,
    int Claimed,
    int Processed,
    int Deferred,
    int Failed,
    bool HasMore,
    string? NextDueAtUtc,
    long ElapsedTicks);

/// <summary>Represents a fixed host-owned failure label without provider or exception detail.</summary>
/// <param name="Error">WakeBodyMustBeEmpty, WakeBodyReadTimeout, or HostFailure.</param>
internal sealed record ErrorResponse(string Error);
