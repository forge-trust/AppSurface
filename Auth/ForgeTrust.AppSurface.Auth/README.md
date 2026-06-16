# ForgeTrust.AppSurface.Auth

`ForgeTrust.AppSurface.Auth` provides passive auth contracts for AppSurface modules.

It does not authenticate users, evaluate policies, redirect responses, issue cookies, validate tokens, sign users in, sign users out, or write audit logs. Host applications still own their security stack, such as ASP.NET Core authentication and authorization in a web host.

Use this package when you are authoring AppSurface modules or host integrations that need one surface-neutral vocabulary for users, sessions, auth decisions, login/logout prompts, and auth audit event descriptions.

## Release Guidance

AppSurface publishes coordinated `v0.1.0` release candidates. Before installing this package from a prerelease feed, read the [v0.1.0 RC 4 release note](../../releases/v0.1.0-rc.4.md) for current release risk, migration guidance, and package readiness.

## Quickstart: Model An Auth Decision

Install the package:

```bash
dotnet package add ForgeTrust.AppSurface.Auth
```

Create a user, session, context, and result:

```csharp
using ForgeTrust.AppSurface.Auth;

var user = new AppSurfaceUser(
    id: "user-123",
    displayName: "Local Admin",
    metadata: new Dictionary<string, string>
    {
        [AppSurfaceAuthMetadataKeys.TenantId] = "tenant-a"
    });

var session = new AppSurfaceSession(
    id: "session-456",
    startedAt: DateTimeOffset.UtcNow,
    expiresAt: DateTimeOffset.UtcNow.AddHours(1));

var context = new AppSurfaceAuthContext(user, session);
var result = AppSurfaceAuthResult.Forbidden(
    context,
    message: "The current user cannot publish docs.");

if (result.Outcome == AppSurfaceAuthOutcome.Forbid)
{
    // A future host adapter can map this to HTTP 403, a RazorWire forbidden state,
    // or an operator diagnostic. This package does not perform that mapping.
}
```

## What The Package Includes

- `AppSurfaceAuthModule`
- `AppSurfaceAuthOptions`
- `AppSurfaceUser`
- `AppSurfaceSession`
- `AppSurfaceAuthContext`
- `AppSurfaceAuthResult`
- `AppSurfaceAuthOutcome`
- `AppSurfaceAuthReason`
- `AppSurfaceLoginPrompt`
- `AppSurfaceLogoutPrompt`
- `AppSurfaceAuthAuditEvent`
- `AppSurfaceAuthMetadataKeys`
- Microsoft Options registration for `AppSurfaceAuthOptions`

## What The Package Does Not Include

- Authentication schemes or handlers
- Cookies, JWT bearer, OAuth, OIDC, or ASP.NET Identity integration
- Authorization policies or policy evaluation
- Middleware, endpoint filters, challenges, or forbids
- Request-scoped auth context accessors
- Login, logout, redirect, or return-url execution
- RazorWire, web, or UI behavior
- Audit sinks, loggers, metrics, traces, or persistence

## Result Outcomes And Reasons

`AppSurfaceAuthResult` separates high-level outcomes from concrete reasons so callers do not treat host setup failures, user denials, unsafe navigation, and stale sessions as the same kind of failure.

| Factory | Outcome | Reason | Problem | Likely cause | Fix | Safe user copy | Future web mapping |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Allowed(...)` | `Allowed` | `None` | The operation may proceed. | Host auth allowed the request. | Continue with the operation. | Optional success copy. | Success response. |
| `Challenge(...)` / `Unauthenticated(...)` | `Challenge` | `Unauthenticated` | The caller is not signed in. | No authenticated host identity was available. | Ask the host auth stack to authenticate the caller. | "Sign in to continue." | HTTP 401 or challenge. |
| `Forbid(...)` / `Forbidden(...)` | `Forbid` | `Forbidden` | The caller is signed in but not allowed. | Host policy denied the authenticated caller. | Show a forbidden state or ask an operator to grant access. | "You do not have permission." | HTTP 403 or forbid. |
| `MissingPolicy(...)` | `SetupFailure` | `MissingPolicy` | The host policy was not configured or could not be found. | A policy name is missing, misspelled, or not registered. | Register the host policy or fix the configured name. | Use generic failure copy. Log the setup issue. | Host setup error or guarded 403. |
| `MissingServices(...)` | `SetupFailure` | `MissingServices` | Required host auth services are unavailable. | The host did not register its auth or authorization services. | Register the host auth services before using the adapter. | Use generic failure copy. Log the setup issue. | Host setup error or guarded 403. |
| `MissingSubject(...)` | `SetupFailure` | `MissingSubject` | An authenticated caller could not be mapped to a stable subject. | The host principal did not include a configured subject claim. | Configure the host to issue a stable subject claim or update the host adapter subject mapping. | Use generic failure copy. Log the setup issue. | Host setup error or guarded 403. |
| `UnsafeReturnUrl(...)` | `UnsafeNavigation` | `UnsafeReturnUrl` | A return or navigation target was unsafe. | User input contained an external, protocol-relative, backslash, or control-character path. | Drop the target and use a safe fallback. | "Return target was not allowed." | Redirect to safe fallback or reject. |
| `StaleOrUnknownSession(...)` | `StaleOrUnknownSession` | `StaleOrUnknownSession` | The session could not be trusted. | The session expired, was missing, or could not be resolved. | Ask the host to refresh or reauthenticate. | "Your session may have expired." | HTTP 401, challenge, or refresh flow. |

## Metadata

Every metadata-bearing contract copies metadata into a read-only dictionary with ordinal keys. Null metadata becomes empty. Keys must be non-empty strings, and values must not be null.

Metadata is context for diagnostics, display, and adapter hand-off. It is not an authorization source of truth unless a host-owned adapter validates the value against the host security system.

Reserved keys live in `AppSurfaceAuthMetadataKeys`:

- `TenantId`
- `PermissionHints`
- `AuthenticationScheme`
- `SubjectId`
- `CorrelationId`

The `appsurface.` prefix is reserved for AppSurface-owned keys. Keep metadata values primitive and non-sensitive so future typed properties can migrate common keys without breaking existing callers.

## Prompt Targets

`AppSurfaceLoginPrompt` and `AppSurfaceLogoutPrompt` are passive descriptions. They do not redirect, challenge, sign in, sign out, set cookies, or call identity providers.

Prompt target paths may be `null` or safe app-relative paths only. Safe paths start with `/`, are not protocol-relative (`//example.com`), are not slash-backslash rooted (`/\example`), contain no backslashes, and contain no control characters. The contracts do not URL-decode input; callers that accept encoded values must decode before creating a prompt.

## Host Auth Versus AppSurface Auth Contracts

Use host auth directly when you need to authenticate a request, configure schemes, evaluate policies, issue challenges, forbid callers, validate tokens, or handle identity-provider flows.

Use AppSurface auth contracts when an AppSurface module needs to describe a user, session, decision, prompt, or audit event without depending on a specific host framework.

## ASP.NET Core Adapter

Use [`ForgeTrust.AppSurface.Auth.AspNetCore`](../ForgeTrust.AppSurface.Auth.AspNetCore/README.md) when an ASP.NET Core host already owns authentication and authorization, but AppSurface-aware code needs mapped request context or named host-policy results.

The ASP.NET Core adapter keeps schemes, policies, middleware, challenges, forbids, redirects, cookies, OIDC, and Identity in the host. It only maps the current request into `AppSurfaceAuthContext` and ASP.NET Core policy outcomes into `AppSurfaceAuthResult`.

## Future Consumers

The next auth slices map host behavior into these contracts:

- #418 adds the ASP.NET Core adapter package for request context and named host-policy results.
- #419 maps Minimal API policy decisions to correct challenge and forbid behavior.
- #421 adds a result-bearing RazorWire auth adapter while preserving boolean authorizer compatibility.
- #422 projects the same auth result states into RazorWire UI components.

## Composition

Register `AppSurfaceAuthModule` from another AppSurface module when you need the auth boundary present in the module graph:

```csharp
public void RegisterDependentModules(ModuleDependencyBuilder builder)
{
    builder.AddModule<AppSurfaceAuthModule>();
}
```

That registration composes the boundary and registers `AppSurfaceAuthOptions`. It has no runtime request effect.
