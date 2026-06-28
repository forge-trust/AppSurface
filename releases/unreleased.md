# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.1`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.AppSurface.Web` now owns the baseline PWA install contract in the existing Web package: app-owned
  `WebOptions.Pwa` metadata maps a manifest endpoint, MVC/Razor head tags, development diagnostics, and an explicit
  opt-in offline fallback, with `appsurface pwa verify` providing a copy-paste CLI proof for the running origin.

## Included in the next coordinated version

### Release and docs surface

- AppSurface LocalSecrets startup failures that happen before the platform command can run now report `Unavailable` with
  `local-secret-store-unavailable` instead of being classified by words in raw OS exception messages. Real locked-store
  process output still maps to `Locked`; startup diagnostics include exception type, `HResult`, and synthetic exit code
  while intentionally omitting raw exception messages, command paths, arguments, absolute paths, logical values, and
  secret values.
- AppSurface Web adds first-class PWA install support without a new package. `WebOptions.Pwa` stays disabled by default,
  requires install-critical metadata when enabled, serves `/manifest.webmanifest` as `application/manifest+json`, maps
  development-only diagnostics under `/_appsurface/pwa`, and emits no service worker unless the app explicitly configures
  an offline fallback strategy. MVC and Razor apps can add `<appsurface:pwa-head />`; custom layouts can copy equivalent
  tags from diagnostics; `appsurface pwa verify --url <origin>` checks the live metadata, icons, secure-origin posture,
  diagnostics, and opt-in service worker.
- The generated starter PWA service worker now scopes cache cleanup to the current AppSurface service-worker owner and
  reaps the earlier global AppSurface cache name without pruning unrelated origin caches or another path-mounted app.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
