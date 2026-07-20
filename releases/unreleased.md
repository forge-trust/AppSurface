# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.4`. It stays provisional until the next tag is cut.

## What is taking shape

- `appsurface coverage run` now explains long-running orchestration with per-operation heartbeats and no-observable-progress classification. The default remains non-terminating: heartbeats render every 30 seconds and `warn` records an incident after 10 minutes without observable progress.

## Included in the next coordinated version

### Release and docs surface

- Add `--watchdog warn|fail|off`, `--heartbeat-interval`, and `--no-progress-timeout` to [`coverage run`](../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-watchdog). Fail mode requests bounded cleanup of discoverable active process trees through .NET [`Process.Kill(entireProcessTree: true)`](https://learn.microsoft.com/dotnet/api/system.diagnostics.process.kill?view=net-10.0), emits `ASCOV121`, and exits `124`; artifact write degradation is reported as `ASCOV122` without replacing the primary run outcome. Cleanup is bounded rather than an absolute descendant guarantee: detached, re-parented, or permission-inaccessible descendants remain outside the platform API's proof boundary.
- Add a bounded, privacy-minimized `coverage-watchdog.json` incident artifact plus a runner-scoped temporary fallback for incidents that occur before the configured output directory can be safely claimed. The fallback run directory uses user-only permissions on Unix. Publishing remains an explicit operator choice because normalized repository-relative names and command metadata can still be sensitive.
- Keep per-test hang detection, test identity, and dump collection with [VSTest `--blame-hang`](https://learn.microsoft.com/dotnet/core/tools/dotnet-test-vstest#--blame-hang) or [Microsoft.Testing.Platform crash and hang dumps](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-crash-hang-dumps). The AppSurface watchdog observes orchestration phases and output bytes; it does not diagnose test-host internals. The guide includes a warn-only fixture recipe with a first heartbeat target below 15 seconds and warning evidence within two minutes.
- [`ForgeTrust.AppSurface.Web` named canary evaluation](../Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints) is now available in preview: applications register typed, application-owned proof
  evaluators and explicitly map one fixed protected route family. Completed evaluations add required `name`, `ready`, and
  `status` fields plus optional typed evidence, a marker fingerprint, and up to 16 registration-declared bounded details.
  Existing `AppSurfaceCanaryResult(status)` construction remains source-compatible. Consumers must tolerate optional
  omissions, unknown fields, and property reordering; the contract remains preview until the
  [#625 caller](https://github.com/forge-trust/AppSurface/issues/625) proves polling and operator actions.
  The canonical guide includes a complete forwarding evaluator, a contrasting migration fixture, copyable
  `System.Text.Json` and `jq` consumers, the #623-to-#624 upgrade contract, and separate under-5-minute authenticated-host
  and under-15-minute cold-path onboarding targets.
  The package emits fixed completion event `62401` with typed evaluation and host facts only; marker, reason, summary,
  correlation, and custom detail values remain response-only. Bounds and declarations constrain shape but do not classify
  or redact application-authored text. The default adapter still returns `200` only for `pass` and `503` for completed
  non-pass states; authenticated diagnostic consumers can opt into status-preserving `AlwaysOk`. Authorization remains
  host-owned and fail-closed, and triggering, retries, polling, aggregation, health-check adaptation, and `/ready` behavior
  remain outside this primitive.
- [`ForgeTrust.AppSurface.Web` health and readiness probes](../Web/ForgeTrust.AppSurface.Web/README.md#health-and-readiness-probes) are now opt-in. New hosts avoid ASP.NET Core health-check registration and `/health` plus `/ready` endpoint mapping unless `WebOptions.Health.Enabled` is explicitly set to `true`; enabled probes also avoid general route-handler binding during startup. Hosts whose deployment or monitoring infrastructure consumes those probes must enable the shared flag; paths, readiness tags, response semantics, validation, and authorization behavior are unchanged.

## Migration watch

- Existing runs can now produce periodic console output and, after a warning boundary, an owned watchdog artifact. Use `--heartbeat-interval 0 --watchdog off` for the previous silent, non-classifying behavior, or tune `--no-progress-timeout` for intentionally quiet suites.
- Existing hosts that consume `/health` or `/ready` must set `WebOptions.Health.Enabled = true` when upgrading.
