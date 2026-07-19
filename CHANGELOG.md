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
- [`ForgeTrust.AppSurface.Web` named canaries](./Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints) now return an additive bounded evidence envelope with required `name`, `ready`, and `status`, optional typed and predeclared detail fields, marker fingerprint correlation, and fixed privacy-bounded completion telemetry. Existing status-only evaluator construction remains source-compatible; polling, retries, triggers, aggregation, and readiness coupling remain outside this preview primitive.
- [`ForgeTrust.AppSurface.Web` health and readiness probes](./Web/ForgeTrust.AppSurface.Web/README.md#health-and-readiness-probes) now default off, avoiding health-check service registration and `/health` plus `/ready` endpoint mapping unless a host explicitly sets `WebOptions.Health.Enabled = true`; enabled probes also avoid general route-handler binding during startup, and existing probe consumers must opt in during upgrade.

## 0.2.0-preview.4 - 2026-07-18

- Narrative release note: [v0.2.0-preview.4](./releases/v0.2.0-preview.4.md)
- Release manifest: `releases/v0.2.0-preview.4.release.json`
- Release evidence bundle: `releases/v0.2.0-preview.4.evidence.json`

## 0.2.0-preview.3 - 2026-07-17

- Narrative release note: [v0.2.0-preview.3](./releases/v0.2.0-preview.3.md)
- Release manifest: `releases/v0.2.0-preview.3.release.json`
- Release evidence bundle: `releases/v0.2.0-preview.3.evidence.json`

## 0.2.0-preview.2 - 2026-07-02

- Narrative release note: [v0.2.0-preview.2](./releases/v0.2.0-preview.2.md)
- Release manifest: `releases/v0.2.0-preview.2.release.json`
- Release evidence bundle: `releases/v0.2.0-preview.2.evidence.json`

## 0.2.0-preview.1 - 2026-06-28

- Narrative release note: [v0.2.0-preview.1](./releases/v0.2.0-preview.1.md)
- Release manifest: `releases/v0.2.0-preview.1.release.json`
- Release evidence bundle: `releases/v0.2.0-preview.1.evidence.json`

## 0.1.0 - 2026-06-27

- Narrative release note: [v0.1.0](./releases/v0.1.0.md)
- Release manifest: `releases/v0.1.0.release.json`
- Release evidence bundle: `releases/v0.1.0.evidence.json`
- Release-candidate history:
  - `0.1.0-rc.1` - 2026-05-29
  - `0.1.0-rc.2` - 2026-06-03
  - `0.1.0-rc.3` - 2026-06-08
  - `0.1.0-rc.4` - 2026-06-16
