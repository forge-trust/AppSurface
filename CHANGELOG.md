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
- Stable package release publishing now requires verified AppSurface Docs archive evidence, so release authors can prove the public docs catalog, exact tree, release manifest, and serveable files before GitHub Releases receive a stable package. The protected stable NuGet workflow also replays that docs export and archive verification before it requests the NuGet trusted publishing token.
- RazorWire export now fails artifact-producing redirects with `RWEXPORT008` when the final response leaves the configured export origin or base path before content is read or written.
- AppSurface CLI export and docs export now preserve RazorWire `RWEXPORT008` redirect-boundary checks by disabling automatic redirects on the shared `ExportEngine` HTTP client.
- RazorWire export now rejects generated artifact paths and release archive entries that cross the physical output-root boundary through symlinks, junctions, reparse points, or lexical escapes with `RWEXPORT009`.
- AppSurface LocalSecrets file fallback now creates missing Unix fallback directories with `0700`, writes or repairs JSON files with `0600`, fails closed on loose parent directories and unsafe path shapes, and lets `appsurface secrets doctor --store-file` report ready, repaired, degraded, or unsupported posture without printing secret values.
- AppSurface LocalSecrets now hardens Linux `secret-tool` resolution by using trusted system candidates or an explicit absolute override instead of executing the first PATH-discovered command.
- `ForgeTrust.AppSurface.Web` now fails startup for AppSurface-managed production CORS when `CorsOptions.AllowedOrigins` contains the literal origin wildcard `*`; replace it with explicit origins such as `https://app.example.com`, keep permissive all-origin behavior to Development through `EnableAllOriginsInDevelopment`, or register host-owned ASP.NET Core CORS for intentionally public wildcard APIs.
- RazorWire hybrid islands now block inline `data:` module specifiers and protocol-relative `//...` module URLs; serve client modules from relative, root-relative, same-origin, explicit HTTPS, or import-map specifiers instead.

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
