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
- RazorWire CLI static export now materializes the lazy page-navigation runtime emitted by AppSurface Docs outlines, so CDN exports succeed and publish consistently when the shared navigation script is required.
- RazorWire now ships a lazy section-copy runtime with generated package assets, public `data-rw-section-copy*` hooks, static export materialization, and AppSurface Docs adoption for outline/content section links.
- CI now includes a measured NuGet cache rollout for selected build, docs export, and code-quality jobs while keeping package-gate restores isolated.
- PackageIndex verification now ignores hidden local cache directories such as `.pnpm-store` and workspace `.nuget/packages` so local cache contents do not require package manifest entries.
- RazorWire and AppSurface CLI exports now accept explicit `--publish-root-extras` manifests for single-file publish-root deployment extras such as GitHub Pages `CNAME`, while AppSurface Docs exact archive exports stay immutable and reject that deployment-owned surface.
- AppSurface Web browser status-page re-execution now preserves original browser error status codes while direct reserved preview routes continue to render normally.
- Package consumers now have a package-first AppSurface Web quickstart that starts from `dotnet new web`, installs `ForgeTrust.AppSurface.Web`, and verifies the first route without cloning the repository.
- AppSurface Docs Turbo-frame navigation now preserves cross-page heading fragments, so generated package chooser links land directly on the intended quickstart section.

## 0.1.0-rc.2 - 2026-06-03

- Narrative release note: [v0.1.0-rc.2](./releases/v0.1.0-rc.2.md)
- Release manifest: `releases/v0.1.0-rc.2.release.json`

## 0.1.0-rc.1 - 2026-05-29

- Narrative release note: [v0.1.0-rc.1](./releases/v0.1.0-rc.1.md)
- Release manifest: `releases/v0.1.0-rc.1.release.json`
