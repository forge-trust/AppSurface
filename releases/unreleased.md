# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.4`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.AppSurface.Auth.AspNetCore` now includes AppSurface-shaped Minimal API policy helpers: `AddAppSurfacePolicy(...)` keeps policy definition in ASP.NET Core, while `RequireSurfacePolicy(...)` evaluates the named host policy through the existing AppSurface evaluator and returns API-safe ProblemDetails JSON for challenge, forbid, missing-policy, missing-service, and missing-subject outcomes instead of triggering browser redirects.
- Sanitized AppSurface Config audit diffs for comparing captured runtime configuration reports.

## Included in the next coordinated version

### Release and docs surface

- `ForgeTrust.AppSurface.Auth.AspNetCore` documents the new Minimal API policy helper flow, package chooser metadata, safe ProblemDetails failure shape, and when native ASP.NET Core `RequireAuthorization(...)` remains the better choice.
- AppSurface Config now exposes a sanitized config audit diff surface. `ConfigAuditReportDiffer` compares two existing `ConfigAuditReport` snapshots without re-resolving providers, `ConfigAuditDiffTextRenderer` renders deterministic same-host or captured-snapshot evidence with redaction uncertainty called out, and `ConfigAuditDiffCommandRunner` gives apps command-framework-agnostic same-host and captured JSON workflows with display-safe problem/cause/fix/docs-link failures.

### AppSurface Flow

- Reduce internal `InMemoryFlowRunner<TContext>` routing overhead by using prevalidated `FlowDefinition<TContext>` execution metadata while keeping public Flow APIs unchanged.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
