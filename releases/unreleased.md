# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.5`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- [`ForgeTrust.AppSurface.Web` named canaries](../Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints)
  now include a bounded protected aggregate snapshot at `GET /_appsurface/canaries`. Operators can select registered
  canaries by exact name or durable tag, receive ordinal partial outcomes under explicit concurrency and deadline caps,
  and parse a privacy-safe envelope with fixed telemetry. The feature does not add triggers, retries, polling, readiness
  effects, or authorization-policy ownership; hosts retain those decisions.

## Migration watch

- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.
