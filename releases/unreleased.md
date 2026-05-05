# Unreleased

This is the living release note for the next coordinated Runnable version. It is intentionally written in the same blog-style shape we want future tagged releases to use, but everything here remains provisional until a tag is cut.

## What is taking shape

Runnable is putting the release contract in place before `v0.1.0`. This slice is about making release notes auditable, public, and reusable:

- a root changelog that acts as the compact ledger
- a public unreleased proof artifact
- a pre-1.0 upgrade policy with a clear migration home
- a top-of-page trust bar for release notes and policy pages
- pull-request guards that keep PR titles and unreleased entries aligned with future release automation

## Included in the next coordinated version

### Release and docs surface

- The `/docs` landing now promotes a Releases entry point alongside product proof paths.
- Runnable now ships a public release hub, a changelog, an unreleased page, and a tagged release template inside the repository.
- Release-note pages can show status, freshness, scope, migration guidance, and provenance in a shared trust bar instead of bespoke page chrome.
- Runnable now ships a generated package chooser that tells first-time adopters which package to install first, which optional modules to add next, and which proof paths to follow for release risk and working examples.
- The root README now has a single hello-world quickstart that starts the smallest web example on an explicit port and proves the response with `curl`.

### Contribution contract

- Pull request titles are now expected to follow Conventional Commits so the merge history is machine-readable for future automation.
- Pull requests are expected to update this page unless maintainers explicitly mark the change as outside the public release story.
- Markdown-only changes on `main` now republish the docs surface, so release-note and policy edits are treated as first-class product updates.
- Runnable now exposes focused GitHub issue forms for bug reports, feature requests, and docs/developer-experience feedback, with the root README and contribution guide pointing developers to that feedback path.
- Public contribution surfaces now steer suspected vulnerabilities away from issue forms and into a private security reporting path.
- GitHub issue template support links now point first-time adopters to the package chooser and release/upgrade contract when they are evaluating install path or migration risk.

### Console and CLI polish

- Runnable console apps can now opt into a command-first output contract so public CLI help and validation flows stay quiet instead of printing Generic Host lifecycle chatter.
- RazorWire CLI now uses that contract for `--help`, `export --help`, invalid option output, and missing-source validation while still preserving command-owned export progress logs.
- RazorWire CLI now names export seed-route files with `-r|--seeds`, matching the seed terminology used throughout the exporter and docs.
- The shared console startup seam now exposes `ConsoleOptions` and `ConsoleOutputMode`, so future public Runnable CLIs can adopt the same behavior without forking startup logic.
- RazorWire CLI now has a first-class .NET tool package contract with the `razorwire` command, supports exact-version `dnx` execution from published or explicit local package sources, and verifies the installed tool path through help and sample export smoke tests. Public package publishing remains manual until the coordinated release automation tracked in #161 lands.
- Project exports now disable persistent MSBuild build servers during CLI-controlled publish and assembly-name probes so captured tool output cannot hang on reused build nodes.
- RazorWire CLI process cleanup now waits for asynchronous stdout and stderr callbacks to flush before disposing launched target processes, which keeps short-lived command output observable in tests and diagnostics.
- RazorWire CLI validation errors now include a concrete source-selection example and `razorwire export --help` hint, so a failed export tells developers the next useful command instead of only naming the bad input.
- PackageIndex now has a real `--help`/`-h` surface that exits successfully, describes its commands and options, and reports unknown commands before printing usage.

### Core diagnostics

- Core static utilities now use explicit `ILogger` overloads and source-generated `[LoggerMessage]` definitions for host-owned diagnostics. `PathUtils.FindRepositoryRoot` can warn when discovery falls back from a missing path, and parallel enumerable cleanup paths now log suppressed cleanup failures at `Debug` when a caller supplies a logger.

### Dependency maintenance

- The centrally managed `YamlDotNet` dependency now targets `17.0.1`, and the affected PackageIndex, RazorDocs, and Aspire lock files have been regenerated.
- The Autofac dependency package now has dedicated test coverage for Runnable module integration, host container setup, dependent module loading, and implementation scanning.

### Configuration validation

- Strongly typed config wrappers now validate resolved object values with DataAnnotations during startup, including defaults, and report operator-friendly `ConfigurationValidationException` failures without echoing attempted values.
- Nested config validation can now opt into Microsoft Options `[ValidateObjectMembers]` and `[ValidateEnumeratedItems]` markers while Runnable owns traversal, path formatting, and cycle protection.
- Scalar config wrappers can now validate resolved primitive values directly with `ConfigValueNotEmpty`, `ConfigValueRange`, and `ConfigValueMinLength` attributes, while wrapper-specific scalar rules can override `ValidateValue`.
- Config wrappers can now opt into required resolved presence with `ConfigKeyRequired`, so startup fails when no provider value and no default are available while defaults and supplied zero values still count as present.
- The new `examples/config-validation` sample demonstrates an intentional startup validation failure for a scalar `ConfigStruct<int>` without printing the invalid configured value.
- Environment variables can now patch individual members of object-valued config loaded from lower-priority providers, so `APP__SETTINGS__DATABASE__PORT` can override one nested value without replacing the rest of the JSON-backed options object.

### Web host development defaults

- Runnable web hosts now choose a deterministic localhost-only development URL when no endpoint is configured, while production, staging, container, and appsettings-based endpoint choices remain untouched.
- OpenAPI's optional web package now has dedicated test coverage for service registration, endpoint mapping, generated document titles, and transformer behavior that removes `ForgeTrust.Runnable.Web` tags at the document and operation levels while preserving unrelated tags, so the public module contract is guarded independently of Scalar.
- Scalar's optional web package now has dedicated test coverage for OpenAPI dependency wiring, Scalar endpoint mapping, no-op lifecycle hooks, and minimal Runnable web host composition.
- Tailwind development watch mode now treats a missing standalone CLI as a recoverable local-tooling gap: the app keeps serving existing CSS and logs a warning that points to the runtime package or `TailwindCliPath` override.
- Runnable's conventional browser 404 page now prioritizes user recovery paths, including documentation search for missing `/docs/...` routes and a home link for other misses, while still documenting how app owners can override the default page.
- Runnable now assigns explicit numeric values to public Web and RazorWire enums, preserving existing ordinals for consumers that persist, serialize, bind, or compare those values.
- Runnable startup now keeps custom `StartupContext.ApplicationName` values as display labels while preserving assembly-backed host identity for ASP.NET static web asset manifests, so custom-labeled web hosts can still serve package styles and scripts.

### RazorWire package guidance

- RazorWire now has a generated UI design contract for package-owned nodes. The contract separates RazorWire UI from app-authored markup and RazorDocs chrome, establishes `data-rw-*` attributes plus `--rw-ui-*` custom properties as the default styling surface, and documents global, form-level, and target-level override expectations for future generated UI.

### RazorDocs product example

- Runnable's own release pages now double as a working RazorDocs example for consumers who want better release notes.
- RazorDocs now supports a static-first versioned docs surface: `/docs` can point at the recommended released tree, `/docs/next` can stay on the live preview, `/docs/v/{version}` can serve exact historical releases, and `/docs/versions` can act as the public archive.
- Published RazorDocs release trees are now catalog-driven and validated before they are mounted, so broken historical exports stay unavailable instead of half-rendering with cross-version search or asset leakage.
- RazorDocs pages can now expose typed `On this page` outlines, explicit proof-path previous/next links, related-page cards, and sidebar anchor navigation from harvested metadata instead of scraping rendered HTML.
- Public docs navigation now groups pages by intent-first sections, preserves authored editorial breadcrumbs, and keeps Start Here recovery links hidden when that section is unavailable.
- RazorDocs landing curation now uses `featured_page_groups`, so root and section landing pages can organize next-step links by reader intent instead of rendering one flat list.
- RazorDocs page lookup now uses one shared path resolver for details pages, landing curation, related-page links, and search recovery links, keeping source paths, canonical `.html` paths, fragments, backslash normalization, and configured docs-root prefixes behaviorally aligned.
- The release contract is designed so future tooling can generate both a changelog entry and a blog-style tagged release note from the same underlying signals.
- RazorDocs now rewrites authored doc links from a harvested target manifest instead of broad suffix heuristics, so normal site links such as `../privacy.html` stay untouched and missing doc targets do not become broken `/docs/...` routes.
- RazorDocs details pages can now render a `Source of truth` strip with `View source`, `Edit this page`, and relative `Last updated` evidence driven by contributor metadata, configured URL templates, and git freshness when available.
- The primary RazorDocs Pages deployment now exports with contributor provenance configured and full git history available, so the public docs artifact can show the same `Source of truth` strip as local smoke tests.
- Contributor provenance now degrades safely: namespace and API pages stay explicit-override-only for the MVP, and missing or slow git history omits only freshness instead of breaking docs rendering.
- RazorDocs generated C# API references can now render per-symbol source links for documented types, methods, properties, and enums that point at the exact source file and line, with immutable refs available when hosts want links pinned to the code version used to build the docs.
- The primary RazorDocs Pages deployment now configures commit-pinned symbol source links, so generated C# API `Source` chips resolve to the exact file and line from the CI build revision.
- Shared RazorDocs badges, metadata chips, provenance strips, and trust bars now live in the shared package stylesheet while `search.css` stays focused on search-specific UI.
- RazorDocs search now keeps failure recovery markup out of the active search shell until the index actually fails to load, so successful searches no longer expose hidden failure copy to text extraction tools.
- RazorDocs now treats `Releases` as a first-class public section and suppresses breadcrumb links to generated parent routes that do not correspond to published docs pages, keeping static export warnings focused on actionable broken links.
- RazorDocs wayfinding coverage now waits for docs content replacement before asserting sequence-link destinations, keeping the details-page proof path deterministic in CI.
- RazorDocs Playwright integration coverage now hosts the standalone docs app in-process through the standalone host builder, avoiding fixture-time `dotnet run` rebuilds and stale standalone `bin` output during focused test runs.

### RazorWire form UX

- RazorWire-enhanced forms now get a convention-based failed-submission stack: durable request markers, default form-local fallback UI, handled server validation helpers, and runtime events for custom consumers.
- Development anti-forgery failures from RazorWire forms now return useful diagnostics with safe production copy, so stale or missing token problems are easier to fix without exposing implementation detail to users.
- The MVC sample now includes `/Reactivity/FormFailures`, covering validation, anti-forgery, authorization, malformed request, server failure, default styling, CSS variable customization, and manual event-driven rendering.
- The MVC sample counter keeps its compact icon-only button while exposing an `Increment counter` accessible name for assistive technology and role-based tests.

## Migration watch

There is no tagged migration guide yet because Runnable has not cut `v0.1.0`. Until then:

- breaking changes should be called out here as soon as they land
- the stable policy lives in [Pre-1.0 upgrade policy](./upgrade-policy.md)
- finalized migration steps move into the tagged release note when the version ships
- custom RazorDocs harvesters that want detail-page outlines and search heading metadata should populate `DocNode.Outline`; pages without outline metadata continue to render without the optional outline section
- `DocAggregator.GetSearchIndexPayloadAsync(...)` is no longer a supported package-consumer API. The live search-index payload is now treated as an internal RazorDocs implementation detail so the host can rebase docs paths and serialize once per request. Consumers that previously called that method directly should switch to the public docs search endpoint or build their own search payload contract instead of depending on RazorDocs' internal snapshot shape.
- existing `rw-active` forms opt into failed-form request markers and default fallback UI; applications with custom failure rendering can use `RazorWireOptions.Forms.FailureMode = Manual`, `RazorWireOptions.Forms.EnableFailureUx = false`, or per-form `data-rw-form-failure="off"`
- RazorDocs authors should migrate flat `featured_pages` metadata to `featured_page_groups`. The old field is ignored and logs a warning; each group needs at least `label` or `intent`, plus a `pages` list containing the existing `question`, `path`, `supporting_copy`, and `order` entries.
- Code that previously read `IHostEnvironment.ApplicationName` to recover a custom Runnable display label should read `StartupContext.ApplicationName` instead. `IHostEnvironment.ApplicationName` now stays aligned with the host entry-assembly identity used for static web asset discovery unless `StartupContext.OverrideEntryPointAssembly` explicitly selects a different manifest identity. `StartupContext.EntryPointAssembly` still defaults to the root module assembly for command/controller/component discovery, so existing cross-assembly scanning behavior remains stable.

## Proof artifacts

- [Release hub](./README.md)
- [Changelog](../CHANGELOG.md)
- [Pre-1.0 upgrade policy](./upgrade-policy.md)
- [Release authoring checklist](./release-authoring-checklist.md)

## Before the first tag

The current intent is that everything already in this repository can be part of `v0.1.0` when Runnable is ready to release. This page is where that pile becomes visible and reviewable before the tag exists.
