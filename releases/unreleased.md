# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.3`. It stays provisional until the next tag is cut.

## What is taking shape

- Reader-intent relevance for AppSurface Docs search.

## Included in the next coordinated version

### Release and docs surface

- AppSurface Docs search now hydrates MiniSearch candidates from the normalized docs payload and applies deterministic reader-intent ranking before both sidebar and full-page rendering. Exact title, path, source, alias, keyword, and entry-point matches stay protected; broad task queries prefer reader-facing guides; explicit API/internal filters override broad-task boosts; and contributor/internal docs are demoted unless the query asks for them directly.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
