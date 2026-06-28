# ForgeTrust.AppSurface.Auth.Testing

`ForgeTrust.AppSurface.Auth.Testing` is a test-only ASP.NET Core authentication harness for proving AppSurface auth behavior.

Use this package in integration test projects when a consumer needs deterministic personas, real ASP.NET Core policy evaluation, AppSurface auth outcomes, and ProblemDetails assertions without writing a one-off fake authentication handler.

## Release Guidance

AppSurface publishes coordinated `v0.1.0` release candidates. Before installing this package from a prerelease feed, read the [v0.1.0 RC 4 release note](../../releases/v0.1.0-rc.4.md) for current release risk, migration guidance, and package readiness.

## Quickstart: WebApplicationFactory Personas

Install the package in a test project:

```bash
dotnet add package ForgeTrust.AppSurface.Auth.Testing --prerelease
```

Configure the factory with explicit personas:

```csharp
using System.Security.Claims;
using ForgeTrust.AppSurface.Auth.Testing;

await using var baseFactory = new WebApplicationFactory<Program>();
await using var factory = baseFactory.WithAppSurfaceTestAuth(options =>
{
    options.SubjectClaimType = "sub";
    options.AddPersona(
        "operator",
        "operator-1",
        [new Claim("role", "operator")]);
    options.AddPersona(
        "viewer",
        "viewer-1",
        [new Claim("role", "viewer")]);
});

using var operatorClient = factory.CreateAppSurfaceClient("operator");
using var response = await operatorClient.GetAsync("/operator-work");
```

No persona selection means the request is anonymous:

```csharp
using var anonymousClient = factory.CreateClient();
using var response = await anonymousClient.GetAsync("/operator-work");
```

For request-level switching, apply the helper to one message:

```csharp
using var request = new HttpRequestMessage(HttpMethod.Get, "/operator-work")
    .WithAppSurfaceTestPersona("viewer");
using var client = factory.CreateClient();
using var response = await client.SendAsync(request);
```

`CreateAppSurfaceClient(...)` validates the persona before sending a request. `WithAppSurfaceTestPersona(...)`
is intentionally transport-only, so an unknown request-level persona is detected by the server-side test handler and
reported as an AppSurface setup failure instead of being treated as anonymous.

## Service Registration API

Use the lower-level registration when the test host is not built with `WebApplicationFactory`:

```csharp
builder.Services.AddAppSurfaceTestAuth(options =>
{
    options.SchemeMode = AppSurfaceTestAuthSchemeMode.NamedScheme;
    options.SubjectClaimType = "sub";
    options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]);
});
```

`AddAppSurfaceTestAuth(...)` composes `ForgeTrust.AppSurface.Auth.AspNetCore`, creates an immutable persona registry, and registers a test authentication scheme when the selected `SchemeMode` enables it. Normal ASP.NET Core authorization policies still run. AppSurface still maps the result through `AppSurfaceAuthResult` and `RequireSurfacePolicy(...)` still renders AppSurface-shaped ProblemDetails for API callers.

If the host registered a custom `IAppSurfaceAspNetCorePolicyEvaluator` before calling `AddAppSurfaceTestAuth(...)`, Auth.Testing decorates that evaluator instead of replacing it. The host evaluator continues to own policy evaluation and result mapping; Auth.Testing only adds the test-only unknown-persona diagnostic.

## Scheme Modes

| Mode | Use when | Behavior |
| --- | --- | --- |
| `DefaultScheme` | You want the shortest WebApplicationFactory test setup. | Registers `AppSurface.Test` and makes it the default authenticate, challenge, and forbid scheme. |
| `NamedScheme` | Your policies opt in to the test scheme by name. | Registers the scheme without changing host defaults. |
| `NoDefault` | You need to prove the harness does not take over host defaults. | Registers the persona registry without registering the test scheme. Policies follow normal ASP.NET Core default-scheme behavior. |

The default scheme name is `AppSurface.Test`. Set `SchemeName` only when the test host needs a different explicit scheme name.

## Subject Mapping

By default, the harness emits `ClaimTypes.NameIdentifier`, which preserves the first default subject mapping used by `ForgeTrust.AppSurface.Auth.AspNetCore`. Set `SubjectClaimType` only when the host policy proof uses a different stable subject claim such as `sub`.

`SubjectClaimType` affects only the generated test principal and the AppSurface subject mapping. It does not map display names, emails, permissions, scopes, sessions, or app-user provisioning.

## Assertions

The assertion helpers do not depend on xUnit, NUnit, or MSTest:

```csharp
AppSurfaceAuthTestAssert.HasOutcome(
    result,
    AppSurfaceAuthOutcome.Forbid,
    AppSurfaceAuthReason.Forbidden);
```

ProblemDetails assertions verify the AppSurface extensions emitted by `RequireSurfacePolicy(...)`:

```csharp
using var json = JsonDocument.Parse(body);

AppSurfaceAuthTestAssert.HasProblemDetails(
    json.RootElement,
    AppSurfaceAuthOutcome.Challenge,
    AppSurfaceAuthReason.Unauthenticated,
    StatusCodes.Status401Unauthorized,
    "OperatorsOnly");
```

Assertion failures throw `AppSurfaceTestAuthAssertionException` with diagnostic code `ASTAUTH006`.

## Troubleshooting

| Problem | Cause | Fix | Code |
| --- | --- | --- | --- |
| Blank persona name | A persona was added without a stable name. | Use a non-empty ordinal name such as `operator`. | `ASTAUTH001` |
| Duplicate persona | Two personas use the same ordinal name. | Rename one persona or remove the duplicate. | `ASTAUTH002` |
| Unknown persona | `CreateAppSurfaceClient(...)` or request-level persona selection used a persona that is not registered. | Add the persona in `WithAppSurfaceTestAuth(...)` or correct the test value. | `ASTAUTH003` |
| Production environment blocked | Test auth started outside `Development`, `Test`, or `Testing`. | Run the test host in a test environment, or set `AllowProductionEnvironmentForTestHost = true` only for isolated production-like integration tests. | `ASTAUTH004` |
| Blank scheme name | `SchemeName` was set to an empty value. | Leave the default `AppSurface.Test` or set a non-empty name. | `ASTAUTH005` |
| Assertion failed | Expected auth contract does not match the actual AppSurface result or ProblemDetails payload. | Fix the host auth setup or update the expected contract. | `ASTAUTH006` |
| Blank subject claim type | `SubjectClaimType` was set to an empty value. | Set a non-empty claim type or leave it null to preserve host mapping. | `ASTAUTH007` |

## What The Package Includes

- `AddAppSurfaceTestAuth(...)`
- `WithAppSurfaceTestAuth(...)`
- `CreateAppSurfaceClient(...)`
- `WithAppSurfaceTestPersona(...)`
- `AppSurfaceTestAuthOptions`
- `AppSurfaceTestPersona`
- `AppSurfaceTestAuthSchemeMode`
- `AppSurfaceTestAuthDiagnosticCodes`
- `AppSurfaceAuthTestAssert`
- `AppSurfaceTestAuthAssertionException`
- deterministic test personas
- production-environment guard
- framework-neutral AppSurface auth result and ProblemDetails assertions

## What The Package Does Not Include

- Production authentication
- Cookies, JWT/OIDC, OAuth, or ASP.NET Identity setup
- Identity-provider hosting
- Authorization policy ownership
- Middleware replacement
- User stores, provisioning, or app-user mapping
- Browser login flows
- Dev Auth runtime personas
- Session freshness simulation
- Policy bypasses

## Auth Package Ladder

Use `ForgeTrust.AppSurface.Auth.Testing` for integration tests that need deterministic personas and canonical AppSurface assertions.

Use `ForgeTrust.AppSurface.Auth.AspNetCore` in production ASP.NET Core hosts that already own authentication and authorization but need AppSurface-shaped auth results.

Use `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` when a web host has chosen an OIDC provider and wants AppSurface-named cookie/OIDC scheme conventions.

Use Dev Auth for local runtime persona switching during manual development. Dev Auth is intentionally different from this package: it is a local runtime convenience, while Auth.Testing is a deterministic test harness.

Use raw ASP.NET Core authentication and authorization when the app needs production schemes, policies, middleware behavior, challenge/forbid execution, redirects, or provider integration.

## Pitfalls

- Do not register Auth.Testing in production app startup. It is for test hosts.
- Do not rely on the internal persona transport header. Use `CreateAppSurfaceClient(...)` or `WithAppSurfaceTestPersona(...)`.
- Do not use `DefaultScheme` when the test must prove an existing host default remains untouched. Use `NamedScheme` or `NoDefault`.
- Do not use personas to model stale sessions. Stale or unknown session behavior belongs to real session seams; this package only helps assert the existing `StaleOrUnknownSession` result and ProblemDetails mapping.
