# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.4`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.AppSurface.Auth` now defines durable external-subject to app-user-id mapping contracts without taking on user-store, ASP.NET Core, OIDC, EF Core, Aspire, or tenant-authority responsibilities.
- `ForgeTrust.AppSurface.Auth.AspNetCore` now includes AppSurface-shaped Minimal API policy helpers: `AddAppSurfacePolicy(...)` keeps policy definition in ASP.NET Core, while `RequireSurfacePolicy(...)` evaluates the named host policy through the existing AppSurface evaluator and returns API-safe ProblemDetails JSON for challenge, forbid, missing-policy, missing-service, and missing-subject outcomes instead of triggering browser redirects.
- Sanitized AppSurface Config audit diffs for comparing captured runtime configuration reports.
- LocalSecrets platform-index self-healing so `appsurface secrets list` no longer surfaces stale names whose stored
  values are missing.
- LocalSecrets Linux `secret-tool` resolution now uses trusted system candidates or an explicit absolute override instead
  of executing the first `secret-tool` discovered on `PATH`.

## Included in the next coordinated version

### Release and docs surface

- `examples/auth-web-razorwire-proof` now gives package adopters a five-minute browser proof for `ForgeTrust.AppSurface.Auth.AspNetCore`: one host-owned `OperatorsOnly` policy drives both a Minimal API response and a RazorWire-facing rendered state while all fake auth and persona switching stays sample-local.
- Added `ExternalSubject`, `AppUserId`, `IAppSurfaceUserIdentityResolver`, `AppSurfaceUserIdentityResolutionContext`, `AppSurfaceUserIdentityResult`, and `AppSurfaceUserIdentityStatus`, with README guidance for uniqueness, idempotency, cancellation, concurrency, PII-safe diagnostics, and ASP.NET Core adapter integration planning.
- `ForgeTrust.AppSurface.Auth.AspNetCore` documents the new Minimal API policy helper flow, package chooser metadata, safe ProblemDetails failure shape, and when native ASP.NET Core `RequireAuthorization(...)` remains the better choice.
- AppSurface Config now exposes a sanitized config audit diff surface. `ConfigAuditReportDiffer` compares two existing `ConfigAuditReport` snapshots without re-resolving providers, `ConfigAuditDiffTextRenderer` renders deterministic same-host or captured-snapshot evidence with redaction uncertainty called out, and `ConfigAuditDiffCommandRunner` gives apps command-framework-agnostic same-host and captured JSON workflows with display-safe problem/cause/fix/docs-link failures.
- AppSurface LocalSecrets platform-backed stores now validate indexed names against live stored values during
  `appsurface secrets list`. Missing values are pruned from the index when validation and repair succeed, and
  `appsurface secrets delete KEY` repairs a stale indexed name when the value is already gone while preserving
  `local-secret-missing` for keys that never existed.
- AppSurface LocalSecrets now hardens Linux Secret Service command selection. Linux uses `/usr/bin/secret-tool`, then
  `/bin/secret-tool`, or an explicit trusted absolute path through `AppSurfaceLocalSecretsOptions.LinuxSecretToolPath`
  and `appsurface secrets --secret-tool-path`. PATH matches are reported only as ignored diagnostic context, invalid
  overrides fail before command launch, and `--secret-tool-path` cannot be combined with `--store-file`.

### AppSurface Flow

- Reduce internal `InMemoryFlowRunner<TContext>` routing overhead by using prevalidated `FlowDefinition<TContext>` execution metadata while keeping public Flow APIs unchanged.
- Reduce synchronous in-memory runner allocations by making `FlowExecutionContext<TContext>` an immutable value-type snapshot passed into each node execution.
- Expand the Flow benchmark suite with runner-shape, generated-authoring, and outcome-allocation lanes so future performance work can attribute remaining overhead before changing public APIs.

## Migration watch

- `AppSurfaceUser.Id` remains a host-owned subject identifier in the existing ASP.NET Core adapter. Consumers that need durable app-owned users should resolve that subject through the new identity resolver contract instead of treating the mapped subject claim as an app user id.
- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
- `FlowExecutionContext<TContext>` is now a readonly record struct instead of a sealed record class. Most node implementations continue to read `FlowId`, `Version`, `NodeId`, `State`, and `ResumeEvent` the same way, but code that depended on reference identity, nullable context parameters, or `context is null` checks should switch to checking the populated members it requires.
