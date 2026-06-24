# ForgeTrust.AppSurface.Auth.AspNetCore

`ForgeTrust.AppSurface.Auth.AspNetCore` maps host-owned ASP.NET Core authentication and authorization into AppSurface auth contracts.

Use this package when an ASP.NET Core app already owns authentication and authorization, but AppSurface modules need a shared request auth context or a named policy result expressed as `AppSurfaceAuthResult`.

## Release Guidance

AppSurface publishes coordinated `v0.1.0` release candidates. Before installing this package from a prerelease feed, read the [v0.1.0 RC 4 release note](../../releases/v0.1.0-rc.4.md) for current release risk, migration guidance, and package readiness.

## Quickstart: Protect A Minimal API Endpoint

Install the package:

```bash
dotnet package add ForgeTrust.AppSurface.Auth.AspNetCore
```

Keep normal ASP.NET Core auth setup in the host:

```csharp
builder.Services
    .AddAuthentication("Bearer")
    .AddJwtBearer();

builder.Services.AddAuthorization(options =>
{
    options.AddAppSurfacePolicy("OperatorsOnly", policy =>
        policy.RequireAuthenticatedUser()
            .RequireClaim("role", "operator"));
});
```

Add the AppSurface adapter:

```csharp
using ForgeTrust.AppSurface.Auth.AspNetCore;

builder.Services.AddAppSurfaceAspNetCoreAuth(options =>
{
    options.MapSubjectClaim("sub");
});
```

Run the host auth middleware in the normal ASP.NET Core order:

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

Require the host policy on a Minimal API endpoint:

```csharp
app.MapPost("/operator-work", HandleOperatorWork)
   .RequireSurfacePolicy("OperatorsOnly");
```

`RequireSurfacePolicy(...)` evaluates the host policy through `IAppSurfaceAspNetCorePolicyEvaluator`. Allowed requests continue to the endpoint handler. Non-allowed results, including challenge, forbid, setup-failure, unsafe-navigation, and stale-or-unknown-session outcomes, return `application/problem+json` with safe AppSurface outcome, reason, policy, and diagnostic metadata instead of calling browser challenge or forbid handlers.

The helper marks the endpoint as anonymous for ASP.NET Core authorization middleware so host fallback policies do not challenge or redirect before the AppSurface endpoint filter runs. The endpoint is not public: the filter still evaluates the named host policy and blocks failed requests.

Consume a named host policy directly when the endpoint needs a custom resource or custom response:

```csharp
app.MapGet(
    "/operator-check",
    async (IAppSurfaceAspNetCorePolicyEvaluator evaluator) =>
    {
        var result = await evaluator.AuthorizeAsync("OperatorsOnly");

        return result.Outcome switch
        {
            AppSurfaceAuthOutcome.Allowed =>
                Results.Ok(new { subject = result.Context?.User?.Id }),
            AppSurfaceAuthOutcome.Challenge =>
                Results.Unauthorized(),
            _ =>
                Results.Json(new { result.Outcome, result.Reason }, statusCode: 403),
        };
    });
```

For the five-minute browser proof, run the [Auth Web/RazorWire proof](../../examples/auth-web-razorwire-proof/README.md). It shows one host-owned ASP.NET Core policy driving both a Minimal API response and a RazorWire-facing state. For integration tests that need deterministic personas without a sample-local fake handler, use [ForgeTrust.AppSurface.Auth.Testing](../ForgeTrust.AppSurface.Auth.Testing/README.md). For lower-level setup diagnostics with fake authentication, see [the ASP.NET Core auth bridge example](../../examples/auth-aspnetcore-bridge/README.md).

## What The Package Includes

- `AppSurfaceAspNetCoreAuthModule`
- `AddAppSurfaceAspNetCoreAuth(...)`
- `AppSurfaceAspNetCoreAuthOptions`
- `AddAppSurfacePolicy(...)`
- `RequireSurfacePolicy(...)`
- `AppSurfacePolicyEndpointMetadata`
- `IAppSurfaceAspNetCoreAuthContextAccessor`
- `IAppSurfaceAspNetCorePolicyEvaluator`
- safe adapter diagnostic metadata keys
- ASP.NET Core request principal to `AppSurfaceAuthContext` mapping
- ASP.NET Core named policy to `AppSurfaceAuthResult` mapping
- Minimal API endpoint filters that render AppSurface auth failures as ProblemDetails JSON

## What The Package Does Not Include

- Authentication schemes or handlers
- Cookies, JWT/OIDC, OAuth, or ASP.NET Identity configuration
- Authorization policy definitions
- Middleware insertion
- ASP.NET Core authorization middleware replacement
- Browser challenge, forbid, redirect, sign-in, or sign-out execution
- Audit sinks, logs, metrics, traces, or persistence
- RazorWire UI or auth components

## Host Auth Versus AppSurface Auth

Use ASP.NET Core directly when you need to authenticate a request, configure schemes, create policies, issue challenges, forbid callers, validate tokens, redirect users, or integrate an identity provider.

Use this adapter when AppSurface-aware code needs to read the already-authenticated request context or ask for a host-owned named policy result without taking a direct dependency on a local `ClaimsPrincipal` mapping.

Use native `RequireAuthorization(...)` for browser flows, cookie redirects, MVC/controller authorization, or endpoints where ASP.NET Core middleware should own the challenge/forbid response. Use `RequireSurfacePolicy(...)` for Minimal API endpoints that should keep ASP.NET Core policies as permission truth but return machine-safe AppSurface ProblemDetails responses for API callers.

## Subject Mapping

AppSurface requires authenticated users to have a stable host-owned subject id. By default, the adapter checks authenticated identities in this order:

1. `ClaimTypes.NameIdentifier`
2. `sub`
3. `appsurface.subject_id`

Use `MapSubjectClaim(...)` to make another claim type first priority:

```csharp
builder.Services.AddAppSurfaceAspNetCoreAuth(options =>
{
    options.MapSubjectClaim("tenant_user_id");
});
```

Only authenticated identities are inspected. Claims on unauthenticated identities are ignored. If a principal is authenticated but no configured subject claim is present, policy evaluation returns `AppSurfaceAuthResult.MissingSubject(...)` with safe diagnostics instead of pretending the caller is anonymous.

Mapped claims are context, not permission truth. Host-owned ASP.NET Core policies remain authoritative for authorization.

The mapped `AppSurfaceUser.Id` is the selected host-owned subject claim. Do not use it as a durable app-owned user id for domain records, preferences, billing, or audit ownership. For that boundary, use `ExternalSubject`, `AppUserId`, and an app-implemented `IAppSurfaceUserIdentityResolver` from `ForgeTrust.AppSurface.Auth`; keep the resolver in the app so persistence and provisioning policy stay app-owned.

ASP.NET Core integration plan: a later adapter can build an `ExternalSubject` from the configured subject claim plus a host-validated issuer and optional partition, then call the app resolver asynchronously. That integration should remain outside Auth core and should not force database or provisioning work into the current synchronous request-context accessor.

## Policy Results

`IAppSurfaceAspNetCorePolicyEvaluator.AuthorizeAsync(...)` observes cancellation before and during policy lookup. ASP.NET Core policy evaluation itself does not expose a cancellation-token overload, so handler execution is not cancellable through this API.

When `AuthorizeAsync(...)` is called without an explicit resource, the adapter passes the current `HttpContext` as the ASP.NET Core authorization resource. Pass a resource explicitly when the host policy expects an endpoint, route model, document, command, or other domain object.

| Situation | Result |
| --- | --- |
| Policy succeeds | `Allowed` |
| Policy challenges | `Challenge` / `Unauthenticated` |
| Policy forbids | `Forbid` / `Forbidden` |
| Policy name is null, empty, or whitespace | `ArgumentException` |
| Nonblank policy name is not registered | `MissingPolicy` |
| ASP.NET Core authorization services are missing | `MissingServices` |
| ASP.NET Core policy services cannot be constructed because framework services are missing | `MissingServices` |
| A policy names an authentication scheme but authentication services or handlers are missing | `MissingServices` |
| Authenticated principal has no configured subject claim | `MissingSubject` |
| Policy provider, authentication handler, or authorization handler throws after setup is present | Exception propagates |

## Endpoint Policy Helpers

`AddAppSurfacePolicy(...)` is a naming helper over normal ASP.NET Core `AuthorizationOptions.AddPolicy(...)`. It validates the policy name and then delegates to ASP.NET Core; it does not introduce AppSurface permissions, roles, tenants, or a custom policy DSL.

`RequireSurfacePolicy(...)` attaches `AppSurfacePolicyEndpointMetadata`, ASP.NET Core allow-anonymous middleware metadata, and an endpoint filter to a Minimal API endpoint or route group. The allow-anonymous metadata keeps host fallback authorization policies from issuing browser-shaped responses before the filter runs; the filter still resolves `IAppSurfaceAspNetCorePolicyEvaluator` from request services and evaluates the policy name supplied by the endpoint metadata.

Failure responses use `application/problem+json`:

| AppSurface outcome | HTTP status | Title |
| --- | ---: | --- |
| `Challenge` | 401 | `Authentication required` |
| `Forbid` | 403 | `Authorization failed` |
| `SetupFailure` | 500 | `AppSurface auth setup failure` |
| `UnsafeNavigation` | 400 | `Unsafe auth navigation` |
| `StaleOrUnknownSession` | 401 | `Stale or unknown auth session` |

ProblemDetails extensions include `appsurfaceAuthOutcome`, `appsurfaceAuthReason`, `appsurfacePolicyName`, and safe adapter metadata such as `appsurface.aspnetcore.diagnostic_code`, `appsurface.aspnetcore.policy_name`, `appsurface.aspnetcore.missing_service`, and `appsurface.aspnetcore.subject_claim_types` when present.

Do not chain `RequireSurfacePolicy(...)` and native `RequireAuthorization(...)` on the same API endpoint. `RequireSurfacePolicy(...)` intentionally opts the endpoint out of ASP.NET Core authorization middleware so AppSurface can own the API response shape; native authorization metadata belongs on endpoints where middleware should own challenge, forbid, redirect, or MVC/controller behavior.

## Diagnostics

Setup failures include safe metadata such as:

- `appsurface.aspnetcore.diagnostic_code`
- `appsurface.aspnetcore.policy_name`
- `appsurface.aspnetcore.missing_service`
- `appsurface.aspnetcore.subject_claim_types`

The adapter does not copy raw claims, tokens, emails, display names, or identity-provider payloads into result metadata.

## Pitfalls

- Register ASP.NET Core authentication and authorization in the host. `AddAppSurfaceAspNetCoreAuth(...)` does not call `AddAuthentication(...)`, create schemes, or create policies.
- Use the accessor or evaluator after `UseAuthentication()` has populated `HttpContext.User`.
- Pass the same authorization resource your host policy expects. The adapter uses `HttpContext` when no resource is supplied.
- Use `RequireSurfacePolicy(...)` for Minimal API response semantics. Use native `RequireAuthorization(...)` when ASP.NET Core should challenge, forbid, redirect, or apply MVC/controller authorization behavior.
- Do not treat mapped metadata as permission truth. Ask ASP.NET Core policies for permission decisions.
- Do not treat a mapped subject claim as a durable app-owned user id. Resolve it through an app-owned identity resolver before writing domain records keyed by app user.
- A missing subject claim is a setup problem for authenticated users. Configure `MapSubjectClaim(...)` or issue a stable subject claim from the host auth system.
