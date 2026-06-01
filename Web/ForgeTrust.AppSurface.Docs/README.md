# ForgeTrust.AppSurface.Docs

Documentation site generation and hosting for AppSurface web applications.

## Overview

`ForgeTrust.AppSurface.Docs` is the reusable Razor Class Library package behind the AppSurface Docs experience. It aggregates Markdown and C# API documentation into a browsable docs UI, supports an optional version archive for published releases, and is intended to be embedded into AppSurface web applications or used by the standalone AppSurface Docs host.

If you are evaluating AppSurface Docs for your own repository, start with [Use AppSurface Docs in your repository](./use-appsurface-docs.md). That page explains the consumer model, host shape, authoring metadata, and adoption checklist before you drill into this package reference.

## Preview and Export Commands

Use the AppSurface CLI when working with this repository's AppSurface Docs surface:

```bash
appsurface docs --repo . --port 5189
appsurface docs preview --repo . --port 5189
appsurface docs export --repo . --output ./dist/docs --mode cdn --strict
appsurface docs verify-archive --catalog ./docs-versions.json --version 1.2.3
appsurface docs verify-archive --catalog ./docs-versions.json --version 1.2.3 --trusted-release-root ./published-docs
```

`appsurface docs` and `appsurface docs preview` run the standalone host for local inspection. `appsurface docs export` starts that same host in-process, binds an internal `http://127.0.0.1:0` listener, resolves the actual Kestrel address, and exports through RazorWire's static export engine.

Preview `--port` values bind localhost only. Add `--all-hosts` to a `--port` preview only when LAN, container, or other non-loopback access is intentional; the all-hosts wildcard can expose the preview host beyond the local machine.

Export defaults to `Production`, writes to `dist/docs` when `--output` is omitted, rejects existing files passed to `--output`, and seeds `/` plus the resolved docs root, `/docs` by default. Pass `--seeds <file>` for deterministic crawl roots in CI. `--seeds` has no short alias because `-r` means `--repo` for AppSurface docs commands. After final files are materialized, export writes `.appsurface-docs-release-manifest.json` and prints the `releaseManifestSha256` catalog snippet to copy into the published version catalog. Use `appsurface docs verify-archive --catalog <path> --version <version>` to check a pinned archive locally before deploy. Pass `--trusted-release-root <path>` when the runtime catalog resolves root-relative `exactTreePath` values through `AppSurfaceDocs:Versioning:TrustedReleaseRootPath`; this keeps local verification on the same trusted-root contract as production.

Redirect aliases default to HTML fallback materialization. Omit `--redirects`, or pass `--redirects html`, for GitHub Pages and generic static hosts. Pass `--mode cdn --redirects netlify` for Netlify-compatible CDN publishing; export writes one root `_redirects` file with exact site-local `301!` rules and does not write alias HTML files. Netlify export validates the encoded provider rule paths, so self-redirects and same-source aliases that point at different canonical routes fail before files are written. Do not hand-author `_redirects` in the export output because the exporter reserves that file for validated redirect rules.

`--strict` and `--mode cdn` check different things. `--strict` fails host startup when every active harvester fails. `--mode cdn` validates the emitted static artifact and preserves RazorWire `RWEXPORT00x` diagnostics for missing or unrewritable managed URLs.

Use `razorwire export` for arbitrary RazorWire applications that need `--url`, `--project`, or `--dll`. Use `appsurface docs export` when AppSurface owns the AppSurface Docs repository host.

## What It Provides

- `AppSurfaceDocsWebModule` for wiring the docs UI into an AppSurface web host
- `AddAppSurfaceDocs()` for typed options binding and core service registration
- `DocAggregator` plus the built-in Markdown, C# API, and annotation-first JavaScript public API harvesters, including structured harvest health diagnostics
- A live harvest observatory that starts the first source-backed harvest during startup, streams real-time RazorWire progress, and keeps first navigation informative instead of appearing hung
- Search UI assets, page-local outline behavior, and the `/docs` MVC surface used by AppSurface Docs consumers
- `DocsUrlBuilder` plus the MVC surface used by AppSurface Docs consumers so the live docs root, search shell, and archive routes stay in one shared contract
- `AppSurfaceDocsVersionCatalog` plus `AppSurfaceDocsVersionCatalogService` for mounting exact published release trees and surfacing release-level status in the public archive
- Structured trust metadata plus a built-in trust bar for release notes, upgrade guides, and other pages that need status and provenance near the top
- A source-backed localization foundation with typed locale options, locale metadata, translation-set inference, and diagnostics for serious multi-language docs systems
- Contributor provenance rendering with a `Source of truth` strip for source links, edit links, and relative `Last updated` timestamps on details pages
- Precompiled Tailwind-powered styling with layout-time path resolution for root-module and embedded hosts

## Styling Boundary

When choosing where a new AppSurface Docs style should live, use this order:

1. If the surface needs a reusable component contract or a selector shared across CSS and JavaScript, use a semantic class.
2. Otherwise, if AppSurface Docs does not fully control the nested content markup, use wrapper-scoped semantic CSS.
3. Otherwise, for one-off package chrome that AppSurface Docs owns directly, prefer Tailwind utility classes in markup.

This section is the normative source of truth for the boundary. `DESIGN.md` explains why the rule exists and how to review edge cases. `ROADMAP.md` only points future work back to this contract.

### Decision Matrix

| Surface | Default | Why | Real examples | Exception / note |
| --- | --- | --- | --- | --- |
| One-off owned package chrome in Razor views | Prefer Tailwind utility classes in markup | AppSurface Docs fully owns the markup, so local utility classes keep intent obvious where the change happens | docs landing shell in `Views/Docs/Index.cshtml`, sidebar shell and layout framing in `Views/Shared/_Layout.cshtml`, one-off page header spacing in `Views/Docs/Details.cshtml` | If the same styling contract repeats across package surfaces, promote it to a semantic component class instead of copying long utility strings |
| Reusable owned package components or stable cross-file UI selectors | Use semantic component classes in the shared package stylesheet | Shared selectors keep repeated UI stable across Razor, CSS, and sometimes JavaScript | `docs-page-badge`, `docs-metadata-chip`, `docs-page-meta`, `docs-provenance-strip`, `docs-trust-bar`, and `docs-outline-*` in `wwwroot/css/app.css` | Utilities can still handle surrounding layout and one-off placement |
| Harvested or generated document bodies that AppSurface Docs does not fully author element by element | Use wrapper-scoped semantic CSS such as `.docs-content ...` in the shared package stylesheet | AppSurface Docs cannot safely push utility classes into nested harvested HTML | headings, paragraphs, code blocks, overload groups, and namespace sections inside `.docs-content` in `Views/Docs/Details.cshtml` and `wwwroot/css/app.css` | Do not rewrite harvested nested HTML just to satisfy utility-class purity |
| JavaScript-generated or stateful UI that needs CSS and JavaScript to share stable hooks | Use semantic hook classes, then style them in CSS | Runtime UI needs stable names both the stylesheet and script can rely on | search result rows, filter chips, active-filter pills, and state containers in `wwwroot/docs/search.css` and `wwwroot/docs/search-client.js` | Use `id` values where uniqueness or ARIA wiring require them, but keep reusable styling and state contracts on semantic classes |

### Common Calls

- New one-off page header spacing or typography in owned Razor markup: use Tailwind utilities in the view.
- New reusable badge, metadata chip, page metadata row, trust/provenance surface, or page-local outline state: add or extend a semantic component class in `wwwroot/css/app.css`, then use utilities around it only when they are purely local.
- For `Views/Docs/Search.cshtml`, keep the stateful search container or interactive hook semantic, but use local utilities for one-off header copy, helper layout, and fallback-link chrome inside that view.
- JavaScript-rendered search result rows should expose one block-level anchor for the whole result, not a JavaScript-only click handler or nested links. Give that anchor a concise `aria-label` such as `Open AppSurface Docs Roadmap in How-to Guides` so screen reader link lists do not read every breadcrumb, badge, path, and snippet. This keeps mobile taps, keyboard focus, copy-link, and open-in-new-tab behavior aligned with normal browser expectations.
- Restyling paragraphs, headings, or code blocks inside `.docs-content`: update wrapper-scoped CSS instead of pushing utility classes into harvested HTML.
- Markdown pages with long-form prose use `.docs-content--markdown` for prose measure, paragraph rhythm, list spacing, links, blockquotes, and inline code. Generated non-Markdown docs and docs marked with `page_type: api` or `page_type: api-reference` use `.docs-content--api` so signatures and reference tables keep the wider base content measure.
- New search filter pill, active-filter surface, or other stateful search UI: use a semantic hook class because CSS and JavaScript both need to recognize it.

### Stylesheet Responsibilities

- `wwwroot/css/app.css` is the Tailwind entry point for the generated package stylesheet (`site.gen.css`). It owns shared AppSurface Docs component primitives and wrapper-scoped document body styling because the generated stylesheet is loaded on every docs page before any search-specific assets.
- `wwwroot/docs/search.css` owns the search shell, interactive search controls, JavaScript-rendered result states, empty/failure states, and search skeletons. It should not define shared page badges, metadata chips, provenance strips, trust bars, or other primitives required by non-search docs pages.
- `assets/src/search-client.ts` is the authored source for the Docs search browser runtime. `pnpm --dir Web run assets:build` bundles and minifies it into `wwwroot/docs/search-client.js`.
- `wwwroot/docs/minisearch.min.js` is generated from the pinned `minisearch` package's distributed UMD browser bundle. The exact version, source path, license, and update procedure live in `THIRD-PARTY-NOTICES.md`.

### Internal Style Tokens

`wwwroot/css/app.css` declares AppSurface Docs' shared dark-slate style tokens on `:root` with `--docs-*` custom properties. These tokens describe the current flagship visual system: slate surfaces, muted borders, readable text, cyan accents, focus rings, active fills, code chrome, table chrome, and skeleton treatments.

The tokens are internal package implementation details. They ship in browser CSS because AppSurface Docs CSS ships to the browser, but hosts should not treat them as a supported override API yet. Future theming work can promote a documented public contract once the host customization model is designed.

Use tokens when a value is either:

- repeated across two or more unrelated selector groups
- part of a documented repeated state such as focus, active selection, muted text, default border, raised surface, code chrome, table chrome, or skeleton loading

Leave a raw literal local when naming it would lie about its scope. Allowed local categories are:

- syntax-highlight token colors such as keyword, string, comment, number, type, member, operator, inserted, and deleted spans
- API signature token colors used only to distinguish return values, parameters, modifiers, literals, and similar generated reference fragments
- one-off semantic page-type badge variants such as example, API/reference, glossary, FAQ, internals, and troubleshooting
- browser or generated-content details that do not represent a reusable design primitive

Do not add broad fallbacks such as `var(--docs-color-text-default, #e2e8f0)` unless a generated asset, load-order, or host-embedding test proves the fallback is needed. The package-owned shared stylesheet should consume the internal tokens directly. `wwwroot/docs/search.css` is the exception: exact published release trees are allowed to carry only `search.css` as their required CSS asset, so it defines `--docs-search-*` fallback aliases that read the shared `--docs-*` tokens when available and preserve a self-contained search UI when the generated package stylesheet is absent.

### Terms

- **Package chrome**: one-off layout and presentation markup that AppSurface Docs owns directly, such as page shells, spacing, and framing.
- **Harvested content**: nested documentation HTML that AppSurface Docs renders but does not fully author element by element, such as the body inside `.docs-content`.
- **Markdown prose surface**: authored Markdown rendered with `.docs-content--markdown`; it optimizes for reading dense release notes, guides, and README-style pages rather than for wide API signatures in generated reference docs.
- **Stable selector / hook**: a semantic class or required unique `id` that Razor, CSS, accessibility wiring, and sometimes JavaScript rely on consistently across files.

### Pitfalls

- Do not refactor between utilities and semantic CSS for purity alone. Follow the surface contract unless a real usability or maintainability problem exists.
- Do not treat required `id` values, such as `docs-search-page-input` or `docs-search-page-filters-panel`, as the reusable styling contract. They exist for uniqueness, targeting, and ARIA relationships.
- Do not assume every child inside a semantic search container needs its own semantic class; local typography and spacing inside one view can still stay inline.
- Do not add semantic classes to static package chrome when plain utilities are clearer and the styling is truly local.
- Do not place non-search primitives in `wwwroot/docs/search.css` just because the layout loads search assets globally today. Use `wwwroot/css/app.css` for shared components so future theming can target one stable package layer.
- Do not introduce new hardcoded slate/cyan literals inside shared selector groups. Add or reuse a `--docs-*` token instead.
- Do not move syntax-highlight colors into the shared token layer until AppSurface Docs has a public code-theme story. Code block chrome can use shared tokens; syntax spans stay local.
- Do not make search result rows feel tappable by adding a row-level `click` listener while the real anchor stays only on the title. That makes touch behavior work while breaking native link affordances.
- Do not rely on the full wrapped row text as the accessible name for a full-row search result link. The row can be visually rich while the link name stays short.
- Do not edit generated `wwwroot/docs/search-client.js` or `wwwroot/docs/minisearch.min.js` by hand. Edit `assets/src/search-client.ts` or the pinned `minisearch` dependency, run `pnpm --dir Web run assets:build`, and then run `pnpm --dir Web run assets:verify`.

## Details Page Heading Ownership

AppSurface Docs details pages render the page title in the package-owned shell for authored Markdown pages. The title comes from `DocDetailsViewModel.Title`, which resolves metadata `title` first, then a leading Markdown H1, then the harvested file or folder fallback.

Because the shell already owns the semantic page H1, `Views/Docs/Details.cshtml` suppresses only a leading rendered Markdown `<h1>` from the harvested body before writing `.docs-content`. This keeps source Markdown portable for GitHub and editor previews, where a top `# Title` is still useful, without showing duplicate page headings in AppSurface Docs.

The suppression is intentionally narrow:

- It runs only when the details shell renders the H1. C# API reference pages keep their harvested body heading because the shell hides its top H1 for generated API content.
- It removes only the first body element when that element is an H1. Later H1 elements remain visible because they are body structure, not duplicated chrome.
- Namespace intros apply the same rule before the intro HTML is wrapped in `.doc-namespace-intro`, so `# Namespace` stays useful in source while the generated namespace shell remains the only page H1.
- For ordinary Markdown pages, suppression happens at render time. `DocNode.Content`, search extraction, and outline generation still see the harvested document as produced by the harvester.
- A leading Markdown H1 still participates in title resolution when explicit metadata `title` is absent, so README-style pages keep their authored title in the shell after the body H1 is suppressed.

Pitfall: do not work around duplicate headings by removing the source `# Title` from README-style pages. That makes the file worse outside AppSurface Docs. Let the AppSurface Docs shell suppress the rendered duplicate instead.

## Generated API language tags

Generated code documentation carries programming-language metadata through `DocMetadata.CodeLanguage`. The built-in C# API harvester marks generated namespace pages and symbol stubs as `csharp`; the optional JavaScript public API harvester marks generated group pages and doclet stubs as `javascript`.

AppSurface Docs normalizes these values for reader chrome and search. `csharp`, `c-sharp`, and `cs` display as `C#`; `javascript`, `java-script`, and `js` display as `JavaScript`; unknown nonblank values fall back to safe title-cased labels. Details pages render the language as a metadata chip, and the built-in search workspace exposes it as a `Language` facet using `?language=` query state. The search index also includes language search terms so queries such as `javascript`, `js`, `csharp`, `CSharp`, `C-Sharp`, and `C#` can find generated API docs.

This language tag describes the source language of extracted API documentation. It is not a locale signal and it is not the same as the `data-doc-code-language` badge used by Markdown code fences.

## Syntax-highlighted code blocks

AppSurface Docs renders fenced Markdown code blocks during Markdown harvest. Supported languages are highlighted server-side, so normal docs pages and exported docs do not need client-side Prism, highlight.js, or Shiki initialization after navigation.

The v1 contract is AppSurface Docs-owned HTML:

```html
<pre class="doc-code doc-code--highlighted doc-code--language-csharp language-csharp" data-doc-code-language="C#"><code>...</code></pre>
```

Plain fallback uses the same shape with `doc-code--plain`. `data-doc-code-language` is renderer chrome: the package stylesheet may display it as a code-block badge, but AppSurface Docs does not insert the language label as text inside `<pre>` or `<code>`. Search indexing, copied code, and plain-text extraction should therefore see the code content without a leading language token. Token spans, when present, use `doc-token` plus semantic modifiers such as `doc-token--keyword`, `doc-token--string`, `doc-token--comment`, `doc-token--number`, `doc-token--type`, `doc-token--member`, `doc-token--operator`, and `doc-token--punctuation`. These classes are internal AppSurface Docs output in v1. They are stable enough for the package stylesheet and tests, but they are not a public custom highlighter API.

### Language aliases

AppSurface Docs uses the first whitespace-delimited code-fence info token as the language. Metadata after the language is ignored in v1, so ` ```csharp {2}` is treated as `csharp` without activating line markers.

| Authored token | Normalized language |
| --- | --- |
| `cs`, `c#`, `csharp` | `csharp` |
| `razor`, `cshtml` | `razor` |
| `xml` | `xml` |
| `json` | `json` |
| `yaml`, `yml` | `yaml` |
| `bash`, `sh`, `shell` | `bash` |
| `html` | `html` |
| `css` | `css` |
| `js`, `javascript` | `javascript` |
| `md`, `markdown` | `markdown` |
| `diff` | `diff` |
| `txt`, `text`, `plain`, `text/plain`, `plaintext` | `plaintext` |

Supported normalized languages render highlighted output when the bundled TextMateSharp grammar loads successfully. `plaintext`, unsupported languages, unknown languages, grammar failures, tokenization failures, and blocks above AppSurface Docs' internal size threshold render as escaped plaintext with the same quiet code-block treatment. A correct plain block is preferred over fake highlighting.

### Authoring pitfalls

- Do not paste raw HTML token spans into Markdown code fences. AppSurface Docs owns token markup.
- Do not rely on automatic language detection. Add the language token explicitly when highlighting matters.
- Do not assume every language alias supports custom semantics beyond normalization.
- Do not use Shiki or Expressive Code line-marker syntax yet. V1 ignores code-fence metadata after the language.
- Do not style highlighter output outside the AppSurface Docs package stylesheet. Code block styling belongs under `.docs-content` in `wwwroot/css/app.css`.

## Harvest Health

`DocAggregator.GetHarvestHealthAsync(CancellationToken)` returns structured health for the same cached harvest snapshot used by docs pages, public sections, and the search index. Hosts should use this API when they need to report whether source-backed docs are healthy, empty by configuration, partially degraded, or unavailable because every harvester failed.

```csharp
var health = await docAggregator.GetHarvestHealthAsync(ct);

foreach (var diagnostic in health.Diagnostics)
{
    var logLevel = diagnostic.Severity switch
    {
        DocHarvestDiagnosticSeverity.Information => LogLevel.Information,
        DocHarvestDiagnosticSeverity.Warning => LogLevel.Warning,
        DocHarvestDiagnosticSeverity.Error => LogLevel.Error,
        DocHarvestDiagnosticSeverity.Critical => LogLevel.Critical,
        _ => LogLevel.Warning
    };

    logger.Log(
        logLevel,
        "AppSurface Docs harvest diagnostic {Code}: {Problem} {Fix}",
        diagnostic.Code,
        diagnostic.Problem,
        diagnostic.Fix);
}
```

The returned `DocHarvestHealthSnapshot` includes:

- `Status`: the aggregate `DocHarvestHealthStatus`.
- `GeneratedUtc`: the timestamp for the cached snapshot generation.
- `RepositoryRoot`: the resolved source root passed to harvesters. Treat this as server-only operational data; redact or omit it before forwarding harvest health to client-visible UI or public APIs.
- `TotalHarvesters`, `SuccessfulHarvesters`, and `FailedHarvesters`: counts for active harvesters that participated in strict aggregate health. Disabled optional harvesters, such as the JavaScript harvester when `AppSurfaceDocs:Harvest:JavaScript:Enabled=false`, are omitted. Default broad JavaScript discovery can still appear in `Harvesters` and diagnostics while staying out of these strict totals unless `StrictHealth=true` or JavaScript `IncludeGlobs` are configured.
- `TotalDocs`: the number of documentation nodes in the final cached docs snapshot after AppSurface Docs post-processing.
- `Harvesters`: one `DocHarvesterHealth` entry per active harvester, including its concrete type name, `DocHarvesterHealthStatus`, raw returned doc count, and optional diagnostic.
- `Diagnostics`: structured `DocHarvestDiagnostic` entries for harvester-level, warning, and aggregate states. AppSurface Docs-created snapshots never expose raw exception messages in diagnostics; exception details stay in host logs.

### Status Contract

`DocHarvestHealthStatus` is intentionally distinct from HTTP or process health:

- `Healthy`: at least one active harvester returned documentation and no harvester failed.
- `Empty`: harvesting completed without failures, but the final docs corpus is empty. This can be valid for an empty repository, a disabled source set, or a host with no registered harvesters.
- `Degraded`: at least one harvester succeeded or returned a valid empty result while another failed, timed out, or canceled. Docs remain usable, but the corpus may be incomplete.
- `Failed`: every active harvester failed, timed out, or canceled. AppSurface Docs returns an empty corpus for compatibility, but the snapshot should be treated as an operational failure.

`DocHarvesterHealthStatus` describes each source contribution:

- `Succeeded`: the harvester returned one or more docs.
- `ReturnedEmpty`: the harvester completed without error and returned no docs.
- `Failed`: the harvester threw while scanning.
- `TimedOut`: the harvester exceeded AppSurface Docs' per-harvester timeout budget.
- `Canceled`: the harvester observed cancellation outside AppSurface Docs' timeout budget.

The public enum numeric values are stable compatibility contracts for consumers that persist, serialize, bind, or compare them. New members may be added later, but existing values must not be reordered or renumbered.

`Healthy` means the strict harvesters completed; it does not mean the snapshot has no warnings. For example, oversized Markdown files and oversized Markdown metadata sidecars emit warning diagnostics while `/docs/_health.json` still returns HTTP `200` with `verification.ok=true` when the remaining docs corpus is otherwise healthy. Consumers that require warning-free docs should inspect `Diagnostics` in addition to the aggregate status.

### Diagnostics

Each `DocHarvestDiagnostic` has a stable `Code`, `Severity`, optional `HarvesterType`, operator-facing `Problem`, likely `Cause`, and suggested `Fix`. Use diagnostic codes for tests, dashboards, and host UI branching instead of parsing log messages.

AppSurface Docs currently emits these codes:

- `DocHarvestDiagnosticCodes.HarvesterTimedOut` (`appsurfacedocs.harvest.harvester_timed_out`)
- `DocHarvestDiagnosticCodes.HarvesterCanceled` (`appsurfacedocs.harvest.harvester_canceled`)
- `DocHarvestDiagnosticCodes.HarvesterFailed` (`appsurfacedocs.harvest.harvester_failed`)
- `DocHarvestDiagnosticCodes.NoHarvesters` (`appsurfacedocs.harvest.no_harvesters`)
- `DocHarvestDiagnosticCodes.AllFailed` (`appsurfacedocs.harvest.all_failed`)
- `DocHarvestDiagnosticCodes.MetadataUnsafeTrustMigrationHref` (`appsurfacedocs.metadata.unsafe_trust_migration_href`)
- `DocHarvestDiagnosticCodes.MarkdownFileTooLarge` (`appsurfacedocs.markdown.file_too_large`)
- `DocHarvestDiagnosticCodes.MarkdownMetadataFileTooLarge` (`appsurfacedocs.markdown.metadata_file_too_large`)
- `DocHarvestDiagnosticCodes.DocReservedRouteCollision` (`appsurfacedocs.routes.reserved_collision`)
- `DocHarvestDiagnosticCodes.DocRouteCollision` (`appsurfacedocs.routes.doc_collision`)
- `DocHarvestDiagnosticCodes.DocRedirectAliasCollision` (`appsurfacedocs.routes.redirect_alias_collision`)
- `DocHarvestDiagnosticCodes.DocImplicitRecoveryAliasCollision` (`appsurfacedocs.routes.implicit_recovery_alias_collision`)
- `DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug` (`appsurfacedocs.routes.invalid_canonical_slug`)
- `DocHarvestDiagnosticCodes.DocInvalidRedirectAlias` (`appsurfacedocs.routes.invalid_redirect_alias`)
- `DocHarvestDiagnosticCodes.DocLossySlugNormalization` (`appsurfacedocs.routes.lossy_slug_normalization`)
- `DocHarvestDiagnosticCodes.LocalizationUnsupportedLocale` (`appsurfacedocs.localization.unsupported_locale`)
- `DocHarvestDiagnosticCodes.LocalizationMissingBase` (`appsurfacedocs.localization.missing_base`)
- `DocHarvestDiagnosticCodes.LocalizationDuplicateVariant` (`appsurfacedocs.localization.duplicate_variant`)
- `DocHarvestDiagnosticCodes.LocalizationLocaleFolderConflict` (`appsurfacedocs.localization.locale_folder_conflict`)
- `DocHarvestDiagnosticCodes.LocalizationFallbackDisabledMissingVariant` (`appsurfacedocs.localization.fallback_disabled_missing_variant`)
- `DocHarvestDiagnosticCodes.LocalizationFallbackConflict` (`appsurfacedocs.localization.fallback_conflict`)
- `DocHarvestDiagnosticCodes.JavaScriptFileTooLarge` (`appsurfacedocs.javascript.file_too_large`)
- `DocHarvestDiagnosticCodes.JavaScriptParseFailed` (`appsurfacedocs.javascript.parse_failed`)
- `DocHarvestDiagnosticCodes.JavaScriptMissingInclude` (`appsurfacedocs.javascript.missing_include`)
- `DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped` (`appsurfacedocs.javascript.reparse_point_skipped`)
- `DocHarvestDiagnosticCodes.JavaScriptUnsupportedPublicShape` (`appsurfacedocs.javascript.unsupported_public_shape`)
- `DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet` (`appsurfacedocs.javascript.malformed_public_doclet`)
- `DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet` (`appsurfacedocs.javascript.incomplete_public_doclet`)
- `DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet` (`appsurfacedocs.javascript.incomplete_public_event_doclet`)
- `DocHarvestDiagnosticCodes.JavaScriptDuplicateAnchor` (`appsurfacedocs.javascript.duplicate_anchor`)

An all-failed snapshot logs one critical message when that snapshot is generated. Reusing the cached health snapshot does not log again. Calling `InvalidateCache()` and then reading docs or harvest health can generate a new snapshot and, if every harvester still fails, a new critical log entry.

### Cancellation and Caching

`GetHarvestHealthAsync(cancellationToken)` observes caller cancellation only while the caller waits for the memoized snapshot. Canceling that wait does not cancel, poison, or evict the shared snapshot computation. A later caller can still receive the completed snapshot.

Health and docs are computed from the same cached snapshot. This is deliberate: a host that reads `GetDocsAsync()` and then `GetHarvestHealthAsync()` sees health for the docs it is serving, not a second harvest with different timing or failures. Use `InvalidateCache()` when an operator explicitly asks AppSurface Docs to refresh source-backed docs.

AppSurface Docs uses an absolute freshness window followed by a bounded stale-while-revalidate window of the same length. After `CacheExpirationMinutes` elapses, requests continue to receive the last good snapshot while one background harvest refreshes the cache. If that refresh succeeds, later reads use the new snapshot; if it fails, the stale snapshot remains available until the stale window ends. A cold start with no previous snapshot still waits for the initial harvest or renders the live harvest observatory.

### Live Harvest Observatory

AppSurface Docs starts the initial harvest in the background by default. If a user reaches a docs page before the first cached snapshot is ready, the request waits briefly and then renders a live harvest observatory instead of holding a blank or apparently hung page. The observatory uses RazorWire Server-Sent Events with replay enabled, so late subscribers receive the latest retained harvest state and continue with live updates.

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "StartupMode": "Background",
      "InitialRequestWaitBudgetMilliseconds": 350,
      "TestingPreHarvestDelayMilliseconds": 0,
      "TestingDelayPerHarvesterMilliseconds": 0,
      "TestingDelayPerDocumentMilliseconds": 0
    }
  }
}
```

`AppSurfaceDocs:Harvest:StartupMode` accepts:

- `Background`: default. Startup schedules the memoized initial harvest and returns immediately unless strict failure mode is enabled.
- `Blocking`: startup waits for the initial harvest to complete.
- `Disabled`: startup does not pre-warm the docs cache; the first docs request starts harvest work.

`InitialRequestWaitBudgetMilliseconds` controls how long a docs request waits for the initial harvest before showing the observatory. The default is `350`. Set it to `0` when you want the observatory immediately for any pending first harvest. Set it higher when a host usually harvests quickly and you prefer to avoid showing the progress page for sub-second starts.

The `Testing*Delay*Milliseconds` options are local/manual testing knobs. The defaults are `0`. Set `TestingPreHarvestDelayMilliseconds` to pause after the run is published but before any harvester starts, `TestingDelayPerHarvesterMilliseconds` to pause each active harvester after it reports `Running`, and `TestingDelayPerDocumentMilliseconds` to publish each harvester's document count one document at a time. For example, `TestingPreHarvestDelayMilliseconds=1000` and `TestingDelayPerDocumentMilliseconds=150` make the observatory visibly unfold. Do not enable these for production traffic.

When the harvest completes successfully, AppSurface Docs first publishes the completed observatory state with replay enabled, then publishes a live-only RazorWire `rw-visit` command for active subscribers. Replay stays state-only, so late subscribers see the completed state and the plain continuation link without being auto-navigated by an old command. The completion view also renders a normal return link so no-JavaScript users can continue manually.

The harvest progress stream is authorized with the same route-exposure policy as the operator health endpoints and, outside Development, with a host-owned RazorWire channel authorizer. In Development it is exposed by default. In non-development hosts, `AppSurfaceDocs:Harvest:Health:ExposeRoutes=Always` exposes the health routes but does not by itself authorize the live progress stream.

### Production live harvest stream authorization

Production or preview hosts that want users to see live harvest progress must register a custom `IRazorWireChannelAuthorizer` before calling `AddAppSurfaceDocs()`. Use `AppSurfaceDocsStreamAuthorization.IsHarvestProgressChannel(channel)` instead of hardcoding the channel name:

```csharp
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Http;

public sealed class DocsHarvestStreamAuthorizer : IRazorWireChannelAuthorizer
{
    public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
    {
        if (!AppSurfaceDocsStreamAuthorization.IsHarvestProgressChannel(channel))
        {
            return new ValueTask<bool>(false);
        }

        var allowed = context.User.Identity?.IsAuthenticated == true
                      && context.User.IsInRole("DocsOperator");

        return new ValueTask<bool>(allowed);
    }
}

services.AddSingleton<IRazorWireChannelAuthorizer, DocsHarvestStreamAuthorizer>();
services.AddAppSurfaceDocs();
```

And enable the harvest health routes so the custom authorizer can approve the live progress stream:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Health": {
        "ExposeRoutes": "Always"
      }
    }
  }
}
```

The built-in RazorWire `AllowAll` mode is not treated as production authorization for the AppSurface Docs harvest progress stream. Registering `IRazorWireChannelAuthorizer` after `AddAppSurfaceDocs()` is an advanced replacement mode: the later authorizer replaces the AppSurface Docs wrapper and must apply the harvest-progress predicate itself.

Pitfalls:

- Do not put secrets, absolute filesystem paths, or raw exception details in harvester diagnostics. The observatory uses the same redacted diagnostic shape as harvest health.
- Do not rely on file-level progress counts in v1. The current stream reports harvester-level progress and aggregate document counts.
- Do not retain or replay visit commands. Keep replay enabled only for safe progress state; navigation commands are live-only.
- Do not use `StartupMode=Disabled` for hosts where first navigation latency matters; that preserves the old lazy-harvest behavior.
- Do not assume `ExposeRoutes=Always` authorizes the live progress stream in non-development environments. It exposes health routes; live progress also needs a custom stream authorizer.

### Operator Diagnostics Routes

AppSurface Docs reserves a redacted operator health page at `{DocsRootPath}/_health` and a machine-readable JSON endpoint at `{DocsRootPath}/_health.json` ahead of the docs catch-all route. Both endpoints return health responses by default only when the host environment is `Development`; otherwise they return `404`. Non-development hosts must opt in with `AppSurfaceDocs:Harvest:Health:ExposeRoutes=Always`.

The JSON response uses the camelCase wire form of `AppSurfaceDocsHarvestHealthResponse`:

- `status`: `Healthy`, `Empty`, `Degraded`, or `Failed`.
- `verification.ok`: `true` for `Healthy` and `Empty`; `false` for `Degraded` and `Failed`.
- `verification.httpStatusCode`: `200` for `Healthy` and `Empty`; `503` for `Degraded` and `Failed`.
- `generatedUtc`, harvester counts, total docs, per-harvester status, and redacted diagnostics.

The response omits `RepositoryRoot`, diagnostic `Cause`, raw exception messages, stack traces, and absolute filesystem paths. Health routes set `Cache-Control: no-store, no-cache` so local and CI checks do not pass or fail on stale operator data.

The sidebar health entry follows `AppSurfaceDocs:Harvest:Health:ShowChrome`, which is independent from route exposure. This lets a host expose `_health.json` for a script without advertising the health page in the docs chrome, or show status-only chrome while the reserved health endpoints still return `404`. When chrome is visible but `ExposeRoutes` hides responses for the current environment, AppSurface Docs renders a non-clickable status chip instead of a link.

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Health": {
        "ExposeRoutes": "DevelopmentOnly",
        "ShowChrome": "DevelopmentOnly"
      }
    }
  }
}
```

Allowed exposure values are `DevelopmentOnly`, `Always`, and `Never`. If you set `ExposeRoutes=Always`, the reserved health endpoints become an operator surface in that environment. Protect them with host-owned authentication, authorization, or network controls when they are reachable by untrusted users.

AppSurface Docs also reserves a route inspector at `{DocsRootPath}/_routes` and a machine-readable JSON endpoint at `{DocsRootPath}/_routes.json`. The inspector shows the public route manifest for the current cached docs snapshot: canonical browser URLs, source-shaped Markdown recovery aliases, declared redirect aliases, and route diagnostics. Add `?path=` to either endpoint to probe a source path, public route, or app-relative docs URL and see whether it resolves directly, redirects through an alias, is hidden, is reserved, or is invalid input.

Route inspector exposure is configured separately from harvest health:

```json
{
  "AppSurfaceDocs": {
    "Diagnostics": {
      "ExposeRouteInspector": "DevelopmentOnly",
      "ShowChrome": "DevelopmentOnly"
    }
  }
}
```

`AppSurfaceDocs:Diagnostics:ExposeRouteInspector` and `AppSurfaceDocs:Diagnostics:ShowChrome` accept `DevelopmentOnly`, `Always`, and `Never`. The defaults are `DevelopmentOnly`, which make the inspector available and discoverable from the sidebar for local development hosts without adding maintainer tooling to public reader navigation. Set `ExposeRouteInspector=Always` only for operator-owned environments that have host-level protection. Set it to `Never` when a development or preview host should reserve the route names but return `404`.

The mental model is:

- `ShowChrome` controls whether the sidebar can advertise diagnostics.
- `ExposeRoutes` and `ExposeRouteInspector` control whether HTTP endpoints respond.
- Chrome never creates a route.
- Route exposure never implies sidebar discovery.

The built-in sidebar renders a compact `Diagnostics` disclosure when at least one diagnostics row is available for the current host. Human pages are primary: `Harvest health` links to `_health`, and `Route inspector` links to `_routes`. JSON actions stay secondary as `Health JSON` and `Routes JSON`. Static exports and published reader artifacts must not contain the diagnostics disclosure or links to `_health`, `_health.json`, `_routes`, or `_routes.json`.

#### See diagnostics locally

Development defaults need no configuration. Start the docs preview and open the configured docs root:

```bash
appsurface docs preview --repo . --port 5189
```

Open `http://localhost:5189/docs`. The sidebar should show `Diagnostics` above the public docs sections. Opening it should show `Harvest health`, `Health JSON`, `Route inspector`, and `Routes JSON` when the default Development routes are enabled. Run the same host with a Production environment to verify the default reader surface: `Diagnostics` should be absent unless you explicitly opt in.

#### Common diagnostics configurations

Development defaults:

```json
{}
```

Production route-only direct access, without sidebar discovery:

```json
{
  "AppSurfaceDocs": {
    "Diagnostics": {
      "ExposeRouteInspector": "Always",
      "ShowChrome": "Never"
    }
  }
}
```

Production chrome plus routes for a protected operator host:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Health": {
        "ExposeRoutes": "Always",
        "ShowChrome": "Always"
      }
    },
    "Diagnostics": {
      "ExposeRouteInspector": "Always",
      "ShowChrome": "Always"
    }
  }
}
```

Health chrome only, with no health route links:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Health": {
        "ExposeRoutes": "Never",
        "ShowChrome": "Always"
      }
    }
  }
}
```

Disable every built-in diagnostics surface explicitly:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Health": {
        "ExposeRoutes": "Never",
        "ShowChrome": "Never"
      }
    },
    "Diagnostics": {
      "ExposeRouteInspector": "Never",
      "ShowChrome": "Never"
    }
  }
}
```

Production exposure does not add authentication or authorization. Protect diagnostics routes at the host, reverse proxy, or network boundary before setting any Production exposure value to `Always`.

Troubleshooting:

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| `Diagnostics` is absent in Production | Chrome defaults to `DevelopmentOnly` | Set the relevant `ShowChrome=Always` only in a protected host |
| `_routes` returns `404` | Route inspector responses are hidden | Set `AppSurfaceDocs:Diagnostics:ExposeRouteInspector=Always` in a protected host |
| Health row has no link | Health chrome is visible but health routes are hidden | Set `AppSurfaceDocs:Harvest:Health:ExposeRoutes=Always`, or keep status-only intentionally |
| JSON action is absent | The corresponding route response is hidden | Expose the route or leave the JSON action hidden |
| Static export contains diagnostics links | Reader artifact leakage | Treat this as a release blocker and fix rendering/export tests before publishing |

The JSON response uses the camelCase wire form of `AppSurfaceDocsRouteInspectorResponse`:

- `probe`: optional resolution details for the requested `path`.
- `entries`: the exposed public route manifest entries.
- `diagnostics`: route diagnostics copied from the manifest.

The probe normalizer accepts repository-style paths such as `packages/README.md`, public docs routes such as `/docs/packages`, and app-relative paths that include the current `PathBase`. It rejects absolute URLs, protocol-relative URLs, paths outside the configured docs root, and `.` or `..` segments before lookup.

### Pitfalls

- Do not parse logs to infer harvest health. Use `GetHarvestHealthAsync()` and diagnostic codes.
- Do not treat `Empty` as a failure. It means AppSurface Docs found no docs without a failed harvester.
- Do not expect raw exception details in public diagnostics. Use host logs for stack traces and exception messages.
- Do not assume the health routes are ASP.NET Core `IHealthCheck` endpoints. They report documentation harvest health, not whole-application liveness.
- Do not set `ExposeRoutes=Always` on a public host without host-owned protection.
- Do not treat the route inspector as reader navigation or export content. It is an operator diagnostic surface and remains hidden outside Development unless explicitly exposed.
- Do not pass untrusted absolute URLs to `?path=`. The inspector rejects them instead of following or normalizing them.

### Strict Startup Failure

Set `AppSurfaceDocs:Harvest:FailOnFailure` to `true` when a host should fail during startup if the cached harvest-health snapshot is `DocHarvestHealthStatus.Failed`.

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "FailOnFailure": true
    }
  }
}
```

Strict mode is built for CI and export hosts that publish docs artifacts. It prevents an all-failed harvest from becoming an empty or untrustworthy release tree. Leave it off for general public runtime hosts unless failing the whole application is the right operational posture for that host.

The startup preflight calls `DocAggregator.GetHarvestHealthAsync(CancellationToken)` and reuses the normal cached docs snapshot. It does not run a second harvester pipeline. `Healthy`, `Empty`, and `Degraded` snapshots continue startup; only aggregate `Failed` throws `AppSurfaceDocsHarvestFailedException`.

Disabled optional harvesters do not count as successful empty harvesters for strict mode. For example, a disabled JavaScript harvester cannot mask Markdown and C# harvesters that both failed.

`AppSurfaceDocsHarvestFailedException` exposes a redacted `DocHarvestFailureSummary` with status, counts, timestamp, and diagnostic code/severity/problem/fix fields. It omits `RepositoryRoot`, raw exception messages, stack traces, and diagnostic `Cause` text. Host logs can still contain lower-level harvester diagnostics because those logs are operator data, not public exception payload.

## Configuration

AppSurface Docs is still source-backed at runtime in this slice. `AppSurfaceDocs:Mode` should stay `Source`, and `AppSurfaceDocs:Bundle` remains reserved for a later reusable runtime-bundle host. Versioning in this slice does **not** change the runtime source mode; it adds a catalog that mounts already-exported release trees beside the live source-backed preview surface.

### Source-backed docs without versioning

Use the default single-surface configuration when you want the live docs experience rooted directly at `/docs`:

```json
{
  "AppSurfaceDocs": {
    "Mode": "Source",
    "CacheExpirationMinutes": 5,
    "Source": {
      "RepositoryRoot": "/path/to/repo"
    }
  }
}
```

If `AppSurfaceDocs:Source:RepositoryRoot` is omitted, the package falls back to repository discovery from the app content root.

### Harvest path policy

AppSurface Docs harvests from source by default, so every host should be intentional about the repository paths it turns into public documentation. `AppSurfaceDocs:Harvest:Paths` defines the global repository-relative boundary shared by the built-in Markdown and C# harvesters. `AppSurfaceDocs:Harvest:Markdown` and `AppSurfaceDocs:Harvest:CSharp` refine that boundary for one source kind.

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Paths": {
        "IncludeGlobs": [
          "README.md",
          "LICENSE",
          "docs/**/*.md",
          "src/**/*.cs"
        ],
        "ExcludeGlobs": [
          "docs/drafts/**",
          "**/generated/**"
        ],
        "DefaultExclusions": {
          "AllowGlobs": {
            "HiddenDirectories": [
              ".github/workflows/README.md"
            ]
          }
        }
      },
      "Markdown": {
        "IncludeGlobs": [
          "README.md",
          "LICENSE",
          "docs/**/*.md"
        ]
      },
      "CSharp": {
        "IncludeGlobs": [
          "src/**/*.cs"
        ],
        "DefaultExclusions": {
          "DisabledGroups": [
            "CSharpExampleSource"
          ]
        }
      }
    }
  }
}
```

Glob syntax is provided by `Microsoft.Extensions.FileSystemGlobbing` and always uses normalized repository-relative paths with `/` separators. AppSurface Docs trims configured patterns, converts `\` to `/`, removes blanks, and deduplicates case-insensitively during `AddAppSurfaceDocs()` post-configuration. Patterns must not be rooted (`/docs/**`, `./docs/**`, `C:/repo/**`), URI-shaped, query or fragment shaped, or contain `..` path segments. Invalid configured patterns fail options validation instead of silently changing the public surface.

Empty include arrays mean "use the built-in candidate set." Nonempty global includes are a real boundary: a Markdown or C# source file must match `Harvest:Paths:IncludeGlobs` before a source-specific include can accept it. Source-specific includes narrow that source kind further. Configured excludes are final denials and win after includes and default-exclusion allows.

AppSurface Docs keeps four package-defined default exclusion groups active when no path config is present:

| Group | Default behavior |
| --- | --- |
| `BuildOutput` | Excludes `node_modules`, `bin`, and `obj` subtrees. |
| `HiddenDirectories` | Excludes dot-prefixed directories such as `.github` and `.codex`, while dot-prefixed files such as `.hidden.md` remain candidates. |
| `TestProjects` | Excludes common test directories and project suffixes such as `Tests`, `.Tests`, `.IntegrationTests`, `-Tests`, and `_Tests`. |
| `CSharpExampleSource` | Excludes C# source under `examples` so sample applications do not become API reference by accident. Markdown in `examples` remains eligible. |

Use `DefaultExclusions:DisabledGroups` when an entire default group should stop applying for a scope. Use `DefaultExclusions:AllowGlobs` when only selected files inside a default group should come back. Group IDs are the names above, not enum ordinals; numeric values such as `0` fail validation instead of mapping to a group. Allows are group-aware: if a path matches both `HiddenDirectories` and `BuildOutput`, it needs an allow for both groups, or one of the groups must be disabled. Configured excludes still win after an allow.

AppSurface Docs also reads repository-owned Git `.gitignore` files by default. This keeps legacy package-manager and generated trees such as `bower_components/`, `dist/`, and `build/` from becoming public docs just because they contain Markdown, C#, or JavaScript files. The ignore policy is loaded lazily for each docs snapshot, follows nested `.gitignore` files only when traversal reaches that directory, and prunes obvious ignored subtrees without enumerating all their files.

VCS ignore rules sit after AppSurface include boundaries and default exclusion groups, but before configured AppSurface excludes. Git negation can only neutralize a previous Git ignore rule. `VcsIgnore:AllowGlobs` can also restore selected VCS-ignored candidates, but those allow globs use AppSurface's normal repository-relative glob syntax, not Git-ignore syntax, and cannot override AppSurface default exclusions or configured excludes.

| Git behavior | AppSurface Docs harvest policy |
| --- | --- |
| Repository `.gitignore` files can ignore generated, package-manager, or build output. | Repository-owned `.gitignore` files under the resolved source root are honored during each source snapshot. |
| `.git/info/exclude` and global excludes are developer- or machine-local. | They are never read, so CI, static export, packaged hosts, and developer machines see the same docs source surface. |
| Git tracked files can still match ignore rules. | AppSurface Docs ignores matching files even if they are tracked, because docs harvesting is source-policy driven rather than index driven. |
| Git can vary case behavior through repository and platform settings. | AppSurface Docs uses ordinal, case-sensitive VCS-ignore matching for reproducible Linux, macOS, and Windows harvests. Configured AppSurface globs remain the package's normal case-insensitive repository-relative globs. |
| Git negation can re-include paths only inside the Git ignore rule stack. | Git negation can neutralize earlier Git ignore rules, and `VcsIgnore:AllowGlobs` can restore selected VCS-only exclusions; neither can bypass AppSurface default exclusions or configured excludes. |

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Paths": {
        "VcsIgnore": {
          "AllowGlobs": [
            "docs/generated-public/**"
          ]
        }
      }
    }
  }
}
```

Disable VCS ignore integration only when the host intentionally wants pre-existing AppSurface behavior:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Paths": {
        "VcsIgnore": {
          "Enabled": false
        }
      }
    }
  }
}
```

AppSurface Docs reads only repository-owned `.gitignore` files under the resolved source root. It does not read `.git/info/exclude`, global Git excludes, or machine-local ignore state, so source-backed docs are reproducible in CI, static export, and packaged hosts. Tracked files that match `.gitignore` are still ignored by AppSurface Docs; use `VcsIgnore:AllowGlobs` for intentional public docs under ignored paths.

If docs disappear after adopting or upgrading AppSurface Docs, diagnose a single path before changing broad policy:

1. Check the nearest repository-owned `.gitignore` files that cover the missing repository-relative path.
2. If the path is intentionally public but lives under an ignored tree, restore only that surface with `AppSurfaceDocs:Harvest:Paths:VcsIgnore:AllowGlobs`.
3. If a host needs the old harvest behavior while migrating, temporarily set `AppSurfaceDocs:Harvest:Paths:VcsIgnore:Enabled=false`.
4. If the path is still missing, check AppSurface default exclusions and configured `ExcludeGlobs`; those policies still win after VCS-ignore allows.
5. Inspect harvest health diagnostics for the VCS-ignore summary and sample paths, then run the focused parity test when debugging package behavior:

```bash
dotnet test Web/ForgeTrust.AppSurface.Docs.Tests/ForgeTrust.AppSurface.Docs.Tests.csproj --filter AppSurfaceDocsHarvestVcsIgnorePolicyTests --logger "console;verbosity=normal"
```

The dedicated `vcs-ignore-parity.yml` workflow runs that parity suite on Linux, macOS, and Windows so case-sensitive ignore behavior and Git oracle traces stay pinned across supported runner families.

Traversal uses the same policy for Markdown, C#, and JavaScript. AppSurface Docs prunes clear default-excluded or configured `/**` subtrees for speed, but it does not prune just because an include glob might miss; includes are evaluated at file level so a narrow include does not accidentally hide a deeper matching file.

File and directory reparse points, including symlinks and junctions, are skipped before the built-in Markdown, C#, and JavaScript harvesters read or descend into them. This keeps the selected repository root as the source boundary even for untrusted repositories that contain links to files outside the root. When global or JavaScript-specific include roots resolve directly to JavaScript reparse points, the JavaScript harvester emits the redacted `appsurfacedocs.javascript.reparse_point_skipped` diagnostic so operators can replace the link with a real source path, point the include at a non-link source, disable JavaScript harvesting, or provide a custom harvester for a host-owned trust model.

The root `LICENSE` file is a Markdown candidate only when it is not a reparse point, and Markdown sidecar metadata files such as `README.md.yml` are ignored when the sidecar itself is a reparse point. When global includes are configured, the root license still needs to match an include such as `LICENSE`.

To host the same live source surface somewhere else, set the route-family root. With versioning disabled, the live docs root defaults to the route root:

```json
{
  "AppSurfaceDocs": {
    "Routing": {
      "RouteRootPath": "/foo/bar"
    }
  }
}
```

That configuration serves the live docs home at `/foo/bar`, search at `/foo/bar/search`, and the live search index at `/foo/bar/search-index.json`.

### Source-backed docs with published-version routing

Enable versioning when you want the host to keep serving the live unreleased snapshot from source while also mounting exact published release trees under stable public routes:

```json
{
  "AppSurfaceDocs": {
    "Mode": "Source",
    "Source": {
      "RepositoryRoot": "/path/to/repo"
    },
    "Routing": {
      "RouteRootPath": "/foo/bar",
      "DocsRootPath": "/foo/bar/next"
    },
    "Versioning": {
      "Enabled": true,
      "CatalogPath": "artifacts/appsurfacedocs/versions.json"
    }
  }
}
```

The simplest release-store layout keeps the catalog beside the published trees:

```text
artifacts/appsurfacedocs/
  versions.json
  releases/
    1.2.3/
      index.html
      search-index.json
```

With that layout, leave `AppSurfaceDocs:Versioning:TrustedReleaseRootPath` unset and use catalog paths such as `./releases/1.2.3`. When the catalog and exported release trees live in different configured locations, point `TrustedReleaseRootPath` at the operator-owned release store and keep each catalog `exactTreePath` relative to that root:

```json
{
  "AppSurfaceDocs": {
    "Versioning": {
      "Enabled": true,
      "CatalogPath": "config/appsurfacedocs/versions.json",
      "TrustedReleaseRootPath": "/srv/appsurface-docs/releases"
    }
  }
}
```

The route-family root owns the stable entry alias, archive, and exact release routes. The docs root owns the live source-backed preview. In the example above, `/foo/bar` is the recommended-release alias, `/foo/bar/versions` is the archive, `/foo/bar/v/{version}` serves immutable release trees, and `/foo/bar/next` remains the live preview.

### Route contract

AppSurface Docs keeps two roots on purpose:

- `AppSurfaceDocs:Routing:RouteRootPath` is the route-family root. It owns the stable entry alias, public archive, and exact-version release routes.
- `AppSurfaceDocs:Routing:DocsRootPath` is the live source-backed docs root. It owns current docs pages, the current search shell, and the current `search-index.json`.

Default routing:

| Configuration | Route root | Live docs root | Archive | Exact versions |
| --- | --- | --- | --- | --- |
| Versioning off, no routing config | `/docs` | `/docs` | `/docs/versions` | `/docs/v/{version}` |
| Versioning on, no routing config | `/docs` | `/docs/next` | `/docs/versions` | `/docs/v/{version}` |
| Versioning off, `RouteRootPath=/foo/bar` | `/foo/bar` | `/foo/bar` | `/foo/bar/versions` | `/foo/bar/v/{version}` |
| Versioning on, `RouteRootPath=/foo/bar` | `/foo/bar` | `/foo/bar/next` | `/foo/bar/versions` | `/foo/bar/v/{version}` |
| Versioning on, `RouteRootPath=/` | `/` | `/next` | `/versions` | `/v/{version}` |

`DocsRootPath` can be configured explicitly, but AppSurface Docs does not infer `RouteRootPath` by stripping `/next` or any other suffix. If you want versioned docs under `/foo/bar`, configure `RouteRootPath=/foo/bar`; setting only `DocsRootPath=/foo/bar/next` keeps the route family at the default `/docs`.

AppSurface Docs registers only the configured docs routes. It does not add a generic `{controller}/{action}` fallback to the host application, so custom docs roots stay isolated from other modules and application routes.

When versioning is enabled and the catalog proves the recommended release points at a healthy public exact tree, the route root is a friendly alias for that same frozen tree. It is not a redirect. Reader navigation, search config, search-index paths, static assets, and frozen-manifest alias redirects stay rooted at the alias, but `<link rel="canonical">` metadata points at the exact-version route. For example, `/docs/guide` serves the recommended tree with canonical `/docs/v/1.2.3/guide`; `/foo/bar/guide` canonicalizes to `/foo/bar/v/1.2.3/guide`; and a root-mounted docs family serves `/guide` with canonical `/v/1.2.3/guide`.

### Public canonical origin

Set `AppSurfaceDocs:Routing:PublicOrigin` when a preview or export host listens on one origin but published metadata should name another origin:

```json
{
  "AppSurfaceDocs": {
    "Routing": {
      "PublicOrigin": "https://docs.example.com"
    }
  }
}
```

`PublicOrigin` affects canonical metadata only. AppSurface Docs still chooses the page route from `RouteRootPath`, `DocsRootPath`, versioning settings, and page metadata before joining that route to the configured origin. For example, `PublicOrigin=https://docs.example.com` and the default docs root render a detail page canonical href such as `https://docs.example.com/docs/guides/intro`.

Mounted published trees follow the same rule. If `PublicOrigin` is set, canonical hrefs for exact archives and recommended aliases are emitted as absolute URLs under that origin and do not receive the current request `PathBase`. If `PublicOrigin` is unset, app-relative exported canonical links remain app-relative and preserve the existing `PathBase` behavior; absolute exported canonical links keep their exported origin while AppSurface Docs rewrites only the path to the active canonical root.

Use `PublicOrigin` for static export, reverse-proxied docs hosts, and CI hosts that crawl an internal loopback URL while publishing to a stable public domain. Leave it unset for local preview, ephemeral review apps, or deployments where AppSurface Docs should keep app-relative canonical links.

Configure only the origin: scheme, host, and optional port. Values such as `https://docs.example.com/docs`, `https://docs.example.com?x=1`, `https://user:pass@docs.example.com`, or `ftp://docs.example.com` are invalid. Put route paths in `RouteRootPath` and `DocsRootPath`, not in `PublicOrigin`.

### Route references

Consumers should resolve `DocsUrlBuilder` and use `DocsUrlBuilder.Routes` instead of hardcoding route strings:

```csharp
var routes = app.Services.GetRequiredService<DocsUrlBuilder>().Routes;
var home = routes.Home;
var search = routes.Search;
var searchIndexRefresh = routes.SearchIndexRefresh;
var searchIndexRefreshMethod = routes.SearchIndexRefreshMethod; // POST
var healthJson = routes.HealthJson;
var routeInspectorJson = routes.RouteInspectorJson;
```

`AppSurfaceDocsRouteReferences` contains `Home`, `Search`, `SearchIndex`, `SearchIndexRefresh`, `SearchIndexRefreshMethod`, `Versions`, `Health`, `HealthJson`, `RouteInspector`, and `RouteInspectorJson`. These values are app-relative. Apply `HttpRequest.PathBase`, `Url.PathBaseAware(...)`, or the host's equivalent presentation helper only at browser-facing boundaries.

The built-in search shell uses those route references for both the enhanced search runtime and the server-rendered recovery surface. Starter query chips render as real links to `Routes.Search` with `?q=` state, and the browse recovery links are generated from the harvested docs snapshot rather than hardcoded `/docs/...` strings. Public reader retry should use `Routes.SearchIndex`; operator UI should submit a browser form to `Routes.SearchIndexRefresh` with `Routes.SearchIndexRefreshMethod`.

Embedded hosts decide their own app-wide browser error UX. The reusable `ForgeTrust.AppSurface.Docs` package registers docs routes and route references, but it does not install a docs-aware application `404` page. Hosts that want stale-docs recovery should opt into AppSurface Web conventional browser status pages, add their own `~/Views/Shared/404.cshtml`, use `BrowserStatusPageModel` for status/original-path context, inject `DocsUrlBuilder`, and link to `DocsUrlBuilder.Routes.Search` through the host's path-base-aware rendering helper.

### Document route identity

AppSurface Docs assigns each cached snapshot a route identity catalog. The catalog keeps source identity separate from browser-facing route identity, so authors can keep Markdown source links portable while readers see structured URLs.

Default route behavior:

- Markdown files publish without `.md.html`. For example, `start-here/appsurface-evaluator.md` publishes at `{DocsRootPath}/start-here/appsurface-evaluator`.
- `README.md`, `README.markdown`, `index.md`, and `index.markdown` collapse to their containing directory. For example, `packages/README.md` publishes at `{DocsRootPath}/packages`.
- Source-shaped Markdown requests for public pages, including copy-pasted paths such as `{DocsRootPath}/packages/README.md` or `{DocsRootPath}/guides/start.md`, permanently redirect to the clean public route. Collision losers and reserved-route conflicts still stay non-public.
- The repository-root README represents the docs home and appears in search as `{DocsRootPath}`. It is not rendered through `/README.md`.
- Generated API docs and other non-Markdown docs keep the existing `.html` route shape, such as `{DocsRootPath}/Namespaces/ForgeTrust.AppSurface.Web.html`.
- Fragments stay fragments. A harvested source path like `guides/intro.md#setup` publishes as `{DocsRootPath}/guides/intro#setup`.

AppSurface Docs reserves document routes that belong to chrome, diagnostics, health, search, sections, versions, and assets. The reserved set includes the docs home, `search`, `search-index.json`, `_health`, `_health.json`, `_routes`, `_routes.json`, `search.css`, `search-client.js`, `outline-client.js`, `minisearch.min.js`, `versions`, and the `sections/` and `v/` route prefixes. Docs that resolve to reserved routes remain internally available for source lookup, but they are not public document winners and emit route diagnostics.

Markdown route segments are normalized deterministically: Unicode is folded where possible, non-spacing marks are removed, ASCII letters are lower-cased, dots are preserved, and unsafe separators become hyphens. When that conversion is lossy, AppSurface Docs emits `DocLossySlugNormalization` so authors can decide whether to set an explicit route.

Use `canonical_slug` when the source path is not the right reader-facing URL:

```yaml
title: Should I Use AppSurface?
canonical_slug: start-here/evaluator
```

Use `redirect_aliases` for deliberate migrations:

```yaml
title: Should I Use AppSurface?
canonical_slug: start-here/evaluator
redirect_aliases:
  - start-here/appsurface-evaluator
  - start-here/appsurface-evaluator.md.html
```

`canonical_slug` and `redirect_aliases` are docs-root-relative route paths. Do not include a query string, fragment, leading docs root, host name, or provider syntax such as Netlify splats, placeholders, query conditions, or forced-status suffixes. Canonical slugs use the same deterministic segment normalization as source-derived Markdown routes. Redirect aliases preserve their literal authored route text after separator cleanup, so legacy URLs such as `Old_Path/Guide.md.html` keep their existing shape instead of being slugified. Aliases redirect permanently to the public canonical route and preserve the request query string. Public Markdown source paths such as `/docs/foo.md` and `/docs/foo.md.html` also redirect to the clean route so GitHub-style copy-pasted links recover automatically. Use `redirect_aliases` for non-source legacy URLs, renamed pages, and old route shapes that are not already implied by the source path. Declared aliases that try to shadow another public Markdown source path are ignored with a `DocRedirectAliasCollision` diagnostic so copy-pasted source URLs keep pointing at their owning page. Use `aliases` metadata for search/discovery terms; use `redirect_aliases` only for old browser URL routes that should redirect. When publishing with `--redirects netlify`, two aliases that differ only by percent encoding must resolve to the same canonical route or export fails.

The cached route identity catalog also exposes a route manifest for exporters. Each manifest entry contains the public canonical live URL, source-shaped Markdown recovery aliases such as `/docs/foo.md` and `/docs/foo.md.html`, declared redirect aliases, and route diagnostics from the final public route catalog. Static AppSurface Docs export consumes that manifest after the snapshot is fully aggregated, so namespace README merging, collision handling, and reserved-route filtering have already selected the same public winners that the live controller serves. When a source-shaped recovery alias would shadow another public route, AppSurface Docs omits that alias from the manifest and emits `DocImplicitRecoveryAliasCollision`.

Static export materializes manifest aliases instead of copying page bodies to every legacy path. The default HTML strategy writes alias files that point at the canonical artifact with a canonical link and meta refresh, while the canonical page keeps the real content. The Netlify strategy writes exact `_redirects` rules from the live alias route to the live canonical route. Both strategies preserve recoverability for source-shaped URLs pasted from the repository without creating duplicate SEO surfaces or letting stale alias pages drift away from the clean route.

### Localization foundation

AppSurface Docs has a source-backed localization foundation for hosts that need multilingual docs without changing the current browser-visible contract yet. Localization is disabled by default. When enabled, AppSurface Docs validates configured locales, reads locale metadata from Markdown front matter and sidecars, infers translation sets for colocated files such as `README.fr.md`, builds an internal locale graph, emits stable diagnostics, and reserves a search projection seam. Phase 1 does not add visible language switchers, fallback pages, localized route matching, localized SEO tags, or locale-filtered search results.

Enable it under `AppSurfaceDocs:Localization`:

```json
{
  "AppSurfaceDocs": {
    "Localization": {
      "Enabled": true,
      "DefaultLocale": "en",
      "Locales": [
        {
          "Code": "en",
          "Label": "English",
          "Lang": "en-US",
          "Direction": "Ltr",
          "RoutePrefix": "en"
        },
        {
          "Code": "fr",
          "Label": "Français",
          "Lang": "fr-FR",
          "Direction": "Ltr",
          "RoutePrefix": "fr"
        }
      ],
      "RouteMode": "LocalePrefix",
      "FallbackMode": "DefaultLocaleWithNotice",
      "SearchMode": "ActiveLocale"
    }
  }
}
```

Author localization metadata either as friendly top-level keys or as a nested `localization:` block:

```yaml
---
title: Getting started
locale: fr
translation_key: guides/getting-started
localized_title: Démarrer
locale_fallback: Disabled
---
```

```yaml
---
title: Getting started
localization:
  locale: fr
  translation_key: guides/getting-started
  localized_title: Démarrer
  locale_fallback: Disabled
---
```

The supported metadata shape is:

- `locale`: optional BCP-47 locale code. It must match a configured `Locales[].Code` when localization is enabled.
- `translation_key`: optional stable identity shared by all translations of the same page. Use a route-like value such as `guides/getting-started`.
- `localized_title`: optional title for locale-aware UI surfaces. The current page title still follows the existing `title`, H1, and fallback resolution.
- `locale_fallback`: optional per-page fallback mode. Supported values are `DefaultLocaleWithNotice` and `Disabled`.

Inference behavior:

- A configured suffix such as `README.fr.md` infers locale `fr` and groups with `README.md`.
- A suffix that looks like a culture tag but is not configured emits `LocalizationUnsupportedLocale` and is excluded from the locale graph so it cannot masquerade as default-locale content.
- A locale-prefixed folder such as `fr/guides/start.md` infers locale only when the page also authors `translation_key`; this avoids treating ordinary folders as language roots by accident.
- A locale-prefixed folder that disagrees with authored `locale` emits `LocalizationLocaleFolderConflict`, and authored metadata wins.
- A localized suffix variant without its base/default-locale document emits `LocalizationMissingBase`.
- Two documents with the same `translation_key` and locale emit `LocalizationDuplicateVariant`.
- A translation set with `locale_fallback: Disabled` or global `FallbackMode: Disabled` emits `LocalizationFallbackDisabledMissingVariant` when a configured locale has no variant.

Decision guidance:

- Prefer colocated files such as `README.md` and `README.fr.md` when translations should stay next to the source page and remain readable in GitHub.
- Prefer explicit `translation_key` when translated source paths differ substantially, when locale folders are used, or when the source file name is not stable enough to identify the concept.
- Keep `RoutePrefix` to one safe segment such as `fr` or `pt-br`. AppSurface Docs rejects prefixes that collide with reserved docs routes such as `search`, `versions`, and `v`.
- Keep exact published-version localization out of this contract for now. Localized release exports need their own immutable artifact design.

Pitfalls:

- Do not expect enabling localization to create visible `/fr/...` pages yet. The internal graph can produce route candidates, but request routing and fallback rendering are follow-up work.
- Do not use locale folder inference without `translation_key`. AppSurface Docs deliberately fails closed so a folder named `fr` can still be ordinary content.
- Do not reuse a `translation_key` for unrelated pages. It is the identity that future switchers, fallback routes, and search grouping will depend on.
- Do not parse diagnostic messages. Branch on `DocHarvestDiagnosticCodes.Localization*` constants.

### Option reference

- `AppSurfaceDocs:Mode`
  - Keep this at `Source` in this slice.
  - `Bundle` still validates as unsupported because reusable request-time bundle hosting is deferred.
- `AppSurfaceDocs:Source:RepositoryRoot`
  - Optional absolute or app-relative repository root for source harvesting.
  - When omitted, AppSurface Docs falls back to repository discovery from the content root.
- `AppSurfaceDocs:Identity:DisplayName`
  - Optional visible product name for the document title and docs chrome.
  - Defaults to `Documentation` when omitted or blank so built-in docs chrome starts with a short title.
  - Razor views HTML-encode this as plain text; do not put markup here.
  - Long display names are clipped with an ellipsis inside the built-in sidebar and mobile header instead of expanding the chrome outside its bounds.
- `AppSurfaceDocs:Identity:Wordmark:HighlightText`
  - Optional substring of the resolved display name that the built-in docs chrome highlights.
  - Defaults to no highlight.
  - Use this when the publishing repository wants a specific product wordmark treatment, such as highlighting `Surface` in a shorter `AppSurface` display name.
  - The value must match part of the resolved display name using ordinal comparison. Only the first occurrence is highlighted.
- `AppSurfaceDocs:Identity:Wordmark:HighlightColor`
  - Optional CSS hex color for the highlighted wordmark substring, such as `#3b82f6`.
  - Defaults to the surrounding wordmark text color.
  - Requires `AppSurfaceDocs:Identity:Wordmark:HighlightText`; otherwise AppSurface Docs rejects the configuration because the color has no visible target.
  - CSS color names, functions, custom properties, semicolon-delimited declarations, and non-hex values are rejected.
- `AppSurfaceDocs:Identity:HomeHref`
  - Optional brand-link target for the built-in docs chrome.
  - Defaults to the configured docs home route.
  - Must be an app-root path such as `/docs` or an application-relative path such as `~/docs`.
  - Remote URLs, relative paths, query strings, fragments, protocol-relative URLs, and unsafe schemes are rejected during startup validation.
- `AppSurfaceDocs:Identity:Logo:Path`
  - Optional logo image path rendered beside the display name in the built-in docs chrome and as the root landing page hero mark.
  - Must be an app-root path such as `/branding/docs-logo.svg` or an application-relative path such as `~/branding/docs-logo.svg`.
  - This is a browser URL path, not a filesystem path.
  - Remote URLs, relative paths, query strings, fragments, backslashes, and traversal segments are rejected.
- `AppSurfaceDocs:Identity:Logo:AltText`
  - Optional accessible text for logo-only renderers that consume the resolved identity.
  - Defaults to the resolved display name when omitted or blank.
  - The built-in AppSurface Docs chrome renders configured logo images as decorative because the visible display name is rendered in the same brand link.
- `AppSurfaceDocs:Identity:Favicon:SvgPath`
  - Optional SVG favicon path.
  - Uses the same app-root or `~/` path rules as the logo.
  - This is a browser URL path, not a filesystem path.
- `AppSurfaceDocs:Identity:Favicon:IcoPath`
  - Optional ICO favicon path.
  - Uses the same app-root or `~/` path rules as the logo.
  - This is a browser URL path, not a filesystem path.
- `AppSurfaceDocs:Identity:Favicon:PngPath`
  - Optional PNG favicon path.
  - Uses the same app-root or `~/` path rules as the logo.
  - This is a browser URL path, not a filesystem path.
- `AppSurfaceDocs:Identity:BrandingAssets:DirectoryPath`
  - Optional filesystem directory that AppSurface Docs serves for consumer-owned logos, favicons, and related brand assets.
  - May be absolute, relative to `AppSurfaceDocs:Source:RepositoryRoot` when configured, or relative to the host content root otherwise.
  - This is a server filesystem path, not a browser URL path.
  - Serves only common web image and icon extensions: `.avif`, `.gif`, `.ico`, `.jpg`, `.jpeg`, `.png`, `.svg`, and `.webp`.
  - Keep this as a dedicated public branding directory, not a broad repository or deployment root.
  - Leave this blank when the owning application serves `Logo:Path` and `Favicon:*Path` itself.
- `AppSurfaceDocs:Identity:BrandingAssets:RequestPath`
  - Optional URL prefix for the configured branding asset directory.
  - Defaults to `/branding`.
  - Uses the same app-root or `~/` path rules as the logo and must not be the application root.
  - Override this only when `/branding` conflicts with an owning application route.

Use `Identity:BrandingAssets` when AppSurface Docs should serve brand files from a repository-owned or mounted directory.
For example, with `DirectoryPath` set to `branding` and the default `RequestPath` of `/branding`, the file
`branding/docs-logo.svg` is rendered with `Logo:Path` set to `/branding/docs-logo.svg`. AppSurface Docs does not join
`Logo:Path` or `Favicon:*Path` with `DirectoryPath`; those path options are the browser URLs that users and crawlers
request.

Leave `Identity:BrandingAssets:DirectoryPath` blank when the owning application already serves the logo or favicon URL.
For example, a host that serves `/assets/docs-logo.svg` itself can set `Logo:Path` to `/assets/docs-logo.svg` without
mounting a branding directory through AppSurface Docs.

When no favicon paths are configured, AppSurface Docs renders the packaged AppSurface Docs document-layers SVG mark as
the default favicon. Standalone AppSurface Docs hosts also serve that same SVG mark at `/favicon.ico` so the browser's
conventional root favicon probe succeeds before or alongside the rendered `<link rel="icon">` metadata. When
`AppSurfaceDocs:Identity:Favicon:SvgPath` is configured in a standalone host, `/favicon.ico` redirects to that SVG path so
the conventional browser probe matches the configured favicon. Embedded hosts do not claim `/favicon.ico`; the owning
application keeps control of its app-wide favicon. If you configure any custom favicon path, the built-in layout renders
only the configured entries, and the host is responsible for serving those files. Use `Identity:BrandingAssets` when those
files should come from a repository-owned or deployment-mounted directory instead of the owning application's normal
static web assets.

- `AppSurfaceDocs:Harvest:FailOnFailure`
  - Defaults to `false`.
  - `AddAppSurfaceDocs()` always registers `AppSurfaceDocsHarvestFailurePreflightService`; this flag controls whether that preflight can fail startup.
  - When `true`, the preflight fails the host with `AppSurfaceDocsHarvestFailedException` only when aggregate harvest health is `Failed`.
  - Use this for release publishing, static export, and CI smoke hosts where publishing empty or untrustworthy docs is worse than a failed build.
  - Do not use this expecting `Empty` or `Degraded` to fail in v1. Empty docs can be intentional, and degraded docs can still be usable.
- `AppSurfaceDocs:Harvest:Health:ExposeRoutes`
  - Defaults to `DevelopmentOnly`.
  - Controls whether `{DocsRootPath}/_health` and `{DocsRootPath}/_health.json` return health responses.
  - AppSurface Docs always reserves the endpoint patterns before the docs catch-all route so health URLs do not fall through to document lookup.
  - `Always` exposes the responses in non-development environments; protect the endpoints at the host boundary when they are publicly reachable.
  - `Never` keeps the reserved endpoints returning `404`, including in development.
- `AppSurfaceDocs:Harvest:Health:ShowChrome`
  - Defaults to `DevelopmentOnly`.
  - Controls whether the built-in sidebar shows health status chrome.
  - This is independent from `ExposeRoutes` so machine-readable checks and visible docs chrome can be configured separately.
  - If routes are hidden for the current environment, the sidebar renders status-only chrome without an `href`.
- `AppSurfaceDocs:Diagnostics:ExposeRouteInspector`
  - Defaults to `DevelopmentOnly`.
  - Controls whether `{DocsRootPath}/_routes` and `{DocsRootPath}/_routes.json` return the route inspector and manifest responses.
  - AppSurface Docs always reserves the endpoint patterns before the docs catch-all route so route-inspector URLs do not fall through to document lookup.
  - `Always` exposes route-manifest diagnostics in non-development environments; protect the endpoints at the host boundary when they are publicly reachable.
  - `Never` keeps the reserved endpoints returning `404`, including in development.
- `AppSurfaceDocs:Diagnostics:ShowChrome`
  - Defaults to `DevelopmentOnly`.
  - Controls whether the built-in sidebar can show route-inspector discovery inside the `Diagnostics` disclosure.
  - This is independent from `ExposeRouteInspector` so route responses can stay direct-access only, or hidden routes can stay absent from the sidebar.
  - If route inspector responses are hidden for the current environment, AppSurface Docs does not render dead `_routes` or `_routes.json` links.
- `AppSurfaceDocs:Harvest:Paths:IncludeGlobs`
  - Defaults to an empty array, which means every built-in harvester starts from its normal candidate set.
  - When nonempty, this is the global source boundary for all built-in harvesters. Markdown, C#, and JavaScript source-specific includes can narrow it but cannot bypass it.
  - Patterns are repository-relative `Microsoft.Extensions.FileSystemGlobbing` globs with `/` separators. `AddAppSurfaceDocs()` trims, slash-normalizes, removes blanks, and deduplicates them.
- `AppSurfaceDocs:Harvest:Paths:ExcludeGlobs`
  - Defaults to an empty array.
  - Excludes win over global includes, source-specific includes, and default-exclusion allows.
  - Use this for host-specific private paths such as generated output, CI artifacts, or draft docs. The reusable package does not treat `generated` or `TestResults` as defaults.
- `AppSurfaceDocs:Harvest:Paths:DefaultExclusions:DisabledGroups`
  - Defaults to an empty array.
  - Disables package default groups globally. Supported group IDs are `BuildOutput`, `HiddenDirectories`, `TestProjects`, and `CSharpExampleSource`.
  - Prefer a disabled group only when the entire group is intentionally public for the host. For one-off exceptions, prefer `AllowGlobs`.
- `AppSurfaceDocs:Harvest:Paths:DefaultExclusions:AllowGlobs`
  - Defaults to an empty dictionary.
  - Maps a default group ID to repository-relative globs that opt matching files back into that group.
  - Allows are group-aware. A path matching multiple enabled groups needs an allow for every matched group, and configured excludes still win afterward.
- `AppSurfaceDocs:Harvest:Paths:VcsIgnore:Enabled`
  - Defaults to `true`.
  - Reads repository-owned Git `.gitignore` files under the resolved source root during each cached docs snapshot.
  - Does not read `.git/info/exclude`, global Git excludes, or machine-local ignore state.
  - VCS-ignore matching is ordinal and case-sensitive for reproducible Linux, macOS, and Windows harvests.
- `AppSurfaceDocs:Harvest:Paths:VcsIgnore:AllowGlobs`
  - Defaults to an empty array.
  - Uses AppSurface repository-relative glob syntax, not Git-ignore syntax.
  - Restores selected candidates excluded only by VCS ignore rules; AppSurface default exclusions and configured excludes still win.
- `AppSurfaceDocs:Harvest:Markdown:IncludeGlobs` / `ExcludeGlobs` / `DefaultExclusions`
  - Defaults mirror the global path option shape, but apply only to Markdown candidates and the root `LICENSE` candidate.
  - Source-specific includes are evaluated after global includes, so they narrow Markdown rather than widening it.
- `AppSurfaceDocs:Harvest:Markdown:MaxFileSizeBytes`
  - Defaults to `1048576` bytes.
  - Markdown files above this limit are skipped before the file is read, before inline front matter is parsed, and before Markdig receives the body. Inline front matter is part of the Markdown body file and is governed by this limit.
  - Oversized files emit `DocHarvestDiagnosticCodes.MarkdownFileTooLarge` with the repository-relative path, actual bytes, configured limit, and fix guidance in the client-visible health diagnostic.
- `AppSurfaceDocs:Harvest:Markdown:MaxMetadataFileSizeBytes`
  - Defaults to `65536` bytes.
  - Paired metadata sidecars such as `README.md.yml` and `README.md.yaml` above this limit are ignored before YAML parsing, while the Markdown body still publishes when it is within `MaxFileSizeBytes`.
  - Oversized sidecars emit `DocHarvestDiagnosticCodes.MarkdownMetadataFileTooLarge` with the repository-relative sidecar path, actual bytes, configured limit, and fix guidance in the client-visible health diagnostic.
- `AppSurfaceDocs:Harvest:CSharp:IncludeGlobs` / `ExcludeGlobs` / `DefaultExclusions`
  - Defaults mirror the global path option shape, but apply only to C# API-reference candidates.
  - `CSharpExampleSource` is only a C# default group. Markdown example READMEs stay eligible unless excluded by other policy.
- `AppSurfaceDocs:Harvest:JavaScript:Enabled`
  - Defaults to `true`.
  - Set to `false` to opt out of JavaScript public API harvesting entirely.
  - When enabled, AppSurface Docs scans policy-approved `.js` files for explicit public doclets. Unannotated JavaScript is ignored.
- `AppSurfaceDocs:Harvest:JavaScript:IncludeGlobs` / `ExcludeGlobs` / `DefaultExclusions`
  - Include globs default to an empty list; exclude globs default to `**/*.min.js`; default-exclusion controls mirror the global path option shape.
  - Use include globs as an optional narrowing or performance boundary, such as `Web/ForgeTrust.RazorWire/assets/contracts/razorwire-public-contracts.js`.
  - Global path rules apply first, then JavaScript-specific includes, default exclusions, and excludes refine the candidate set.
  - Configured JavaScript include roots that resolve to symlinks, junctions, or other reparse points are skipped before reads or descent. Exact includes and symlinked include roots emit `appsurfacedocs.javascript.reparse_point_skipped` with repository-relative wording only; globbed child symlink files are skipped silently.
  - Exact missing JavaScript include files emit `appsurfacedocs.javascript.missing_include`. This preserves a distinct compatibility signal from parse/read failures while still blocking strict JavaScript health when JavaScript participates through `StrictHealth=true` or nonempty JavaScript include globs.
- `AppSurfaceDocs:Harvest:JavaScript:GroupNameRules`
  - Defaults to an empty list.
  - Names already-eligible JavaScript source trees when public doclets do not declare a nonblank `@namespace` or `@module`.
  - Rules are evaluated in order; the first matching rule wins.
  - Each rule requires a nonblank `Name` and at least one valid repository-relative `IncludeGlobs` pattern.
  - Group name rules do not include files in the harvest. Use `IncludeGlobs`, `ExcludeGlobs`, and shared path policy for harvest boundaries.
- `AppSurfaceDocs:Harvest:JavaScript:RequirePublicTag`
  - Defaults to `true`.
  - Requires harvested doclets to carry `@public`. `@internal`, `@private`, and `@ignore` always exclude a doclet.
  - Broad default discovery always requires `@public`, even when this is `false`; the compatibility escape applies only when JavaScript include globs are explicitly configured.
- `AppSurfaceDocs:Harvest:JavaScript:StrictHealth`
  - Defaults to `false`.
  - Keeps broad default JavaScript discovery best-effort for aggregate health: empty results, parse/read diagnostics, and timeouts remain visible but do not mask or cause Markdown/C# strict failures.
  - JavaScript participates in strict aggregate health when this is `true` or JavaScript `IncludeGlobs` is nonempty.
- `AppSurfaceDocs:Harvest:JavaScript:RequireCompleteEventDoclets`
  - Defaults to `false`.
  - When `true`, incomplete public JavaScript event doclets emit `appsurfacedocs.javascript.incomplete_public_event_doclet` with error severity and fail harvest health.
  - `DocAggregator` treats `appsurfacedocs.javascript.incomplete_public_event_doclet` as a strict blocking diagnostic: when `RequireCompleteEventDoclets` emits it, the JavaScript harvester is treated as though `ParticipatesInStrictHealth=true` and that diagnostic becomes the harvester health result, regardless of whether JavaScript would otherwise participate in `StrictHealth`.
  - This applies only to public `@event` doclets. Parse failures, oversized files, unsupported shapes, and malformed doclets stay governed by `StrictHealth` or explicit JavaScript include globs.
- `AppSurfaceDocs:Harvest:JavaScript:MaxFileSizeBytes`
  - Defaults to `262144`.
  - Files above this limit are skipped with a structured harvest diagnostic so generated bundles do not dominate docs snapshot time.

Markdown resource limits are byte counts. Configure them in JSON:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Markdown": {
        "MaxFileSizeBytes": 1048576,
        "MaxMetadataFileSizeBytes": 65536
      }
    }
  }
}
```

Or through environment variables:

```bash
AppSurfaceDocs__Harvest__Markdown__MaxFileSizeBytes=1048576
AppSurfaceDocs__Harvest__Markdown__MaxMetadataFileSizeBytes=65536
```

These settings are pre-read byte guards. They do not provide Markdig parser-complexity, AST-depth, timeout, or cancellation limits after a Markdown file has been admitted for parsing. Prefer `AppSurfaceDocs:Harvest:Markdown:ExcludeGlobs` or `AppSurfaceDocs:Harvest:Paths:ExcludeGlobs` for generated docs; raise the limits only for intentional authored Markdown and metadata.

- `AppSurfaceDocs:Routing:RouteRootPath`
  - Controls the route-family root for stable entry, archive, and exact-version routes.
  - Defaults to `/docs` when versioning is on.
  - Defaults to `DocsRootPath` when versioning is off.
  - Relative-looking values such as `foo/bar` are normalized to app-relative paths like `/foo/bar` during `AddAppSurfaceDocs()` post-configuration.
  - `/` is supported for single-purpose root-mounted docs hosts.
  - The path must be app-relative, must not end with `/` except for `/`, cannot contain query or fragment segments, and cannot be a reserved child such as `/foo/bar/versions` or `/foo/bar/v`.
- `AppSurfaceDocs:CacheExpirationMinutes`
  - Controls the absolute lifetime of the shared docs snapshot that backs docs pages, public-section data, and `{DocsRootPath}/search-index.json`; for example, `/docs/search-index.json` by default or `/docs/next/search-index.json` when `AppSurfaceDocs:Routing:DocsRootPath` is `/docs/next`.
  - Defaults to `5` minutes.
  - Must be a finite positive number from `0.016666666666666666` through `35791394.1`, inclusive.
  - Must map to a whole number of seconds, because `{DocsRootPath}/search-index.json` uses the same duration for its private `Cache-Control` `max-age` header.
  - Do not use `0`, sub-second values, or extreme values such as `double.MaxValue`; AppSurface Docs rejects values outside the supported range during options validation.
- `AppSurfaceDocs:Routing:DocsRootPath`
  - Controls the live source-backed docs root.
  - Defaults to the route root when versioning is off.
  - Defaults to `{RouteRootPath}/next` when versioning is on.
  - Relative-looking values such as `foo/bar/preview` are normalized to app-relative paths like `/foo/bar/preview` during `AddAppSurfaceDocs()` post-configuration.
  - `/` is supported for single-purpose unversioned docs hosts.
  - The path must be app-relative, must not end with `/` except for `/`, and cannot contain query or fragment segments.
  - When versioning is on, it cannot equal the route root and cannot use the route root's reserved archive or exact-version children, such as `/foo/bar/versions`, `/foo/bar/v`, or `/foo/bar/v/1.2.3`.
- `AppSurfaceDocs:Routing:PublicOrigin`
  - Optional public origin used when rendering absolute canonical metadata.
  - When omitted, details pages render app-relative canonical links.
  - When set, it must be an absolute `http` or `https` origin such as `https://docs.example.com` or `https://docs.example.com:8443`.
  - The value is normalized during `AddAppSurfaceDocs()` post-configuration and must not include a docs path, query string, fragment, userinfo, or non-HTTP(S) scheme.
  - This setting does not move docs routes. Use `RouteRootPath` and `DocsRootPath` for path routing, then use `PublicOrigin` only for the host portion of canonical metadata.
- `AppSurfaceDocs:Localization:Enabled`
  - Defaults to `false`.
  - When `false`, AppSurface Docs keeps existing routes, visible UI, and search payload behavior.
  - When `true`, `DefaultLocale` and at least one configured locale are required.
- `AppSurfaceDocs:Localization:DefaultLocale`
  - Defaults to `en`.
  - Must match one configured locale code when localization is enabled.
- `AppSurfaceDocs:Localization:Locales`
  - Defaults to an empty array.
  - Each entry requires `Code` when localization is enabled. `Code` and optional `Lang` must be valid BCP-47 culture tags.
  - `Label` is optional reader-facing text for future language UI.
  - `Direction` supports `Ltr` and `Rtl`.
  - `RoutePrefix` defaults to `Code`; it must be one safe segment and cannot collide with reserved docs routes.
- `AppSurfaceDocs:Localization:RouteMode`
  - Defaults to `LocalePrefix`.
  - The enum is public and versioned for future route strategies, but `LocalePrefix` is the only supported value today.
- `AppSurfaceDocs:Localization:FallbackMode`
  - Defaults to `DefaultLocaleWithNotice`.
  - `Disabled` means missing variants should not receive fallback pages once localized route rendering exists.
- `AppSurfaceDocs:Localization:SearchMode`
  - Defaults to `ActiveLocale`.
  - Phase 1 preserves the existing v1 search payload for every projection; locale-filtered search is deferred.
- `AppSurfaceDocs:Versioning:Enabled`
  - Turns on the published-version route contract and archive surface.
  - Does not switch the runtime into bundle mode.
- `AppSurfaceDocs:Versioning:CatalogPath`
  - Required when versioning is enabled.
  - Points to the JSON catalog that describes the published exact-version trees and the recommended release alias.
  - Relative paths resolve from the app content root.
- `AppSurfaceDocs:Versioning:TrustedReleaseRootPath`
  - Optional. Defaults to the directory containing `CatalogPath`.
  - Defines the only filesystem tree that catalog `exactTreePath` values may publish.
  - Relative configured values resolve from the app content root.
  - The root and exact release trees must be ordinary directories. Symlinks, junctions, reparse points, and metadata-inspection failures are denied.
- `AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes`
  - Defaults to `2097152` bytes (2 MiB).
  - Must be between `1` and `33554432` bytes (32 MiB). There is no disable sentinel.
  - Defines the published-tree HTML/search-index rewrite limit. AppSurface Docs checks exported `.html` files and the root `search-index.json` before request-time rewrites, and checks `search-index.json` during catalog validation before parsing.
  - Does not limit streamed static assets such as images, fonts, CSS, JavaScript, or the source-backed docs harvesters.

```json
{
  "AppSurfaceDocs": {
    "Versioning": {
      "Enabled": true,
      "CatalogPath": "config/appsurfacedocs/versions.json",
      "TrustedReleaseRootPath": "/srv/appsurface-docs/releases",
      "MaxRewrittenFileSizeBytes": 2097152
    }
  }
}
```

Production hosts can set the same value with `AppSurfaceDocs__Versioning__MaxRewrittenFileSizeBytes`.

### JavaScript public API harvesting

JavaScript harvesting is for intentional browser runtime contracts: custom events, globals, small public helpers, constants, typedefs, attributes, config fields, module mount contracts, CSS custom properties, and CSS hooks that application authors need to consume. It is enabled by default, but it is annotation-first: AppSurface Docs publishes only supported public doclets and ignores unannotated JavaScript.

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "JavaScript": {
        "IncludeGlobs": [
          "Web/ForgeTrust.RazorWire/assets/contracts/razorwire-public-contracts.js"
        ]
      }
    }
  }
}
```

The v1 harvester parses policy-approved `.js` files with Acornima and reads JSDoc-shaped block comments. It parses modules first and falls back to script parsing for classic browser runtimes. It renders group pages such as `api/javascript/razorwire`, adds fragment-addressable search stubs for each item, and uses `@namespace` or `@module` as the group name. Without an explicit group, configured group rules run next; if no rule matches, classic browser globals such as `window.RazorWire` infer `RazorWire`, and other doclets use source-path fallback.

JavaScript API family names resolve in this order:

1. the first nonblank `@namespace`
2. the first nonblank `@module`
3. the first configured `GroupNameRules` match
4. classic browser global inference from `window.*`
5. fallback identity from the source path

Configured group rules are useful when a docs-only contract manifest or package-owned source tree should publish under a reader-facing family name without repeating the same tag on every doclet:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "JavaScript": {
        "GroupNameRules": [
          {
            "Name": "RazorWire",
            "IncludeGlobs": [
              "Web/ForgeTrust.RazorWire/assets/contracts/**/*.js"
            ]
          }
        ]
      }
    }
  }
}
```

Rules are ordered and first match wins. Put narrow package paths before broad fallback paths. A rule only names files that normal harvest policy already accepted; it does not widen `IncludeGlobs`, bypass excludes, or publish unannotated JavaScript. If a doclet has `@namespace` or `@module`, that tag wins over any matching rule so source-owned API names remain stable.

Path fallback is deterministic and path-aware. A single untagged file such as `src/public-api.js` uses `public-api` as its reader-facing family label and publishes at `api/javascript/src-public-api`, so its route stays stable if another `public-api.js` appears later. If multiple fallback files share the same stem, AppSurface Docs also disambiguates the visible family labels with path context, such as `forms/public-api` and `widgets/public-api`, so unrelated contracts do not silently merge onto one page. Add `@namespace`, `@module`, or a configured group rule when fallback labels are not the names readers should see.

Supported public shapes:

- attached `function name(...) {}` doclets
- attached `const name = (...) => ...` and `const name = function (...) { ... }` doclets, with one declarator per statement
- attached `const name = value` doclets, with one declarator per statement
- attached `window.Name = ...` or `window["Name"] = ...` doclets
- standalone `@event event:name` doclets
- standalone `@typedef {Type} Name` doclets
- standalone `@attribute name` doclets for package-owned HTML or `data-*` attributes
- standalone `@config name` doclets for runtime configuration fields
- standalone `@moduleContract name` doclets for mount/import contracts
- standalone `@cssCustomProperty --name` doclets for public CSS variables
- standalone `@cssHook selector` doclets for stable styling hooks paired with `@hookKind`

Event doclets should include `@target`, `@firesWhen`, `@bubbles`, `@cancelable`, and detail payload fields through `@property detail.name`. Use `@detail none` only when the event deliberately carries no payload. Add `@example` when the event needs consumption guidance beyond the contract fields.

Set `AppSurfaceDocs:Harvest:JavaScript:RequireCompleteEventDoclets=true` when public browser events must be release-blocking. In that mode, each public `@event` must include `@target`, `@firesWhen`, and either at least one valid `detail.*` property or `@detail none`. `@bubbles`, `@cancelable`, and `@example` remain recommended authoring fields, but they are intentionally excluded from the blocking strict-event contract. Valid detail property names are exactly `detail.` plus dot-separated segments. Segments may contain letters, digits, `_`, `$`, `-`, and a trailing `[]`. Optional JSDoc brackets and defaults are accepted, so `detail.message`, `[detail.message]`, `[detail.message="fallback"]`, `detail.items[]`, and `detail.items[].id` are valid. Values such as `detail`, `[detail]`, `detail.`, `detail..message`, `detail. message`, `detail.[x]`, `Detail.message`, `form`, and `message` are invalid. `@detail none` is contradictory when any `detail.*` property is present.

```js
/**
 * A RazorWire-enhanced form submission failed and custom UI may handle the failure.
 * @public
 * @namespace RazorWire
 * @event razorwire:form:failure
 * @target form[data-rw-form="true"]
 * @firesWhen a RazorWire-enhanced form receives an unhandled failure response or a network error.
 * @bubbles true
 * @cancelable true
 * @property {HTMLFormElement} detail.form - Submitted form.
 * @property {number|null} detail.statusCode - HTTP status code when a response was received.
 * @example
 * form.addEventListener('razorwire:form:failure', event => {
 *   event.preventDefault();
 * });
 */
```

Browser-contract doclets should carry enough fields for readers to use them without reading source. Attributes need `@target` and `@type`; config fields need `@type` and `@source`; module contracts need `@signature` and `@target`; CSS custom properties need `@target` and `@syntax`; CSS hooks need `@hookKind`, `@target`, and `@stability`.

```js
/**
 * Stable generated error block selector.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-form-error-generated="true"]
 * @hookKind data-attribute
 * @target generated form failure UI
 * @stability stable
 */
```

Unsupported public classes, CommonJS export inference, malformed public doclets, incomplete event contracts, oversized files, parse failures, missing exact includes, configured reparse-point includes, and duplicate normalized anchors emit `DocHarvestDiagnostic` entries. Hosts should branch on `DocHarvestDiagnosticCodes.JavaScript*` constants rather than parsing log text. Unsupported shapes are skipped instead of rendered partially. The strict event diagnostic code is `DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet` (`appsurfacedocs.javascript.incomplete_public_event_doclet`). The configured-link diagnostic is `DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped` (`appsurfacedocs.javascript.reparse_point_skipped`); its problem, cause, and fix are redacted to repository-relative include paths and do not reveal the symlink target.

Use the CLI health verifier in CI when degraded docs health should block release output:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj -c Release -- \
  docs verify-health \
  --repo . \
  --require-complete-event-doclets \
  --environment Production \
  --startup-timeout-seconds 30
```

The verifier starts the standalone docs host on loopback, reads the same redacted response shape as `{DocsRootPath}/_health.json`, and exits nonzero when `verification.ok=false`. The reported status code matches the health endpoint contract: `200` for `Healthy` or `Empty`, `503` for `Degraded` or `Failed`; `docs verify-health` also fails when the HTTP response status and `verification.httpStatusCode` disagree. `docs verify-health --startup-timeout-seconds` defaults to 10 seconds when omitted; pass `0` to disable the startup watchdog instead of waiting indefinitely. `AppSurfaceDocs:Harvest:FailOnFailure` still fails startup only for aggregate `Failed` snapshots; use `docs verify-health` when a `Degraded` snapshot, such as incomplete strict event docs with Markdown still available, should block publication.

Pitfalls:

- Do not add broad `**/*.js` include globs just to turn JavaScript harvesting on. It is already on; include globs are for narrowing default discovery.
- Do not expect `GroupNameRules` to harvest files. They only name JavaScript API families after the normal path policy accepts a file.
- Do not put a broad `GroupNameRules` entry before a narrower package rule unless you want the broad name to win.
- Do not rely on fallback file names as stable product names for packages. Prefer `@namespace`, `@module`, or a configured group rule for public API families.
- Do not document minified, generated, `node_modules`, `bin`, `obj`, or test assets. The default JavaScript and shared path policy excludes minified, build-output, and test paths; add explicit excludes for host-specific generated source.
- Do not point JavaScript includes at symlinks, junctions, or other reparse points, even when the target stays inside the repository. Built-in JavaScript harvesting treats links as a trust-boundary crossing and skips them before reading. Replace the link with a real source file, include the real non-link path, disable JavaScript harvesting, or publish that source through a custom harvester.
- When documenting package browser contracts, prefer a small docs-only contract manifest such as `Web/ForgeTrust.RazorWire/assets/contracts/razorwire-public-contracts.js` over generated runtime outputs.
- Do not attach one public doclet to `const first = ..., second = ...`; split public JavaScript API constants or functions into one declaration statement per doclet.
- Do not rely on automatic event inference from `dispatchEvent(new CustomEvent(...))`. V1 documents explicit public doclets only.
- Do not pair `@detail none` with `@property detail.*`; either the event has no payload or its payload shape is documented.
- Do not put `@public` on classes, default exports, or CommonJS exports until a later harvester slice supports those shapes.
- Do not treat Acornima as a runtime JavaScript execution engine. AppSurface Docs uses it only to parse configured source for documentation, and `ForgeTrust.AppSurface.Docs` carries `THIRD-PARTY-NOTICES.md` for the redistributed package.

### Published version catalog

The version catalog is the release-level source of truth for version routing and archive presentation:

```json
{
  "recommendedVersion": "1.2.3",
  "versions": [
    {
      "version": "1.2.3",
      "label": "1.2.3 (Current)",
      "summary": "Recommended release for new evaluations and adoption.",
      "exactTreePath": "./releases/1.2.3",
      "releaseManifestSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "supportState": "Current",
      "visibility": "Public",
      "advisoryState": "None"
    },
    {
      "version": "1.1.0",
      "label": "1.1.0",
      "summary": "Supported for teams finishing an upgrade.",
      "exactTreePath": "./releases/1.1.0",
      "supportState": "Maintained",
      "visibility": "Public",
      "advisoryState": "Vulnerable"
    }
  ]
}
```

- `recommendedVersion`
  - Exact version string that should also be mounted at `RouteRootPath`.
  - Must point at a public, available version or the route-root entry falls back to the archive-style recovery surface.
- `versions[].version`
  - Exact version identifier such as `1.2.3` or `1.2.3-rc.1`.
- `versions[].label`
  - Optional reader-facing label shown in the archive. Defaults to `version`.
- `versions[].summary`
  - Optional archive summary copy.
- `versions[].exactTreePath`
  - Path to the exported stable docs subtree for one exact release.
  - Must be relative to `AppSurfaceDocs:Versioning:TrustedReleaseRootPath`. When that option is unset, the trusted release root defaults to the directory containing the catalog file.
  - Values such as `./releases/1.2.3` are valid after normalization. Rooted paths and paths containing `..` are unavailable and are never mounted.
  - AppSurface Docs can mount that same artifact at `RouteRootPath` for the recommended alias and at `{RouteRootPath}/v/{version}` for the exact release surface.
- `versions[].releaseManifestSha256`
  - Optional SHA-256 digest of `.appsurface-docs-release-manifest.json` for this exact tree.
  - When present, AppSurface Docs verifies the manifest digest, manifest schema, listed file lengths, listed file digests, and handler-servable file coverage before mounting the release.
  - When absent, a shape-valid legacy tree can still mount ordinary docs content, but archive SVG is denied because the archive is not catalog-verified.
  - A manifest inside an archive is not trusted by itself; the catalog-pinned digest is the trust anchor.
- `versions[].supportState`
  - Archive posture badge. Supported values are `Current`, `Maintained`, `Deprecated`, and `Archived`.
- `versions[].visibility`
  - Archive visibility policy. Supported values are `Public` and `Hidden`.
- `versions[].advisoryState`
  - Release-level warning badge. Supported values are `None`, `Vulnerable`, and `SecurityRisk`.

### Exact-version tree contract

Each `exactTreePath` directory is treated as a prebuilt static subtree for one exact release. It is usually exported from the stable `/docs` surface, and at minimum it must include:

- `.appsurface-docs-route-manifest.json` at the tree root for new exports
  - The hidden manifest freezes the docs-root-relative canonical route and alias graph that existed when the release was exported.
  - It stores route identity only. It does not store `PublicOrigin`, PathBase, `RouteRootPath`, exact-version mount roots, or absolute URLs.
  - Missing manifests are supported for legacy archives; malformed legacy manifests disable archive alias recovery for that release without disabling normal file serving.
  - Verified archives parse this manifest from bytes covered by `.appsurface-docs-release-manifest.json`; malformed pinned route manifests keep the release unavailable because redirects are behavior-affecting archive content.
- `.appsurface-docs-release-manifest.json` at the tree root for new exports
  - The hidden release manifest lists the final exported files, byte lengths, content types when known, SHA-256 algorithm names, and lowercase SHA-256 digests.
  - The manifest is deterministic and is written after final export materialization, so rewritten HTML, redirect artifacts, binary assets, and `.appsurface-docs-route-manifest.json` are covered.
  - Export refuses unsupported hidden paths such as `.nojekyll` or `.well-known/...` with `ASDOCSARCHIVE005` instead of printing a catalog pin the runtime cannot verify.
  - The manifest excludes itself. Its own SHA-256 digest is printed by export and should be copied into `versions[].releaseManifestSha256`.
  - The release manifest proves local archive integrity only when the trusted catalog pins its digest. It is not a signature or build provenance attestation.
- `index.html` at the tree root
- `search.html` at the tree root
  - The shell should contain useful server-rendered anchors before JavaScript runs: starter query URLs and browse recovery links for the strongest available docs entry points.
- `search-index.json` at the tree root
  - The payload must remain valid JSON with a top-level `documents` array so version-local search can load safely.
  - Every `documents[]` entry must include non-empty string `path` and `title` properties.
  - Each `documents[].path` value is archive metadata, not a general URL field. Store canonical, root-relative AppSurface Docs paths such as `/docs/guide.html`, `/docs/packages/README.md.html`, `/docs/v/1.2.3/guide.html` for that same version, or `/docs/guide.html?q=term#section`.
  - Do not store request `PathBase`, `PublicOrigin`, custom `RouteRootPath`, deployment-specific aliases, protocol-relative URLs, absolute URLs, executable schemes, traversal, encoded separators, or docs operational/assets routes such as `/docs/search`, `/docs/search-index.json`, `/docs/search.css`, `/docs/search-client.js`, `/docs/_health`, `/docs/_routes`, or `/docs/_search-index/refresh`.
  - Missing or blank `path`/`title` values, malformed payloads, and unsafe `path` values cause AppSurface Docs to reject only the affected published release tree during startup validation.
  - The payload must stay under `AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes`; oversized payloads are rejected during catalog validation before JSON parsing, so the release is marked unavailable before readers hit broken search.
- `search.css` at the tree root. The bundled search stylesheet carries search-local fallbacks for the shared style tokens so exact release search controls remain styled even when a historical/static export does not include `site.gen.css`.
- `search-client.js` at the tree root
- `outline-client.js` at the tree root for outline-aware exports whose HTML references the page-local outline runtime
- `minisearch.min.js` at the tree root
- any section, detail, partial, and asset routes that belong to the exported docs surface for that release

AppSurface Docs does not regenerate these trees at request time. It resolves extensionless requests back to the exported `.html` files and rewrites stable-root HTML plus `search-index.json` payloads so the same artifact can serve both the recommended alias and `{RouteRootPath}/v/{version}` honestly, including custom roots such as `/foo/bar`. Exported `.html` files and the root `search-index.json` must stay under the published tree rewrite limit in `AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes`. Oversized rewritten artifacts fail closed instead of streaming unrewritten content; other static assets continue to stream normally. The mount contract has two roots: `MountRootPath` controls normal serving, links, assets, search config, search-index payloads, and redirects; `CanonicalRootPath` controls only `<link rel="canonical">` metadata. Exact mounts self-canonicalize. Recommended alias mounts use the route-family root as `MountRootPath` and the matching exact-version root as `CanonicalRootPath`, so `/docs` remains friendly while crawlers see `/docs/v/{version}` as the durable duplicate URL.

### Published tree rewrite limit

`AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes` is a public resource guard for published release trees. The default is `2097152` bytes (2 MiB), chosen conservatively because this repository does not check in a representative exported docs corpus outside build outputs. Hosts with larger generated docs can raise the value up to `33554432` bytes (32 MiB), but raising it increases per-request memory exposure for public rewritten HTML and search-index requests.

Use the limit for exported `.html` pages and the root `search-index.json` only. It is not a general docs file-size policy, it does not cap source harvesting, and it does not block images, fonts, CSS, JavaScript, or other streamed assets. When AppSurface Docs rejects an oversized rewritten artifact, diagnostics include the artifact type, observed size, configured limit, `AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes`, the failure outcome, and this section name. Remediate by shrinking or re-exporting the artifact, or by setting a larger explicit limit within the supported range.

When the hidden frozen route manifest is present, mounted archives also use it before file lookup to redirect archived source-shaped Markdown aliases and declared redirect aliases to the mount-local canonical route. For example, a manifest alias of `packages/README.md` with canonical route `packages` redirects to `/docs/v/1.2.3/packages` when the tree is mounted at `/docs/v/1.2.3`, or to `/foo/bar/packages` when the recommended release is mounted at a custom route root. Redirects preserve query strings; request fragments cannot be preserved because browsers do not send them to the server, but a manifest canonical route may still include its own fragment such as `guide#advanced`. Exporters should validate `.appsurface-docs-route-manifest.json`, `search-index.json`, `search.css`, `search-client.js`, `minisearch.min.js`, and, for outline-aware exports, `outline-client.js` before publishing because a missing required runtime asset or a malformed search payload keeps that release unavailable or incomplete until the artifact is fixed. The version catalog intentionally does not crawl historical HTML to infer optional outline support; old exact archives stay immutable, and any future modernization should be an explicit rebuild from source into a new self-contained tree. Use the [RazorWire CLI](../ForgeTrust.RazorWire.Cli/README.md) or another static-export pipeline to publish those trees ahead of time.

If a strict search-index path failure appears after upgrade, inspect the affected tree's `search-index.json`, find the reported `documents[index]`, and rewrite valid docs pages back to canonical `/docs/...` paths. For example, replace `https://docs.example.com/foo/bar/guide.html`, `/some-base/docs/guide.html`, or `/foo/bar/guide.html` with `/docs/guide.html` before rebuilding or republishing the archive. Do not repair unsafe rows by pointing them at external sites or operational docs endpoints; those entries should be removed or regenerated from a valid docs route.

### Archive ordering

- The public archive preserves the authored order of `versions[]` from the catalog.
- Use that list order when you want a specific narrative ordering that differs from plain lexical version sorting.

### Availability and failure behavior

- Version validation is best-effort and release-local.
- A missing or malformed `exactTreePath` marks only that release unavailable.
- A missing, invalid, or unsafe `TrustedReleaseRootPath` is catalog-level configuration failure. The archive shows a sanitized availability message, logs retain operator details, and no published exact tree is mounted.
- A public version with a valid `releaseManifestSha256` pin mounts only after archive verification succeeds.
- A public version with a `releaseManifestSha256` pin whose manifest is missing, mismatched, malformed, incomplete, or inconsistent is unavailable and does not mount.
- A public version without `releaseManifestSha256` is treated as a legacy unverified archive: normal docs HTML, search, image, font, and runtime assets continue when the tree shape is valid, but archive SVG is denied with `ASDOCSARCHIVE010`.
- Healthy published versions and the live preview surface continue to load.
- If the configured `recommendedVersion` is hidden, missing, or unavailable, AppSurface Docs does not mount it at the route root; that entry route falls back to the archive-style recovery surface with a link to the live preview.

Archive verification diagnostics use stable `ASDOCSARCHIVE###` codes in structured logs. Common branches include `ASDOCSARCHIVE001` for a missing manifest, `ASDOCSARCHIVE002` for a catalog digest mismatch, `ASDOCSARCHIVE003` for unsupported schema or invalid payload, `ASDOCSARCHIVE004` for duplicate manifest paths, `ASDOCSARCHIVE005` for unsafe paths, `ASDOCSARCHIVE006` for missing listed files, `ASDOCSARCHIVE007` for length mismatch, `ASDOCSARCHIVE008` for digest mismatch, `ASDOCSARCHIVE009` for handler-servable files not listed in the manifest, and `ASDOCSARCHIVE010` for denied active SVG. Public archive messages stay sanitized and do not expose absolute filesystem paths.

### Migrating absolute exactTreePath values

Older catalogs could point `exactTreePath` at an absolute filesystem path. Absolute values are now unavailable because catalog data must not choose an arbitrary public mount root.

Before:

```json
{
  "AppSurfaceDocs": {
    "Versioning": {
      "Enabled": true,
      "CatalogPath": "config/appsurfacedocs/versions.json"
    }
  }
}
```

```json
{
  "versions": [
    {
      "version": "1.2.3",
      "exactTreePath": "/srv/appsurface-docs/releases/1.2.3"
    }
  ]
}
```

After:

```json
{
  "AppSurfaceDocs": {
    "Versioning": {
      "Enabled": true,
      "CatalogPath": "config/appsurfacedocs/versions.json",
      "TrustedReleaseRootPath": "/srv/appsurface-docs/releases"
    }
  }
}
```

```json
{
  "versions": [
    {
      "version": "1.2.3",
      "exactTreePath": "1.2.3"
    }
  ]
}
```

### Pitfalls

- Do not set `AppSurfaceDocs:Routing:DocsRootPath` to the same value as `RouteRootPath` when versioning is enabled. That collides with the stable published-release alias.
- Do not configure only `DocsRootPath=/foo/bar/next` and expect archive routes to move to `/foo/bar`; set `RouteRootPath=/foo/bar` explicitly.
- Do not point `recommendedVersion` at a hidden or broken release tree.
- Do not put absolute paths in `versions[].exactTreePath`. Configure `TrustedReleaseRootPath` once, then keep catalog paths relative to that release store.
- Do not put the trusted release root, exact release trees, frozen route manifests, or served child assets behind symlinks, junctions, or other reparse points. AppSurface Docs denies them because published trees are public static-file roots.
- Do not expect the recommended alias to rewrite ordinary links to the exact-version route. Only canonical metadata moves from the alias root to the exact root.
- Do not assume `AppSurfaceDocs:Versioning:Enabled` means the runtime can read request-time bundles. This slice still serves the live preview from source and mounts published releases as static trees.
- Do not forget `search-index.json` in an exported release tree. A release without it is intentionally marked unavailable.
- Do not store deployment-specific search-index document paths in an exact tree. The archive stores canonical `/docs/...` paths; AppSurface Docs rewrites them to the recommended alias, exact-version route, custom route root, and request `PathBase` while serving.
- Do not treat `AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes` as a cache or total CPU limit. It bounds rewritten input size for published `.html` and root `search-index.json`; under-limit rewritten files can still require request-time parsing and rewriting.
- Do not raise `AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes` casually. Larger limits allow larger public request-time reads; prefer shrinking or re-exporting unusually large HTML or search-index artifacts when practical.
- Do not hand-edit `.appsurface-docs-route-manifest.json` to add aliases. New exports validate duplicate aliases, aliases that collide with canonical routes, and aliases that equal their own canonical route. Runtime ignores ambiguous aliases from hand-edited or legacy manifests and keeps serving normal files.
- Do not hand-edit `.appsurface-docs-release-manifest.json` after export. Any byte change requires copying the new manifest digest into `releaseManifestSha256`, and file changes require a fresh export.
- Do not describe catalog-pinned release manifests as signed provenance. A compromised catalog can pin compromised archive bytes; Sigstore, GitHub artifact attestations, and SLSA-style provenance are future trust layers.

`AppSurfaceDocs:CacheExpirationMinutes` is interpreted as minutes. Use shorter values for source-backed development hosts where authors need edits to appear quickly; use longer values for production hosts when harvesters are expensive or the docs corpus changes only during deploys.

### Search index refresh

`GET {DocsRootPath}/search-index.json` is a read-only reader endpoint. Legacy query strings such as `?refresh=1` and `?refresh=true` are ignored so crawlers, browser reloads, and copied reader links cannot mutate the server snapshot cache.

Packaged operator refresh lives at `POST {DocsRootPath}/_search-index/refresh`. The endpoint always requires MVC anti-forgery validation and a host-owned authorization policy named by `AppSurfaceDocs:Diagnostics:SearchIndexRefreshPolicy`. Blank or whitespace policy names are normalized to `null`; when the policy is missing, the packaged endpoint denies refresh.

```json
{
  "AppSurfaceDocs": {
    "Diagnostics": {
      "SearchIndexRefreshPolicy": "DocsSearchIndexRefresh"
    }
  }
}
```

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        "DocsSearchIndexRefresh",
        policy => policy.RequireAuthenticatedUser()
            .RequireClaim("scope", "docs.search-index.refresh"));
});
```

A Razor/MVC operator surface can post to the packaged endpoint with the normal anti-forgery token:

```cshtml
@inject ForgeTrust.AppSurface.Docs.Services.DocsUrlBuilder DocsUrls

<form method="@DocsUrls.Routes.SearchIndexRefreshMethod" action="@DocsUrls.Routes.SearchIndexRefresh">
    @Html.AntiForgeryToken()
    <button type="submit">Refresh search index</button>
</form>
```

Host automation should usually use a host-owned admin or job endpoint instead of the browser-form-shaped packaged route. After applying host-specific authentication, authorization, audit logging, and replay controls, call `DocAggregator.InvalidateCache()` directly.

| Request state | Response |
| --- | --- |
| Missing or invalid anti-forgery token | Framework anti-forgery failure, normally `400` |
| No configured refresh policy, blank policy, missing policy provider/service, or policy not found | `403` |
| Unauthenticated user after anti-forgery succeeds | `403` |
| Authenticated user who fails the configured policy | `403` |
| Authenticated user who satisfies the configured policy | `204`, cache invalidated |
| Non-POST request to `{DocsRootPath}/_search-index/refresh` | `405` |

Pitfalls:

- Do not set `CacheExpirationMinutes` to `0` to disable caching. AppSurface Docs rejects zero and negative values because every request would rebuild the docs snapshot and search index.
- Do not set tiny positive values below `0.016666666666666666` minutes; the search-index `Cache-Control` `max-age` header cannot represent sub-second cache lifetimes.
- Do not set fractional-second values such as `0.333` minutes. AppSurface Docs rejects values that cannot round-trip to a whole-second `max-age`.
- Do not set huge finite values such as `double.MaxValue`. AppSurface Docs caps the value so the derived search-index `Cache-Control` `max-age` remains representable.
- The search-index response uses the same duration for its private `Cache-Control` `max-age`, so client refresh behavior stays aligned with server-side snapshot reuse.
- Do not use `{DocsRootPath}/search-index.json?refresh=1` for operations. It is intentionally read-only compatibility noise; use the POST operator route or a host-owned automation path.

## Contributor Provenance

AppSurface Docs can render a lightweight `Source of truth` strip directly under the page title and summary on details pages. The strip is evidence-driven:

- `View source` links to the authored source when AppSurface Docs can identify one safely.
- `Edit this page` links to an edit surface when the host configures one safely.
- `Last updated` renders as relative time with an exact machine-readable `<time datetime="...">` value behind it.

If a page has no trustworthy contributor evidence, AppSurface Docs omits the strip entirely instead of rendering placeholder copy.

### Host configuration

Contributor provenance is configured under `AppSurfaceDocs:Contributor`:

```json
{
  "AppSurfaceDocs": {
    "Mode": "Source",
    "Source": {
      "RepositoryRoot": "/path/to/repo"
    },
    "Contributor": {
      "Enabled": true,
      "DefaultBranch": "main",
      "SourceRef": "8b7c6d5",
      "SourceUrlTemplate": "https://github.com/forge-trust/AppSurface/blob/{branch}/{path}",
      "SymbolSourceUrlTemplate": "https://github.com/forge-trust/AppSurface/blob/{ref}/{path}#L{line}",
      "EditUrlTemplate": "https://github.com/forge-trust/AppSurface/edit/{branch}/{path}",
      "LastUpdatedMode": "Git"
    }
  }
}
```

Field behavior:

- `Enabled` defaults to `true`. Set it to `false` to disable all contributor provenance rendering.
- `DefaultBranch` is the stable branch or ref used when expanding configured source and edit templates, and the fallback source ref for symbol links.
- `SourceRef` is the preferred ref for generated C# API symbol links. Use a commit SHA when the docs build knows one.
- `SourceUrlTemplate` and `EditUrlTemplate` support only `{branch}` and `{path}` tokens, and configured templates must include `{path}` so each page expands to its own source or edit target.
- `SymbolSourceUrlTemplate` supports only `{path}`, `{line}`, `{branch}`, and `{ref}` for generated C# API symbol links. It must include `{path}` and `{line}`.
- `LastUpdatedMode` supports `None` and `Git`. `None` is the default so hosts opt into git-backed freshness explicitly; `Git` resolves freshness from local repository history when a trustworthy source path exists.

Host contract:

- If `Enabled` is `false`, AppSurface Docs skips contributor rendering and does not enforce `DefaultBranch` or `{path}` template requirements at startup.
- If `Enabled` is `true` and `SourceUrlTemplate` or `EditUrlTemplate` is configured, `DefaultBranch` is required and AppSurface Docs fails options validation on startup when it is missing.
- If `Enabled` is `true` and `SourceUrlTemplate` or `EditUrlTemplate` is configured, that template must contain `{path}`. AppSurface Docs rejects startup when a template would collapse every page to one shared URL.
- If `Enabled` is `true` and `SymbolSourceUrlTemplate` is configured, the template must contain `{path}` and `{line}`, and unsupported `{token}` placeholders are rejected at startup. If it contains `{ref}`, AppSurface Docs uses `SourceRef` first and falls back to `DefaultBranch`; one of those values must be configured. If it contains `{branch}`, `DefaultBranch` must be configured.
- Templates expand both the branch and normalized source path segment-by-segment, so slash-separated refs stay readable while spaces and other special characters are still URL-escaped safely.
- Git-backed freshness runs during docs snapshot generation, not during view rendering. AppSurface Docs uses a bounded snapshot-time freshness budget so slow or wedged git lookups degrade to omitted timestamps instead of stretching one timeout across the whole docs corpus. If git is unavailable, shallow, or missing history for a page, AppSurface Docs omits only `Last updated`.
- Hosts that want `LastUpdatedMode: Git` in CI or export jobs must provide real history for the docs checkout. For GitHub Actions, use `actions/checkout` with `fetch-depth: 0` or another checkout shape that preserves commit history for the rendered files.

### C# symbol source links

Generated C# API pages can render small `Source` links beside documented types, enums, method overloads, and properties. These are symbol-level links, not page-level namespace links. Namespace API pages are synthetic and may contain declarations from many files, so AppSurface Docs only links a generated API symbol when the harvester captured an exact source path and 1-based declaration line for that rendered anchor.

`SymbolSourceUrlTemplate` is separate from `SourceUrlTemplate` because symbol links need `{line}` and page-level Markdown links do not. Prefer `{ref}` with `SourceRef` when publishing docs from CI so readers jump to the same code version used to build the docs:

```json
{
  "AppSurfaceDocs": {
    "Contributor": {
      "DefaultBranch": "main",
      "SourceRef": "8b7c6d5",
      "SymbolSourceUrlTemplate": "https://github.com/forge-trust/AppSurface/blob/{ref}/{path}#L{line}"
    }
  }
}
```

Custom harvesters can populate `DocNode.SymbolSourceProvenance`, but AppSurface Docs only renders links for content that also includes the compatible placeholder emitted by the built-in C# harvester. The current placeholder contract is an implementation detail for generated API HTML:

```html
<span data-appsurfacedocs-symbol-source="anchor-id"></span>
```

AppSurface Docs expands or removes those placeholders during snapshot generation before HTML sanitization runs. If a placeholder has no safe href, if the source path is not repository-relative, if the line number is invalid, if duplicate placeholders make an anchor ambiguous, or if multiple provenance entries claim the same anchor, AppSurface Docs omits the symbol link. A missing link is better than a confident wrong line.

When a namespace intro is merged into a generated namespace API page, the page-level strip still points to the intro source and uses the label `Namespace intro source`. The generated API symbols on the same page use their own inline `Source` links.

### Page-level overrides

Authors can supply a nested `contributor:` block in inline Markdown front matter or in a paired sidecar such as `page.md.yml`:

```yaml
contributor:
  hide_contributor_info: true
  source_path_override: Web/ForgeTrust.AppSurface.Docs/README.md
  source_url_override: https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs/README.md
  edit_url_override: https://github.com/forge-trust/AppSurface/edit/main/Web/ForgeTrust.AppSurface.Docs/README.md
  last_updated_override: 2026-04-22T23:19:00Z
```

Field behavior:

- `hide_contributor_info: true` suppresses the strip entirely for that page.
- `source_path_override` feeds template expansion and git freshness when the rendered page does not map cleanly to `DocNode.Path`. It must stay repository-relative; rooted paths and traversal segments are ignored.
- `source_url_override` and `edit_url_override` bypass template generation entirely. AppSurface Docs accepts only absolute `http`/`https` URLs or root-relative paths for these overrides.
- `last_updated_override` must stay a real timestamp. AppSurface Docs renders it through the same relative-time treatment as git-backed freshness.

### Automatic versus explicit provenance

AppSurface Docs is intentionally conservative about automatic provenance:

- Markdown pages use their harvested source path automatically.
- Harvested C# API symbols can get inline source links when `SymbolSourceUrlTemplate` is configured, but namespace-synthetic pages do not get one automatic page-level C# source link.
- Synthetic or merged pages can still opt into source, edit, or freshness evidence through explicit `contributor:` overrides.

This keeps AppSurface Docs from inventing fake precision for pages that do not have one trustworthy underlying source file.

### Pitfalls

- Do not configure source or edit templates without `DefaultBranch`. AppSurface Docs rejects that startup shape because local git state is too brittle to guess from.
- Do not configure source or edit templates without `{path}`. That shape cannot identify one source file per page, so AppSurface Docs rejects it at startup.
- Do not author free-text freshness copy in the provenance strip. Use `last_updated_override` for an exact timestamp, and use `trust.freshness` for broader lifecycle guidance.
- Do not expect shallow CI clones to populate `Last updated`. AppSurface Docs degrades safely by omitting freshness when history is unavailable. In GitHub Actions, prefer `actions/checkout` with `fetch-depth: 0` for pages that should surface git-backed freshness.
- Do not add `{line}` to `SourceUrlTemplate`; use `SymbolSourceUrlTemplate` for symbol links so Markdown page provenance keeps working.
- Do not invent additional `SymbolSourceUrlTemplate` tokens. AppSurface Docs rejects unsupported placeholders such as `{commit}` or `{linen}` instead of rendering silently broken links.
- Do not expect automatic edit links on namespace-synthetic API pages. Symbol links point to source browsing locations, while authored namespace intros keep the page-level edit link.

## Namespace Intros

AppSurface Docs can merge authored namespace-intro Markdown into a generated namespace API page so teams can explain a namespace in prose without replacing the generated symbol list. AppSurface-authored package/project namespace intros should use `NAMESPACE.md`; docs-owned namespace `README.md` paths remain supported for compatibility.

### Authoring contract

`NAMESPACE.md` qualifies as a namespace intro only when all of these are true:

- The C# API harvester generated a namespace page at `Namespaces/{Dotted.Namespace}`.
- The authored file is named `NAMESPACE.md`.
- The file is harvested as a root documentation node, not as a child fragment.
- `NAMESPACE.md.yml` declares `namespace: Dotted.Namespace`, or exactly one colocated `.csproj` resolves by `RootNamespace`, `AssemblyName`, project filename, or folder name to an existing generated namespace page.

Compatibility README paths qualify only when all of these are true:

- The C# API harvester generated a namespace page at `Namespaces/{Dotted.Namespace}`.
- The authored file is named `README.md`.
- The README is harvested as a root documentation node, not as a child fragment.
- The README directory resolves to the same dotted namespace as the generated page.
- The path has an explicit docs-owned prefix before the namespace directory, currently a `docs/` segment or a `Namespaces/` segment.

Positive examples:

| Source path | Merged target |
| --- | --- |
| `Web/ForgeTrust.RazorWire/NAMESPACE.md` | `Namespaces/ForgeTrust.RazorWire` |
| `docs/ForgeTrust.AppSurface.Web/README.md` | `Namespaces/ForgeTrust.AppSurface.Web` |
| `Namespaces/ForgeTrust.AppSurface.Web/README.md` | `Namespaces/ForgeTrust.AppSurface.Web` |

Negative examples:

| README path | Behavior |
| --- | --- |
| `Web/ForgeTrust.AppSurface.Web/README.md` | Stays a package README page. |
| `src/ForgeTrust.AppSurface.Web/README.md` | Stays a source-adjacent README page if harvested. |
| `README.md` | Stays the repository-root docs landing source. |
| `docs/Unknown.Namespace/README.md` | Stays a normal README page unless a generated `Namespaces/Unknown.Namespace` page exists. |

### Merge behavior

- The generated namespace page keeps its `Namespaces/{Dotted.Namespace}` route.
- Child namespace links render first when the generated page has them, then intro HTML is inserted as the namespace intro, then any `Common entry points` panel renders before generated type and member detail.
- A leading intro H1 is suppressed during merge because the namespace page shell already renders the page H1.
- The standalone intro node is removed after a successful merge so readers do not see duplicate pages.
- Intro metadata can override the namespace page metadata only when the field is meaningful for the merged namespace page. Authored `title`, `summary`, `aliases`, `keywords`, `related_pages`, `breadcrumbs`, contributor provenance, and `entry_points` transfer. Derived Markdown defaults, README/package visibility flags, canonical slugs, authored redirect aliases, trust metadata, localization metadata, section landing metadata, sequence metadata, and featured-page groups do not transfer.
- Intro-relative links are resolved from the source path before the standalone intro page is removed.
- Source-shaped requests for consumed intro paths redirect to the generated namespace page, including paths such as `Web/ForgeTrust.RazorWire/NAMESPACE.md`, `Web/ForgeTrust.RazorWire/NAMESPACE`, `docs/ForgeTrust.AppSurface.Web/README.md`, and `docs/ForgeTrust.AppSurface.Web/README`.
- Contributor provenance points at the intro source, while symbol-level source links still point at the generated API declarations.

### Common entry points

Namespace intro metadata can define a compact `Common entry points` panel. AppSurface's own package/project namespace intros should use colocated `NAMESPACE.md` plus `NAMESPACE.md.yml`:

```yaml
# Web/ForgeTrust.RazorWire/NAMESPACE.md.yml
namespace: ForgeTrust.RazorWire
title: ForgeTrust.RazorWire
summary: Start here for RazorWire registration, endpoint mapping, options, and stream-result entry points.
entry_points:
  - label: AddRazorWire(...)
    summary: Register RazorWire services and package-owned options.
    target: ForgeTrust-RazorWire-RazorWireServiceCollectionExtensions-AddRazorWire-method-group
    keywords:
      - register RazorWire
      - services
  - label: RazorWireOptions
    summary: Configure package behavior without replacing the rendering pipeline.
    target: ForgeTrust-RazorWire-RazorWireOptions
```

Entry-point fields:

- `label` is required, decoded, trimmed, and limited to 80 characters.
- `summary` is optional, decoded, trimmed, and limited to 220 characters.
- `target` is an anchor ID from the generated namespace page. Authors may include one leading `#`; AppSurface Docs stores it without the hash and allows only letters, digits, `_`, `-`, `.`, and `:`.
- `href` is an escape hatch used only when `target` is absent or invalid. It must be a fragment such as `#anchor` or an app-relative docs URL under the active docs root, for example `/docs/...` or `/foo/bar/...`.
- `keywords` are distinct search terms, up to 20 values of 80 characters each.
- `order` is an optional non-negative integer. Ordered entries render first, then unordered entries keep author order.

When `target` resolves, the whole editorial row is one real anchor to the generated API section. When `target` is valid but stale, the row renders as unlinked text with `Target unavailable`, AppSurface Docs logs a warning, and harvest health includes `DocHarvestDiagnosticCodes.NamespaceEntryPointTargetUnresolved`. A stale entry point does not fail the docs site.

### Decision guidance

Use `NAMESPACE.md` when AppSurface-authored content is specifically about a namespace API surface: concepts, intended usage, lifecycle notes, or cross-type orientation for that namespace. When `NAMESPACE.md.yml` includes `namespace: Dotted.Namespace`, that explicit target wins; without it, AppSurface Docs infers only from exactly one colocated `.csproj` and an exact generated namespace page match. If inference fails or multiple project files are colocated, the `NAMESPACE.md` source is hidden from public routes and harvest health reports a warning so the author can add explicit metadata.

Docs-owned namespace README paths such as `docs/ForgeTrust.RazorWire/README.md` and `Namespaces/ForgeTrust.RazorWire/README.md` remain supported for compatibility with existing content and portable folder-index README layouts. They are not the internal AppSurface house style for new namespace intros.

Use a package README when the content is about package adoption: installation, package-level configuration, examples, compatibility, and links to broader guides. Package READMEs such as `Web/ForgeTrust.AppSurface.Web/README.md` and `src/ForgeTrust.AppSurface.Web/README.md` do not automatically become namespace intros, even when the folder name matches a namespace. That boundary is intentional so package docs do not disappear into API pages by folder-name coincidence.

### Pitfalls

- Do not move package READMEs under package folders expecting them to merge into namespace pages.
- Do not rely on the final folder name alone. A README path needs a docs-owned prefix before the namespace directory, and `NAMESPACE.md` inference uses only exact matches.
- Do not expect a namespace intro to create a namespace API page. It only merges into a namespace page produced by the C# harvester.
- Do not use entry points as a full symbol resolver. V1 targets generated anchor IDs on the same namespace page.
- Do not rely on `hide_from_search` or `hide_from_public_nav` in a consumed namespace intro to hide the namespace page. Those flags apply to the standalone source node and are dropped during transfer.

## Usage

Reference the package and add the module to your AppSurface web application:

```csharp
await WebApp<AppSurfaceDocsWebModule>.RunAsync(args);
```

## Public Sections

AppSurface Docs now organizes public documentation around a fixed section-first model instead of a flat directory-first landing.

### Built-in sections

- `Start Here`
- `Concepts`
- `How-to Guides`
- `Examples`
- `Packages`
- `API Reference`
- `Releases`
- `Troubleshooting`
- `Internals`

These sections back the current docs home, the sidebar shell, and the dedicated section routes under the current docs surface, for example `{DocsRootPath}/sections/{slug}`.

### `nav_group` normalization and fallback rules

- `nav_group` can explicitly select a built-in public section by canonical label, slug, or alias.
- Invalid explicit `nav_group` values log a warning and fall back to AppSurface Docs-derived section assignment instead of creating ad hoc groups.
- Markdown docs with no explicit `nav_group` are derived into built-in sections using path and filename heuristics:
  - repository-root `README.md` and start-like names such as `quickstart` or `getting-started` fall into `Start Here`
  - `examples/` content falls into `Examples`
  - `releases/` content and root changelogs fall into `Releases`
  - package chooser and AppSurface package-level README paths fall into `Packages`
  - concepts, architecture, explanation, and glossary-style paths fall into `Concepts`
  - troubleshooting, faq, debug, and error-oriented paths fall into `Troubleshooting`
  - internal-oriented paths fall into `Internals`
  - anything else falls into `How-to Guides`
- API reference content continues to use the canonical `API Reference` section.

### API reference sidebar shape

The primary sidebar keeps API Reference collapsed at the package and namespace level. Generated type and member anchors
stay available from the namespace page itself through `On this page`, source links, and search, but they are not emitted
as nested links in the global left rail. Deeper namespaces nest under the nearest namespace page that exists in the
sidebar and use only their leaf label, so `AppSurface.Core` can show child links such as `Defaults` and `Extensions`
instead of repeating `AppSurface.Core.Defaults` and `AppSurface.Core.Extensions`. This keeps large harvested API surfaces
browsable without making readers scan hundreds of symbols before they intentionally open a namespace page.

Configure `AppSurfaceDocs:Sidebar:NamespacePrefixes` when a host wants package names shortened in that rail. For example,
`ForgeTrust.AppSurface.` turns `ForgeTrust.AppSurface.Docs.Services` into a `Web` family heading with
`Docs.Services` as the namespace link label.

### Section routes and landing docs

- The current docs surface exposes section routes such as `{DocsRootPath}/sections/start-here`.
- Only canonical slugs are served directly; label- or alias-shaped section requests redirect to the canonical section route.
- When a section has an authored landing doc, AppSurface Docs redirects the section route to that page.
- Sections with visible pages but no landing doc render a grouped fallback section page instead of a dead end.
- Invalid slugs or sections with no public pages render an unavailable section surface with recovery links back to the current docs home and `Start Here`.

### `section_landing`

Use `section_landing: true` on a page to mark it as the authored entry point for its public section.

```yaml
title: Start Here
nav_group: Start Here
section_landing: true
summary: Start with the strongest evaluator proof path before drilling into implementation detail.
```

Field behavior and pitfalls:

- The page must still belong to a valid built-in public section through explicit or derived `nav_group`.
- If multiple docs in one section set `section_landing: true`, AppSurface Docs keeps the lowest `order` value, then the lowest canonical path, and logs a warning for the others.
- A section landing doc can also author `featured_page_groups`; AppSurface Docs uses those reader-intent groups for section-level “next steps” on the detail page and collapses the first resolved rows into section preview links surfaced on the current docs home.
- `HideFromPublicNav = true` always wins. Hidden pages do not appear in section routes, the sidebar, the docs home, or the public search index even if they declare a section or landing status.
- Default harvesting excludes test-project directories such as `Tests`, `Test`, `*.Tests`, `*.UnitTests`, and `*.IntegrationTests`. The C# harvester also skips `examples` directories so example README walkthroughs can stay public without publishing generated API reference for example application internals.

## Docs Link Authoring

AppSurface Docs rewrites links inside harvested Markdown so authors can use source-friendly paths while readers stay on public docs-surface routes such as `{DocsRootPath}/start-here/appsurface-evaluator` with Turbo history support.

### Authoring contract

- Link to another harvested doc with its source path, such as `./guide.md`, `../CHANGELOG.md`, or `/releases/unreleased.md`.
- Link to an already public docs route only when the target is a harvested doc, such as `/docs/releases/unreleased`, `/docs/next/releases/unreleased`, or a custom-root equivalent like `/foo/bar/releases/unreleased`.
- Use ordinary site URLs, such as `/privacy.html` or `../status.html`, for non-doc pages. AppSurface Docs leaves those links untouched.
- Use browser-facing URLs for metadata fields that render plain anchors without content rewriting, such as `trust.migration.href`.
  `trust.migration.href` accepts relative URLs, root-relative URLs, fragment links, and absolute `http` or `https` URLs.
  Blank values are treated as absent. Nonblank executable schemes such as `javascript:` or `data:`, protocol-relative URLs
  such as `//example.com`, control-character values, and other absolute schemes are rejected before rendering.

### Catalog-backed rewriting

During aggregation, AppSurface Docs builds a route identity catalog from the harvested documentation nodes. Link rewriting consults that catalog before converting any source or public-looking link into the active docs surface.

This means a link is rewritten only when the target exists in the harvested docs set. A missing `./guide.md`, an ambiguous docs route like `/docs/missing`, or a normal site page like `../privacy.html` remains authored as-is instead of being guessed into a broken docs route.

### Pitfalls

- Do not rely on file extensions alone. A `.md`, `.cs`, or `.html` suffix does not make a link an AppSurface Docs target unless the target was harvested.
- If a doc link is not rewritten, first confirm the target file is included by the active harvester and not excluded by directory policy.
- Public docs-surface links are safe for exported docs, but source-relative Markdown links are usually easier to keep portable in GitHub and editor previews.
- Details pages emit a canonical link for the clean public route. In CDN export mode, RazorWire rewrites app-relative canonical links to the emitted static artifact URL, such as `/docs/guides/intro.html`, so exported pages and HTML redirect alias artifacts agree on the same static canonical destination. Netlify `_redirects` rules still use the live alias and canonical routes because provider redirects run before static artifact lookup.
- Set `AppSurfaceDocs:Routing:PublicOrigin` before export when the crawl happens on loopback but the artifact will publish under a public origin. Otherwise canonical links stay app-relative instead of naming the production host.

## Landing Curation

AppSurface Docs can turn the root docs landing into a curated reader-intent surface by reading `featured_page_groups` from the repository-root `README.md` metadata.

### Authoring contract

`featured_page_groups` is parsed as part of `DocMetadata`, so the metadata contract stays page-agnostic. AppSurface Docs uses those groups in two places:

- the root `README.md` metadata drives grouped proof-path rows on the current docs home
- any authored section landing doc can drive grouped section-level next-step rows and the section preview links shown on the current docs home

Authors can now supply that metadata in either of two places:

- Inline Markdown front matter at the top of the `.md` file
- A paired sidecar YAML file such as `README.md.yml` or `README.md.yaml`

Inline front matter remains the default authoring path for ordinary docs pages. Paired sidecars are the recommended escape hatch for portability-sensitive files such as `README.md`, where raw front matter renders poorly on GitHub and other plain Markdown surfaces.

```yaml
# README.md.yml
title: AppSurface
summary: Follow the proof paths that explain what this framework is for and how it composes.
featured_page_groups:
  - intent: understand
    label: Understand the model
    summary: Start here when you need the mental model before choosing an implementation path.
    order: 10
    pages:
      - question: How does composition work?
        path: guides/composition.md
        supporting_copy: Start with the composition guide before drilling into APIs.
        order: 10
  - label: See it working
    order: 20
    pages:
      - question: Show me an end-to-end example
        path: examples/hello-world/README.md
        order: 10
```

### Field behavior

- `intent` is the stable group identity. If omitted, AppSurface Docs derives one from `label`.
- `label` is the reader-facing group heading. If omitted, AppSurface Docs title-cases `intent`.
- `summary` explains when a reader should choose the group.
- `order` is optional on groups and pages. Lower values sort first, and ties preserve authored order.
- `pages` must contain the featured destinations for the group. Empty page lists are skipped.
- `question` is the reader-facing label shown on a row. If omitted, AppSurface Docs falls back to the destination page title.
- `path` accepts either the source path or canonical docs path for the destination page, including an exact `#fragment` suffix when the card should land on a specific section. AppSurface Docs normalizes forward-slash and backslash separators during resolution while preserving fragment identifiers, and the same resolver used by page details handles the configured live docs root.
- `supporting_copy` is optional landing-only text. If omitted, AppSurface Docs falls back to the destination page summary.

Author three to five groups for a broad landing page, and one to three pages per group. Prefer plain reader intents such as `understand`, `choose-package`, `see-it-working`, `release-risk`, and `api-reference`. Use custom intents when your product has domain-specific decisions that those defaults do not capture.

Preview locally from the repository root with the standalone docs host:

```bash
dotnet run --project Web/ForgeTrust.AppSurface.Docs.Standalone -- --urls http://localhost:5189
```

Or use the AppSurface CLI shape, which keeps AppSurface Docs workflows under the `appsurface` command family:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs --repo .
```

The CLI prints the docs home after the preview host is listening and attempts to open that page in the system browser. The AppSurface CLI defaults the host environment to `Development`, so when no endpoint is configured it uses AppSurface Web's deterministic per-workspace localhost URL while suppressing routine ASP.NET Core lifecycle output. The standalone host remains the reusable runtime seam; `appsurface docs` is the public CLI entry point for the same preview workflow rather than a separate legacy docs tool.

### Fallback and visibility rules

- If the root `README.md` is missing, the landing stays on the neutral docs index.
- If `featured_page_groups` is missing, the landing uses the neutral docs index unless the Start Here public section can provide the built-in proof-path fallback.
- If `featured_page_groups: []` is authored inline, the explicit empty list is authoritative and suppresses sidecar fallback.
- If both `README.md.yml` and `README.md.yaml` exist for the same Markdown file, AppSurface Docs logs a warning and ignores both sidecars until the conflict is removed.
- If both sidecar metadata and inline front matter define the same field, inline front matter wins and the sidecar acts as fallback metadata only.
- Invalid sidecar YAML logs a warning and falls back to the inline/default metadata path instead of breaking the page harvest.
- Oversized sidecars emit `appsurfacedocs.markdown.metadata_file_too_large`, are ignored before YAML parsing, and do not block the Markdown body from publishing. Sidecars use `AppSurfaceDocs:Harvest:Markdown:MaxMetadataFileSizeBytes`; inline front matter stays part of the Markdown body and uses `AppSurfaceDocs:Harvest:Markdown:MaxFileSizeBytes`.
- If a featured path is missing, hidden from public navigation, or duplicated, AppSurface Docs skips it and logs a warning.
- If all featured entries are skipped, AppSurface Docs logs one final warning and falls back instead of rendering broken rows.
- The old flat `featured_pages` field is ignored and logs a migration warning. If both fields are present, `featured_page_groups` wins.

Diagnostics include the source file, field path when available, problem, cause, and fix. Common warnings are stale `featured_pages`, missing group identity, missing or null `pages`, flat-looking group entries, blank `path`, missing destination, hidden destination, duplicate destination, invalid YAML, sidecar extension conflicts, and all groups skipped after resolution.

### Pitfalls

- Do not create both `.yml` and `.yaml` sidecars for the same Markdown file. AppSurface Docs treats that as an authoring error and ignores both.
- Do not use a sidecar as a second secret metadata system. It supports the same `DocMetadata` schema as inline front matter, and it is best reserved for files whose Markdown needs to stay portable on other surfaces.
- Do not put long prose into sidecars to bypass the Markdown body limit. Sidecars are capped separately because they are metadata, not document body content.
- Do not put `path` or `question` directly under a group. Page fields belong under `pages`.
- Prefer source-relative paths for authored curation when the docs may be exported or mounted under more than one route. Canonical docs-surface paths are accepted for parity with browser links, but source paths stay easier to review and move with the file.
- README portability matters most at the repository and package level. In this repo, authored `README.md` files should stay free of inline front matter so GitHub renders them cleanly.

## Metadata-Driven Wayfinding

AppSurface Docs can render two kinds of page-local wayfinding on details pages without scraping rendered HTML after the fact:

- `On this page` links come from the harvested `DocNode.Outline` contract.
- `Previous` and `Next` proof-path links come from explicit metadata, not folder inference.

### Page-local outline behavior

`On this page` is local navigation for the current detail page. It intentionally does not mirror the left sidebar, which remains global documentation navigation. This keeps the two maps separate: the sidebar answers "where am I in the docs product?" while the outline answers "where am I on this page?"

The built-in Markdown harvester creates the display outline from rendered H2-H3 headings, then applies a repeated-heading policy before exposing `DocNode.Outline` to details views and search heading metadata. The page body HTML is not rewritten by this policy. Suppressed H3 headings keep their rendered IDs, so direct hash links and full-body search recall still work.

By default, Markdown pages include H2-H3 outline entries unless repeated H3 titles would dominate the reader-facing outline. Troubleshooting pages use the lower automatic threshold because they commonly repeat mechanic headings such as "Symptom" and "Cause" under each problem section. A page is treated as troubleshooting when it authors `page_type: troubleshooting` or when path-derived public-section metadata places it in `Troubleshooting`.

Authors can override the automatic behavior with nested outline metadata:

```yaml
outline:
  max_heading_level: 3
  repeated_heading_policy: include
```

`outline.max_heading_level` accepts `2` or `3` and wins over `outline.repeated_heading_policy` when both are present. `outline.repeated_heading_policy` accepts:

- `auto`: use the repeated-heading heuristic
- `include`: keep H2-H3 entries even when repeated H3 headings dominate
- `h2_only`: expose only H2 entries

Invalid child values are ignored field by field and produce metadata diagnostics such as `invalid-outline-max-heading-level` or `invalid-outline-repeated-heading-policy`. A malformed `outline` value such as `outline: true` or `outline: []` is ignored as `invalid-outline-metadata`; when inline front matter is malformed this way, paired sidecar outline metadata can still act as fallback because the invalid outline object normalizes away.

When `DocDetailsViewModel.HasOutline` is true, AppSurface Docs renders one semantic outline nav:

- wide desktop (`>=1280px`): a sticky right rail beside the article
- narrower viewports: a closed-by-default `On this page` toggle above the article
- all viewports: the same outline list, never separate desktop and mobile TOCs

The outline client enhances the server-rendered links by:

- using `#main-content` as the scroll root for `IntersectionObserver`
- marking the current section with `aria-current="location"`
- refreshing the active section from the scroll position on scroll, throttled through `requestAnimationFrame`, so long sections do not stay pinned to the previous outline item until the next heading enters the observer band
- keeping the active outline link visible inside the sticky desktop rail when the page-local outline is taller than the viewport
- easing outline-link clicks to the target section over 620 ms, while preserving instant jumps for readers who prefer reduced motion and canceling the animation when the reader manually wheels or touches the scroll root
- initializing from the current URL hash
- rebinding after RazorWire/Turbo frame navigation replaces `rw:island id="doc-content"`
- containing scroll inside the expanded compact outline so touch or wheel input over `On this page` does not move the article behind it
- collapsing the mobile outline after an outline link is chosen
- skipping missing heading targets for active-state tracking while leaving their normal hash links intact instead of marking stale entries current or closing the drawer

If JavaScript is unavailable, the server-rendered outline remains a normal list of hash links. If `IntersectionObserver` is unavailable, AppSurface Docs keeps static and hash-based behavior rather than adding a scroll polling fallback.

### Sequence contract

Use `sequence_key` together with `order` when a set of pages should behave like one proof path:

```yaml
sequence_key: razorwire-proof
order: 20
related_pages:
  - Web/ForgeTrust.RazorWire/README.md
  - Web/ForgeTrust.RazorWire/Docs/antiforgery.md
```

- `sequence_key` opts a page into a specific sequence. Pages do not join a sequence just because they share a folder.
- `order` determines the relative previous/next position inside that sequence.
- `related_pages` stays independent from sequencing and can point to source paths, canonical docs paths, or exact page titles.
- AppSurface Docs publishes authored sequence metadata to the current-surface search index for custom clients and integrations. The live source surface emits that payload at `{DocsRootPath}/search-index.json` (for example, `/docs/search-index.json` by default when versioning is off, `/docs/next/search-index.json` by default when versioning is on, or `/foo/bar/search-index.json` for a custom unversioned route root); exported exact-version trees carry their own `search-index.json` payload at the tree root. The `sequence_key` front-matter value becomes `sequenceKey`, `order` stays `order`, and `related_pages` stays separate as `relatedPages`; for example: `{ "sequenceKey": "razorwire-proof", "order": 20, "relatedPages": ["Web/ForgeTrust.RazorWire/README.md"] }`.

### Resolution rules

- Previous/next links render only when the current page has both `sequence_key` and `order`.
- AppSurface Docs only sequences navigable pages. Fragment-only anchor stubs and pages hidden from public navigation do not appear in proof-path navigation.
- Related pages are deduplicated against the current page and any resolved previous/next neighbors.

### Pitfalls

- Do not rely on filename prefixes or folder adjacency for proof-path behavior in this slice. Use explicit `sequence_key` values instead.
- Do not expect `related_pages` to imply ordering. Related links stay unordered beyond the authored list order.

## Metadata-Driven Page Type Display

AppSurface Docs treats `page_type` metadata as structured UI input, not just as opaque search metadata. The built-in landing cards, detail pages, and search results all normalize the same metadata through `DocMetadataPresentation.ResolvePageTypeBadge()`.

### Built-in normalization

- Known values such as `guide`, `example`, `api-reference`, `internals`, `how-to`, `start-here`, `troubleshooting`, `glossary`, `faq`, and `release` render with stable labels and intentional badge variants.
- Release aliases `release-note` and `release-notes` render as the canonical `Release` badge with the normalized `release` value and variant.
- Unknown values still render safely: AppSurface Docs normalizes whitespace, underscores, and dashes, then falls back to a neutral title-cased badge.
- Missing or blank `page_type` values render no badge at all instead of leaving empty chrome behind.

### Search payload contract

The current-surface `search-index.json` payload continues to emit the raw `pageType` metadata value and now also includes:

- `pageTypeLabel` for the normalized display label used by the built-in search UI
- `pageTypeVariant` for the built-in badge variant suffix used by CSS classes such as `docs-page-badge--guide`
- `publicSection` for the normalized built-in section slug when the page is publicly visible
- `publicSectionLabel` for the reader-facing section label
- `isSectionLanding` for authored section landing entry points
- `entryPoints` for namespace-intro entry-point labels, summaries, targets, hrefs, and keywords when an intro source is consumed into a generated namespace page
- `language` and `languageLabel` for generated API documentation language facets and result chrome
These fields let custom search clients stay visually aligned with the landing and detail experiences without re-implementing the mapping table.

Search runtime note: the bundled `minisearch.min.js` asset is generated from the pinned upstream MiniSearch browser bundle, not a CDN or hand-maintained compatibility shim. The built-in search client indexes `title`, `aliases`, `keywords`, `summary`, `headings`, `bodyText`, namespace `entryPoints`, and generated API `languageSearchText` as first-class MiniSearch fields with field-specific boosts. Package maintainers changing the search runtime should update the pinned package, rebuild the generated asset, verify the third-party notice, and run the asset verification scripts before shipping.

When authored metadata uses `release-note` or `release-notes`, AppSurface Docs keeps the raw `pageType` metadata value in the payload but emits `pageTypeLabel = "Release"` and `pageTypeVariant = "release"` so built-in and custom clients can present release pages consistently.

## Custom Harvester Outline Contract

The built-in Markdown and C# harvesters now populate `DocNode.Outline` directly during harvest. Custom `IDocHarvester` implementations should do the same when they want:

- `On this page` links on details views
- heading metadata in the current-surface `search-index.json`
- stable behavior without re-parsing rendered HTML later

Each outline entry should provide the rendered fragment `Id`, the reader-facing `Title`, and the normalized heading `Level`. For visual parity with the built-in wayfinding UI, custom `IDocHarvester` implementations should populate `DocNode.Outline` only with entries that have a non-empty rendered fragment `Id` and non-empty `Title`; headings or generated sections missing either value are skipped by the built-ins. The Markdown harvester extracts source-ordered H2-H3 headings, with titles normalized from inline heading text and IDs taken from the rendered heading fragment, then applies the Markdown outline policy described above before assigning `DocNode.Outline`. The C# harvester emits level 2 entries for documented types and enums, and level 3 entries for method groups and properties. Matching those defaults keeps custom outlines aligned with the built-in `On this page` rail, active-section behavior, and search heading metadata.

Public visibility note:

- `HideFromSearch = true` removes a page from the search payload directly.
- `HideFromPublicNav = true` also removes the page from the search payload because the public shell treats hidden pages as fully non-public.
- Default path exclusions run before metadata is assigned. Test-project README files and C# source under test-project directories are not harvested, and C# source under `examples` is skipped so generated API-reference pages for example apps do not enter navigation, search, or direct docs routing.

## Trust Metadata For Release Notes And Policy Pages

AppSurface Docs can also render a top-of-page trust bar from nested `trust` metadata. AppSurface uses this for its own release notes, upgrade policy, and changelog pages so the product doubles as a working example for consumers.

```yaml
trust:
  status: Unreleased
  summary: This page is provisional until the next tag is cut.
  freshness: Updated as changes land on main.
  change_scope: Repository-wide.
  migration:
    label: Read the upgrade policy
    href: /docs/releases/upgrade-policy
  archive: Tagged release notes will keep the final narrative once the version ships.
  sources:
    - CHANGELOG.md
    - releases/unreleased.md
```

### Field behavior

- `status` is the compact top-level state, such as `Unreleased` or `Pre-1.0 policy`.
- `summary` is the short trust statement shown beside the status.
- `freshness` explains how current the page is and how stable readers should assume it is.
- `change_scope` calls out which surfaces the note covers.
- `migration` is an optional label plus browser-facing `href` to the adoption guidance. Blank hrefs are absent; unsafe
  nonblank hrefs are omitted and reported as `DocHarvestDiagnosticCodes.MetadataUnsafeTrustMigrationHref`.
- `archive` explains where the durable tagged record or long-term home lives.
- `sources` is an optional list of provenance notes or upstream artifacts.

### Merge behavior

- Inline front matter and sidecar YAML both use the same nested `trust` schema.
- Inline metadata wins over sidecar metadata field by field.
- An unsafe inline `migration.href` is treated as absent, not as a tombstone, so a safe sidecar `migration.href` can still
  render while the inline source reports a diagnostic.
- Explicit empty lists such as `sources: []` are authoritative and suppress fallback lists.

### Pitfalls

- Use a browser-facing `href` for `migration`, not a source path, because the trust bar renders a plain link without path rewriting.
- Do not use `javascript:`, `data:`, `mailto:`, protocol-relative, or other non-http absolute schemes for `migration.href`;
  AppSurface Docs drops those hrefs and keeps harvesting the page so the warning appears in logs and harvest health.
- Keep private maintainer-only runbooks outside harvested docs. Hidden pages are removed from nav and search, but they are still public if linked directly.
- Do not turn the trust bar into marketing chrome. It should answer status, safety, and provenance questions quickly.

## Related Projects

- [ForgeTrust.AppSurface.Docs.Standalone](../ForgeTrust.AppSurface.Docs.Standalone/README.md) for the exportable host used in docs export and smoke testing
- [Back to Web List](../README.md)
- [Back to Root](../../README.md)

## Notes

- This package is the reusable documentation surface; `ForgeTrust.AppSurface.Docs.Standalone` is the thin executable wrapper used for local hosting and export scenarios.
- The bundled AppSurface Docs UI includes its generated stylesheet and docs runtime files as static web assets and assembly-embedded fallback resources. The layout resolves the correct stylesheet path automatically from the host's root module shape for standalone/root-module hosts versus embedded application-part consumers, and it renders AppSurface Docs-owned CSS and JavaScript URLs with content-derived `v` query strings. Those version keys are computed from the embedded package assets so route-local aliases such as `/docs/search.css` and `/docs/search-client.js` are cache-busted even when ASP.NET Core cannot resolve them as direct static-web-asset paths. Keep new AppSurface Docs-owned chrome assets on the `AppSurfaceDocsAssetVersioner` path; plain `asp-append-version` is not enough for endpoint aliases or legacy redirects.
- Consumers do not need to call `services.AddTailwind()` unless they also want Tailwind build/watch integration for their own host application's CSS.
- It depends on the Tailwind package family for AppSurface Docs package build-time styling generation and on the caching package for docs aggregation performance.
