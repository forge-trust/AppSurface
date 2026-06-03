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
- AppSurface Flow is now a first-class package family with typed long-running process contracts, local in-memory execution, and a passive Durable Task adapter boundary.
- AppSurface Docs exact release archives now emit catalog-pinned release manifests, and runtime archive serving requires verified archive bytes before mounting public release HTML, JavaScript, CSS, SVG, and search payloads.
- Behavior change: AppSurface Docs configured branding directories no longer serve SVG by default; trusted operators must set `AppSurfaceDocs:Identity:BrandingAssets:AllowSvgAssets=true` before AppSurface Docs serves SVG files from that directory.
- Behavior change: AppSurface Docs `AppSurfaceDocs:Harvest:Health:ExposeRoutes=Always` continues to expose harvest health routes outside Development, but live harvest progress streaming now also requires a custom host-owned `IRazorWireChannelAuthorizer`.
- AppSurface Docs version catalogs now require trusted-root-relative `exactTreePath` values under `AppSurfaceDocs:Versioning:TrustedReleaseRootPath`; migrate old absolute tree paths by moving the parent into the trusted root setting and keeping catalog entries relative.
- AppSurface Docs Markdown harvesting now skips oversized Markdown bodies before read/Markdig parse and ignores oversized `.md.yml` / `.md.yaml` sidecars before YAML parse, emitting visible warning diagnostics while otherwise healthy snapshots remain healthy by default.
- AppSurface Docs C# harvesting now applies a parser-input byte budget before UTF-8 decoding and Roslyn parsing, skipping oversized source with `appsurfacedocs.csharp.file_too_large` while healthy sibling API docs continue harvesting.
- AppSurface Docs JavaScript harvesting now skips symlinks, junctions, and other reparse points before explicit include reads, include-root traversal, and recursive child descent. Configured JavaScript link includes emit `appsurfacedocs.javascript.reparse_point_skipped` with redacted recovery guidance and block strict JavaScript health when JavaScript participates.
- Test fixture paths now use a shared path-under-base helper and repository policy test, so dynamic test fixture paths must stay under their intended root unless a reasoned allowlist entry documents intentional platform-path behavior.
- RazorWire stream endpoints now reject malformed channels with `400`, deny unauthorized channels with `403`, and return `429` when per-process live-channel or subscription admission limits are exhausted before hub subscriber state is allocated.
- RazorWire and AppSurface hybrid export now fail missing browser-delivered static assets with `RWEXPORT003` while still tolerating live/page routes, frames, forms, streams, islands, canonical metadata, DNS hints, and page-shaped preload or prefetch hints.
- Config audit discovered-key reports now expose value display state and omit raw unknown or broad-descendant scalar values unless the exact leaf key is registered or the value is redacted.
- AppSurface Docs standalone 404 pages now render a harvest-free recovery hub with safe links to Search, Docs home, and route-safe Start Here or Packages destinations while preserving API, JSON, and non-browser 404 behavior.

## 0.1.0-rc.1 - 2026-05-29

- Narrative release note: [v0.1.0-rc.1](./releases/v0.1.0-rc.1.md)
- Release manifest: `releases/v0.1.0-rc.1.release.json`
