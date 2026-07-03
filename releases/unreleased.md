# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.2`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- AppSurface Docs adds Theme Contract v1. Hosts can configure `AppSurfaceDocs:Theme` with dark-family presets (`AppSurfaceDark` and `GraphiteDark`), accent/link color overrides, and density/chrome controls. The package validates enum values, CSS hex colors, null nested theme sections, and contrast-sensitive overrides at startup, emits resolved root attributes and CSS variables into rendered HTML, and freezes those variables into static exports and published archives instead of adding a dynamic themed CSS asset route.
- Split stable and prerelease NuGet publish tag triggers so prerelease tags no longer start the stable publish workflow before the prerelease gate.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
