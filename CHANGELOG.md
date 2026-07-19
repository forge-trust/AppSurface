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
- [`ForgeTrust.AppSurface.Web` health and readiness probes](./Web/ForgeTrust.AppSurface.Web/README.md#health-and-readiness-probes) now default off, avoiding health-check service registration and `/health` plus `/ready` endpoint mapping unless a host explicitly sets `WebOptions.Health.Enabled = true`; enabled probes also avoid general route-handler binding during startup, and existing probe consumers must opt in during upgrade.
- [`ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth`](./Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#responsive-placement-and-customization) now keeps its marker in normal document flow at widths up to 640 CSS pixels while preserving the fixed desktop overlay, with overflow-safe actions and documented host placement and customization guidance.
- [`ForgeTrust.RazorWire`](./Web/ForgeTrust.RazorWire/README.md#choose-who-supplies-turbo) now emits its exact Turbo 8.0.23 UMD runtime from the package and same application origin by default. Apps can instead select a validated same-origin custom path or explicitly take full host-managed ownership of Turbo sourcing and load order. The 8.0.12-to-8.0.23 update preserves RazorWire APIs and runtime-source policy while adding risk-mapped Drive, Frame, Stream, form, island, and Behavior Kit compatibility evidence.
- [`ForgeTrust.RazorWire` Form Interactions](./Web/ForgeTrust.RazorWire/Docs/form-interactions.md) now resets duplicated mark-for-removal fields to their authored inactive value instead of submitting an empty Boolean value that ASP.NET Core rejects during combined duplicate, add, and delete edits.
- [`ForgeTrust.AppSurface.Web.OpenApi`](./Web/ForgeTrust.AppSurface.Web.OpenApi/README.md) now requires `Microsoft.OpenApi` in the range `[2.7.5, 3.0.0)` and uses `Microsoft.AspNetCore.OpenApi` 10.0.9, keeping .NET 10 consumers on the supported 2.x line above the range affected by [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc) without changing OpenAPI or Scalar endpoint behavior.
- AppSurface Docs now exposes Theme Contract v1 through `AppSurfaceDocs:Theme`, adding dark-family presets, supported accent/link overrides, density/chrome controls, startup validation, and export/archive-safe resolved CSS variables.
- [`AppSurface Docs`](./Web/ForgeTrust.AppSurface.Docs/README.md#default-razor-layout-and-deliberate-host-overrides) now isolates its built-in views behind a package-specific absolute Razor layout, preventing ordinary host `_ViewStart` and `_Layout` conventions from silently removing Docs styling, client configuration, and search scripts while preserving deliberate Razor Class Library overrides.
- AppSurface Auth adds an AppHost-only `ForgeTrust.AppSurface.Auth.Aspire.Keycloak` package for real local Keycloak OIDC proof, with deterministic realm/client/user import, secret-safe OIDC projection, fixed-port/readiness diagnostics, and a focused AppHost/web/verifier sample.
- `appsurface pwa verify` can now prove real entry pages such as `/account/resume`, follow same-origin path-base-safe redirects, assert manifest values and icon declarations, decode PNG icon dimensions for CI evidence, and prove the configured AppSurface service worker is absent when offline support is disabled.
- [`ForgeTrust.AppSurface.Web`](./Web/ForgeTrust.AppSurface.Web/Docs/pwa-install.md) now composes independent offline and push capabilities into one service worker, exposes inert explicit browser registration, validates a strict privacy-safe notification payload and click destination, and keeps permission, subscription, and delivery application-owned.
- [`ForgeTrust.AppSurface.Web` PWA badging](./Web/ForgeTrust.AppSurface.Web/Docs/pwa-install.md#application-icon-badging) now exposes default-off, identical page and active-worker set/clear adapters with PathBase-aware versioned helper delivery, sanitized outcomes, and explicit server-known diagnostics while leaving push payloads and CLI verification unchanged.
- [`ForgeTrust.AppSurface.Web.Push`](./Web/ForgeTrust.AppSurface.Web.Push/README.md) adds an optional protected Web Push rail with exact outbound-origin custody, validated VAPID key rings, explicit cookie-antiforgery or bearer endpoint mapping, gesture-safe browser subscription, one-attempt encrypted sending, safe classification, and race-safe 404/410 cleanup.

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
