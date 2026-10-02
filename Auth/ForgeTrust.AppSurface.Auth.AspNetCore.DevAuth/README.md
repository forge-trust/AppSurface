# ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth

`ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` adds fake, local-only ASP.NET Core authentication for AppSurface package consumers who need to try auth-aware endpoints without configuring OIDC, cookies, ASP.NET Identity, or an external identity provider.

DevAuth is development tooling. It is not production authentication, a user store, durable app-user mapping, OIDC, token validation, tenant authority, audit logging, or the Auth.Testing integration-test harness.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

Use the [AppSurface Auth adoption ladder](../../start-here/auth-adoption-ladder.md) when choosing between DevAuth, Auth.Testing, OIDC, and host-owned ASP.NET Core authentication.

## Quickstart

Install the package in an ASP.NET Core app:

```bash
dotnet package add ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth
```

Configure AppSurface auth mapping, seed local personas, require the DevAuth named scheme in policies, and map the control page:

```csharp
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

builder.Services.AddAuthorization(options =>
{
    options.AddAppSurfacePolicy(
        "OperatorsOnly",
        policy => policy
            .AddAuthenticationSchemes(AppSurfaceDevAuthDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim("role", "operator"));
});

builder.Services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim("sub"));
builder.Services.AddAppSurfaceDevAuth(builder.Environment, dev =>
{
    dev.Users.Add(
        "admin",
        user => user
            .DisplayName("Local Admin")
            .Subject("admin-1")
            .Claim("role", "operator"));
    dev.Users.Add(
        "viewer",
        user => user
            .DisplayName("Local Viewer")
            .Subject("viewer-1")
            .Claim("role", "viewer"));
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (
    HttpContext httpContext,
    IHostEnvironment environment,
    IOptions<AppSurfaceDevAuthOptions> devAuthOptions,
    IDataProtectionProvider dataProtectionProvider) => Results.Content(
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Local proof app</title>
        </head>
        <body>
          <header>Local proof app</header>
          {{AppSurfaceDevAuthMarker.Render(httpContext, environment, devAuthOptions, dataProtectionProvider)}}
          <main>Protected application content</main>
        </body>
        </html>
        """,
        "text/html"));

app.MapGet("/api/auth-proof", () => Results.Json(new { result = "allowed" }))
   .RequireSurfacePolicy("OperatorsOnly");

app.MapAppSurfaceDevAuth();
```

Run in Development:

```bash
DOTNET_ENVIRONMENT=Development dotnet run
```

DevAuth activates only in `Development` by default. For a host-owned proof environment, add the exact environment name explicitly:

```csharp
builder.Services.AddAppSurfaceDevAuth(builder.Environment, dev =>
{
    dev.AllowedEnvironmentNames.Add("Staging");
    // personas...
});
```

Only add local or proof environments that may safely expose fake personas. `AllowedEnvironmentNames` is a DevAuth activation allow-list, not a production security boundary.

<a id="persona-selection-activation"></a>

## Opt-In Persona Fixture Activation

Use [`IAppSurfaceDevAuthPersonaSelectionHandler`](#persona-selection-activation) when selecting a configured local persona must prepare host-owned fixtures before DevAuth returns its normal redirect or control-page response. This is an optional, request-scoped hook. Without a registration, DevAuth keeps the existing selection behavior and does not add activation work or cancellation checks.

The compiled source of truth is the complete [DevAuth example](../../examples/auth-aspnetcore-dev-auth/README.md#persona-fixture-activation). Its setup, handler, and fixture store below are source-extracted excerpts from one working project, not independent copy-and-run programs. The complete host is defined by [`Program.cs`](../../examples/auth-aspnetcore-dev-auth/Program.cs), [`LocalFixtureActivation.cs`](../../examples/auth-aspnetcore-dev-auth/LocalFixtureActivation.cs), [`LocalCandidateFixtureStore.cs`](../../examples/auth-aspnetcore-dev-auth/LocalCandidateFixtureStore.cs), and the [readiness and work routes](../../examples/auth-aspnetcore-dev-auth/LocalCandidatePages.cs), compiled by the [example project](../../examples/auth-aspnetcore-dev-auth/AuthAspNetCoreDevAuthExample.csproj). It retains the existing admin/viewer proof. Run the project to compile and start the full composition:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project examples/auth-aspnetcore-dev-auth -- --urls http://127.0.0.1:5058
```

### Register the optional handler

The sample uses normal dependency injection: one singleton process-local store and one scoped handler. The configured labeler and reviewer personas have distinct role policies and landing pages. The complete source also configures the package auth mapping and the existing admin/viewer personas.

<!-- appsurface:snippet id="devauth-persona-activation-registration" file="examples/auth-aspnetcore-dev-auth/Program.cs" marker="devauth-persona-activation-registration" lang="csharp" -->
```csharp
using System.Security.Claims;
using AuthAspNetCoreDevAuthExample;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Admit only local authorities before any identity or fixture mutation.
builder.Services.AddHostFiltering(options =>
{
    options.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];
    options.AllowEmptyHosts = false;
});

builder.Services.AddAuthorization(options =>
{
    options.AddAppSurfacePolicy(
        "OperatorsOnly",
        policy => policy
            .AddAuthenticationSchemes(AppSurfaceDevAuthDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim("role", "operator"));
    options.AddAppSurfacePolicy(
        "ViewersOnly",
        policy => policy
            .AddAuthenticationSchemes(AppSurfaceDevAuthDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim("role", "viewer"));
    options.AddAppSurfacePolicy("LabelersOnly", policy => policy
        .AddAuthenticationSchemes(AppSurfaceDevAuthDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser().RequireClaim("role", "labeler"));
    options.AddAppSurfacePolicy("ReviewersOnly", policy => policy
        .AddAuthenticationSchemes(AppSurfaceDevAuthDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser().RequireClaim("role", "reviewer"));
});

builder.Services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim("sub"));
builder.Services.AddAppSurfaceDevAuth(builder.Environment, dev =>
{
    dev.Users.Add(
        "admin",
        user => user
            .DisplayName("Local Admin")
            .Subject("admin-1")
            .Claim("role", "operator")
            .Claim("tenant", "local-demo")
            .LandingUrl("/"));
    dev.Users.Add(
        "viewer",
        user => user
            .DisplayName("Local Viewer")
            .Subject("viewer-1")
            .Claim("role", "viewer")
            .Claim("tenant", "local-demo")
            .LandingUrl("/viewer"));
    dev.Users.Add("labeler", user => user.DisplayName("Local Labeler").Subject("labeler-1")
        .Claim("role", "labeler").LandingUrl("/candidate/label"));
    dev.Users.Add("reviewer", user => user.DisplayName("Local Reviewer").Subject("reviewer-1")
        .Claim("role", "reviewer").LandingUrl("/candidate/review"));
});

builder.Services.AddSingleton<LocalCandidateFixtureStore>();
builder.Services.AddScoped<IAppSurfaceDevAuthPersonaSelectionHandler, LocalFixtureActivation>();

var app = builder.Build();

app.UseHostFiltering();
app.UseMiddleware<LocalFixtureActivationFailureMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
```
<!-- /appsurface:snippet -->

`AddScoped` is important because DevAuth resolves the optional, unkeyed service from the current selection request's `HttpContext.RequestServices`. Do not resolve it from the root provider, store it in singleton options, instantiate it at startup, or create another scope. DevAuth resolves one service, so a host with several preparation steps should compose them in this implementation rather than register an assumed ordered handler pipeline.

### Implement activation and readiness

The selected `persona` argument is the validated configured persona from the current POST. The incoming `HttpContext.User` and request cookies can still represent the previously selected persona; use `persona.Id` to choose the requested fixtures. The sample composes a stable shared candidate ensure with readiness for the selected role. Admin and viewer selections succeed as no-ops.

<!-- appsurface:snippet id="devauth-persona-activation-handler" file="examples/auth-aspnetcore-dev-auth/LocalFixtureActivation.cs" marker="devauth-persona-activation-handler" lang="csharp" -->
```csharp
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
```
<!-- /appsurface:snippet -->

The sample-specific preparation lambda ensures the shared candidate only for labeler and reviewer, checks cancellation after ensure, and then marks the selected role ready. Admin and viewer remain no-ops. This is an excerpt from the complete handler composition below, not a standalone C# expression: `RunAsync` owns the cancellation checks around `prepare`, the one awaited step, and safe lifecycle logging shown in the preceding source block.

<!-- appsurface:snippet id="devauth-persona-activation-prepare" file="examples/auth-aspnetcore-dev-auth/LocalFixtureActivation.cs" marker="devauth-persona-activation-prepare" lang="csharp" -->
```csharp
if (persona.Id is "labeler" or "reviewer")
{
    store.Ensure();
    token.ThrowIfCancellationRequested();
    store.MarkReady(persona.Id);
}
```
<!-- /appsurface:snippet -->

The sample store makes the candidate identity independent of the selected persona and returns immutable snapshots. Labeler readiness, reviewer readiness, labeling completion, and review completion are separate state. Its transitions are short synchronous operations under one lock; it does not hold a lock across asynchronous work.

<!-- appsurface:snippet id="devauth-persona-fixture-store" file="examples/auth-aspnetcore-dev-auth/LocalCandidateFixtureStore.cs" marker="devauth-persona-fixture-store" lang="csharp" -->
```csharp
namespace AuthAspNetCoreDevAuthExample;

/// <summary>
/// Holds one synthetic, process-local scenario independently of the selected persona.
/// </summary>
/// <remarks>
/// All transitions use a short synchronous lock. Activation and product actions only move readiness or completed
/// work forward; reads never initialize data. Restarting the host resets the scenario. This sample supplies neither
/// durable storage nor cross-process idempotence; a consuming host must choose those policies itself.
/// </remarks>
internal sealed class LocalCandidateFixtureStore
{
    /// <summary>The fixed fixture identity shared by both operator roles.</summary>
    internal const string ScenarioKey = "candidate-review-demo-v1";

    private readonly object _gate = new();
    private LocalCandidateSnapshot? _candidate;

    // missing --Ensure--> one candidate --MarkReady(role)--> role ready --TryComplete(role)--> completed
    // Each arrow and Read holds only _gate; no logging, rendering, callbacks or await occurs inside it.
    // Returning immutable records keeps earlier reads stable after later forward-only transitions.

    /// <summary>Returns an immutable coherent snapshot, or null without creating a candidate.</summary>
    internal LocalCandidateSnapshot? Read()
    {
        lock (_gate)
        {
            return _candidate;
        }
    }

    /// <summary>Atomically ensures the shared candidate while preserving all existing readiness and work.</summary>
    internal LocalCandidateSnapshot Ensure()
    {
        lock (_gate)
        {
            return _candidate ??= new LocalCandidateSnapshot("synthetic-candidate-001", false, false, false, false);
        }
    }

    /// <summary>Marks an already ensured candidate ready for one configured operator; never creates data.</summary>
    /// <param name="personaId">The validated configured labeler or reviewer ID.</param>
    /// <exception cref="InvalidOperationException">No candidate exists, or the operator is unknown.</exception>
    internal void MarkReady(string personaId)
    {
        lock (_gate)
        {
            var candidate = _candidate ?? throw new InvalidOperationException("Ensure the scenario before marking readiness.");
            _candidate = personaId switch
            {
                "labeler" => candidate with { LabelerReady = true },
                "reviewer" => candidate with { ReviewerReady = true },
                _ => throw new InvalidOperationException("Unknown scenario operator."),
            };
        }
    }

    /// <summary>Completes only the ready operator's work; repeated completion is idempotent.</summary>
    /// <param name="personaId">The configured operator ID chosen by the authorized host route.</param>
    /// <returns>False for missing data, an unknown role or a role that is not ready. No data is ensured.</returns>
    internal bool TryComplete(string personaId)
    {
        lock (_gate)
        {
            if (_candidate is not { } candidate || !candidate.IsReady(personaId))
            {
                return false;
            }

            _candidate = personaId == "labeler"
                ? candidate with { LabelingCompleted = true }
                : candidate with { ReviewCompleted = true };
            return true;
        }
    }
}

/// <summary>An immutable point-in-time view of one shared candidate and its independent role state.</summary>
/// <param name="Id">Stable synthetic candidate ID.</param>
/// <param name="LabelerReady">Whether labeler activation has finished.</param>
/// <param name="ReviewerReady">Whether reviewer activation has finished.</param>
/// <param name="LabelingCompleted">Whether labeling work has completed.</param>
/// <param name="ReviewCompleted">Whether review work has completed.</param>
internal sealed record LocalCandidateSnapshot(
    string Id,
    bool LabelerReady,
    bool ReviewerReady,
    bool LabelingCompleted,
    bool ReviewCompleted)
{
    /// <summary>Checks readiness for a configured role; unknown roles are never ready.</summary>
    internal bool IsReady(string personaId) => personaId switch
    {
        "labeler" => LabelerReady,
        "reviewer" => ReviewerReady,
        _ => false,
    };
}
```
<!-- /appsurface:snippet -->

In the complete sample, `GET /candidate/label` and `GET /candidate/review` only read state. They return HTTP 200 when the selected role is ready and HTTP 409 with an explicit POST reselection form when it is not. Those GETs never create or repair fixtures. Only successful explicit selection prepares readiness; role-protected completion POSTs change that role's work and return HTTP 303 to its page. Readiness means the handler finished successfully before normal navigation, not merely that a persona cookie or visible marker exists.

### Request, response, and failure ownership

For an admitted selection, DevAuth validates and looks up the configured persona, then queues its protected response cookie. It resolves the optional unkeyed handler from that request's service provider. A host with no handler follows the existing success path without the added `RequestAborted` checks. A handler factory or constructor exception occurs during resolution: it leaves the queued cookie header unmodified, invokes no handler, and occurs before DevAuth captures or checks `RequestAborted`. Only after resolution returns a handler does DevAuth perform these steps:

1. Capture `HttpContext.RequestAborted` and throw if it is already cancelled.
2. Call `ActivateAsync(persona, httpContext, cancellationToken)` once and await it.
3. Check the same token again after normal completion, before either successful response branch.

The await completes before both a safe local redirect and the control-page response. Guard failures and invalid persona IDs do not resolve the service. Control/status GETs, clearing the persona, authentication, and ordinary application requests do not activate fixtures. Selecting the same persona again is a new activation call; DevAuth does not cache success or promise exactly-once host writes.

```text
admission guards -> configured persona lookup -> queue protected Set-Cookie header
                                           -> resolve optional request-scoped handler
                                              ├─ absent: existing redirect/control response
                                              ├─ resolution throws: host error pipeline
                                              └─ present: capture RequestAborted
                                                          -> pre-invocation cancellation check
                                                          -> await ActivateAsync
                                                          -> post-await cancellation check
                                                          -> existing safe target resolution
                                                          -> redirect or control-page response
```

Handler exceptions and cancellation skip both success response branches. The endpoint has queued a `Set-Cookie` header before resolution, but that does not prove a browser received it. Host exception middleware and transport behavior determine whether it is delivered, replaced, or discarded. A host's exception policy must account for that boundary; the package makes no cookie-delivery guarantee on failure.

The handler must finish required preparation before it returns. It must not write or start the response, set navigation headers, or redirect; DevAuth owns successful navigation and the host pipeline owns failures. Compose multi-step work inside the one handler. If the host performs I/O, observe the supplied request token. A host may apply its own finite deadline by linking a deadline token with the supplied token for its downstream calls; the package has no activation timeout. A handler that ignores cancellation can continue holding the selection request open, and the post-await check can only prevent normal navigation after that handler returns.

Cookie delivery and fixture writes are not atomic. The protected cookie is queued before handler resolution and invocation. A constructor or service-factory failure, activation exception, cancellation, or response-delivery failure can therefore leave the browser with the new persona while fixtures are missing or partially prepared. DevAuth does not catch, retry, compensate, or roll back host work. These failures bypass normal success navigation and propagate to the host's exception/cancellation pipeline. Keep ensure operations idempotent and atomic in the persistence system that owns the fixtures, and have fixture-dependent pages check readiness themselves.

The sample demonstrates one host-owned error policy: only its typed `LocalFixtureActivationException` is rendered as a fixed HTTP 500 before the response starts; the middleware keeps the already queued `Set-Cookie`, adds no `Location`, does not call `Response.Clear`, and exposes no exception text. It is installed after any outer general error handler and before authentication, authorization, and endpoints. Arbitrary exceptions, cancellation, and typed failures after response start propagate. This policy is sample-specific. Other host middleware can replace or discard the cookie, so do not assume cookie delivery on errors outside that declared policy.

### Recovery and local diagnostics

| Failure | What to check | Safe recovery |
| --- | --- | --- |
| Handler resolution or construction fails | Check the scoped registration and constructor dependency graph, and correlate the host's error log using the request trace ID. The sample activation logger cannot emit a start record if DI fails before constructing or invoking the handler. | Fix the registration/factory failure, then explicitly select the persona again. Do not treat a cookie from the failed response as readiness. |
| Preparation partially fails | Check the host's own readiness state. In the sample, the fixed HTTP 500 can retain the persona cookie, and the next role-page GET returns HTTP 409 without repairing state. | POST the same persona from the explicit reselection form after making the ensure operation safe to repeat. Preserve existing work when retrying. |
| Request is cancelled | Use the request trace ID to find the sample's `Outcome=cancel` record when activation began, then check fixture readiness because cancellation may follow committed writes. | Let the request end; use the role page's read-only readiness result and explicitly reselect when preparation remains incomplete. Do not assume cancellation rolled back writes. |

The sample logs only `TraceId`, a configured safe persona ID, and the fixed `start`, `success`, `failure`, or `cancel` outcome. Search the local host logs by the request's `TraceId` and inspect the corresponding activation record. Resolution failures happen before the handler logs and must be correlated through the host's normal request/error logging. Never diagnose this path by dumping cookies, claims, response bodies, or raw exception payloads.

The sample's store is process-local and shared by one fixed scenario key, `candidate-review-demo-v1`; its synthetic ID and completed work survive persona switches and overlapping requests while the host process remains alive. Restarting the sample resets the candidate. The sample does not claim durable storage, cross-process uniqueness, or exactly-once effects. A real host chooses its persistence boundary, transaction/idempotence policy, concurrency control, deadlines, and compensation/retry rules.

Open the persona lab:

```text
/_appsurface/dev-auth
```

### Return To The Host Page

To return to a host page after selecting or clearing a persona, open the control page with a URI-encoded local target:

```text
/_appsurface/dev-auth/?returnUrl=%2Fprotected%3Ftab%3Dauth
```

DevAuth carries a safe rooted local path through every select and clear form action, then returns through the existing local redirect after the mutation. An explicit `/` is valid. Missing, blank, non-rooted, absolute, protocol-relative, backslash-containing, or control-character request values are omitted from the forms. Clearing then leaves the browser on the normal updated DevAuth control response; selecting instead uses the selected persona's configured `LandingUrl` when present and otherwise leaves the browser on that response. Rejected request targets do not produce a diagnostic because omission is the fail-closed fallback.

### Persona Landing URLs

When a fake persona should recover to a page it can use after selection, configure a safe local landing URL on that persona:

```csharp
dev.Users.Add(
    "viewer",
    user => user
        .DisplayName("Local Viewer")
        .Subject("viewer-1")
        .Claim("role", "viewer")
        .LandingUrl("/viewer"));
```

DevAuth resolves a successful persona selection in this order:

| Target | Use it when | Invalid-value behavior |
| --- | --- | --- |
| Explicit host `returnUrl` | The control-page request or marker explicitly names a safe target | Request values are omitted. |
| Selected persona `LandingUrl` | No explicit safe host target exists | Registration fails with `ASDEV007`; DevAuth never silently normalizes configured landing metadata to `/`. |
| Source fallback | Neither target exists | The control page renders its updated response; the marker returns to the current host path and query. |

`LandingUrl` is local-proof navigation metadata, not authorization policy. DevAuth does not verify that a persona can access the configured page. Keep the landing path rooted and local, and let the host own authorization. Clearing a persona never uses its prior landing URL.

The control page lets you select a seeded persona, clear the persona cookie, inspect safe local claims, and copy a visible marker such as `DEV AUTH: Local Admin (AppSurface.DevAuth)`. For a persistent in-app indicator, render `AppSurfaceDevAuthMarker` from your local layout or proof page. The renderer returns an empty string when the current environment is not in `AllowedEnvironmentNames`, so layouts do not need their own `environment.IsDevelopment()` guard. With default styles, the marker is a fixed bottom-right overlay above 640 CSS pixels and participates in normal document flow at widths up to and including 640 CSS pixels. It starts collapsed, keeps the active fake persona visible, and expands to POST-only persona controls that prefer the selected persona's landing URL over the marker's implicit current-page fallback.

The host must provide `<meta name="viewport" content="width=device-width, initial-scale=1">` so the 640 CSS-pixel breakpoint tracks the device width. Render the marker after persistent application chrome and before `<main>` (or the equivalent primary content container). At narrow widths, that host-owned location becomes the marker's in-flow position, so opening the disclosure pushes following content rather than covering it. The host also owns outer spacing and the containing layout.

## API Reference

- `AddAppSurfaceDevAuth(IHostEnvironment environment, Action<AppSurfaceDevAuthOptions> configure)` registers the named DevAuth authentication scheme and startup safety validation. The `configure` callback is evaluated once during registration, and the same validated options are used for both scheme registration and runtime DevAuth behavior.
- `MapAppSurfaceDevAuth(this IEndpointRouteBuilder endpoints)` maps the local-only control page, status JSON, select persona endpoint, and clear persona endpoint. The control-page GET accepts an optional safe rooted local `returnUrl`, carries it through every select and clear form action, and preserves it after successful mutation. Without an explicit safe target, selecting a persona uses its configured `LandingUrl` when present; otherwise selection renders the updated control response. Clear does not use persona landing metadata. Rejected request targets are omitted. Control and mutation endpoints return not found when the active environment is not allowed; status remains read-only and reports `enabled: false`. The control page root always includes a static-auditable DevAuth control-page marker attribute so static export audits can reject DevAuth UI before it is written to disk.
- `AppSurfaceDevAuthMarker.Render(HttpContext, IHostEnvironment, IOptions<AppSurfaceDevAuthOptions>, IDataProtectionProvider, Action<AppSurfaceDevAuthMarkerOptions>?)` returns safe HTML for an explicit in-app DevAuth state marker. It returns `string.Empty` when the active environment is not allowed. With default styles, the marker is fixed above 640 CSS pixels and in flow at or below 640 CSS pixels. The marker root always includes a static-auditable DevAuth marker attribute so static export audits can reject DevAuth UI even when the CSS class prefix is customized.
- `AppSurfaceDevAuthDefaults.AuthenticationScheme` is `AppSurface.DevAuth`.
- `AppSurfaceDevAuthDefaults.PathPrefix` is `/_appsurface/dev-auth`.
- `AppSurfaceDevAuthDefaults.CookieName` is `.AppSurface.DevAuth.Persona`.
- `AppSurfaceDevAuthDefaults.SubjectClaimType` is `sub`.
- `AppSurfaceDevAuthOptions.Users` contains seeded local personas.
- `IAppSurfaceDevAuthPersonaSelectionHandler.ActivateAsync(AppSurfaceDevAuthPersona persona, HttpContext httpContext, CancellationToken cancellationToken)` is the optional host callback for awaited fixture preparation after an admitted explicit persona selection. DevAuth resolves it from request services after queuing the protected cookie, invokes it at most once for that request, and checks `RequestAborted` before invocation and after normal completion. See [persona selection activation](#opt-in-persona-fixture-activation) for scope, response ownership, composition, failure, and recovery behavior.
- `AppSurfaceDevAuthUserBuilder.LandingUrl(string landingUrl)` configures a selected persona's optional safe local fallback after selection. It is validated during registration and throws `ASDEV007` for blank, non-rooted, absolute, protocol-relative, backslash-containing, or control-character values.
- `AppSurfaceDevAuthPersona.LandingUrl` exposes the immutable nullable configured landing URL. It is navigation metadata only; it is not a claim, cookie payload, status field, or authorization decision.
- `AppSurfaceDevAuthOptions.SchemeName` overrides the registered authentication scheme. It defaults to `AppSurfaceDevAuthDefaults.AuthenticationScheme`.
- `AppSurfaceDevAuthOptions.PathPrefix` overrides the local control-page and status endpoint path prefix. It defaults to `AppSurfaceDevAuthDefaults.PathPrefix`.
- `AppSurfaceDevAuthOptions.CookieName` overrides the selected-persona cookie name. It defaults to `AppSurfaceDevAuthDefaults.CookieName`.
- `AppSurfaceDevAuthOptions.AllowedEnvironmentNames` controls where DevAuth may activate. It defaults to `Development`; names are compared case-insensitively after trimming for comparison. The set must contain at least one non-blank value.
- `AppSurfaceDevAuthOptions.UseAsDefaultSchemeForLocalProof` is off by default. Enable it only for throwaway local proof hosts where DevAuth intentionally owns the whole auth stack.
- `AppSurfaceDevAuthOptions.AllowDevAuthOverrideForLocalProof` is off by default. Enable it only when a local proof intentionally composes DevAuth with other registered auth schemes.
- `AppSurfaceDevAuthOptions.RequireLoopbackControlRequests` is on by default.
- `AppSurfaceDevAuthOptions.DisplayClaimTypes` controls which issued claims may appear in the local HTML preview. It defaults to `sub`, `role`, and `tenant`.
- `AppSurfaceDevAuthMarkerOptions.CssClassPrefix` changes the CSS class prefix for marker elements. The default is the package-owned DevAuth marker prefix.
- `AppSurfaceDevAuthMarkerOptions.AdditionalCssClass` appends host-owned classes to the marker root.
- `AppSurfaceDevAuthMarkerOptions.IncludeDefaultStyles` is on by default. Disable it to skin and position the marker entirely with host CSS.
- `AppSurfaceDevAuthMarkerOptions.ShowPersonaControls` is on by default. Disable it when a page should show state but send persona changes through the full control page.
- `AppSurfaceDevAuthMarkerOptions.StartExpanded` is off by default to keep the fixed desktop overlay compact. Enable it when a proof page should show controls immediately; at narrow widths, the default-styled expanded marker remains in flow.
- `AppSurfaceDevAuthMarkerOptions.ReturnUrl` is an explicit host-owned target that overrides persona landing URLs. When unset, marker selection uses a configured persona landing URL first and otherwise returns to the current request path and query.

Persona IDs must be route-safe local identifiers containing only ASCII letters, digits, `.`, `_`, or `-`. The dot-segment IDs `.` and `..` are not allowed, and ids that look like tokens, secrets, passwords, keys, credentials, or emails are rejected. Persona IDs are used in the selection endpoint path and stored as the protected cookie payload.

Persona state is stored in a protected, HttpOnly, SameSite=Strict cookie that contains only the persona id. DevAuth adds the `Secure` cookie attribute on HTTPS requests and omits it on plain HTTP localhost so browser-based local proof works. Blank, unknown, stale, reset, or tampered cookie state authenticates as no result.

The authentication handler issues every seeded persona claim, but the control page does not display every issued claim. Claims are rendered only when their type is in `DisplayClaimTypes`, their value is short, and neither the type nor the value looks like a token, secret, password, key, credential, or email. Display names and subjects that look sensitive are redacted from HTML and status JSON. Hidden claims are counted without showing their values.

The marker renderer uses the same safe display rules as the control page and status JSON. It does not render arbitrary claims. Marker select and clear buttons call the same POST-only mutation endpoints as the control page and use only safe local return URLs, so external `returnUrl` values do not redirect.

## Responsive Placement And Customization

The package default deliberately changes placement rather than visibility: above 640 CSS pixels the marker remains the existing fixed bottom-right development overlay; at 640 CSS pixels or below it becomes an ordinary in-flow element. The host render location is therefore visually significant on narrow screens. The default non-obstruction guarantee applies when the marker is rendered in an ordinary document-flow container after persistent application chrome and before main content.

The host owns viewport metadata and outer spacing. Add the standard viewport tag to the document `<head>`, then use `AdditionalCssClass` for local spacing or a higher-specificity placement override:

```csharp
var marker = AppSurfaceDevAuthMarker.Render(
    httpContext,
    environment,
    devAuthOptions,
    dataProtectionProvider,
    options => options.AdditionalCssClass = "local-dev-auth");
```

```css
@media (max-width: 640px) {
  body > .local-dev-auth {
    margin: 12px 16px;
  }
}
```

Use `CssClassPrefix` when integrating the existing hierarchy with host CSS. Set `IncludeDefaultStyles = false` only when the host will provide the complete visual and responsive placement contract; no package CSS is emitted in that mode. Custom CSS can intentionally override package placement, but the host then owns overlap prevention.

For a complete host-owned skin, change the prefix and disable package styles together:

```csharp
var marker = AppSurfaceDevAuthMarker.Render(
    httpContext,
    environment,
    devAuthOptions,
    dataProtectionProvider,
    options =>
    {
        options.CssClassPrefix = "local-dev-auth";
        options.IncludeDefaultStyles = false;
    });
```

The host must then style the emitted `local-dev-auth` hierarchy and own both desktop and narrow-screen placement. This minimal starting point keeps the warning visible, preserves the desktop overlay, and reserves mobile layout space:

```css
.local-dev-auth {
  color: #111827;
  background: #fff;
  border: 2px solid #b91c1c;
}

@media (min-width: 641px) {
  .local-dev-auth {
    position: fixed;
    right: 16px;
    bottom: 16px;
    z-index: 2147483647;
    max-width: min(360px, calc(100vw - 32px));
  }
}

@media (max-width: 640px) {
  .local-dev-auth {
    position: static;
    max-width: none;
  }

  .local-dev-auth__actions form,
  .local-dev-auth__button {
    min-width: 0;
    max-width: 100%;
    overflow-wrap: anywhere;
  }
}
```

## Marker Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| The marker still behaves like a desktop overlay on a phone. | The host document omits the viewport meta tag, so the browser uses a wider layout viewport. | Add `<meta name="viewport" content="width=device-width, initial-scale=1">` to `<head>`. |
| The narrow marker covers or clips application content. | The marker is inside a fixed, absolutely positioned, clipped, or otherwise overlapping host container, or host CSS overrides the package placement. | Render it in ordinary flow after persistent application chrome and before main content; remove the conflicting container or own placement with custom CSS. |
| The marker is below the fold on a narrow screen. | Normal flow reserves space but does not keep the marker pinned to the viewport; its visibility depends on the host render location. | Render it after persistent application chrome and before main content. Use a host-owned fixed or sticky override only when persistent visibility is more important than package-guaranteed non-obstruction. |
| The in-flow marker touches the viewport or adjacent content. | AppSurface does not choose host-specific outer spacing. | Add a host class with `AdditionalCssClass` and apply narrow-screen margin in host CSS. |
| A custom-skinned marker does not switch placement at 640 pixels. | `IncludeDefaultStyles = false` removes all package CSS, including the responsive rule. | Add the host's own media query and overlap-prevention behavior, or re-enable package styles. |

## Contributor Test Loop

The fast loop skips browser integration tests:

```bash
dotnet test Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests.csproj --filter "Category!=Integration"
```

Run only the responsive browser contract while iterating on marker layout or keyboard behavior:

```bash
dotnet test Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests.csproj --filter "Category=Integration"
```

Run the complete focused project before landing:

```bash
dotnet test Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests.csproj
```

The integration fixture installs Playwright Chromium automatically. The first integration or full run may download the browser; later runs reuse the installed browser.

Persona mutation endpoints are loopback-only by default and reject cross-site browser POSTs when `Origin`, `Referer`, or Fetch Metadata identifies another origin. This keeps arbitrary websites from silently changing the fake persona in a developer's local browser. Command-line local tooling without browser origin headers can still post to the endpoints from loopback.

## DevAuth Versus Other Auth Packages

Use `ForgeTrust.AppSurface.Auth` when reusable modules need surface-neutral auth vocabulary, auth results, prompts, audit event descriptions, or durable external-subject to app-user-id mapping contracts.

Use `ForgeTrust.AppSurface.Auth.AspNetCore` when an ASP.NET Core host already owns authentication and authorization, but AppSurface modules need mapped request context or named host-policy results.

Use DevAuth only when you need fake local personas in Development, or in an explicitly opted-in local/proof environment, so a package consumer can try AppSurface auth-aware endpoints without an identity provider.

Use OIDC or native ASP.NET Core authentication packages for real sign-in, cookies, external identity providers, redirects, token validation, and production auth flows.

Use `ForgeTrust.AppSurface.Auth.Testing` for deterministic automated auth scenarios. DevAuth is for local developer interaction, not browser automation authority.

## What Not To Copy To Production

- Do not enable DevAuth outside configured allowed environments, and add only local/proof environment names that keep fake personas visible.
- Do not treat persona claims as production identity, tenant authority, or permission truth.
- Do not put tokens, passwords, secrets, raw emails, or production identity payloads into seeded personas.
- Do not add sensitive claim types to `DisplayClaimTypes`; the control page still refuses common secret/token/email shapes.
- Do not use the DevAuth persona cookie as production session management.
- Do not hide real auth scheme conflicts with `AllowDevAuthOverrideForLocalProof`.

## Diagnostics

DevAuth diagnostics use `Problem:`, `Cause:`, `Fix:`, and `Docs:` wording and the safe metadata key `appsurface.devauth.diagnostic_code`.

| Code | Meaning |
| --- | --- |
| `ASDEV001` | DevAuth was enabled in an environment that is not in `AllowedEnvironmentNames`. |
| `ASDEV002` | DevAuth detected an existing real authentication scheme or default. |
| `ASDEV003` | DevAuth was enabled without seeded personas. |
| `ASDEV004` | A selected persona did not contain the configured subject claim. |
| `ASDEV005` | The reserved path prefix was invalid or conflicted with the local control surface. |
| `ASDEV006` | A persona id was invalid, unknown, stale, duplicated, or tampered. |
| `ASDEV007` | A configured persona `LandingUrl` was blank or not a safe rooted local path. |

Diagnostics, HTML, and status JSON do not include raw tokens, secrets, passwords, raw emails, or unbounded identity-provider payloads.

## Pitfalls

- Call `UseAuthentication()` before endpoints that depend on selected personas.
- Call `UseAuthorization()` before AppSurface policy-protected endpoints when your host uses normal ASP.NET Core authorization middleware.
- Call `MapAppSurfaceDevAuth()` so the persona lab and status JSON exist.
- Register at most one scoped `IAppSurfaceDevAuthPersonaSelectionHandler` only when a valid explicit selection must prepare host-owned fixtures before normal navigation; keep activation idempotent, readiness independently observable, and response handling in the host pipeline.
- Add `AppSurfaceDevAuthDefaults.AuthenticationScheme` to policies that should evaluate DevAuth personas.
- Use simple route-safe persona IDs such as `admin`, `viewer`, or `qa.local_1`; dot segments, sensitive-looking ids, query strings, fragments, encoded slashes, spaces, and other punctuation are rejected with `ASDEV006`.
- Configure `LandingUrl(...)` only with a safe rooted local path. Unlike a request `returnUrl`, an unsafe configured landing URL stops registration with `ASDEV007`; DevAuth does not silently redirect it to `/`.
- Call `Subject(...)` for every persona and keep it aligned with `AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim(...))`.
- Keep the DevAuth marker visible in local sample pages so fake auth is impossible to miss.
- Prefer `AppSurfaceDevAuthMarker.Render(...)` over copying the generated control-page HTML. Use `StartExpanded = true` when the marker should show controls immediately, and use `IncludeDefaultStyles = false`, `CssClassPrefix`, and `AdditionalCssClass` when the marker needs to match a consumer app.
- Include the standard viewport meta tag and render the marker after persistent application chrome and before main content. The package cannot reserve safe space when a host puts the marker inside a fixed, absolute, clipped, or overlapping container.
- DevAuth does not automatically inject a marker into arbitrary responses. Add it explicitly to the pages or local layout where the fake-auth state should be visible; the renderer self-suppresses outside allowed environments.
- If persona selection returns a same-origin 403, make sure custom local UI posts from the same scheme, host, and port as the mapped DevAuth endpoints.
- If persona selection leaves you in the persona lab, verify that the initial control-page URL contained a URI-encoded, rooted local `returnUrl` or that the selected persona has a `LandingUrl`. When neither target is configured, the control-page response is expected. Inspect the rendered form action when debugging; rejected values are intentionally omitted rather than diagnosed or redirected to `/`.
- If a lower-privilege persona returns to an unusable page from the marker, leave `AppSurfaceDevAuthMarkerOptions.ReturnUrl` unset and configure that persona's `LandingUrl(...)`. Set the marker option only when the host deliberately owns the destination for every persona.

## Upgrade And Removal

`LandingUrl` is an additive opt-in feature. Existing DevAuth hosts that do not configure it keep their current control-page and marker fallback behavior. Hosts that add it should verify the landing page in local proof, but no cookie, endpoint, status-JSON, or production-auth migration is required.

Remove DevAuth before deploying a host:

1. Remove the `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` package reference.
2. Remove `AddAppSurfaceDevAuth(...)`.
3. Remove `MapAppSurfaceDevAuth()`.
4. Remove DevAuth scheme references from policies.
5. Configure real ASP.NET Core authentication and keep `ForgeTrust.AppSurface.Auth.AspNetCore` only for AppSurface result mapping.

For a working proof, see [the DevAuth example](../../examples/auth-aspnetcore-dev-auth/README.md).
