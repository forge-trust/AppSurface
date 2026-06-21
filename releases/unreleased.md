# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.4`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.AppSurface.Auth` now defines durable external-subject to app-user-id mapping contracts without taking on user-store, ASP.NET Core, OIDC, EF Core, Aspire, or tenant-authority responsibilities.
- `ForgeTrust.AppSurface.Auth.AspNetCore` now includes AppSurface-shaped Minimal API policy helpers: `AddAppSurfacePolicy(...)` keeps policy definition in ASP.NET Core, while `RequireSurfacePolicy(...)` evaluates the named host policy through the existing AppSurface evaluator and returns API-safe ProblemDetails JSON for challenge, forbid, missing-policy, missing-service, and missing-subject outcomes instead of triggering browser redirects.
- Add `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc`, a public preview ASP.NET Core cookie + OIDC convenience package with explicit AppSurface scheme names, `SaveTokens=false` by default, passive prompt helpers, safe diagnostics, event chaining, and package chooser/readiness coverage.
- Sanitized AppSurface Config audit diffs for comparing captured runtime configuration reports.
- AppSurface Observability package defaults for sending app-side logs, traces, and metrics to Aspire or another OTLP collector.
- LocalSecrets platform-index self-healing so `appsurface secrets list` no longer surfaces stale names whose stored
  values are missing.

## Included in the next coordinated version

### Release and docs surface

- `examples/auth-web-razorwire-proof` now gives package adopters a five-minute browser proof for `ForgeTrust.AppSurface.Auth.AspNetCore`: one host-owned `OperatorsOnly` policy drives both a Minimal API response and a RazorWire-facing rendered state while all fake auth and persona switching stays sample-local.
- Added `ExternalSubject`, `AppUserId`, `IAppSurfaceUserIdentityResolver`, `AppSurfaceUserIdentityResolutionContext`, `AppSurfaceUserIdentityResult`, and `AppSurfaceUserIdentityStatus`, with README guidance for uniqueness, idempotency, cancellation, concurrency, PII-safe diagnostics, and ASP.NET Core adapter integration planning.
- `ForgeTrust.AppSurface.Auth.AspNetCore` documents the new Minimal API policy helper flow, package chooser metadata, safe ProblemDetails failure shape, and when native ASP.NET Core `RequireAuthorization(...)` remains the better choice.
- Document the AppSurface OIDC package boundary, including when to use raw ASP.NET Core OIDC or provider SDKs instead, and add a no-secret local registration proof example.
- AppSurface Config now exposes a sanitized config audit diff surface. `ConfigAuditReportDiffer` compares two existing `ConfigAuditReport` snapshots without re-resolving providers, `ConfigAuditDiffTextRenderer` renders deterministic same-host or captured-snapshot evidence with redaction uncertainty called out, and `ConfigAuditDiffCommandRunner` gives apps command-framework-agnostic same-host and captured JSON workflows with display-safe problem/cause/fix/docs-link failures.
- AppSurface Observability adds `ForgeTrust.AppSurface.Observability` with module-first OpenTelemetry logging, tracing, and metrics registration, endpoint-driven OTLP exporter setup, service identity resource metadata, and docs for Aspire and non-Aspire adoption paths.
- RazorWire export now owns HTTP redirect handling for artifact-producing fetches, including crawled routes and conventional `404.html` staging. Same-origin redirects remain supported, while redirects outside the configured export origin and base path fail with `RWEXPORT008` before response content is read or written; routes that intentionally point to a different host or app path should be modeled as external references instead of exporter-managed artifacts.
- AppSurface LocalSecrets platform-backed stores now validate indexed names against live stored values during
  `appsurface secrets list`. Missing values are pruned from the index when validation and repair succeed, and
  `appsurface secrets delete KEY` repairs a stale indexed name when the value is already gone while preserving
  `local-secret-missing` for keys that never existed.

### AppSurface Flow

- Reduce internal `InMemoryFlowRunner<TContext>` routing overhead by using prevalidated `FlowDefinition<TContext>` execution metadata while keeping public Flow APIs unchanged.
- Reduce synchronous in-memory runner allocations by making `FlowExecutionContext<TContext>` an immutable value-type snapshot passed into each node execution.
- Expand the Flow benchmark suite with runner-shape, generated-authoring, and outcome-allocation lanes so future performance work can attribute remaining overhead before changing public APIs.

## Migration watch

- `AppSurfaceUser.Id` remains a host-owned subject identifier in the existing ASP.NET Core adapter. Consumers that need durable app-owned users should resolve that subject through the new identity resolver contract instead of treating the mapped subject claim as an app user id.
- Hosts adopting `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` must still call `UseAuthentication()` and `UseAuthorization()` in ASP.NET Core order and must explicitly configure default schemes if they want host-wide defaults.
- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
- `FlowExecutionContext<TContext>` is now a readonly record struct instead of a sealed record class. Most node implementations continue to read `FlowId`, `Version`, `NodeId`, `State`, and `ResumeEvent` the same way, but code that depended on reference identity, nullable context parameters, or `context is null` checks should switch to checking the populated members it requires.
