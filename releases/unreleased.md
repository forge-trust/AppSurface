# Unreleased

This is the living release note for the next coordinated AppSurface version. It is intentionally written in the same blog-style shape we want future tagged releases to use, but everything here remains provisional until a tag is cut.

## What is taking shape

AppSurface is putting the release contract in place before `v0.1.0`. This slice is about making release notes auditable, public, and reusable:

- a root changelog that acts as the compact ledger
- a public unreleased proof artifact
- a pre-1.0 upgrade policy with a clear migration home
- a top-of-page trust bar for release notes and policy pages
- pull-request guards that keep PR titles and unreleased entries aligned with future release automation

## Included in the next coordinated version

### Release and docs surface

- The `/docs` landing now promotes a Releases entry point alongside product proof paths.
- AppSurface now ships a public release hub, a changelog, an unreleased page, and a tagged release template inside the repository.
- Release-note pages can show status, freshness, scope, migration guidance, and provenance in a shared trust bar instead of bespoke page chrome.
- AppSurface now ships a generated package chooser that tells first-time adopters which package to install first, which optional modules to add next, and which proof paths to follow for release risk and working examples.
- AppSurface now builds and uploads validated prerelease package artifacts on pull requests and manual workflow runs, using the package chooser manifest as the single source of truth for publish decisions, dependency expectations, tool packages, first-party DLL version identity, and Tailwind runtime payload presence before NuGet publishing is enabled.
- AppSurface now has a protected, tag-only NuGet prerelease publish workflow that revalidates package artifact manifests, force-fetches the pushed annotated tag before validation, uses NuGet Trusted Publishing instead of a long-lived API key, requires a reviewed `nuget-prerelease` environment, writes a redacted publish ledger, and smoke-restores published packages from a clean NuGet configuration before a prerelease is considered ready.
- The public docs Start Here path now leads with an AppSurface evaluator sequence for teams comparing module-based startup against plain ASP.NET Core `Program.cs` configuration.
- The root README now has a single hello-world quickstart that starts the smallest web example on an explicit port and proves the response with `curl`.

### Contribution contract

- Pull request titles are now expected to follow Conventional Commits so the merge history is machine-readable for future automation.
- Pull requests are expected to update this page unless maintainers explicitly mark the change as outside the public release story.
- Markdown-only changes on `main` now republish the docs surface, so release-note and policy edits are treated as first-class product updates.
- AppSurface now exposes focused GitHub issue forms for bug reports, feature requests, and docs/developer-experience feedback, with the root README and contribution guide pointing developers to that feedback path.
- Public contribution surfaces now steer suspected vulnerabilities away from issue forms and into a private security reporting path.
- GitHub issue template support links now point first-time adopters to the package chooser and release/upgrade contract when they are evaluating install path or migration risk.

### Console and CLI polish

- AppSurface console apps can now opt into a command-first output contract so public CLI help and validation flows stay quiet instead of printing Generic Host lifecycle chatter.
- RazorWire CLI now uses that contract for `--help`, `export --help`, invalid option output, and missing-source validation while still preserving command-owned export progress logs.
- RazorWire CLI now names export seed-route files with `-r|--seeds`, matching the seed terminology used throughout the exporter and docs.
- The shared console startup seam now exposes `ConsoleOptions` and `ConsoleOutputMode`, so future public AppSurface CLIs can adopt the same behavior without forking startup logic.
- RazorWire CLI now has a first-class .NET tool package contract with the `razorwire` command, supports exact-version `dnx` execution from published or explicit local package sources, and verifies the installed tool path through help and sample export smoke tests. Public package publishing remains manual until the coordinated release automation tracked in #161 lands.
- RazorWire CLI export now defaults to CDN-safe static output. Managed internal URLs discovered in HTML and CSS are rewritten to emitted artifacts, `<img>` and `<source>` `srcset` candidates are both covered, RazorDocs frame content emits static partials, conventional `404.html` participates in the same validation, and CDN validation fails with `RWEXPORT###` diagnostics when required frame or asset dependencies cannot become files.
- Project exports now disable persistent MSBuild build servers during CLI-controlled publish and assembly-name probes so captured tool output cannot hang on reused build nodes.
- RazorWire CLI process cleanup now waits for asynchronous stdout and stderr callbacks to flush before disposing launched target processes, which keeps short-lived command output observable in tests and diagnostics.
- RazorWire CLI validation errors now include a concrete source-selection example and `razorwire export --help` hint, so a failed export tells developers the next useful command instead of only naming the bad input.
- RazorWire CLI users who still want extensionless, server-routed export output should pass `--mode hybrid`. The default `cdn` mode is for plain static hosts and CDNs, not S3-specific infrastructure.
- PackageIndex now has a real `--help`/`-h` surface that exits successfully, describes its commands and options, and reports unknown commands before printing usage.

### Core diagnostics

- Core static utilities now use explicit `ILogger` overloads and source-generated `[LoggerMessage]` definitions for host-owned diagnostics. `PathUtils.FindRepositoryRoot` can warn when discovery falls back from a missing path, and parallel enumerable cleanup paths now log suppressed cleanup failures at `Debug` when a caller supplies a logger.

### Dependency maintenance

- The centrally managed `YamlDotNet` dependency now targets `17.0.1`, and the affected PackageIndex, RazorDocs, and Aspire lock files have been regenerated.
- The Autofac dependency package now has dedicated test coverage for AppSurface module integration, host container setup, dependent module loading, and implementation scanning.

### Configuration validation

- Strongly typed config wrappers now validate resolved object values with DataAnnotations during startup, including defaults, and report operator-friendly `ConfigurationValidationException` failures without echoing attempted values.
- Configuration audits can now produce a source-aware report for discovered wrappers and explicitly registered keys, showing provider order, file and environment provenance, defaults, validation diagnostics, and redacted display-safe values.
- Nested config validation can now opt into Microsoft Options `[ValidateObjectMembers]` and `[ValidateEnumeratedItems]` markers while AppSurface owns traversal, path formatting, and cycle protection.
- Scalar config wrappers can now validate resolved primitive values directly with `ConfigValueNotEmpty`, `ConfigValueRange`, and `ConfigValueMinLength` attributes, while wrapper-specific scalar rules can override `ValidateValue`.
- Config wrappers can now opt into required resolved presence with `ConfigKeyRequired`, so startup fails when no provider value and no default are available while defaults and supplied zero values still count as present.
- `ConfigKeyAttribute` now lives in its own public API source file, keeping configuration key attributes discoverable while leaving `AppSurfaceConfigModule` focused on service registration.
- The new `examples/config-validation` sample demonstrates an intentional startup validation failure for a scalar `ConfigStruct<int>` without printing the invalid configured value.
- Environment variables can now patch individual members of object-valued config loaded from lower-priority providers, so `APP__SETTINGS__DATABASE__PORT` can override one nested value without replacing the rest of the JSON-backed options object.

### Web host development defaults

- AppSurface web hosts now choose a deterministic localhost-only development URL when no endpoint is configured, while production, staging, container, and appsettings-based endpoint choices remain untouched.
- OpenAPI's optional web package now has dedicated test coverage for service registration, endpoint mapping, generated document titles, and transformer behavior that removes `ForgeTrust.AppSurface.Web` tags at the document and operation levels while preserving unrelated tags, so the public module contract is guarded independently of Scalar.
- Scalar's optional web package now has dedicated test coverage for OpenAPI dependency wiring, Scalar endpoint mapping, no-op lifecycle hooks, and minimal AppSurface web host composition.
- Tailwind development watch mode now treats a missing standalone CLI as a recoverable local-tooling gap: the app keeps serving existing CSS and logs a warning that points to the runtime package or `TailwindCliPath` override.
- AppSurface's conventional browser 404 page now prioritizes user recovery paths, including documentation search for missing `/docs/...` routes and a home link for other misses, while still documenting how app owners can override the default page.
- AppSurface Web now ships conventional browser status pages for empty HTML `401`, `403`, and `404` responses. The public surface is now `BrowserStatusPageMode`, `BrowserStatusPageModel`, `UseConventionalBrowserStatusPages()`, and `DisableBrowserStatusPages()`, with preview routes at `/_appsurface/errors/401`, `/_appsurface/errors/403`, and `/_appsurface/errors/404`.
- Browser status page overrides are status-specific: use `~/Views/Shared/401.cshtml`, `~/Views/Shared/403.cshtml`, or `~/Views/Shared/404.cshtml`. JSON/API responses, non-empty responses, and non-GET/HEAD requests keep their original behavior.
- Static export remains deliberately 404-only. RazorWire CLI probes `/_appsurface/errors/404` and writes `404.html`; it does not emit `401.html` or `403.html`.
- AppSurface Web can now opt into a conventional production 500 page backed by ASP.NET Core exception handling, rendering only safe generic copy and a request id while leaving Development exception diagnostics and API-oriented responses alone.
- AppSurface now assigns explicit numeric values to public Web and RazorWire enums, preserving existing ordinals for consumers that persist, serialize, bind, or compare those values.
- AppSurface startup now keeps custom `StartupContext.ApplicationName` values as display labels while preserving assembly-backed host identity for ASP.NET static web asset manifests, so custom-labeled web hosts can still serve package styles and scripts.

### RazorWire package guidance

- RazorWire now has a generated UI design contract for package-owned nodes. The contract separates RazorWire UI from app-authored markup and RazorDocs chrome, establishes `data-rw-*` attributes plus `--rw-ui-*` custom properties as the default styling surface, and documents global, form-level, and target-level override expectations for future generated UI.
- RazorWire README snippets are now source-backed by the MVC sample through a MarkdownSnippets generator, with CI verification and README contract tests guarding quickstart drift.

### RazorDocs product example

- AppSurface's own release pages now double as a working RazorDocs example for consumers who want better release notes.
- RazorDocs now supports a static-first versioned docs surface: `/docs` can point at the recommended released tree, `/docs/next` can stay on the live preview, `/docs/v/{version}` can serve exact historical releases, and `/docs/versions` can act as the public archive.
- RazorDocs route families can now be mounted away from `/docs` with `RazorDocs:Routing:RouteRootPath`, so consumers can host docs under paths such as `/foo/bar` while archive, exact-version, search, metadata, and static export URLs stay aligned. RazorDocs now registers only its configured docs routes instead of an app-wide controller/action fallback, keeping docs routing isolated from other modules.
- Published RazorDocs release trees are now catalog-driven and validated before they are mounted, so broken historical exports stay unavailable instead of half-rendering with cross-version search or asset leakage.
- RazorDocs pages can now expose typed `On this page` outlines, explicit proof-path previous/next links, related-page cards, and sidebar anchor navigation from harvested metadata instead of scraping rendered HTML.
- RazorDocs details pages now render those `On this page` outlines as a page-local navigation surface, using a sticky desktop rail, a compact narrow-viewport drawer, and active-section state that keeps the reader oriented without competing with the global sidebar.
- RazorDocs compact `On this page` outlines now stay visible while reading on narrow viewports, showing smaller previous/next context around the current section with reduced-motion-safe rolling label updates.
- RazorDocs details pages now emit the outline client as a normal deferred script asset, so static exports publish `/docs/outline-client.js` through the existing asset crawler instead of depending on an inline loader.
- RazorDocs detail-page outlines now keep long-section active states and the desktop right rail aligned, including the full-height rail rule, active-item visibility on long pages, and animated section jumps.
- Public docs navigation now groups pages by intent-first sections, preserves authored editorial breadcrumbs, and keeps Start Here recovery links hidden when that section is unavailable.
- RazorDocs API Reference navigation now keeps the primary sidebar at package and namespace depth, nests deeper namespace pages under their nearest parent with leaf labels, and leaves generated type and member anchors to namespace-page outlines, source links, and search instead of expanding hundreds of symbols in the left rail.
- RazorDocs landing curation now uses `featured_page_groups`, so root and section landing pages can organize next-step links by reader intent instead of rendering one flat list.
- RazorDocs now exposes structured harvest health through `DocAggregator.GetHarvestHealthAsync(...)`, letting hosts distinguish healthy, valid-empty, degraded, and all-failed source-backed docs snapshots while keeping raw exception details in logs.
- RazorDocs hosts can now opt into strict startup failure with `RazorDocs:Harvest:FailOnFailure`, so CI, release, and static export runs fail before listening when every configured harvester fails while runtime hosts stay tolerant by default.
- RazorDocs now exposes a local-first harvest health UI at `{DocsRootPath}/_health` plus a machine-readable `{DocsRootPath}/_health.json` endpoint, shown by default in Development and configurable independently from sidebar chrome for operator-owned environments.
- RazorDocs page lookup now uses one shared path resolver for details pages, landing curation, related-page links, and search recovery links, keeping source paths, canonical `.html` paths, fragments, backslash normalization, and configured docs-root prefixes behaviorally aligned.
- RazorDocs authored Markdown pages now publish clean canonical routes that follow their public section hierarchy, so teams can link to URLs such as `/docs/packages` instead of repository-shaped `README.md.html` paths while source-path lookups and declared aliases continue to work.
- The release contract is designed so future tooling can generate both a changelog entry and a blog-style tagged release note from the same underlying signals.
- RazorDocs now rewrites authored doc links from a harvested target manifest instead of broad suffix heuristics, so normal site links such as `../privacy.html` stay untouched and missing doc targets do not become broken `/docs/...` routes.
- RazorDocs details pages can now render a `Source of truth` strip with `View source`, `Edit this page`, and relative `Last updated` evidence driven by contributor metadata, configured URL templates, and git freshness when available.
- The primary RazorDocs Pages deployment now exports with contributor provenance configured and full git history available, so the public docs artifact can show the same `Source of truth` strip as local smoke tests.
- Contributor provenance now degrades safely: namespace and API pages stay explicit-override-only for the MVP, and missing or slow git history omits only freshness instead of breaking docs rendering.
- RazorDocs generated C# API references can now render per-symbol source links for documented types, methods, properties, and enums that point at the exact source file and line, with immutable refs available when hosts want links pinned to the code version used to build the docs.
- The primary RazorDocs Pages deployment now configures commit-pinned symbol source links, so generated C# API `Source` chips resolve to the exact file and line from the CI build revision.
- Pull requests now run the RazorDocs CDN static export during PR validation, while Pages artifact upload and deployment remain limited to `main`, so broken managed links are caught before the public docs pipeline reaches deployment.
- RazorWire CDN export now ignores authoring-only source-navigation anchors, including repo-relative links to common source and project files and app-rendered anchors marked with `data-rw-export-ignore`.
- RazorDocs snapshot caching is now configurable with `RazorDocs:CacheExpirationMinutes`, so development hosts can shorten reuse while production hosts can choose a longer docs and search-index cache lifetime.
- Shared RazorDocs badges, metadata chips, provenance strips, and trust bars now live in the shared package stylesheet while `search.css` stays focused on search-specific UI.
- RazorDocs shared package chrome and search UI now consume one internal `--docs-*` design-token layer, with `search.css` fallback aliases so exact published release trees keep styled search controls even when they load the search stylesheet without the generated package stylesheet.
- RazorDocs authored Markdown pages now use a dedicated prose treatment with a shorter line length, stronger paragraph rhythm, readable lists, clearer links, blockquotes, and inline code while generated API pages keep the wider reference layout.
- RazorDocs fenced Markdown code blocks now render server-side syntax highlighting through TextMateSharp with RazorDocs-owned token classes, language badges, and escaped plaintext fallback when a language is unknown, unsupported, oversized, or cannot be tokenized safely.
- RazorDocs search now keeps failure recovery markup out of the active search shell until the index actually fails to load, so successful searches no longer expose hidden failure copy to text extraction tools.
- RazorDocs search now opens as a richer workspace with representative starter rows, filter-first browsing, stronger no-results recovery, and normalized release badge aliases.
- RazorDocs harvesting now excludes test-project docs and generated example-app API reference from the docs surface while keeping authored example README walkthroughs public.
- RazorDocs now includes a repository root `LICENSE` file as a docs artifact when present, so repo-relative license links remain revision-correct and still pass CDN static export validation.
- RazorDocs now documents the namespace README merge contract with positive and negative examples, while detail-page titles wrap on narrow screens so long package names do not clip on mobile.
- RazorDocs details pages now suppress duplicated leading Markdown H1s when the generated shell owns the page heading, including leading comment markers and merged namespace README intros.
- RazorDocs now has an authored consumer landing page for teams evaluating how to use RazorDocs in their own repositories, and the root docs landing features it through `featured_page_groups` instead of hardcoded controller copy.
- RazorDocs now treats `Releases` as a first-class public section and suppresses breadcrumb links to generated parent routes that do not correspond to published docs pages, keeping static export warnings focused on actionable broken links.
- RazorDocs wayfinding coverage now waits for docs content replacement before asserting sequence-link destinations, keeping the details-page proof path deterministic in CI.
- RazorDocs Playwright integration coverage now hosts the standalone docs app in-process through the standalone host builder, avoiding fixture-time `dotnet run` rebuilds and stale standalone `bin` output during focused test runs.

### RazorWire form UX

- RazorWire-enhanced forms now get a convention-based failed-submission stack: durable request markers, default form-local fallback UI, handled server validation helpers, and runtime events for custom consumers.
- Development anti-forgery failures from RazorWire forms now return useful diagnostics with safe production copy, so stale or missing token problems are easier to fix without exposing implementation detail to users.
- The MVC sample now includes `/Reactivity/FormFailures`, covering validation, anti-forgery, authorization, malformed request, server failure, default styling, CSS variable customization, and manual event-driven rendering.
- The MVC sample counter keeps its compact icon-only button while exposing an `Increment counter` accessible name for assistive technology and role-based tests.

## Migration watch

There is no tagged migration guide yet because AppSurface has not cut `v0.1.0`. Until then:

- breaking changes should be called out here as soon as they land
- the stable policy lives in [Pre-1.0 upgrade policy](./upgrade-policy.md)
- finalized migration steps move into the tagged release note when the version ships
- custom RazorDocs harvesters that want detail-page outlines and search heading metadata should populate `DocNode.Outline`; pages without outline metadata continue to render without the optional outline section
- RazorDocs hosts that need source-backed harvest status should use `DocAggregator.GetHarvestHealthAsync(...)` and branch on `DocHarvestHealthStatus` plus diagnostic codes. Empty snapshots are not failures, degraded snapshots may still serve partial docs, and hosts that publish release artifacts should enable `RazorDocs:Harvest:FailOnFailure` when all-failed snapshots must stop startup.
- `DocAggregator.GetSearchIndexPayloadAsync(...)` is no longer a supported package-consumer API. The live search-index payload is now treated as an internal RazorDocs implementation detail so the host can rebase docs paths and serialize once per request. Consumers that previously called that method directly should switch to the public docs search endpoint or build their own search payload contract instead of depending on RazorDocs' internal snapshot shape.
- existing `rw-active` forms opt into failed-form request markers and default fallback UI; applications with custom failure rendering can use `RazorWireOptions.Forms.FailureMode = Manual`, `RazorWireOptions.Forms.EnableFailureUx = false`, or per-form `data-rw-form-failure="off"`
- RazorDocs authors should migrate flat `featured_pages` metadata to `featured_page_groups`. The old field is ignored and logs a warning; each group needs at least `label` or `intent`, plus a `pages` list containing the existing `question`, `path`, `supporting_copy`, and `order` entries.
- Code that previously read `IHostEnvironment.ApplicationName` to recover a custom AppSurface display label should read `StartupContext.ApplicationName` instead. `IHostEnvironment.ApplicationName` now stays aligned with the host entry-assembly identity used for static web asset discovery unless `StartupContext.OverrideEntryPointAssembly` explicitly selects a different manifest identity. `StartupContext.EntryPointAssembly` still defaults to the root module assembly for command/controller/component discovery, so existing cross-assembly scanning behavior remains stable.
- Web apps with custom conventional 404 views should change `@model ForgeTrust.AppSurface.Web.NotFoundPageModel` to `@model ForgeTrust.AppSurface.Web.BrowserStatusPageModel`. The old 404-only options API names have moved to `BrowserStatusPage*` names before `v0.1.0`; conventional production `500` exception pages now ship as the opt-in fix for issue #224.

## Proof artifacts

- [Release hub](./README.md)
- [Changelog](../CHANGELOG.md)
- [Pre-1.0 upgrade policy](./upgrade-policy.md)
- [Release authoring checklist](./release-authoring-checklist.md)

## Before the first tag

The current intent is that everything already in this repository can be part of `v0.1.0` when AppSurface is ready to release. This page is where that pile becomes visible and reviewable before the tag exists.
