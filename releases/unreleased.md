# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.6`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.
- `ForgeTrust.AppSurface.Docs` adds the fixed `AppSurfaceLight` preset. Hosts can select a complete light Docs shell and, when needed, override only the existing validated accent and link roles. The preset emits its resolved token graph and `color-scheme: light` before package stylesheets, freezes that payload into static exports, and does not enable visitor-controlled appearance storage or scripts. See the [fixed light configuration reference](../Web/ForgeTrust.AppSurface.Docs/README.md#fixed-appsurfacelight-configuration) for the light-preset recipe and when a shared theme pair or full layout override is the better fit.

<!-- appsurface:unreleased-entries section="taking-shape" -->

## Included in the next coordinated version

### Release and docs surface

- Add release-facing changes here as they land.

<!-- appsurface:unreleased-entries section="included" -->

## Migration watch

- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.

<!-- appsurface:unreleased-entries section="migration-watch" -->
