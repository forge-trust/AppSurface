# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.2`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- AppSurface Flow is now a first-class package family. `ForgeTrust.AppSurface.Flow` provides stable `net10.0` typed input/output port process contracts, generated-case authoring attributes and analyzer support, inferred graph mapping for unambiguous ports, discriminated outcome records, graph validation, a definition registry, and an in-memory runner, while `ForgeTrust.AppSurface.Flow.DurableTask` adds a passive Durable Task adapter boundary with runner/client services, resume-event authorization, timeout, late-event and retry behavior, and context serialization validation. Semantic Kernel remains out of v1 scope.
- Release preparation now generates per-version release evidence bundles at `releases/v{version}.evidence.json`, and publish validation checks the bundle at the annotated tag commit before GitHub Release creation. The bundle proves repository release-artifact consistency; it is not a signature or hosted-build attestation.
- AppSurface CI coverage now runs through a dedicated `ForgeTrust.AppSurface.CoverageRunner` tool behind the stable `scripts/coverage-solution.sh` entrypoint. The runner writes independent per-project coverage/JUnit artifacts, captures stable per-project logs, supports `COVERAGE_PARALLELISM` for bounded same-job test concurrency, and keeps the merged Cobertura output path used by Codecov.
- The AppSurface CLI now includes `appsurface coverage run` as the public package-consumer coverage orchestrator for private .NET repositories. It supports `.sln`/`.slnx` discovery, repeated `--test-project`, `--dry-run`/`--list-projects`, safe AppSurface-owned output cleanup, stable per-project artifact directories, package-owned ReportGenerator merging, and Coverlet-required local test orchestration without mutating consumer projects or reading consumer tool manifests.
- Coverage runs now emit `slow-test-diagnostics.md` and `slow-test-diagnostics.json` next to the merged coverage artifacts. The diagnostics rank project and JUnit test-case timings, preserve best-effort parser warnings without changing coverage exit codes, and report diagnostic aggregation overhead in seconds and as a percent of total runner time.
- The AppSurface CLI now includes `appsurface coverage gate` for private Cobertura threshold enforcement. The command writes `coverage-gate.json` and `coverage-gate.md`, optionally appends the Markdown result to GitHub Actions step summaries, uses `ASCOV###` diagnostics for CI failures, and keeps coverage upload/trend/dashboard behavior out of the v1 gate.
- RazorWire CLI static export now recognizes the inline autoload marker that AppSurface Docs outlines emit for page navigation and materializes `/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js` in CDN exports. AppSurface Docs now mirrors the shared `razorwire:page-nav:active-change` event instead of owning duplicate outline scroll/hash tracking, so published docs rely on the same navigation runtime as package consumers.
- RazorWire now has first-class section copy for long-form documentation and reference pages. Authored buttons use `data-rw-section-copy`, generated buttons opt in with `data-rw-section-copy-target`, the lazy `section-copy.js` runtime owns clipboard success/fallback state through stable `data-rw-section-copy*` hooks, and static export materializes the runtime even when pages rely on lazy `<rw:scripts/>` detection.
- AppSurface Web browser status-page re-execution now preserves the original `401`, `403`, or `404` response status while rendering conventional browser recovery pages, so standalone docs 404 recovery keeps `NotFound` semantics across supported runtimes.
- `razorwire export` and `appsurface export` now accept `--publish-root-extras <manifest>` for explicit single-file publish-root extras such as GitHub Pages `CNAME`, with `RWEXPORT007` validation for schema, symlink, reserved provider file, generated-output collision, existing-target, and exact release archive incompatibility failures. `appsurface docs export` remains intentionally clean and does not expose the option for exact docs archive artifacts.
- Package consumers can now follow a package-first AppSurface Web quickstart from a fresh `dotnet new web` app, install `ForgeTrust.AppSurface.Web`, and verify the first route before choosing optional modules.
- AppSurface Docs Turbo-frame navigation now preserves cross-page heading fragments, so generated package chooser links land directly on the package-first quickstart section instead of the top of the target page.
- `ForgeTrust.AppSurface.Aspire` now includes a working local Aspire AppHost example at `examples/aspire-apphost`, stronger package guidance for profile and component composition, and explicit troubleshooting for `-- local`, generated `Projects.*` references, duplicate resources, and unsupported deployment/pass-through arguments.

### CI and package validation

- CI now includes a measured NuGet cache rollout for selected build, docs export, and code-quality jobs while keeping package-gate restores isolated.
- PackageIndex verification now ignores hidden local cache directories such as `.pnpm-store` and workspace `.nuget/packages` so local cache contents do not require package manifest entries.
- Fast non-package CI that does not compile Tailwind-consuming projects can now skip Tailwind runtime binary resolution while package validation and release paths force binary resolution on and fail before creating an empty runtime package.
- PackageIndex workflow policy tests now reject additional `TailwindRuntimeBinaryResolutionEnabled=false` forms, including long MSBuild property switches, semicolon property lists, and shell environment assignments.
- Tailwind runtime source builds now cache downloaded standalone CLI binaries in a shared user-level cache by default instead of each worktree's `obj` tree. Set `TailwindDownloadCacheRoot` to choose a CI cache volume or isolate a build.

### RazorWire package guidance

- RazorWire page navigation now keeps the active same-page link perceivable inside visible overflowing vertical nav surfaces, using the nav container's `scroll-padding-block` / `scroll-padding-top` / `scroll-padding-bottom` values as reveal insets while preserving document scroll.
- RazorWire page-navigation docs now include a compact section-context recipe that derives previous/current/next labels from the existing `razorwire:page-nav:active-change` event while keeping the labels and chrome app-owned.

### AppSurface Docs product example

- AppSurface adds an experimental `ForgeTrust.AppSurface.Intelligence` package with typed product-intelligence event contracts, privacy validation, no-op default registration, and host-owned sink hooks. AppSurface Docs and RazorWire now dogfood the experimental contracts for safe docs search, recovery-link, form-failure, form-recovery, and stream-admission signals while keeping PostHog as a recipe rather than a package dependency.
- AppSurface Docs JavaScript public API harvesting now recognizes documented CSS property hooks such as RazorWire page-navigation scroll-padding contracts, so generated API references can describe browser styling insets without malformed-doclet diagnostics.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
