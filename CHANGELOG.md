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
- Release preparation now generates per-version release evidence bundles at `releases/v{version}.evidence.json`; publish validation checks the bundle at the annotated tag commit before GitHub Release creation.
- AppSurface CI coverage now runs through a dedicated `ForgeTrust.AppSurface.CoverageRunner` tool behind the stable `scripts/coverage-solution.sh` entrypoint, preserving Codecov artifact paths while adding bounded same-job coverage scheduling.
- AppSurface CLI now includes `appsurface coverage run` for package consumers that already instrument private .NET test projects with Coverlet, producing local merged Cobertura artifacts without mutating consumer repos or reading their tool manifests.
- AppSurface CLI now includes `appsurface coverage merge` for private matrix fan-in workflows that already produce Cobertura shards, with package-owned ReportGenerator merging, safe AppSurface-owned output cleanup, and `ASCOV130` through `ASCOV139` diagnostics.
- Coverage runs now write slow-test diagnostics in Markdown and JSON, including project timings, parsed JUnit test-case evidence, best-effort parser warnings, and diagnostic aggregation overhead in seconds and as a percent of runner time.
- AppSurface CLI now includes `appsurface coverage gate` for private Cobertura line, branch, and optional changed-line threshold enforcement with local JSON/Markdown reports, GitHub Actions step-summary output, and `ASCOV###` diagnostics.
- AppSurface Web modules can now register endpoint-aware middleware after routing and AppSurface-managed CORS through `ConfigureEndpointAwareMiddleware`, with root modules invoked before dependency modules so host-owned authentication and authorization can run before feature middleware.
- RazorWire CLI static export now materializes the lazy page-navigation runtime emitted by AppSurface Docs outlines, so CDN exports succeed and publish consistently when the shared navigation script is required.
- RazorWire now ships a lazy section-copy runtime with generated package assets, public `data-rw-section-copy*` hooks, static export materialization, and AppSurface Docs adoption for outline/content section links.
- CI now includes a measured NuGet cache rollout for selected build, docs export, and code-quality jobs while keeping package-gate restores isolated.
- PackageIndex verification now ignores hidden local cache directories such as `.pnpm-store` and workspace `.nuget/packages` so local cache contents do not require package manifest entries.
- Fast non-package CI that does not compile Tailwind-consuming projects can now skip Tailwind runtime binary resolution while package validation and release paths force binary resolution on and fail before creating an empty runtime package.
- RazorWire and AppSurface CLI exports now accept explicit `--publish-root-extras` manifests for single-file publish-root deployment extras such as GitHub Pages `CNAME`, while AppSurface Docs exact archive exports stay immutable and reject that deployment-owned surface.
- AppSurface Web browser status-page re-execution now preserves original browser error status codes while direct reserved preview routes continue to render normally.
- Package consumers now have a package-first AppSurface Web quickstart that starts from `dotnet new web`, installs `ForgeTrust.AppSurface.Web`, and verifies the first route without cloning the repository.
- AppSurface Docs Turbo-frame navigation now preserves cross-page heading fragments, so generated package chooser links land directly on the intended quickstart section.
- AppSurface Flow now includes generated-case authoring for typed long-running process graphs, with build-time diagnostics, generated adapters, serializable envelopes, package-transitive analyzer assets, local runner coverage, Durable Task serialization validation, and a generated-authoring approval example.
- `ForgeTrust.AppSurface.Aspire` now has a working local Aspire AppHost example, richer package guidance, and an overridable `AspireProfile.PassThroughArgs` seam for profiles that intentionally pass known AppHost arguments.

## 0.1.0-rc.2 - 2026-06-03

- Narrative release note: [v0.1.0-rc.2](./releases/v0.1.0-rc.2.md)
- Release manifest: `releases/v0.1.0-rc.2.release.json`

## 0.1.0-rc.1 - 2026-05-29

- Narrative release note: [v0.1.0-rc.1](./releases/v0.1.0-rc.1.md)
- Release manifest: `releases/v0.1.0-rc.1.release.json`
