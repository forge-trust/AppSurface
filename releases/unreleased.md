# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.1`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.RazorWire` now includes passive auth projection TagHelpers over `AppSurfaceAuthResult`, and `ForgeTrust.RazorWire.Auth.AspNetCore` connects those helpers to host-owned ASP.NET Core policies without making RazorWire own authentication or endpoint enforcement.
- `ForgeTrust.AppSurface.Web` now owns the baseline PWA install contract in the existing Web package: app-owned
  `WebOptions.Pwa` metadata maps a manifest endpoint, MVC/Razor head tags, development diagnostics, and an explicit
  opt-in offline fallback, with `appsurface pwa verify` providing a copy-paste CLI proof for the running origin.

## Included in the next coordinated version

### Release and docs surface

- `ForgeTrust.RazorWire` documents auth projection helpers, including `rw:auth-view`, `rw:auth-gate`, `rw:permission-gate`, `rw:login-link`, and `rw:logout-button`, with a paired endpoint-enforcement example and DevAuth marker guidance that keeps local fake personas separate from reusable UI projection.
- AppSurface LocalSecrets startup failures that happen before the platform command can run now report `Unavailable` with
  `local-secret-store-unavailable` instead of being classified by words in raw OS exception messages. Real locked-store
  process output still maps to `Locked`; startup diagnostics include exception type, `HResult`, and synthetic exit code
  while intentionally omitting raw exception messages, command paths, arguments, absolute paths, logical values, and
  secret values.
- Coverage gate tests now create their disposable Git fixture commits with commit signing disabled for the child process,
  so local solution coverage runs no longer fail when a developer has SSH commit signing enabled.
- AppSurface Web adds first-class PWA install support without a new package. `WebOptions.Pwa` stays disabled by default,
  requires install-critical metadata when enabled, serves `/manifest.webmanifest` as `application/manifest+json`, maps
  development-only diagnostics under `/_appsurface/pwa`, and emits no service worker unless the app explicitly configures
  an offline fallback strategy. MVC and Razor apps can add `<appsurface:pwa-head />`; custom layouts can copy equivalent
  tags from diagnostics; `appsurface pwa verify --url <origin>` checks the live metadata, icons, secure-origin posture,
  diagnostics, and opt-in service worker.
- The generated starter PWA service worker now scopes cache cleanup to the current AppSurface service-worker owner and
  reaps the earlier global AppSurface cache name without pruning unrelated origin caches or another path-mounted app.
- Stable package release publishing now gates on verified AppSurface Docs archive evidence. Release authors pass the
  staged docs `versions.json` and trusted archive root into `appsurface-release check` or `publish`; the tool verifies the
  selected public catalog entry, exact tree path, pinned release-manifest digest, route manifest safety, and every
  serveable file before stable GitHub Release publishing can continue. The protected stable NuGet workflow now repeats the
  export and archive verification against the checked-in release evidence before it can request the NuGet trusted publishing
  token.
- AppSurface Auth now has a Start Here adoption ladder that helps package consumers choose between host-owned
  ASP.NET Core auth, Auth core, Auth.AspNetCore, DevAuth, OIDC, Auth.Testing, and RazorWire-facing proof surfaces
  without implying AppSurface owns production identity providers, policies, user stores, or enforcement.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
