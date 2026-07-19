# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.4`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.RazorWire` now owns a deterministic Turbo 8.0.23 default: `<rw:scripts />` emits an exact package-carried, same-origin runtime before RazorWire, while explicit custom and host-managed policies cover app-owned same-origin files or fully host-owned URL, integrity, CSP, and load-order requirements. The upgrade from 8.0.12 is API-neutral and carries an explicit upstream-risk review plus focused Drive, Frame, Stream, form, island, and Behavior Kit compatibility evidence.
- `ForgeTrust.RazorWire` Form Interactions now keeps duplicated mark-for-removal fields model-bindable by restoring the app-authored inactive value, defaulting to `false`, while identity and concurrency fields still clear. Combined duplicate, add, mark-delete, and submit workflows no longer fail with an empty Boolean value.

## Included in the next coordinated version

### Release and docs surface

- [`ForgeTrust.AppSurface.Web` health and readiness probes](../Web/ForgeTrust.AppSurface.Web/README.md#health-and-readiness-probes) are now opt-in. New hosts avoid ASP.NET Core health-check registration and `/health` plus `/ready` endpoint mapping unless `WebOptions.Health.Enabled` is explicitly set to `true`; enabled probes also avoid general route-handler binding during startup. Hosts whose deployment or monitoring infrastructure consumes those probes must enable the shared flag; paths, readiness tags, response semantics, validation, and authorization behavior are unchanged.
- [`ForgeTrust.RazorWire`](../Web/ForgeTrust.RazorWire/README.md#choose-who-supplies-turbo) replaces its implicit CDN dependency with a packaged Turbo 8.0.23 UMD asset served through Razor Class Library static assets and embedded fallbacks. `RazorWireOptions.Turbo` defaults to `Bundled`, supports a strictly validated same-origin `CustomPath`, and offers `HostManaged` for hosts that own cross-origin sourcing, integrity, CSP metadata, and parser-blocking order. Static CDN and hybrid exports materialize the bundled runtime instead of leaving a network dependency behind.

## Migration watch

- Existing hosts that consume `/health` or `/ready` must set `WebOptions.Health.Enabled = true` when upgrading.
- Remove any app-authored duplicate Turbo tag when adopting the new bundled default. CSP policies can replace the former jsDelivr allowance with `'self'`. Hosts that intentionally retain their own tag must set `RazorWireOptions.Turbo.RuntimeMode` to `HostManaged`, load Turbo before `<rw:scripts />` without `async` or `defer`, and treat versions other than 8.0.23 as host-tested compatibility choices. Turbo 8.0.23 removes upstream-deprecated `Turbo.clearCache()`, `data-turbo-cache="false"`, and legacy form polyfills; these were never AppSurface-defined APIs.
