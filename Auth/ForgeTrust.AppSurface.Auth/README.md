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
    id: "host-subject-123",
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
- `ExternalSubject`
- `AppUserId`
- `IAppSurfaceUserIdentityResolver`
- `AppSurfaceUserIdentityResolutionContext`
- `AppSurfaceUserIdentityResult`
- `AppSurfaceUserIdentityStatus`
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
- User stores, user provisioning implementation, database schema, or persistence migrations
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

## Durable App-User Mapping

OIDC, SAML, cookies, test auth handlers, and enterprise gateways usually give the app an authenticated external subject. Most apps still need a durable app-owned user id for domain records, preferences, ownership, audit trails, and billing. This package defines that boundary without becoming the user store.

Use `ExternalSubject` for the authenticated external identity tuple:

```csharp
var subject = new ExternalSubject(
    issuer: "https://login.example.com",
    subject: principalSubject,
    partitionKey: tenantRealm);
```

The uniqueness key is `(Issuer, Subject, PartitionKey)` with ordinal comparison. `PartitionKey` is optional host-validated namespace context for issuers where subject ids are only unique within a realm, tenant, client, or environment. It is not tenant authority and should not be used as a permission source unless the host app validates it through its own security model.

Do not use email, display name, subject alone, tenant id alone, or another mutable profile claim as the durable identity key. Those values can be reassigned, renamed, duplicated across issuers, or corrected by an identity provider without meaning "same app user" in your domain.

Use `AppUserId` for the durable app-owned user id returned by your app resolver:

```csharp
public sealed class SqlUserIdentityResolver : IAppSurfaceUserIdentityResolver
{
    public async ValueTask<AppSurfaceUserIdentityResult> ResolveAsync(
        ExternalSubject subject,
        AppSurfaceUserIdentityResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var appUser = await LoadOrProvisionUserAsync(subject, cancellationToken);
        if (appUser is null)
        {
            return AppSurfaceUserIdentityResult.ProvisioningDenied(
                subject,
                metadata: new Dictionary<string, string>
                {
                    ["correlation_id"] = context.CorrelationId ?? string.Empty
                });
        }

        return AppSurfaceUserIdentityResult.Resolved(
            new AppUserId(appUser.Id),
            subject,
            metadata: new Dictionary<string, string>
            {
                ["resolution"] = appUser.WasCreated ? "provisioned" : "loaded"
            });
    }
}
```

`ResolveAsync(...)` is neutral on purpose. Your application chooses whether resolution only loads an existing mapping, creates a user on first sign-in, requires an invite, blocks disabled users, or records an operator review event. Successful resolution should be idempotent for the same external subject tuple, honor cancellation before expensive work and during awaited I/O, and handle concurrent first sign-ins without creating duplicate app users.

Identity resolution failures are separate from `AppSurfaceAuthResult`:

| Factory | Status | Problem | Likely cause | Fix | Safe user copy | Operator diagnostic |
| --- | --- | --- | --- | --- | --- | --- |
| `Resolved(...)` | `Resolved` | The external subject mapped to a durable app-owned user id. | Existing or newly provisioned mapping matched exactly one app user. | Continue with app-owned user state. | Optional success copy. | Record the app user id only when your app permits it; never log raw provider payloads by default. |
| `MissingSubject(...)` | `MissingSubject` | No external subject was available to resolve. | Host auth produced no stable subject claim or skipped identity mapping. | Fix the host subject claim mapping before calling the resolver. | "Sign in again or contact support." | Log the adapter, correlation id, and configured claim names without raw tokens. |
| `MalformedSubject(...)` | `MalformedSubject` | The supplied subject was invalid for the resolver. | The issuer, subject, or partition failed app-owned validation. | Reject the mapping and correct the upstream identity contract. | "This account cannot be used here." | Log which field failed validation, not the raw field value. |
| `DisabledAppUser(...)` | `DisabledAppUser` | The mapped app user exists but is disabled. | An operator, policy, billing state, or compliance rule blocked the app user. | Show a disabled-account or support path. | "This account is disabled." | Log the app-owned disabled reason if it is safe for operators. |
| `StaleOrUnknownSession(...)` | `StaleOrUnknownSession` | The host session cannot be trusted for mapping. | Session expiry, revoked login, missing session record, or stale host context. | Ask the host to refresh or reauthenticate before mapping. | "Your session may have expired." | Log session freshness state and correlation id. |
| `DuplicateMapping(...)` | `DuplicateMapping` | More than one mapping matched the same external subject tuple. | A uniqueness constraint is missing, a migration imported duplicates, or concurrent first sign-in created two mappings. | Fail closed, repair the app store, then add an app-owned uniqueness guard. | "We could not safely identify your account." | Log duplicate count and safe mapping ids for repair. |
| `StoreUnavailable(...)` | `StoreUnavailable` | The app-owned identity store was unavailable. | Database, cache, network, or dependency outage. | Retry later or show a temporary failure. | "Account lookup is temporarily unavailable." | Log dependency name, timeout/retry state, and correlation id. |
| `ProvisioningDenied(...)` | `ProvisioningDenied` | The app declined to create or attach a user. | Invite, approval, billing, plan, or domain policy denied provisioning. | Show invite, approval, or access-request UX. | "Request access to continue." | Log safe policy code and next operator action. |

Copy this pattern:

```csharp
var subject = new ExternalSubject(issuer, subjectId, partitionKey);
var result = await resolver.ResolveAsync(
    subject,
    new AppSurfaceUserIdentityResolutionContext(correlationId),
    cancellationToken);

if (result.Succeeded)
{
    var appUserId = result.AppUserId!.Value;
}
```

Do not copy these patterns:

```csharp
// Do not treat AppSurfaceUser.Id from a host adapter as your durable app user id.
var appUserId = authContext.User?.Id;

// Do not log raw external subject values by default.
logger.LogInformation("Mapped {Issuer} {Subject}", subject.Issuer, subject.Subject);

// Do not rely on tenant or partition metadata as permission truth.
var isAdmin = subject.PartitionKey == "admin";
```

`ExternalSubject.ToString()` and `AppUserId.ToString()` redact raw values by default. Metadata and messages should also avoid raw subject ids, tokens, emails, display names, identity-provider payloads, and database connection details unless the app has an explicit safe-diagnostics policy.

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

Use `ExternalSubject`, `AppUserId`, and `IAppSurfaceUserIdentityResolver` when a host-authenticated subject must be mapped to durable app-owned user state. Keep persistence, provisioning policy, tenant authority, and permission checks in the consuming app.

## ASP.NET Core Adapter

Use [`ForgeTrust.AppSurface.Auth.AspNetCore`](../ForgeTrust.AppSurface.Auth.AspNetCore/README.md) when an ASP.NET Core host already owns authentication and authorization, but AppSurface-aware code needs mapped request context or named host-policy results.

The ASP.NET Core adapter keeps schemes, policies, middleware, challenges, forbids, redirects, cookies, OIDC, and Identity in the host. It only maps the current request into `AppSurfaceAuthContext` and ASP.NET Core policy outcomes into `AppSurfaceAuthResult`.

Today, `AppSurfaceUser.Id` from the ASP.NET Core adapter is the stable host-owned subject claim selected by the adapter. Treat it as an external subject, not as your durable app-owned user id. A future adapter slice can compose `ExternalSubject` with `IAppSurfaceUserIdentityResolver` asynchronously without adding ASP.NET Core dependencies to this package.

## Composition

Register `AppSurfaceAuthModule` from another AppSurface module when you need the auth boundary present in the module graph:

```csharp
public void RegisterDependentModules(ModuleDependencyBuilder builder)
{
    builder.AddModule<AppSurfaceAuthModule>();
}
```

That registration composes the boundary and registers `AppSurfaceAuthOptions`. It has no runtime request effect.
