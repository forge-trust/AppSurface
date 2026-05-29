# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.1`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- Add release-facing changes here.

### Console and CLI polish

- RazorWire CLI process execution now follows an explicit reliability contract for one-shot commands and launched target apps. No action is expected: command names, flags, defaults, and export semantics are unchanged. Target-app failures that were previously hidden behind startup or readiness timeouts may now surface earlier with captured output and recovery guidance.

### Dependency maintenance

- The central .NET dependency set now carries `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Hosting`, and `Microsoft.Extensions.Logging.Console` `10.0.8`, while the ABP benchmark host now uses ABP `10.4.0`; solution lock files were regenerated so locked restore sees the same graph in CI and local development.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
