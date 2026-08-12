# ForgeTrust.AppSurface.Auth

`ForgeTrust.AppSurface.Auth` provides passive auth contracts for AppSurface modules, including a transition-bound
delegated-agent approval vocabulary for local task harnesses.

It does not authenticate users, evaluate policies, redirect responses, issue cookies, validate tokens, sign users in, sign users out, or write audit logs. Host applications still own their security stack, such as ASP.NET Core authentication and authorization in a web host.

Use this package when you are authoring AppSurface modules or host integrations that need one surface-neutral vocabulary for users, sessions, auth decisions, login/logout prompts, auth audit event descriptions, or delegated-agent approval descriptions.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

Use the [AppSurface Auth adoption ladder](../../start-here/auth-adoption-ladder.md) when deciding whether to start with Auth core, the ASP.NET Core adapter, DevAuth, OIDC, Auth.Testing, or raw ASP.NET Core authentication.

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
- `AgentActionMetadata`, `AgentActionRequest`, and `AgentActionBinding`
- `AgentIdentityReference` and `AgentApproverReference`
- `AgentAuthorizationDecision` and `AgentConfirmationRequest`
- `AgentApprovalReceipt` and `AgentApprovalConsumptionResult`
- `AgentAuthorizationAuditEvent` and `AgentApprovalDiagnosticCodes`
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
- Agent runtimes, tool execution, agent grants, policy engines, receipt stores, or receipt consumption
- Approval inboxes, notification delivery, HTTP approval endpoints, or automated-approval eligibility logic

## Delegated Agent Task Approval

Use the delegated-agent contracts when a local harness proposes one consequential workflow transition and the host
needs a narrow, revocable, auditable approval boundary. The contracts are designed for a task-approval model: the
agent proposes an action, the host evaluates current authority and a narrow agent grant, a human may approve the exact
binding, and the host consumes that approval at most once immediately before execution.

Start from the [Auth adoption ladder](../../start-here/auth-adoption-ladder.md#package-ladder), then run the local
fixture from a clone of this repository:

```bash
dotnet test Auth/ForgeTrust.AppSurface.Auth.Tests/ForgeTrust.AppSurface.Auth.Tests.csproj \
  --filter FullyQualifiedName~AgentApprovalLifecycleProofTests
```

The fixture proves one successful consumption plus refusal for confirmation denial, replay, expiry, revocation,
workflow-state change, intent-digest change, lost human authority, and missing agent grant. It uses a fake clock and
in-memory test store; it is not an AppSurface runtime implementation.

### The golden path

```csharp
using ForgeTrust.AppSurface.Auth;

var action = new AgentActionMetadata(
    "workflow.approve",
    "Approve workflow",
    AgentActionRisk.High,
    AgentConfirmationPosture.AlwaysRequireHuman,
    AgentActionRedaction.DoNotExposeArguments);

var request = new AgentActionRequest(
    action,
    new AgentActionBinding(
        actionId: action.ActionId,
        taskId: "task-42",
        workflowInstanceId: "workflow-42",
        expectedState: "pending",
        expectedStateVersion: "7",
        transition: "approve",
        bindingProfile: "workflow-approval/v1",
        safeIntentDigest: "sha256:host-derived-safe-digest"),
    new AgentIdentityReference("harness:local"),
    correlationId: "approval-42",
    requestedAt: DateTimeOffset.UtcNow,
    safeSummary: "Approve the production release workflow.");

// The host evaluates current human authority and the agent's narrow task grant.
var confirmation = new AgentConfirmationRequest(
    request,
    new AgentApproverReference("subject:release-manager"),
    expiresAt: request.RequestedAt.AddMinutes(5));
var decision = AgentAuthorizationDecision.ConfirmationRequired(confirmation);

// Only after durable human approval, the host issues an opaque receipt and persists it itself.
// FromConfirmedRequest also rejects an issuance after the confirmation has expired.
var receipt = AgentApprovalReceipt.FromConfirmedRequest(
    "host-opaque-receipt-reference",
    confirmation,
    issuedAt: DateTimeOffset.UtcNow,
    expiresAt: confirmation.ExpiresAt);

// Immediately before execution, the host atomically claims the receipt, rechecks current
// authority/grant/state/binding, then returns AgentApprovalConsumptionResult. AppSurface
// deliberately does not provide the store, claim operation, or workflow execution.
```

These contracts make the host boundary explicit; a constructor call is not an authorization decision. Never accept a
receipt from agent input or treat it as execution authority without a host-owned durable record, atomic claim, and
current authority/grant/state/binding checks. A host may use different identity, policy, storage, audit, or workflow
technologies, but it must not let an agent inherit the human's full permissions or approve itself.

### Contract lifecycle

```text
AgentActionRequest -> AgentAuthorizationDecision
                         | Allowed | Denied | ConfirmationRequired
                         v
                AgentConfirmationRequest -> AgentApprovalReceipt
                                                   |
                                                   v
                                  host atomic claim + current rechecks
                                                   |
                                                   v
        Consumed | AlreadyConsumed | Expired | Revoked | Stale | BindingMismatch | Denied
```

`AgentActionBinding` binds the stable action identifier, task/harness run, workflow instance, expected state and
concurrency version, requested transition, a host-defined binding profile, and a host-derived safe intent digest. Its
`Matches(...)` method compares every approval-relevant field with ordinal semantics. The profile is versioned so the
host can reproduce its own normalisation rule; AppSurface does not prescribe a JSON representation, hash algorithm,
JWT, signature, database schema, or token format.

`AgentIdentityReference` is a stable host-local agent or harness reference. `AgentApproverReference` is deliberately a
different type so a host does not confuse an approver with an agent, a durable `AppUserId`, or a raw provider credential.
A host can derive the approver reference from an [`ExternalSubject`](#durable-app-user-mapping), an app-owned identity,
or another validated host subject namespace.

### Confirmation and redaction rules

`AgentConfirmationRequest` is the input to host-owned UI, CLI, or other human confirmation behavior. It carries the
exact request, approver, expiry, safe summary, rationale, action metadata, and binding. It does not render UI or send a
notification. A host confirmation display must fail closed—remove or disable approval when the request expires, the
workflow state changes, or the binding cannot be reproduced.

`AgentActionMetadata.Redaction` is guidance, not a policy bypass. Use `SafeSummaryOnly` for a host-provided safe
summary, `RequireHostRedaction` when the host must transform additional detail, and `DoNotExposeArguments` for actions
whose raw arguments never belong in confirmation, audit, or diagnostic displays. Do not place tokens, credentials,
raw identity-provider payloads, or sensitive workflow inputs in metadata, messages, summaries, or digests.

v0 supports **approve** and **deny** only. An edit changes the proposed transition, so it must become a new
`AgentActionRequest` with a new binding, evaluation, and receipt. Never mutate an approved request or reuse a receipt
for edited arguments.

### Consumption outcomes and recovery

The host owns atomic receipt consumption. A successful `Consumed` result is the only outcome that may execute the
bound transition, and exactly one competing claimant may receive it. Every other result is terminal for that attempt.
Branch on the typed `Outcome` (or `IsConsumed`), not a diagnostic code; hosts may add stable subcodes only within the
canonical family for that outcome.

Treat the claim and execution record as one durable host transaction, or write an outbox/idempotency record with the
claim. A process crash after a claim but before execution must recover without losing the transition or executing it
twice. Do not mark a receipt consumed and then fire-and-forget execution.

| Outcome | Stable code | What happened | Host next step |
| --- | --- | --- | --- |
| `Consumed` | `agent-approval.consumed` | The host atomically claimed a valid receipt. | Execute the exact bound transition once and record the correlated audit event. |
| `AlreadyConsumed` | `agent-approval.already-consumed` | A competing attempt already claimed the receipt. | Do not retry execution; inspect the correlated audit trail. |
| `Expired` | `agent-approval.expired` | The receipt expired before claim. | Re-evaluate and request a fresh confirmation. |
| `Revoked` | `agent-approval.revoked` | The host revoked the receipt before claim. | Do not execute; re-evaluate if the action is still needed. |
| `Stale` | `agent-approval.stale` | The expected workflow state or version changed. | Build a fresh action request from current state. |
| `BindingMismatch` | `agent-approval.binding-mismatch` | The host could not reproduce the bound action. | Investigate binding profile/input changes, then build a fresh request. |
| `Denied` | `agent-approval.consumption-denied` | Current human authority or the agent grant is absent. | Do not execute; restore host authority/grant or stop the action. |

Every decision, receipt, consumption attempt, and terminal outcome can be represented with
`AgentAuthorizationAuditEvent`. The type requires the canonical code family for its event kind, an approver for an
`Approved` event, and a receipt reference for receipt lifecycle events. It remains a passive audit description: the host
decides whether audit delivery must be synchronous, durable through an outbox, retried, or fail closed. A contract
object does not prove a sink received it. Use `AgentAuthorizationAuditEvent.FromReceipt(...)` for receipt-backed events
so the receipt's binding, agent, approver, receipt reference, and correlation identifier cannot drift apart.

Display-safe strings are limited to 4,096 characters. Metadata is limited to 32 entries, 128-character keys,
1,024-character values, and 16,384 characters in total. These limits protect confirmation, audit, and diagnostic
surfaces; hosts should store larger operational detail in a separate bounded record.

### Flow Durable Task mapping

`ForgeTrust.AppSurface.Auth` does not reference Flow. A Durable Task host that already uses
[`IFlowResumeAuthorizer`](../../Flow/ForgeTrust.AppSurface.Flow.DurableTask/README.md#delegated-agent-approval-mapping)
can project a successfully consumed receipt into its existing authorizer. The host remains responsible for defensive
metadata projection and for mapping every non-consumed outcome to a stable deny code; there is no Auth-to-Flow adapter
or package dependency.

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

For future CLI authentication, see the [AppSurface CLI authenticated command design](../../Cli/ForgeTrust.AppSurface.Cli/docs/authenticated-command-design.md). CLI auth remains outside this package until the command-gate, token-cache, and non-interactive auth contracts prove which pieces are genuinely surface-neutral.

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
