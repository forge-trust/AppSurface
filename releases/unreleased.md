# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.4`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.RazorWire` now owns a deterministic Turbo 8.0.23 default: `<rw:scripts />` emits an exact package-carried, same-origin runtime before RazorWire, while explicit custom and host-managed policies cover app-owned same-origin files or fully host-owned URL, integrity, CSP, and load-order requirements. The upgrade from 8.0.12 is API-neutral and carries an explicit upstream-risk review plus focused Drive, Frame, Stream, form, island, and Behavior Kit compatibility evidence.
- `ForgeTrust.RazorWire` Form Interactions now keeps duplicated mark-for-removal fields model-bindable by restoring the app-authored inactive value, defaulting to `false`, while identity and concurrency fields still clear. Combined duplicate, add, mark-delete, and submit workflows no longer fail with an empty Boolean value.

## Included in the next coordinated version

### Release and docs surface

- [`appsurface coverage run`](../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-driver-selection) now defaults to the
  VSTest `coverlet.collector` driver, validates each selected project's effective test runner and direct package
  references before cleanup or execution, and normalizes one validated Cobertura attachment per invocation into the
  stable artifact path. Native Microsoft Testing Platform projects fail preflight because they require `coverlet.MTP`;
  `--coverage-driver msbuild` remains available only as an explicit compatibility path and emits a reliability warning.
- The [ASP.NET Core DevAuth example](../examples/auth-aspnetcore-dev-auth/README.md#what-the-verifier-proves) now has deterministic, staged startup proof: synchronous build failures stop immediately, child exits and Kestrel readiness are observed separately, a child-owned listening record gates the real-loopback HTTP workflow, and cleanup targets only the recorded child. A child-scoped standard .NET host setting avoids configuration-reload stalls in restricted file-watcher environments without changing normal example or consumer behavior. Focused in-process host coverage complements rather than replaces the real-socket verifier, and failures preserve only bounded, sanitized, allowlisted evidence. This is a contributor-experience correction; it adds no package API, package or production-host runtime behavior, package version, or release implication.
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
- [`ForgeTrust.RazorWire`](../Web/ForgeTrust.RazorWire/README.md#choose-who-supplies-turbo) upgrades its package-owned Turbo UMD payload from 8.0.12 to 8.0.23 while preserving the existing `Bundled`, same-origin `CustomPath`, and `HostManaged` runtime-source contract. Static CDN and hybrid exports continue to materialize the exact bundled runtime.

## Migration watch

- Existing `appsurface coverage run` consumers that reference `coverlet.msbuild` must replace it with
  `coverlet.collector`, or explicitly pass `--coverage-driver msbuild` while completing the migration. The command never
  silently falls back between drivers.
- Existing hosts that consume `/health` or `/ready` must set `WebOptions.Health.Enabled = true` when upgrading.
- Hosts using `HostManaged` with a Turbo version other than 8.0.23 own compatibility testing for that version. Turbo 8.0.23 removes upstream-deprecated `Turbo.clearCache()`, `data-turbo-cache="false"`, and legacy form polyfills; these were never AppSurface-defined APIs.
