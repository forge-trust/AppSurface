# Changelog

This changelog is the compact release ledger for AppSurface. The monorepo ships in unison, so each tagged version covers packages, CLI tooling, examples, and docs-facing behavior from this repository together.

## Reading guide

- `Unreleased` tracks the next coordinated version and points to the living release note.
- Future tagged sections will use the shape `## x.y.z - YYYY-MM-DD`.
- Every tagged section will link to a matching narrative release note in [`releases/`](./releases/README.md).
- Breaking or behavior-changing updates must record migration guidance here and in the matching release note.

## Unreleased

- Narrative release note: [Upcoming release note](./releases/unreleased.md)
- Upgrade policy: [Pre-1.0 upgrade policy](./releases/upgrade-policy.md)
- Authoring workflow: [Release authoring checklist](./releases/release-authoring-checklist.md)
- `ForgeTrust.AppSurface.Auth` now defines durable external-subject to app-user-id mapping contracts, with Auth core staying free of user-store, ASP.NET Core, OIDC, persistence, and tenant-authority ownership.
- `ForgeTrust.AppSurface.Auth.AspNetCore` can now protect Minimal API endpoints with host-owned ASP.NET Core policies while returning AppSurface-shaped ProblemDetails JSON for API callers.
- AppSurface Config can now compare sanitized audit reports, render deterministic same-host or captured-snapshot evidence, and expose command-framework-agnostic diff workflows with display-safe failures.
- RazorWire export now fails artifact-producing redirects with `RWEXPORT008` when the final response leaves the configured export origin or base path before content is read or written.
- AppSurface LocalSecrets platform-backed stores now validate indexed names against live stored values during `appsurface secrets list`, prune stale names after successful repair, and let `appsurface secrets delete KEY` clean stale indexed names whose values are already gone.
- AppSurface Flow now uses value-type execution contexts and deeper benchmark coverage to reduce and track synchronous in-memory runner allocation overhead.
- `ForgeTrust.AppSurface.Web` now fails startup for AppSurface-managed production CORS when `CorsOptions.AllowedOrigins` contains the literal origin wildcard `*`; replace it with explicit origins such as `https://app.example.com`, keep permissive all-origin behavior to Development through `EnableAllOriginsInDevelopment`, or register host-owned ASP.NET Core CORS for intentionally public wildcard APIs.

## 0.1.0-rc.4 - 2026-06-16

- Narrative release note: [v0.1.0-rc.4](./releases/v0.1.0-rc.4.md)
- Release manifest: `releases/v0.1.0-rc.4.release.json`
- Release evidence bundle: `releases/v0.1.0-rc.4.evidence.json`

## 0.1.0-rc.3 - 2026-06-08

- Narrative release note: [v0.1.0-rc.3](./releases/v0.1.0-rc.3.md)
- Release manifest: `releases/v0.1.0-rc.3.release.json`
- Release evidence bundle: `releases/v0.1.0-rc.3.evidence.json`

## 0.1.0-rc.2 - 2026-06-03

- Narrative release note: [v0.1.0-rc.2](./releases/v0.1.0-rc.2.md)
- Release manifest: `releases/v0.1.0-rc.2.release.json`

## 0.1.0-rc.1 - 2026-05-29

- Narrative release note: [v0.1.0-rc.1](./releases/v0.1.0-rc.1.md)
- Release manifest: `releases/v0.1.0-rc.1.release.json`
