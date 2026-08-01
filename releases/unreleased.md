# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.5`. It stays provisional until the next tag is cut.

## What is taking shape

- [`appsurface canary poll`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-canary-poll) now turns one existing protected named-canary evaluation into a read-only, caller-owned deployment proof. It validates an application base URL and environment-only marker/credential sources before dispatch, preserves path bases, disables redirects and hidden client timeouts, parses the preview compatibility core by property name, and gives `pending` plus recoverable transport failures an explicit bounded polling lifecycle. Safe text and JSON outcomes expose only the canary name, attempts, elapsed time, diagnostic, bounded reason/summary, next action, and documentation URL; raw credentials, markers, headers, URLs, and response bodies never render. `pass` alone exits `0`; semantic canary failures, protocol failures, transient exhaustion, deadlines, and cancellation use stable nonzero exits. The tool remains a caller rail, not a canary trigger, readiness probe, deployment controller, identity broker, or composite Action.

## Included in the next coordinated version

### Release and docs surface

- Add release-facing changes here.

## Migration watch

- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.
