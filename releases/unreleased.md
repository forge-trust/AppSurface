# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.1`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- Add release-facing changes here.

### Dependency maintenance

- The central .NET dependency set now carries `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Hosting`, and `Microsoft.Extensions.Logging.Console` `10.0.8`, while the ABP benchmark host now uses ABP `10.4.0`; solution lock files were regenerated so locked restore sees the same graph in CI and local development.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
