# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.2`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.RazorWire` Behavior Kit now exposes root-scoped registrations for DOM enhancement and page-lifecycle registrations for logical browser visits, including Skoolit-style PWA display-mode telemetry without fake body selectors or app-owned Turbo listeners.

## Included in the next coordinated version

### Release and docs surface

- [`appsurface coverage run`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) can now start long-running non-exclusive test projects earlier with `--schedule longest-first`. It reuses prior `timings.json` data when available, preserves integration and Playwright projects as exclusive barriers, supports explicit priority projects, fails invalid explicit timing or priority input before tests run, warns and preserves input order for unmeasured projects when inferred prior timings are missing or unusable, and keeps artifact names stable.
- AppSurface Docs adds Theme Contract v1. Hosts can configure `AppSurfaceDocs:Theme` with dark-family presets (`AppSurfaceDark` and `GraphiteDark`), accent/link color overrides, and density/chrome controls. The package validates enum values, CSS hex colors, null nested theme sections, and contrast-sensitive overrides at startup, emits resolved root attributes and CSS variables into rendered HTML, and freezes those variables into static exports and published archives instead of adding a dynamic themed CSS asset route.
- Documentation and release authoring guidance now require concept links instead of mention-only prose: start with adopter outcomes, explain internal feature labels in plain language, link named packages, concepts, workflows, diagnostics, guides, examples, and CLI commands to their canonical docs, and keep maintainer evidence after the adoption path.
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

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
