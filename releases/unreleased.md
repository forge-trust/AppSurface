# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.2`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- AppSurface now has `ForgeTrust.AppSurface.Auth` as a surface-neutral auth vocabulary package for module authors and host integrations. It defines passive user, session, context, result, login/logout prompt, audit-event, and metadata-key contracts while still avoiding runtime authentication, authorization policy evaluation, redirects, audit sinks, and identity-provider integration.
- AppSurface Flow is now a first-class package family. `ForgeTrust.AppSurface.Flow` provides stable `net10.0` typed input/output port process contracts, generated-case authoring attributes and analyzer support, inferred graph mapping for unambiguous ports, discriminated outcome records, graph validation, a definition registry, and an in-memory runner, while `ForgeTrust.AppSurface.Flow.DurableTask` adds a passive Durable Task adapter boundary with runner/client services, resume-event authorization, timeout, late-event and retry behavior, and context serialization validation. Semantic Kernel remains out of v1 scope.
- AppSurface Docs JavaScript API pages can now resolve public doclets into reader-facing API families. Explicit `@namespace` and `@module` tags remain authoritative, ordered `GroupNameRules` can name known source trees, and untagged fallback groups use path-aware identities so same-stem files in different folders do not merge.
- AppSurface Docs built-in Markdown and C# harvesters now skip file and directory reparse points during direct and aggregated source traversal, and Markdown root `LICENSE` plus paired sidecar metadata reads use the same non-reparse boundary so symlinks cannot pull documentation content from outside the selected repository root.
- AppSurface Docs JavaScript harvesting now applies the same no-reparse boundary to explicit includes, global include roots, recursive traversal, child files, and child directories. Configured JavaScript include roots that resolve to symlinks, junctions, or other reparse points emit `appsurfacedocs.javascript.reparse_point_skipped` with redacted recovery guidance and block strict JavaScript health when JavaScript participates.
- Older `v0.1` preview routes now redirect to the current RC2 release note, so package consumers land on one canonical release story instead of a stale pre-RC preview.
- Public package READMEs now link directly to the [v0.1.0 RC 2 release note](./v0.1.0-rc.2.md) for release risk, migration guidance, and package readiness.
- The AppSurface CLI now reports package SemVer from `appsurface --version`, preserving prerelease labels such as `0.1.0-rc.1` while omitting leading `v` tags and build metadata. Tool smoke validation now installs the package and checks `--version` exactly against the package manifest so future RC publishes cannot look stable by accident.
- Package maintainers now have a generated `packages/readiness.md` evidence dashboard that groups packages by product family, reports package-index readiness evidence, and keeps blocker/notes annotations separate from live NuGet publish status.
- The release authoring checklist now records the preview-rollup rule: when a tagged or release-candidate note supersedes a preview, remove the preview source file and carry its browser routes as `redirect_aliases` on the canonical note.
- Release preparation now leaves `CHANGELOG.md` as a compact ledger, moves detailed release narrative into tagged release notes, and makes generated release PR reports stop at a manual maintainer review gate before merge, tag, or publish.
- AppSurface CI coverage now runs through a dedicated `ForgeTrust.AppSurface.CoverageRunner` tool behind the stable `scripts/coverage-solution.sh` entrypoint. The runner writes independent per-project coverage/JUnit artifacts, captures stable per-project logs, supports `COVERAGE_PARALLELISM` for bounded same-job test concurrency, and keeps the merged Cobertura output path used by Codecov.
- The AppSurface CLI now includes `appsurface coverage gate` for private Cobertura threshold enforcement. The command writes `coverage-gate.json` and `coverage-gate.md`, optionally appends the Markdown result to GitHub Actions step summaries, uses `ASCOV###` diagnostics for CI failures, and keeps coverage upload/trend/dashboard behavior out of the v1 gate.
- RazorWire CLI static export now recognizes the inline autoload marker that AppSurface Docs outlines emit for page navigation and materializes `/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js` in CDN exports. AppSurface Docs now mirrors the shared `razorwire:page-nav:active-change` event instead of owning duplicate outline scroll/hash tracking, so published docs rely on the same navigation runtime as package consumers.
- RazorWire now has first-class section copy for long-form documentation and reference pages. Authored buttons use `data-rw-section-copy`, generated buttons opt in with `data-rw-section-copy-target`, the lazy `section-copy.js` runtime owns clipboard success/fallback state through stable `data-rw-section-copy*` hooks, and static export materializes the runtime even when pages rely on lazy `<rw:scripts/>` detection.
- AppSurface Web browser status-page re-execution now preserves the original `401`, `403`, or `404` response status while rendering conventional browser recovery pages, so standalone docs 404 recovery keeps `NotFound` semantics across supported runtimes.
- `razorwire export` and `appsurface export` now accept `--publish-root-extras <manifest>` for explicit single-file publish-root extras such as GitHub Pages `CNAME`, with `RWEXPORT007` validation for schema, symlink, reserved provider file, generated-output collision, existing-target, and exact release archive incompatibility failures. `appsurface docs export` remains intentionally clean and does not expose the option for exact docs archive artifacts.
- Package consumers can now follow a package-first AppSurface Web quickstart from a fresh `dotnet new web` app, install `ForgeTrust.AppSurface.Web`, and verify the first route before choosing optional modules.
- AppSurface Docs Turbo-frame navigation now preserves cross-page heading fragments, so generated package chooser links land directly on the package-first quickstart section instead of the top of the target page.

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
