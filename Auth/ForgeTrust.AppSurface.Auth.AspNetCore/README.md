# ForgeTrust.AppSurface.Auth.AspNetCore

`ForgeTrust.AppSurface.Auth.AspNetCore` maps host-owned ASP.NET Core authentication and authorization into AppSurface auth contracts.

Use this package when an ASP.NET Core app already owns authentication and authorization, but AppSurface modules need a shared request auth context or a named policy result expressed as `AppSurfaceAuthResult`.

## Release Guidance

AppSurface publishes coordinated `v0.1.0` release candidates. Before installing this package from a prerelease feed, read the [v0.1.0 RC 4 release note](../../releases/v0.1.0-rc.4.md) for current release risk, migration guidance, and package readiness.

## Quickstart: Evaluate A Host Policy

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
    options.AddPolicy("OperatorsOnly", policy =>
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

Consume a named host policy as an AppSurface result:

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

For a local proof with fake authentication, see [the ASP.NET Core auth bridge example](../../examples/auth-aspnetcore-bridge/README.md).

## What The Package Includes

- `AppSurfaceAspNetCoreAuthModule`
- `AddAppSurfaceAspNetCoreAuth(...)`
- `AppSurfaceAspNetCoreAuthOptions`
- `IAppSurfaceAspNetCoreAuthContextAccessor`
- `IAppSurfaceAspNetCorePolicyEvaluator`
- safe adapter diagnostic metadata keys
- ASP.NET Core request principal to `AppSurfaceAuthContext` mapping
- ASP.NET Core named policy to `AppSurfaceAuthResult` mapping

## What The Package Does Not Include

- Authentication schemes or handlers
- Cookies, JWT/OIDC, OAuth, or ASP.NET Identity configuration
- Authorization policy definitions
- Middleware insertion
- Endpoint filters or Minimal API helpers
- Challenge, forbid, redirect, sign-in, or sign-out execution
- Audit sinks, logs, metrics, traces, or persistence
- RazorWire UI or auth components

## Host Auth Versus AppSurface Auth

Use ASP.NET Core directly when you need to authenticate a request, configure schemes, create policies, issue challenges, forbid callers, validate tokens, redirect users, or integrate an identity provider.

Use this adapter when AppSurface-aware code needs to read the already-authenticated request context or ask for a host-owned named policy result without taking a direct dependency on a local `ClaimsPrincipal` mapping.

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
- Do not treat mapped metadata as permission truth. Ask ASP.NET Core policies for permission decisions.
- A missing subject claim is a setup problem for authenticated users. Configure `MapSubjectClaim(...)` or issue a stable subject claim from the host auth system.
