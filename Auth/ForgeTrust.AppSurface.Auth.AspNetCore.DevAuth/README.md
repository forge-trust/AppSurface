# ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth

`ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` adds fake, local-only ASP.NET Core authentication for AppSurface package consumers who need to try auth-aware endpoints without configuring OIDC, cookies, ASP.NET Identity, or an external identity provider.

DevAuth is development tooling. It is not production authentication, a user store, durable app-user mapping, OIDC, token validation, tenant authority, audit logging, or the future test harness.

## Release Guidance

AppSurface publishes coordinated `v0.1.0` release candidates. Before installing this package from a prerelease feed, read the [v0.1.0 RC 4 release note](../../releases/v0.1.0-rc.4.md), the [unreleased proof artifact](../../releases/unreleased.md), and the current [package chooser](../../packages/README.md) for release risk and package readiness.

## Quickstart

Install the package in an ASP.NET Core app:

```bash
dotnet package add ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth
```

Configure AppSurface auth mapping, seed local personas, require the DevAuth named scheme in policies, and map the control page:

```csharp
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

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

app.MapGet("/api/auth-proof", () => Results.Json(new { result = "allowed" }))
   .RequireSurfacePolicy("OperatorsOnly");

app.MapAppSurfaceDevAuth();
```

Run in Development:

```bash
DOTNET_ENVIRONMENT=Development dotnet run
```

Open the persona lab:

```text
/_appsurface/dev-auth
```

The control page lets you select a seeded persona, clear the persona cookie, inspect safe local claims, and copy a visible marker such as `DEV AUTH: Local Admin (AppSurface.DevAuth)`.

## API Reference

- `AddAppSurfaceDevAuth(IHostEnvironment environment, Action<AppSurfaceDevAuthOptions> configure)` registers the named DevAuth authentication scheme and startup safety validation. The `configure` callback is evaluated once during registration, and the same validated options are used for both scheme registration and runtime DevAuth behavior.
- `MapAppSurfaceDevAuth(this IEndpointRouteBuilder endpoints)` maps the local-only control page, status JSON, select persona endpoint, and clear persona endpoint.
- `AppSurfaceDevAuthDefaults.AuthenticationScheme` is `AppSurface.DevAuth`.
- `AppSurfaceDevAuthDefaults.PathPrefix` is `/_appsurface/dev-auth`.
- `AppSurfaceDevAuthDefaults.CookieName` is `.AppSurface.DevAuth.Persona`.
- `AppSurfaceDevAuthDefaults.SubjectClaimType` is `sub`.
- `AppSurfaceDevAuthOptions.Users` contains seeded local personas.
- `AppSurfaceDevAuthOptions.SchemeName` overrides the registered authentication scheme. It defaults to `AppSurfaceDevAuthDefaults.AuthenticationScheme`.
- `AppSurfaceDevAuthOptions.PathPrefix` overrides the local control-page and status endpoint path prefix. It defaults to `AppSurfaceDevAuthDefaults.PathPrefix`.
- `AppSurfaceDevAuthOptions.CookieName` overrides the selected-persona cookie name. It defaults to `AppSurfaceDevAuthDefaults.CookieName`.
- `AppSurfaceDevAuthOptions.UseAsDefaultSchemeForLocalProof` is off by default. Enable it only for throwaway local proof hosts where DevAuth intentionally owns the whole auth stack.
- `AppSurfaceDevAuthOptions.AllowDevAuthOverrideForLocalProof` is off by default. Enable it only when a local proof intentionally composes DevAuth with other registered auth schemes.
- `AppSurfaceDevAuthOptions.RequireLoopbackControlRequests` is on by default.
- `AppSurfaceDevAuthOptions.DisplayClaimTypes` controls which issued claims may appear in the local HTML preview. It defaults to `sub`, `role`, and `tenant`.

Persona IDs must be route-safe local identifiers containing only ASCII letters, digits, `.`, `_`, or `-`. The dot-segment IDs `.` and `..` are not allowed, and ids that look like tokens, secrets, passwords, keys, credentials, or emails are rejected. Persona IDs are used in the selection endpoint path and stored as the protected cookie payload.

Persona state is stored in a protected, HttpOnly, SameSite=Strict cookie that contains only the persona id. DevAuth adds the `Secure` cookie attribute on HTTPS requests and omits it on plain HTTP localhost so browser-based local proof works. Blank, unknown, stale, reset, or tampered cookie state authenticates as no result.

The authentication handler issues every seeded persona claim, but the control page does not display every issued claim. Claims are rendered only when their type is in `DisplayClaimTypes`, their value is short, and neither the type nor the value looks like a token, secret, password, key, credential, or email. Display names and subjects that look sensitive are redacted from HTML and status JSON. Hidden claims are counted without showing their values.

## DevAuth Versus Other Auth Packages

Use `ForgeTrust.AppSurface.Auth` when reusable modules need surface-neutral auth vocabulary, auth results, prompts, audit event descriptions, or durable external-subject to app-user-id mapping contracts.

Use `ForgeTrust.AppSurface.Auth.AspNetCore` when an ASP.NET Core host already owns authentication and authorization, but AppSurface modules need mapped request context or named host-policy results.

Use DevAuth only when you need fake local personas in Development so a package consumer can try AppSurface auth-aware endpoints without an identity provider.

Use OIDC or native ASP.NET Core authentication packages for real sign-in, cookies, external identity providers, redirects, token validation, and production auth flows.

Use the future test harness for deterministic automated auth scenarios once that package exists. DevAuth is for local developer interaction, not browser automation authority.

## What Not To Copy To Production

- Do not enable DevAuth outside Development.
- Do not treat persona claims as production identity, tenant authority, or permission truth.
- Do not put tokens, passwords, secrets, raw emails, or production identity payloads into seeded personas.
- Do not add sensitive claim types to `DisplayClaimTypes`; the control page still refuses common secret/token/email shapes.
- Do not use the DevAuth persona cookie as production session management.
- Do not hide real auth scheme conflicts with `AllowDevAuthOverrideForLocalProof`.

## Diagnostics

DevAuth diagnostics use `Problem:`, `Cause:`, `Fix:`, and `Docs:` wording and the safe metadata key `appsurface.devauth.diagnostic_code`.

| Code | Meaning |
| --- | --- |
| `ASDEV001` | DevAuth was enabled outside Development. |
| `ASDEV002` | DevAuth detected an existing real authentication scheme or default. |
| `ASDEV003` | DevAuth was enabled without seeded personas. |
| `ASDEV004` | A selected persona did not contain the configured subject claim. |
| `ASDEV005` | The reserved path prefix was invalid or conflicted with the local control surface. |
| `ASDEV006` | A persona id was invalid, unknown, stale, duplicated, or tampered. |

Diagnostics, HTML, and status JSON do not include raw tokens, secrets, passwords, raw emails, or unbounded identity-provider payloads.

## Pitfalls

- Call `UseAuthentication()` before endpoints that depend on selected personas.
- Call `UseAuthorization()` before AppSurface policy-protected endpoints when your host uses normal ASP.NET Core authorization middleware.
- Call `MapAppSurfaceDevAuth()` so the persona lab and status JSON exist.
- Add `AppSurfaceDevAuthDefaults.AuthenticationScheme` to policies that should evaluate DevAuth personas.
- Use simple route-safe persona IDs such as `admin`, `viewer`, or `qa.local_1`; dot segments, sensitive-looking ids, query strings, fragments, encoded slashes, spaces, and other punctuation are rejected with `ASDEV006`.
- Call `Subject(...)` for every persona and keep it aligned with `AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim(...))`.
- Keep the DevAuth marker visible in local sample pages so fake auth is impossible to miss.

## Upgrade And Removal

Remove DevAuth before deploying a host:

1. Remove the `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` package reference.
2. Remove `AddAppSurfaceDevAuth(...)`.
3. Remove `MapAppSurfaceDevAuth()`.
4. Remove DevAuth scheme references from policies.
5. Configure real ASP.NET Core authentication and keep `ForgeTrust.AppSurface.Auth.AspNetCore` only for AppSurface result mapping.

For a working proof, see [the DevAuth example](../../examples/auth-aspnetcore-dev-auth/README.md).
