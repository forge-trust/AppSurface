// docs:snippet devauth-persona-activation-handler:start
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

namespace AuthAspNetCoreDevAuthExample;

/// <summary>Composes shared scenario ensure and role readiness within the current selection request.</summary>
/// <remarks>
/// Registered scoped with a singleton store. Admin and viewer selections succeed without creating candidate data.
/// Logs contain fixed outcomes, a request trace identifier and only recognized configured persona IDs. They never
/// include cookies, claims or exception payloads. A real host can await persistence between ensure and readiness
/// with a cooperative request token and a host-owned deadline; preparation must finish before normal return.
/// </remarks>
internal sealed class LocalFixtureActivation(
    LocalCandidateFixtureStore store,
    ILogger<LocalFixtureActivation> logger) : IAppSurfaceDevAuthPersonaSelectionHandler
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(
        AppSurfaceDevAuthPersona persona,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        return RunAsync(persona, httpContext, cancellationToken, logger, token =>
        {
            // docs:snippet devauth-persona-activation-prepare:start
            if (persona.Id is "labeler" or "reviewer")
            {
                store.Ensure();
                token.ThrowIfCancellationRequested();
                store.MarkReady(persona.Id);
            }
            // docs:snippet devauth-persona-activation-prepare:end

            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Awaits one host preparation step with safe correlation logs and cooperative cancellation.</summary>
    /// <param name="persona">Validated configured persona, normalized to a recognized ID for logging.</param>
    /// <param name="httpContext">Current request used only for trace correlation.</param>
    /// <param name="cancellationToken">Captured selection request token.</param>
    /// <param name="logger">The scoped handler's logger; no exception or request payload is logged.</param>
    /// <param name="prepare">Host composition that finishes readiness or throws. Never writes the response.</param>
    /// <remarks>
    /// The intentionally internal composition boundary lets HTTP tests substitute deterministic partial failure or
    /// cancellation while exercising the same logging policy. There is no runtime fault flag, extra service or retry.
    /// </remarks>
    internal static async ValueTask RunAsync(
        AppSurfaceDevAuthPersona persona,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        ILogger<LocalFixtureActivation> logger,
        Func<CancellationToken, ValueTask> prepare)
    {
        var safeId = persona.Id is "labeler" or "reviewer" or "admin" or "viewer" ? persona.Id : "other";
        LogOutcome(logger, httpContext.TraceIdentifier, safeId, "start");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await prepare(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            LogOutcome(logger, httpContext.TraceIdentifier, safeId, "success");
        }
        catch (OperationCanceledException)
        {
            LogOutcome(logger, httpContext.TraceIdentifier, safeId, "cancel");
            throw;
        }
        catch (Exception)
        {
            LogOutcome(logger, httpContext.TraceIdentifier, safeId, "failure");
            throw;
        }
    }

    /// <summary>Writes the sample's safe structured correlation fields without serializing request or exception data.</summary>
    internal static void LogOutcome(ILogger<LocalFixtureActivation> logger, string traceId, string safePersonaId, string outcome)
    {
        logger.LogInformation("Local fixture activation TraceId={TraceId} PersonaId={PersonaId} Outcome={Outcome}",
            traceId, safePersonaId, outcome);
    }
}

/// <summary>Signals a host-owned fixture preparation failure eligible for the sample's explicit retry policy.</summary>
/// <remarks>Only this exception before response start is converted to the sample's fixed safe failure response.</remarks>
internal sealed class LocalFixtureActivationException : Exception;

/// <summary>Preserves the queued persona cookie while presenting the sample's narrow activation recovery policy.</summary>
/// <remarks>
/// Install after any outer general error handler and before authentication, authorization and endpoints.
/// Unknown failures, cancellation and failures after response start propagate to the outer host pipeline.
/// This sample does not guarantee cookie delivery under other hosts' exception middleware.
/// </remarks>
internal sealed class LocalFixtureActivationFailureMiddleware(RequestDelegate next)
{
    /// <summary>Fixed safe copy shown without exception details, response clearing or successful navigation.</summary>
    internal const string FailureMessage = "Persona selection did not finish. Fixtures are not ready. Open /_appsurface/dev-auth/ and select the persona again.";

    /// <summary>Runs the next middleware and handles only a typed activation failure before response start.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (LocalFixtureActivationException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(FailureMessage, context.RequestAborted);
        }
    }
}
// docs:snippet devauth-persona-activation-handler:end
