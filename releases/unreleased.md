# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.4`. It stays provisional until the next tag is cut.

## What is taking shape

- Sanitized AppSurface Config audit diffs for comparing captured runtime configuration reports.

## Included in the next coordinated version

### Release and docs surface

- `examples/auth-web-razorwire-proof` now gives package adopters a five-minute browser proof for `ForgeTrust.AppSurface.Auth.AspNetCore`: one host-owned `OperatorsOnly` policy drives both a Minimal API response and a RazorWire-facing rendered state while all fake auth and persona switching stays sample-local.
- AppSurface Config now exposes a sanitized config audit diff surface. `ConfigAuditReportDiffer` compares two existing `ConfigAuditReport` snapshots without re-resolving providers, `ConfigAuditDiffTextRenderer` renders deterministic same-host or captured-snapshot evidence with redaction uncertainty called out, and `ConfigAuditDiffCommandRunner` gives apps command-framework-agnostic same-host and captured JSON workflows with display-safe problem/cause/fix/docs-link failures.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
