# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.2`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.RazorWire` Behavior Kit now exposes root-scoped registrations for DOM enhancement and page-lifecycle registrations for logical browser visits, including Skoolit-style PWA display-mode telemetry without fake body selectors or app-owned Turbo listeners.

## Included in the next coordinated version

### Release and docs surface

- Documentation and release authoring guidance now require concept links instead of mention-only prose: start with adopter outcomes, explain internal feature labels in plain language, link named packages, concepts, workflows, diagnostics, guides, examples, and CLI commands to their canonical docs, and keep maintainer evidence after the adoption path.
- AppSurface DevAuth now centralizes its environment activation policy. DevAuth remains Development-by-default, but
  package consumers can explicitly add local/proof environment names through `AllowedEnvironmentNames`; the marker
  self-suppresses outside allowed environments and mapped control/mutation endpoints stay fail-closed.
- Split stable and prerelease NuGet publish tag triggers so prerelease tags no longer start the stable publish workflow before the prerelease gate.
- AppSurface DevAuth marker overlays now start collapsed by default while keeping the active fake persona visible, and
  `AppSurfaceDevAuthMarkerOptions.StartExpanded` lets local proof pages opt back into immediate persona controls.
- `ForgeTrust.RazorWire` documents `<rw:scripts behavior-kit="true" />`, the queue-backed `window.RazorWire.behaviors` stub, root `register(...)`, page-lifecycle `registerLifecycle(...)`, stable diagnostics, and guidance for choosing built-in managers, root behaviors, lifecycle behaviors, islands, or app-owned JavaScript.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
