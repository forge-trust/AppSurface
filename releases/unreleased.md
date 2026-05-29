# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.1`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- The `/docs` landing now promotes a Releases entry point alongside product proof paths.
- AppSurface now ships a public release hub, a changelog, an unreleased page, and a tagged release template inside the repository.
- AppSurface now has a consumer-facing [v0.1.0 release preview](./v0.1-preview.md) that package consumers can read before the tag exists. Package-facing docs route release-risk questions into that preview while this unreleased page remains the merged-work proof artifact.
- Release-note pages can show status, freshness, scope, migration guidance, and provenance in a shared trust bar instead of bespoke page chrome.
- AppSurface now ships a generated package chooser that tells first-time adopters which package to install first, which optional modules to add next, and which proof paths to follow for release risk and working examples.
- AppSurface now builds and uploads validated prerelease package artifacts on pull requests and manual workflow runs, using the package chooser manifest as the single source of truth for publish decisions, dependency expectations, tool packages, first-party DLL version identity, and Tailwind runtime payload presence before NuGet publishing is enabled.
- AppSurface NuGet package metadata now points the package project website to `https://appsurface.dev` while keeping repository metadata on GitHub, and package validation rejects artifacts that drift from that public website URL.
- AppSurface now has a protected, tag-only NuGet prerelease publish workflow that revalidates package artifact manifests, force-fetches the pushed annotated tag before validation, uses NuGet Trusted Publishing instead of a long-lived API key, requires a reviewed `nuget-prerelease` environment, writes a redacted publish ledger, and smoke-restores published packages from a clean NuGet configuration before a prerelease is considered ready.
- The public docs Start Here path now leads with an AppSurface evaluator sequence for teams comparing module-based startup against plain ASP.NET Core `Program.cs` configuration.
- Search fallback verification now proves the server-rendered search shell stays useful for no-JS readers, crawlers, failed `search-index.json` requests, and path-base or published-tree mounts before the browser search runtime takes over.
- The root README now has a single hello-world quickstart that starts the smallest web example on an explicit port and proves the response with `curl`.

### Contribution contract

- Pull request titles are now expected to follow Conventional Commits so the merge history is machine-readable for future automation.
- Pull requests are expected to update this page unless maintainers explicitly mark the change as outside the public release story.
- Test and integration process helpers now use CliWrap for child-process execution, keeping stdout and stderr diagnostics available while aligning test infrastructure with the preferred process-launch abstraction.
- The primary build workflow now declares explicit read-only `GITHUB_TOKEN` contents permissions, keeping CI aligned with least-privilege GitHub Actions defaults.
- Markdown-only changes on `main` now republish the docs surface, so release-note and policy edits are treated as first-class product updates.
- AppSurface now exposes focused GitHub issue forms for bug reports, feature requests, and docs/developer-experience feedback, with the root README and contribution guide pointing developers to that feedback path.
- Public contribution surfaces now steer suspected vulnerabilities away from issue forms and into a private security reporting path.
- GitHub issue template support links now point first-time adopters to the package chooser and release/upgrade contract when they are evaluating install path or migration risk.
- The repository now advertises its trust signals from the root README and backs them with Dependabot updates, a formatting-based code-quality workflow, and the repo's existing CodeQL code-scanning setup.

### Console and CLI polish

- AppSurface console apps can now opt into a command-first output contract so public CLI help and validation flows stay quiet instead of printing Generic Host lifecycle chatter.
- AppSurface now publishes the public `appsurface` .NET tool for repository-level workflows, starting with `appsurface docs` for AppSurface Docs preview and `appsurface docs export` for repo-owned AppSurface Docs static export. RazorWire-specific export workflows remain owned by the separate `razorwire` tool.
- RazorWire CLI now uses that contract for `--help`, `export --help`, invalid option output, and missing-source validation while still preserving command-owned export progress logs.
- RazorWire CLI now names export seed-route files with `-r|--seeds`, matching the seed terminology used throughout the exporter and docs.
- The shared console startup seam now exposes `ConsoleOptions` and `ConsoleOutputMode`, so future public AppSurface CLIs can adopt the same behavior without forking startup logic.
- RazorWire CLI now has a first-class .NET tool package contract with the `razorwire` command, supports exact-version `dnx` execution from published or explicit local package sources, and verifies the installed tool path through help and sample export smoke tests. Public package publishing remains manual until the coordinated release automation tracked in #161 lands.
- AppSurface Docs preview now starts the standalone host in-process through `appsurface docs`, keeps routine ASP.NET Core lifecycle output quiet, prints the resolved docs URL plus a concise harvest summary, and attempts to open the page in the system browser once Kestrel is listening.
- RazorWire CLI export now defaults to CDN-safe static output. Managed internal URLs discovered in HTML and CSS are rewritten to emitted artifacts, `<img>` and `<source>` `srcset` candidates are both covered, AppSurface Docs frame content emits static partials, conventional `404.html` participates in the same validation, and CDN validation fails with `RWEXPORT###` diagnostics when required frame or asset dependencies cannot become files.
- RazorWire CLI export discovery now parses HTML with AngleSharp while keeping emitted HTML/CSS rewrites source-preserving. It discovers browser-valid markup such as unquoted and differently cased attributes, exact-token supported `<link rel>` values, `srcset` candidates, CSS `url(...)`, and string-form CSS `@import "..."` references without treating CSS comments, strings, or malformed tokens as dependencies. `RWEXPORT###` diagnostics now include reference provenance and normalized paths, so stricter parser-backed validation explains which static reference to export, fix, ignore, or leave to `--mode hybrid`.
- AppSurface Docs export now has `--redirects html|netlify`. The default `html` strategy keeps GitHub Pages and generic static-host behavior by writing alias HTML fallback files. The `netlify` strategy requires `--mode cdn`, reserves the root `_redirects` file, and writes exact site-local `301!` rules from manifest aliases to canonical live routes without using public origins or emitted `.html` artifact URLs.
- Project exports now disable persistent MSBuild build servers during CLI-controlled publish and assembly-name probes so captured tool output cannot hang on reused build nodes.
- RazorWire CLI process cleanup now waits for asynchronous stdout and stderr callbacks to flush before disposing launched target processes, which keeps short-lived command output observable in tests and diagnostics.
- RazorWire CLI validation errors now include a concrete source-selection example and `razorwire export --help` hint, so a failed export tells developers the next useful command instead of only naming the bad input.
- RazorWire CLI users who still want extensionless, server-routed export output should pass `--mode hybrid`. The default `cdn` mode is for plain static hosts and CDNs, not S3-specific infrastructure.
- PackageIndex now has a real `--help`/`-h` surface that exits successfully, describes its commands and options, and reports unknown commands before printing usage.
- AppSurface docs preview now defaults to the Development host environment when no `--environment` is supplied, so local `appsurface docs` runs with no configured endpoint use the deterministic per-workspace localhost port instead of falling through to Kestrel's port `5000` default.
- AppSurface-owned OpenAPI and Scalar endpoints now use production exposure gates. OpenAPI and Scalar remain zero-configuration in Development, but non-development hosts must opt in with `AppSurfaceWebOpenApi:ExposeEndpoint=Always` and, for Scalar, `AppSurfaceWebScalar:ExposeEndpoint=Always`. Scalar also requires the AppSurface-owned OpenAPI endpoint to be exposed and never maps OpenAPI on its own.

### Core diagnostics

- Core static utilities now use explicit `ILogger` overloads and source-generated `[LoggerMessage]` definitions for host-owned diagnostics. `PathUtils.FindRepositoryRoot` can warn when discovery falls back from a missing path, and parallel enumerable cleanup paths now log suppressed cleanup failures at `Debug` when a caller supplies a logger.
- `ProcessUtils` now runs through CliWrap while keeping the existing AppSurface process contract for captured output, streaming logs, cancellation, and non-throwing non-zero exit codes.

### Dependency maintenance

- The dotnet dependency group has been refreshed to the latest compatible package set, with affected NuGet lock files regenerated. CliFx now targets 3.0.0, and AppSurface Console registers source-generated command descriptors while preserving the existing `ConsoleApp<TModule>` and `ConsoleStartup<TModule>` hosting model.
- The centrally managed `YamlDotNet` dependency now targets `17.0.1`, and the affected PackageIndex, AppSurface Docs, and Aspire lock files have been regenerated.
- The Autofac dependency package now has dedicated test coverage for AppSurface module integration, host container setup, dependent module loading, and implementation scanning.
- The GitHub Actions dependency group now refreshes pinned workflow actions for build, release, package, benchmark, and quality jobs while keeping Codecov uploads token-gated so Dependabot validation can still pass when repository secrets are unavailable.

### Configuration validation

- Strongly typed config wrappers now validate resolved object values with DataAnnotations during startup, including defaults, and report operator-friendly `ConfigurationValidationException` failures without echoing attempted values.
- Configuration audits can now produce a source-aware report for discovered wrappers and explicitly registered keys, showing provider order, file and environment provenance, defaults, validation diagnostics, and redacted display-safe values.
- Configuration audits now expose additive `DiscoveredKeys` for effective merged file-backed configuration visible to enumerable providers. Existing callers continue to work because the property defaults to an empty list, providers without enumeration are ignored, and unknown discovered keys are report-only rather than failing diagnostics.
- AppSurface config now includes a console-agnostic `ConfigDiagnosticsCommandRunner` plus a compiled console sample wrapper for app-owned `config diagnostics` commands. The v1 command path audits the active AppSurface environment only, keeps the Config package free of CliFx and Console dependencies, treats redacted output as internal support data, and documents the startup boundary for apps that fail before commands can run.
- Configuration audits now omit non-sensitive collection parent display values instead of serializing collection contents, preventing nested fields such as passwords, tokens, secrets, and API keys from leaking through raw collection dumps while preserving redaction for sensitive keys and source metadata.
- Audit collection traversal now uses immutable per-entry options snapshots with a mutable registration builder, so opt-in traversal settings are stable once an audit key is registered.
- Config wrappers can now opt into audit collection traversal with `ConfigAuditCollectionTraversalAttribute`, carrying safe traversal defaults through wrapper discovery while still letting explicit manual audit registrations override individual options.
- Nested config validation can now opt into Microsoft Options `[ValidateObjectMembers]` and `[ValidateEnumeratedItems]` markers while AppSurface owns traversal, path formatting, and cycle protection.
- Scalar config wrappers can now validate resolved primitive values directly with `ConfigValueNotEmpty`, `ConfigValueRange`, and `ConfigValueMinLength` attributes, while wrapper-specific scalar rules can override `ValidateValue`.
- Config wrappers can now opt into required resolved presence with `ConfigKeyRequired`, so startup fails when no provider value and no default are available while defaults and supplied zero values still count as present.
- `ConfigKeyAttribute` now lives in its own public API source file, keeping configuration key attributes discoverable while leaving `AppSurfaceConfigModule` focused on service registration.
- The new `examples/config-validation` sample demonstrates an intentional startup validation failure for a scalar `ConfigStruct<int>` without printing the invalid configured value.
- Environment variables can now patch individual members of object-valued config loaded from lower-priority providers, so `APP__SETTINGS__DATABASE__PORT` can override one nested value without replacing the rest of the JSON-backed options object.

### Web host development defaults

- AppSurface web hosts now choose a deterministic localhost-only development URL when no endpoint is configured, while production, staging, container, and appsettings-based endpoint choices remain untouched.
- AppSurface startup environment resolution now treats command-line `--environment` as the highest-priority source before `ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT`, keeping module startup context aligned with Generic Host configuration.
- AppSurface web hosts now fail fast when startup does not complete before `WebOptions.StartupTimeout`, which defaults to 10 seconds and catches pre-bind stalls from sandbox restrictions, package layout issues, static asset discovery, or hosted services that block startup.
- Startup watchdog failures now surface Codex sandbox markers, the observed startup phase, safe path context, static web asset mode, endpoint startup arguments, and a sandbox-first rerun recommendation when applicable.
- OpenAPI's optional web package now has dedicated test coverage for service registration, endpoint mapping, generated document titles, and transformer behavior that removes `ForgeTrust.AppSurface.Web` tags at the document and operation levels while preserving unrelated tags, so the public module contract is guarded independently of Scalar.
- Scalar's optional web package now has dedicated test coverage for OpenAPI dependency wiring, Scalar endpoint mapping, no-op lifecycle hooks, and minimal AppSurface web host composition.
- AppSurface Web CORS options can now restrict allowed request headers and HTTP methods, with production defaults that require explicit preflight headers and methods instead of silently allowing any.
- Tailwind build execution now uses a compiled MSBuild task with stable `ASTW###` diagnostics, structured CLI arguments, bounded output capture, cancellation support, and a packed-package smoke test that proves task/dependency loading from a real nupkg consumer.
- Tailwind development watch mode now treats a missing standalone CLI as a recoverable local-tooling gap: the app keeps serving existing CSS and logs a warning that points to the runtime package or `TailwindOptions.CliPath` override.
- AppSurface's conventional browser 404 page now prioritizes user recovery paths, including documentation search for missing `/docs/...` routes and a home link for other misses, while still documenting how app owners can override the default page.
- AppSurface Web now ships conventional browser status pages for empty HTML `401`, `403`, and `404` responses. The public surface is now `BrowserStatusPageMode`, `BrowserStatusPageModel`, `UseConventionalBrowserStatusPages()`, and `DisableBrowserStatusPages()`, with preview routes at `/_appsurface/errors/401`, `/_appsurface/errors/403`, and `/_appsurface/errors/404`.
- Browser status page overrides are status-specific: use `~/Views/Shared/401.cshtml`, `~/Views/Shared/403.cshtml`, or `~/Views/Shared/404.cshtml`. JSON/API responses, non-empty responses, and non-GET/HEAD requests keep their original behavior.
- Static export remains deliberately 404-only. RazorWire CLI probes `/_appsurface/errors/404` and writes `404.html`; it does not emit `401.html` or `403.html`.
- AppSurface Web can now opt into a conventional production 500 page backed by ASP.NET Core exception handling, rendering only safe generic copy and a request id while leaving Development exception diagnostics and API-oriented responses alone.
- AppSurface now assigns explicit numeric values to public Web and RazorWire enums, preserving existing ordinals for consumers that persist, serialize, bind, or compare those values.
- AppSurface startup now keeps custom `StartupContext.ApplicationName` values as display labels while preserving assembly-backed host identity for ASP.NET static web asset manifests, so custom-labeled web hosts can still serve package styles and scripts.

### RazorWire package guidance

- RazorWire now has a generated UI design contract for package-owned nodes. The contract separates RazorWire UI from app-authored markup and AppSurface Docs chrome, establishes `data-rw-*` attributes plus `--rw-ui-*` custom properties as the default styling surface, and documents global, form-level, and target-level override expectations for future generated UI.
- RazorWire browser runtime assets now have an authored TypeScript pipeline under `Web/ForgeTrust.RazorWire/assets/src`, generated committed outputs under `wwwroot/razorwire`, focused pnpm typecheck/test/build/verify commands, a pack-only freshness guard, and a docs-only JavaScript contract manifest so public API harvesting survives minification.
- RazorWire README snippets are now source-backed by the MVC sample through a MarkdownSnippets generator, with CI verification and README contract tests guarding quickstart drift.
- RazorWire stream subscriptions are now denied by default through `RazorWireOptions.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.DenyAll`, with an explicit `RazorWireStreamAuthorizationMode.AllowAll` mode for public/demo streams and `IRazorWireChannelAuthorizer` preserved as the request-aware extension point for user, tenant, and workflow-specific channels.
- RazorWire streams can now emit same-origin Turbo Drive visit commands with `RazorWireStreamBuilder.Visit(...)`, giving live subscribers a narrow one-shot navigation primitive while keeping retained replay channels reserved for state snapshots.

### AppSurface Docs product example

- AppSurface's own release pages now double as a working AppSurface Docs example for consumers who want better release notes.
- AppSurface Docs now supports a static-first versioned docs surface: `/docs` can point at the recommended released tree, `/docs/next` can stay on the live preview, `/docs/v/{version}` can serve exact historical releases, and `/docs/versions` can act as the public archive.
- Recommended AppSurface Docs aliases now emit canonical metadata for the matching exact-version route when they serve the same frozen release tree, so `/docs` stays reader-friendly while crawlers can consolidate duplicates under `/docs/v/{version}`.
- Recommended AppSurface Docs alias coverage now guards canonical query and fragment preservation, unmapped canonical href fallbacks, and non-canonical link asset rewriting so future release-tree mounts keep that crawler contract intact.
- AppSurface Docs route families can now be mounted away from `/docs` with `AppSurfaceDocs:Routing:RouteRootPath`, so consumers can host docs under paths such as `/foo/bar` while archive, exact-version, search, metadata, and static export URLs stay aligned. AppSurface Docs now registers only its configured docs routes instead of an app-wide controller/action fallback, keeping docs routing isolated from other modules.
- AppSurface Docs can now render absolute canonical metadata with `AppSurfaceDocs:Routing:PublicOrigin`, and `appsurface docs` plus `appsurface docs export` expose the same setting as `--public-origin` so loopback preview or export hosts can publish canonical links for their real public origin.
- Published AppSurface Docs release trees are now catalog-driven and validated before they are mounted, so broken historical exports stay unavailable instead of half-rendering with cross-version search or asset leakage.
- AppSurface Docs pages can now expose typed `On this page` outlines, explicit proof-path previous/next links, related-page cards, and sidebar anchor navigation from harvested metadata instead of scraping rendered HTML.
- AppSurface Docs details pages now render those `On this page` outlines as a page-local navigation surface, using a sticky desktop rail, a compact narrow-viewport drawer, and active-section state that keeps the reader oriented without competing with the global sidebar.
- AppSurface Docs compact `On this page` outlines now stay visible while reading on narrow viewports, showing smaller previous/next context around the current section with reduced-motion-safe rolling label updates.
- AppSurface Docs compact `On this page` outlines now contain their own scrolling while expanded, preventing touch or wheel input over the outline from scrolling the article behind it.
- AppSurface Docs detail-page outlines and section headers now include copy-link actions for deep section URLs, with a manual-copy fallback when clipboard writes are unavailable or denied.
- AppSurface Docs details pages now emit the outline client as a normal deferred script asset, so static exports publish `/docs/outline-client.js` through the existing asset crawler instead of depending on an inline loader.
- AppSurface Docs detail-page outlines now keep long-section active states and the desktop right rail aligned, including the full-height rail rule, active-item visibility on long pages, and animated section jumps.
- AppSurface Docs Markdown outlines now adapt when repeated H3 headings would overwhelm the reader-facing `On this page` rail, while retaining full rendered headings, fragment IDs, and search recall in the page body.
- The AppSurface Docs homepage now keeps navigation rows title-led and quiet: Start Here, featured pages, and secondary routes no longer repeat "Open..." labels, and each row uses a decorative circled chevron while preserving full-row links, Turbo frame behavior, and explicit accessible labels.
- Public docs navigation now groups pages by intent-first sections, preserves authored editorial breadcrumbs, and keeps Start Here recovery links hidden when that section is unavailable.
- AppSurface Docs API Reference navigation now keeps the primary sidebar at package and namespace depth, nests deeper namespace pages under their nearest parent with leaf labels, and leaves generated type and member anchors to namespace-page outlines, source links, and search instead of expanding hundreds of symbols in the left rail.
- AppSurface Docs landing curation now uses `featured_page_groups`, so root and section landing pages can organize next-step links by reader intent instead of rendering one flat list.
- AppSurface Docs now exposes structured harvest health through `DocAggregator.GetHarvestHealthAsync(...)`, letting hosts distinguish healthy, valid-empty, degraded, and all-failed source-backed docs snapshots while keeping raw exception details in logs.
- AppSurface Docs hosts can now opt into strict startup failure with `AppSurfaceDocs:Harvest:FailOnFailure`, so CI, release, and static export runs fail before listening when every configured harvester fails while runtime hosts stay tolerant by default.
- AppSurface Docs now renders a compact Development-only diagnostics disclosure in the sidebar for harvest health and route-inspector tools, lets operators configure sidebar discovery independently from route exposure, and strips that maintainer chrome from static export artifacts.
- AppSurface Docs source harvesting now honors repository-owned Git `.gitignore` files by default, so legacy generated and package-manager-owned trees such as `bower_components/`, `dist/`, and `build/` stay out of Markdown, C#, and JavaScript docs harvests unless a host restores selected paths with `AppSurfaceDocs:Harvest:Paths:VcsIgnore:AllowGlobs`. AppSurface Docs still ignores tracked files that match those repository rules, never reads `.git/info/exclude` or global excludes, and can temporarily keep the old harvest behavior with `AppSurfaceDocs:Harvest:Paths:VcsIgnore:Enabled=false` while a host migrates intentional public docs.
- AppSurface Docs and RazorWire now compile their package-owned runtime assets into their assemblies and map endpoint fallbacks for those assets. Static web assets remain the normal host path when manifests are available, while packaged CLI hosts can still serve the docs stylesheet, search scripts, outline script, and RazorWire runtime from compiled assemblies.
- AppSurface Docs now exposes a local-first harvest health UI at `{DocsRootPath}/_health` plus a machine-readable `{DocsRootPath}/_health.json` endpoint, shown by default in Development and configurable independently from sidebar chrome for operator-owned environments.
- AppSurface Docs now exposes a Development-only route inspector at `{DocsRootPath}/_routes` plus `{DocsRootPath}/_routes.json`, letting maintainers inspect canonical routes, recovery aliases, declared aliases, route diagnostics, and individual path probes without adding maintainer tools to public reader navigation.
- AppSurface Docs now starts the initial source harvest in the background by default and renders a live RazorWire harvest observatory for cold requests that outlive the first-request wait budget, so readers see redacted progress instead of a hung page while the docs snapshot is assembled.
- AppSurface Docs harvest completion now publishes retained completion state before a live-only RazorWire visit command, so active readers leave the observatory automatically while late subscribers replay state without inheriting stale navigation.
- Harvest observability includes deterministic local testing delays, shared startup/request coordination, source-shaped route redirects before observatory fallback, sidebar suppression while harvesting, and safe app-relative return navigation once the snapshot is ready.
- The harvest observatory now keeps request-derived return URLs out of raw progress fragments, leaving Razor views to emit validated app-relative return links through normal HTML encoding while the live progress renderer carries only encoded status content.
- Stale-while-revalidate memo policies are now available for absolute-expiration entries, allowing AppSurface Docs and future callers to serve bounded stale snapshots while one background refresh revalidates the value.
- RazorWire streams support opt-in replay for retained state messages, with `rw:stream-source replay="true"` subscribing late readers to the bounded replay buffer before live events.
- AppSurface Docs page lookup now uses one shared path resolver for details pages, landing curation, related-page links, and search recovery links, keeping source paths, canonical `.html` paths, fragments, backslash normalization, and configured docs-root prefixes behaviorally aligned.
- AppSurface Docs authored Markdown pages now publish clean canonical routes that follow their public section hierarchy, so teams can link to URLs such as `/docs/packages` instead of repository-shaped `README.md.html` paths while source-path lookups and declared aliases continue to work.
- AppSurface Docs now permanently redirects public Markdown source-shaped requests such as `/docs/packages/README.md` to the clean canonical route, so links copied from GitHub or editor paths recover instead of falling into the generic 404 page.
- AppSurface Docs static exports now carry those source-shaped recovery paths forward as validated redirect aliases. The default HTML strategy writes tiny redirect artifacts, while Netlify export writes a validated `_redirects` provider file so CDN-hosted docs recover links pasted from repository Markdown paths without publishing duplicate page bodies.
- AppSurface Docs exact release exports now include a frozen route manifest, letting mounted historical archives redirect source-shaped and declared aliases to the canonical routes from that release instead of reinterpreting old links through current docs rules.
- AppSurface Docs now treats `Packages` as a first-class public section and standardizes AppSurface-owned namespace intros on colocated `NAMESPACE.md` files, while keeping docs-folder namespace README merging as compatibility behavior for portable layouts.
- AppSurface Docs now renders content-derived cache keys on package-owned CSS and JavaScript assets, including search, MiniSearch, outline, and generated stylesheet URLs, so browsers and CDNs fetch matching chrome after static asset deployments instead of reusing stale cached files.
- The release contract is designed so future tooling can generate both a changelog entry and a blog-style tagged release note from the same underlying signals.
- AppSurface Docs now rewrites authored doc links from a harvested target manifest instead of broad suffix heuristics, so normal site links such as `../privacy.html` stay untouched and missing doc targets do not become broken `/docs/...` routes.
- AppSurface Docs details pages can now render a `Source of truth` strip with `View source`, `Edit this page`, and relative `Last updated` evidence driven by contributor metadata, configured URL templates, and git freshness when available.
- The primary AppSurface Docs Pages deployment now exports with contributor provenance configured and full git history available, so the public docs artifact can show the same `Source of truth` strip as local smoke tests.
- Contributor provenance now degrades safely: namespace and API pages stay explicit-override-only for the MVP, and missing or slow git history omits only freshness instead of breaking docs rendering.
- AppSurface Docs generated C# API references can now render per-symbol source links for documented types, methods, properties, and enums that point at the exact source file and line, with immutable refs available when hosts want links pinned to the code version used to build the docs.
- The primary AppSurface Docs Pages deployment now configures commit-pinned symbol source links, so generated C# API `Source` chips resolve to the exact file and line from the CI build revision.
- Pull requests now run the AppSurface Docs CDN static export during PR validation, while Pages artifact upload and deployment remain limited to `main`, so broken managed links are caught before the public docs pipeline reaches deployment.
- The primary AppSurface Docs Pages export now uses `appsurface docs export --repo .` instead of the generic `razorwire export --project` child-process path, so CI exercises the public AppSurface docs command, in-process standalone host startup, strict harvest failure, and CDN validation together.
- RazorWire CDN export now ignores authoring-only source-navigation anchors, including repo-relative links to common source and project files and app-rendered anchors marked with `data-rw-export-ignore`.
- AppSurface Docs snapshot caching is now configurable with `AppSurfaceDocs:CacheExpirationMinutes`, so development hosts can shorten reuse while production hosts can choose a longer docs and search-index cache lifetime.
- AppSurface Docs source harvesting now has configurable global, Markdown, C#, and JavaScript path-policy scopes, including include/exclude globs and named default-exclusion controls for build output, hidden directories, test projects, and example C# source.
- Shared AppSurface Docs badges, metadata chips, provenance strips, and trust bars now live in the shared package stylesheet while `search.css` stays focused on search-specific UI.
- AppSurface Docs shared package chrome and search UI now consume one internal `--docs-*` design-token layer, with `search.css` fallback aliases so exact published release trees keep styled search controls even when they load the search stylesheet without the generated package stylesheet.
- AppSurface Docs release trust bars now reject unsafe `trust.migration.href` schemes during Markdown metadata harvest, treat blank migration hrefs as absent, surface `unsafe-trust-migration-href` diagnostics through harvest health, and still allow safe sidecar metadata to provide the migration link.
- AppSurface Docs legacy asset redirects now validate app-relative redirect targets before preserving cache-busting query strings, closing an open-redirect path while keeping virtual-path deployments and packaged asset fallbacks working.
- AppSurface Docs authored Markdown pages now use a dedicated prose treatment with a shorter line length, stronger paragraph rhythm, readable lists, clearer links, blockquotes, and inline code while generated API pages keep the wider reference layout.
- AppSurface Docs fenced Markdown code blocks now render server-side syntax highlighting through TextMateSharp with AppSurface Docs-owned token classes, language badges, and escaped plaintext fallback when a language is unknown, unsupported, oversized, or cannot be tokenized safely.
- AppSurface Docs code-fence language badges now render as package-owned chrome instead of inline copied text, keeping search indexing and clipboard behavior focused on the authored code body while preserving visible language labels.
- AppSurface Docs generated C# and JavaScript API documentation now carries first-class source-language metadata, renders language chips, and exposes a searchable `Language` facet in the search workspace.
- AppSurface Docs search now keeps failure recovery markup out of the active search shell until the index actually fails to load, so successful searches no longer expose hidden failure copy to text extraction tools.
- AppSurface Docs search now renders starter query URLs and browse recovery links in the server shell, so no-JS readers, crawlers, static `search.html` exports, and failed `search-index.json` fetches still have real documentation paths to follow.
- AppSurface Docs search now opens as a richer workspace with representative starter rows, filter-first browsing, stronger no-results recovery, and normalized release badge aliases.
- AppSurface Docs search now has a first-class pnpm and TypeScript asset workspace, building the generated search client and pinned upstream MiniSearch browser runtime from typed source with typecheck, field-search, provenance, and generated-file verification gates in CI.
- AppSurface Docs harvesting now excludes test-project docs and generated example-app API reference from the docs surface while keeping authored example README walkthroughs public.
- AppSurface Docs now includes a repository root `LICENSE` file as a docs artifact when present, so repo-relative license links remain revision-correct and still pass CDN static export validation.
- AppSurface Docs now documents the namespace README merge contract with positive and negative examples, while detail-page titles wrap on narrow screens so long package names do not clip on mobile.
- AppSurface Docs details pages now suppress duplicated leading Markdown H1s when the generated shell owns the page heading, including leading comment markers and merged namespace README intros.
- AppSurface Docs now has an authored consumer landing page for teams evaluating how to use AppSurface Docs in their own repositories, and the root docs landing features it through `featured_page_groups` instead of hardcoded controller copy.
- AppSurface Docs namespace pages now merge namespace-level `README.md` guidance back above generated API reference content, support authored `entry_points` sidecar metadata for common API starting points, redirect consumed README source-shaped routes to the generated namespace page, and project those entry-point terms into the search payload while the richer search engine follow-up remains separate.
- AppSurface Docs now keeps built-in brand titles inside the sidebar/header bounds with ellipsis clipping for long names, defaults docs chrome to the shorter `Documentation` title, and lets the public AppSurface Pages export opt into the compact AppSurface wordmark plus blue `Surface` highlight through `AppSurfaceDocs:Identity` configuration.
- AppSurface Docs public branding now distinguishes the spaced product name from the `AppSurfaceDocs` configuration root, renders the sidebar footer from the configured docs identity instead of static RazorWire copy, and guards those public surfaces with regression tests.
- AppSurface Docs now supports repository-owned branding asset directories for consumer logos and favicons, while AppSurface's own Pages export opts into its custom stacked-pane icon and default standalone docs consumers keep the built-in document-layers favicon.
- AppSurface Docs now renders a configured docs logo as the root landing page hero mark, keeping the first-screen content aligned with custom-branded docs chrome and favicons.
- AppSurface Docs now treats `Releases` as a first-class public section and suppresses breadcrumb links to generated parent routes that do not correspond to published docs pages, keeping static export warnings focused on actionable broken links.
- AppSurface Docs wayfinding coverage now waits for docs content replacement before asserting sequence-link destinations, keeping the details-page proof path deterministic in CI.
- AppSurface Docs Playwright integration coverage now hosts the standalone docs app in-process through the standalone host builder, avoiding fixture-time `dotnet run` rebuilds and stale standalone `bin` output during focused test runs.
- AppSurface Docs now uses the new AppSurface brand system across the docs shell, landing page, search workspace, iconography, and responsive article outline, while keeping the UI focused on developer wayfinding instead of marketing chrome.
- AppSurface Docs search result rows now use one semantic full-row link, so touch users can tap anywhere in a visible result while keyboard focus, copied links, and open-in-new-tab behavior stay native.
- First localization foundation slice for AppSurface Docs: disabled-by-default locale configuration, localized front matter metadata, inferred `README.fr.md`-style variant grouping, diagnostics for unsupported or ambiguous locale signals, and an internal route/search graph seam for later visible localized pages.
- AppSurface Docs JavaScript public API harvesting now runs by default for policy-approved `.js` files while still publishing only explicit `@public` browser contracts. Hosts can opt out with `AppSurfaceDocs:Harvest:JavaScript:Enabled=false`, narrow scanning with JavaScript include globs, and keep broad discovery best-effort unless `StrictHealth=true` is enabled.
- AppSurface Docs now dogfoods RazorWire JavaScript API harvesting through `Web/ForgeTrust.RazorWire/assets/contracts/razorwire-public-contracts.js` instead of minified runtime output, keeping generated browser assets small while preserving documented globals, events, DOM hooks, CSS hooks, and island module contracts.
- AppSurface Config audit reports now support opt-in safe collection element traversal with bounded depth, element, and node limits, source-aware array/list provenance, redacted dictionary key labels, element identity metadata, deterministic numeric rendering, and diagnostics for unsupported or truncated traversal.

### RazorWire form UX

- RazorWire-enhanced forms now get a convention-based failed-submission stack: durable request markers, default form-local fallback UI, handled server validation helpers, and runtime events for custom consumers.
- Development anti-forgery failures from RazorWire forms now return useful diagnostics with safe production copy, so stale or missing token problems are easier to fix without exposing implementation detail to users.
- The MVC sample now includes `/Reactivity/FormFailures`, covering validation, anti-forgery, authorization, malformed request, server failure, default styling, CSS variable customization, and manual event-driven rendering.
- The MVC sample now persists its demo username cookie with `Secure`, `HttpOnly`, and `SameSite=Lax`, and its browser-level regression coverage runs through `localhost` so local development keeps the secure-cookie behavior observable.
- The MVC sample counter keeps its compact icon-only button while exposing an `Increment counter` accessible name for assistive technology and role-based tests.

### Dependency maintenance

- The central .NET dependency set now carries `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Hosting`, and `Microsoft.Extensions.Logging.Console` `10.0.8`, while the ABP benchmark host now uses ABP `10.4.0`; solution lock files were regenerated so locked restore sees the same graph in CI and local development.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
