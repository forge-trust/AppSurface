# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.1`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- AppSurface LocalSecrets startup failures that happen before the platform command can run now report `Unavailable` with
  `local-secret-store-unavailable` instead of being classified by words in raw OS exception messages. Real locked-store
  process output still maps to `Locked`; startup diagnostics include exception type, `HResult`, and synthetic exit code
  while intentionally omitting raw exception messages, command paths, arguments, absolute paths, logical values, and
  secret values.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
