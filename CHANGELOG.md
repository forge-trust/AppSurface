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
- `appsurface coverage gate` patch coverage now supports local Git refs, CI-produced unified diff files, and piped unified diff text, with fail-closed validation and report provenance for external diff artifacts.
- AppSurface CI coverage now dogfoods the `appsurface coverage run` command, running from source via `dotnet run --project`, for the default full-solution coverage lane while preserving Cobertura, JUnit, slow-test diagnostics, Codecov, and `coverage gate` evidence.
- `appsurface coverage run` now supports AppSurface-managed `--test-results junit`; `--slow-test-diagnostics` implies managed JUnit results and records parser status, warnings, metadata completeness, and diagnostic overhead in `timings.json`.
- Package artifact validation now installs the packed `ForgeTrust.AppSurface.Cli` tool into a clean consumer fixture before publication and proves `coverage run`, `coverage merge`, a passing `coverage gate`, and a deliberately failing `coverage gate` all behave from the packaged tool.
- AppSurface Docs search now keeps MiniSearch candidate matching while applying deterministic reader-intent ranking for exact lookups, aliases, entry points, broad task queries, explicit API/internal filters, and contributor/internal demotion.
- Evaluators can run `examples/product-readiness-lab` as a report-first product proof, then use the paired Aspire AppHost verifier to exercise Postgres-backed product state while keeping Durable Task worker/client hosting and storage provider setup explicitly host-owned.
- AppSurface Docs search now preserves multi-word spacing while readers type in the full-page and sidebar search boxes.
- RazorWire now includes a Cloud Run hybrid-hosting guide for split-origin deployments that serve exported static pages from one origin while a live RazorWire app serves streams, islands, and lazy anti-forgery forms from another origin.
- Package validation now gates redistributed package payload provenance through `packages/third-party-payloads.yml`, package-root notices, generated-first-party evidence, and audited exceptions before prerelease artifacts can pass `verify-packages`.

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
