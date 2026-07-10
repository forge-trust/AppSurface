# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.2`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- AppSurface Docs adds Theme Contract v1. Hosts can configure `AppSurfaceDocs:Theme` with dark-family presets (`AppSurfaceDark` and `GraphiteDark`), accent/link color overrides, and density/chrome controls. The package validates enum values, CSS hex colors, null nested theme sections, and contrast-sensitive overrides at startup, emits resolved root attributes and CSS variables into rendered HTML, and freezes those variables into static exports and published archives instead of adding a dynamic themed CSS asset route.
- Documentation and release authoring guidance now require concept links instead of mention-only prose: start with adopter outcomes, explain internal feature labels in plain language, link named packages, concepts, workflows, diagnostics, guides, examples, and CLI commands to their canonical docs, and keep maintainer evidence after the adoption path.
- AppSurface DevAuth now centralizes its environment activation policy. DevAuth remains Development-by-default, but
  package consumers can explicitly add local/proof environment names through `AllowedEnvironmentNames`; the marker
  self-suppresses outside allowed environments and mapped control/mutation endpoints stay fail-closed.
- Split stable and prerelease NuGet publish tag triggers so prerelease tags no longer start the stable publish workflow before the prerelease gate.
- AppSurface DevAuth marker overlays now start collapsed by default while keeping the active fake persona visible, and
  `AppSurfaceDevAuthMarkerOptions.StartExpanded` lets local proof pages opt back into immediate persona controls.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
