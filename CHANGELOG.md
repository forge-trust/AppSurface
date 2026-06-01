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
- AppSurface Docs exact release archives now emit catalog-pinned release manifests, and runtime archive serving verifies those manifests before allowing active archive SVG.
- Behavior change: AppSurface Docs configured branding directories no longer serve SVG by default; trusted operators must set `AppSurfaceDocs:Identity:BrandingAssets:AllowSvgAssets=true` before AppSurface Docs serves SVG files from that directory.
- Behavior change: AppSurface Docs `AppSurfaceDocs:Harvest:Health:ExposeRoutes=Always` continues to expose harvest health routes outside Development, but live harvest progress streaming now also requires a custom host-owned `IRazorWireChannelAuthorizer`.
- AppSurface Docs version catalogs now require trusted-root-relative `exactTreePath` values under `AppSurfaceDocs:Versioning:TrustedReleaseRootPath`; migrate old absolute tree paths by moving the parent into the trusted root setting and keeping catalog entries relative.
- AppSurface Docs Markdown harvesting now skips oversized Markdown bodies before read/Markdig parse and ignores oversized `.md.yml` / `.md.yaml` sidecars before YAML parse, emitting visible warning diagnostics while otherwise healthy snapshots remain healthy by default.
- AppSurface Docs JavaScript harvesting now skips symlinks, junctions, and other reparse points before explicit include reads, include-root traversal, and recursive child descent. Configured JavaScript link includes emit `appsurfacedocs.javascript.reparse_point_skipped` with redacted recovery guidance and block strict JavaScript health when JavaScript participates.
- RazorWire stream endpoints now reject malformed channels with `400`, deny unauthorized channels with `403`, and return `429` when per-process live-channel or subscription admission limits are exhausted before hub subscriber state is allocated.

## 0.1.0-rc.1 - 2026-05-29

- Narrative release note: [v0.1.0-rc.1](./releases/v0.1.0-rc.1.md)
- Release manifest: `releases/v0.1.0-rc.1.release.json`
