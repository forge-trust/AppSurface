# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.2`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.RazorWire` Behavior Kit now exposes root-scoped registrations for DOM enhancement and page-lifecycle registrations for logical browser visits, including Skoolit-style PWA display-mode telemetry without fake body selectors or app-owned Turbo listeners.

## Included in the next coordinated version

### Release and docs surface

- `ForgeTrust.AppSurface.Web.OpenApi` now uses `Microsoft.AspNetCore.OpenApi` 10.0.9 and directly requires `Microsoft.OpenApi` in the range `[2.7.5, 3.0.0)`, keeping .NET 10 consumers on the supported 2.x line above the range affected by [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc) while preserving existing OpenAPI and Scalar APIs and endpoint behavior.
- [`appsurface coverage run`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) can now start long-running non-exclusive test projects earlier with `--schedule longest-first`. It reuses prior `timings.json` data when available, preserves integration and Playwright projects as exclusive barriers, supports explicit priority projects, fails invalid explicit timing or priority input before tests run, warns and preserves input order for unmeasured projects when inferred prior timings are missing or unusable, and keeps artifact names stable.
- `ForgeTrust.AppSurface.Web` adds preview named canary evaluation: applications register typed, application-owned proof
  evaluators and explicitly map one fixed protected route family. The default adapter returns `200` only for `pass` and
  `503` for completed non-pass states; authenticated diagnostic consumers can opt into status-preserving `AlwaysOk`.
  Authorization remains host-owned and fail-closed, inputs and failures are redacted, reserved-route conflicts fail at
  startup, and triggering, retries, polling, aggregation, and `/ready` behavior remain outside this primitive.
- AppSurface Docs adds Theme Contract v1. Hosts can configure `AppSurfaceDocs:Theme` with dark-family presets (`AppSurfaceDark` and `GraphiteDark`), accent/link color overrides, and density/chrome controls. The package validates enum values, CSS hex colors, null nested theme sections, and contrast-sensitive overrides at startup, emits resolved root attributes and CSS variables into rendered HTML, and freezes those variables into static exports and published archives instead of adding a dynamic themed CSS asset route.
- Documentation and release authoring guidance now require concept links instead of mention-only prose: start with adopter outcomes, explain internal feature labels in plain language, link named packages, concepts, workflows, diagnostics, guides, examples, and CLI commands to their canonical docs, and keep maintainer evidence after the adoption path.
- The repository coverage script now runs the same aggregate and pull-request patch thresholds as CI's coverage lane. CI supplies `HEAD^1` for synthetic pull-request merge checkouts, while local runs compare against `origin/main` by default and baseline jobs can omit patch thresholds explicitly.
- AppSurface DevAuth now centralizes its environment activation policy. DevAuth remains Development-by-default, but
  package consumers can explicitly add local/proof environment names through `AllowedEnvironmentNames`; the marker
  self-suppresses outside allowed environments and mapped control/mutation endpoints stay fail-closed.
- Split stable and prerelease NuGet publish tag triggers so prerelease tags no longer start the stable publish workflow before the prerelease gate.
- AppSurface DevAuth marker overlays now start collapsed by default while keeping the active fake persona visible, and
  `AppSurfaceDevAuthMarkerOptions.StartExpanded` lets local proof pages opt back into immediate persona controls.
- Add `ForgeTrust.AppSurface.Auth.Aspire.Keycloak`, an AppHost-only real local OIDC proof package that builds on
  the official Aspire Keycloak hosting integration, generates deterministic realm/client/user import JSON, projects
  only safe OIDC settings into a paired `Auth.AspNetCore.Oidc` web proof, adds fixed-port/readiness diagnostics, and
  keeps Keycloak/Aspire hosting dependencies out of runtime web packages.
- `ForgeTrust.RazorWire` documents `<rw:scripts behavior-kit="true" />`, the queue-backed `window.RazorWire.behaviors` stub, root `register(...)`, page-lifecycle `registerLifecycle(...)`, stable diagnostics, and guidance for choosing built-in managers, root behaviors, lifecycle behaviors, islands, or app-owned JavaScript.
- `appsurface pwa verify` now supports route-shaped readiness evidence for real app entry pages. Apps can verify `--base-url` plus `--entry-path`, follow same-origin redirects that stay under the verified path base, assert manifest `start_url`, `scope`, `display`, colors, and icon declarations, decode PNG icon dimensions, write schema v2 JSON evidence, and prove that the configured AppSurface service worker is not reachable when offline support is disabled.
- AppSurface Web PWA diagnostics now expose the configured service-worker path separately from the active offline service-worker path, so verifier evidence can distinguish "offline disabled and no worker mapped" from "offline enabled with worker/fallback endpoints."
- [`ForgeTrust.AppSurface.Web` PWA support](../Web/ForgeTrust.AppSurface.Web/Docs/pwa-install.md) now activates one generated service worker from independent offline or push options. Push-only apps get no cache or fetch interception; combined apps retain the narrow offline strategy. The package adds an inert `window.AppSurface.Pwa.register()` helper, a strict versioned notification/click adapter, custom-handler imports, PathBase-safe worker metadata, and value-free browser diagnostics without requesting permission, creating subscriptions, or owning delivery.

## Migration watch

- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.
- `Pwa.Enabled` now controls install metadata only. Apps may activate the shared worker with `Pwa.Offline.Enabled` or `Pwa.Push.Enabled`; existing `Pwa.Offline.ServiceWorkerPath` assignments remain compatible, while new code should use `Pwa.Worker.ServiceWorkerPath`.
- Do not remove or move an already registered service-worker endpoint in one deployment. First ship unregister/replacement cleanup from the old path, let clients receive it, and only then stop mapping that path. See the [PWA migration guidance](../Web/ForgeTrust.AppSurface.Web/Docs/pwa-install.md#migration-and-pitfalls).
