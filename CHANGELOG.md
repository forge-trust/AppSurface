# Changelog

This changelog is the compact release ledger for AppSurface. The monorepo ships in unison, so each tagged version covers packages, CLI tooling, examples, and docs-facing behavior from this repository together.

## Reading guide

- `Unreleased` tracks the next coordinated version and points to the living release note.
- Future tagged sections will use the shape `## x.y.z - YYYY-MM-DD`.
- Every tagged section will link to a matching narrative release note in [`releases/`](./releases/README.md).
- Breaking or behavior-changing updates must record migration guidance here and in the matching release note.

## Unreleased

- Narrative release note: [Upcoming release note](./releases/unreleased.md)
- Upgrade policy: [Pre-1.0 upgrade policy](./releases/upgrade-policy.md)
- Authoring workflow: [Release authoring checklist](./releases/release-authoring-checklist.md)
- Stable package release publishing now requires AppSurface Docs archive evidence that has been verified, so release authors can prove
  the public docs catalog, exact tree, release manifest, and serveable files match before GitHub Releases receive a
  stable package. The protected stable NuGet workflow also replays that docs export and archive verification before it requests
  the NuGet trusted publishing token.
- RazorWire now renders passive AppSurface auth-result UI states with `rw:auth-view`, gate, login-link, and logout-button helpers, plus an ASP.NET Core adapter package that delegates to host-owned AppSurface policy evaluation.
- RazorWire static and hybrid export now force auth helpers into a static-safe anonymous projection and fail with `RWEXPORT010` before writing protected auth content, auth diagnostics, DevAuth markers, or unsafe auth metadata to generated text artifacts.
- AppSurface LocalSecrets startup failures before platform commands run now report `local-secret-store-unavailable` with exception type, `HResult`, and synthetic exit code only; run `appsurface secrets doctor` for the same namespace to diagnose missing tools or headless sessions without leaking raw OS exception messages.
- `ForgeTrust.AppSurface.Web` now owns first-class PWA install metadata: enable `WebOptions.Pwa` to serve a manifest, emit Razor head metadata, expose development diagnostics, and opt into a starter offline fallback, then prove the running app with `appsurface pwa verify`.
- AppSurface Auth adds a Start Here adoption ladder for choosing between host-owned ASP.NET Core auth, Auth core, Auth.AspNetCore, DevAuth, OIDC, Auth.Testing, and RazorWire-facing proof surfaces while keeping production identity providers, policies, user stores, and enforcement host-owned.

## 0.2.0-preview.1 - 2026-06-28

- Narrative release note: [v0.2.0-preview.1](./releases/v0.2.0-preview.1.md)
- Release manifest: `releases/v0.2.0-preview.1.release.json`
- Release evidence bundle: `releases/v0.2.0-preview.1.evidence.json`

## 0.1.0-rc.4 - 2026-06-16

- Narrative release note: [v0.1.0-rc.4](./releases/v0.1.0-rc.4.md)
- Release manifest: `releases/v0.1.0-rc.4.release.json`
- Release evidence bundle: `releases/v0.1.0-rc.4.evidence.json`

## 0.1.0-rc.3 - 2026-06-08

- Narrative release note: [v0.1.0-rc.3](./releases/v0.1.0-rc.3.md)
- Release manifest: `releases/v0.1.0-rc.3.release.json`
- Release evidence bundle: `releases/v0.1.0-rc.3.evidence.json`

## 0.1.0-rc.2 - 2026-06-03

- Narrative release note: [v0.1.0-rc.2](./releases/v0.1.0-rc.2.md)
- Release manifest: `releases/v0.1.0-rc.2.release.json`

## 0.1.0-rc.1 - 2026-05-29

- Narrative release note: [v0.1.0-rc.1](./releases/v0.1.0-rc.1.md)
- Release manifest: `releases/v0.1.0-rc.1.release.json`
