# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.6`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

<!-- appsurface:unreleased-entries section="taking-shape" -->

## Included in the next coordinated version

### Release and docs surface

- Add fine-grained, redacted live harvest progress for built-in Docs parsers. The additive snapshot fields expose parser phase, source-unit counts, and nullable rolling `builtInDocumentsPerSecond`; custom `IDocHarvester` implementations remain compatible and status-only, with no migration or configuration required.
- Add release-facing changes here as they land.

<!-- appsurface:unreleased-entries section="included" -->

## Migration watch

- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.

<!-- appsurface:unreleased-entries section="migration-watch" -->
