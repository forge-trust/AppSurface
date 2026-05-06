# ForgeTrust.Runnable.Web.RazorDocs

Documentation site generation and hosting for Runnable web applications.

## Overview

`ForgeTrust.Runnable.Web.RazorDocs` is the reusable Razor Class Library package behind the RazorDocs experience. It aggregates Markdown and C# API documentation into a browsable docs UI, supports an optional version archive for published releases, and is intended to be embedded into Runnable web applications or used by the standalone RazorDocs host.

If you are evaluating RazorDocs for your own repository, start with [Use RazorDocs in your repository](./use-razordocs.md). That page explains the consumer model, host shape, authoring metadata, and adoption checklist before you drill into this package reference.

## What It Provides

- `RazorDocsWebModule` for wiring the docs UI into a Runnable web host
- `AddRazorDocs()` for typed options binding and core service registration
- `DocAggregator` plus the built-in Markdown and C# harvesters
- `DocsUrlBuilder` plus the MVC surface used by RazorDocs consumers so the live docs root, search shell, and archive routes stay in one shared contract
- `RazorDocsVersionCatalog` plus `RazorDocsVersionCatalogService` for mounting exact published release trees and surfacing release-level status in the public archive
- Structured trust metadata plus a built-in trust bar for release notes, upgrade guides, and other pages that need status and provenance near the top
- Contributor provenance rendering with a `Source of truth` strip for source links, edit links, and relative `Last updated` timestamps on details pages
- Precompiled Tailwind-powered styling with layout-time path resolution for root-module and embedded hosts

## Styling Boundary

When choosing where a new RazorDocs style should live, use this order:

1. If the surface needs a reusable component contract or a selector shared across CSS and JavaScript, use a semantic class.
2. Otherwise, if RazorDocs does not fully control the nested content markup, use wrapper-scoped semantic CSS.
3. Otherwise, for one-off package chrome that RazorDocs owns directly, prefer Tailwind utility classes in markup.

This section is the normative source of truth for the boundary. `DESIGN.md` explains why the rule exists and how to review edge cases. `ROADMAP.md` only points future work back to this contract.

### Decision Matrix

| Surface | Default | Why | Real examples | Exception / note |
| --- | --- | --- | --- | --- |
| One-off owned package chrome in Razor views | Prefer Tailwind utility classes in markup | RazorDocs fully owns the markup, so local utility classes keep intent obvious where the change happens | docs landing shell in `Views/Docs/Index.cshtml`, sidebar shell and layout framing in `Views/Shared/_Layout.cshtml`, one-off page header spacing in `Views/Docs/Details.cshtml` | If the same styling contract repeats across package surfaces, promote it to a semantic component class instead of copying long utility strings |
| Reusable owned package components or stable cross-file UI selectors | Use semantic component classes in the shared package stylesheet | Shared selectors keep repeated UI stable across Razor, CSS, and sometimes JavaScript | `docs-page-badge`, `docs-metadata-chip`, `docs-page-meta`, `docs-provenance-strip`, `docs-trust-bar` in `wwwroot/css/app.css` | Utilities can still handle surrounding layout and one-off placement |
| Harvested or generated document bodies that RazorDocs does not fully author element by element | Use wrapper-scoped semantic CSS such as `.docs-content ...` in the shared package stylesheet | RazorDocs cannot safely push utility classes into nested harvested HTML | headings, paragraphs, code blocks, overload groups, and namespace sections inside `.docs-content` in `Views/Docs/Details.cshtml` and `wwwroot/css/app.css` | Do not rewrite harvested nested HTML just to satisfy utility-class purity |
| JavaScript-generated or stateful UI that needs CSS and JavaScript to share stable hooks | Use semantic hook classes, then style them in CSS | Runtime UI needs stable names both the stylesheet and script can rely on | search result rows, filter chips, active-filter pills, and state containers in `wwwroot/docs/search.css` and `wwwroot/docs/search-client.js` | Use `id` values where uniqueness or ARIA wiring require them, but keep reusable styling and state contracts on semantic classes |

### Common Calls

- New one-off page header spacing or typography in owned Razor markup: use Tailwind utilities in the view.
- New reusable badge, metadata chip, page metadata row, or trust/provenance surface: add or extend a semantic component class in `wwwroot/css/app.css`, then use utilities around it only when they are purely local.
- For `Views/Docs/Search.cshtml`, keep the stateful search container or interactive hook semantic, but use local utilities for one-off header copy, helper layout, and fallback-link chrome inside that view.
- Restyling paragraphs, headings, or code blocks inside `.docs-content`: update wrapper-scoped CSS instead of pushing utility classes into harvested HTML.
- Long authored Markdown pages use `.docs-content--markdown` for prose measure, paragraph rhythm, list spacing, links, blockquotes, and inline code. Generated C# API pages use `.docs-content--api` so signatures and reference tables keep the wider base content measure.
- New search filter pill, active-filter surface, or other stateful search UI: use a semantic hook class because CSS and JavaScript both need to recognize it.

### Stylesheet Responsibilities

- `wwwroot/css/app.css` is the Tailwind entry point for the generated package stylesheet (`site.gen.css`). It owns shared RazorDocs component primitives and wrapper-scoped document body styling because the generated stylesheet is loaded on every docs page before any search-specific assets.
- `wwwroot/docs/search.css` owns the search shell, interactive search controls, JavaScript-rendered result states, empty/failure states, and search skeletons. It should not define shared page badges, metadata chips, provenance strips, trust bars, or other primitives required by non-search docs pages.

### Terms

- **Package chrome**: one-off layout and presentation markup that RazorDocs owns directly, such as page shells, spacing, and framing.
- **Harvested content**: nested documentation HTML that RazorDocs renders but does not fully author element by element, such as the body inside `.docs-content`.
- **Markdown prose surface**: authored Markdown rendered with `.docs-content--markdown`; it optimizes for reading dense release notes, guides, and README-style pages rather than for wide generated API signatures.
- **Stable selector / hook**: a semantic class or required unique `id` that Razor, CSS, accessibility wiring, and sometimes JavaScript rely on consistently across files.

### Pitfalls

- Do not refactor between utilities and semantic CSS for purity alone. Follow the surface contract unless a real usability or maintainability problem exists.
- Do not treat required `id` values, such as `docs-search-page-input` or `docs-search-page-filters-panel`, as the reusable styling contract. They exist for uniqueness, targeting, and ARIA relationships.
- Do not assume every child inside a semantic search container needs its own semantic class; local typography and spacing inside one view can still stay inline.
- Do not add semantic classes to static package chrome when plain utilities are clearer and the styling is truly local.
- Do not place non-search primitives in `wwwroot/docs/search.css` just because the layout loads search assets globally today. Use `wwwroot/css/app.css` for shared components so future theming can target one stable package layer.

## Configuration

RazorDocs is still source-backed at runtime in this slice. `RazorDocs:Mode` should stay `Source`, and `RazorDocs:Bundle` remains reserved for a later reusable runtime-bundle host. Versioning in this slice does **not** change the runtime source mode; it adds a catalog that mounts already-exported release trees beside the live source-backed preview surface.

### Source-backed docs without versioning

Use the default single-surface configuration when you want the live docs experience rooted directly at `/docs`:

```json
{
  "RazorDocs": {
    "Mode": "Source",
    "CacheExpirationMinutes": 5,
    "Source": {
      "RepositoryRoot": "/path/to/repo"
    }
  }
}
```

If `RazorDocs:Source:RepositoryRoot` is omitted, the package falls back to repository discovery from the app content root.

### Source-backed docs with published-version routing

Enable versioning when you want the host to keep serving the live unreleased snapshot from source while also mounting exact published release trees under stable public routes:

```json
{
  "RazorDocs": {
    "Mode": "Source",
    "Source": {
      "RepositoryRoot": "/path/to/repo"
    },
    "Routing": {
      "DocsRootPath": "/docs/next"
    },
    "Versioning": {
      "Enabled": true,
      "CatalogPath": "artifacts/razordocs/versions.json"
    }
  }
}
```

### Route contract

- When `RazorDocs:Versioning:Enabled` is `false`, the live source-backed docs surface stays rooted at `/docs`.
- When `RazorDocs:Versioning:Enabled` is `true`, the live source-backed docs surface moves to `/docs/next` by default.
- When versioning is enabled, `/docs` becomes the stable public entry alias for the catalog's recommended published release.
- Exact published versions are mounted under `/docs/v/{version}`.
- The public archive is always mounted at `/docs/versions`.
- The live search shell and search index follow the current source surface root. That means `/docs/search` and `/docs/search-index.json` when versioning is off, and `/docs/next/search` plus `/docs/next/search-index.json` when versioning is on.

### Option reference

- `RazorDocs:Mode`
  - Keep this at `Source` in this slice.
  - `Bundle` still validates as unsupported because reusable request-time bundle hosting is deferred.
- `RazorDocs:Source:RepositoryRoot`
  - Optional absolute or app-relative repository root for source harvesting.
  - When omitted, RazorDocs falls back to repository discovery from the content root.
- `RazorDocs:CacheExpirationMinutes`
  - Controls the absolute lifetime of the shared docs snapshot that backs docs pages, public-section data, and `{DocsRootPath}/search-index.json`; for example, `/docs/search-index.json` by default or `/docs/next/search-index.json` when `RazorDocs:Routing:DocsRootPath` is `/docs/next`.
  - Defaults to `5` minutes.
  - Must be a finite positive number from `0.016666666666666666` through `35791394.1`, inclusive.
  - Must map to a whole number of seconds, because `{DocsRootPath}/search-index.json` uses the same duration for its private `Cache-Control` `max-age` header.
  - Do not use `0`, sub-second values, or extreme values such as `double.MaxValue`; RazorDocs rejects values outside the supported range during options validation.
- `RazorDocs:Routing:DocsRootPath`
  - Controls the live source-backed docs root.
  - Defaults to `/docs` when versioning is off.
  - Defaults to `/docs/next` when versioning is on.
  - Relative-looking values such as `docs/preview` are normalized to app-relative paths like `/docs/preview`.
  - The one supported exception is `/`, which mounts docs at the application root for single-purpose docs hosts.
  - Other than that root-mount exception, the path must start with `/docs`, must not end with `/`, and cannot contain query or fragment segments.
  - When versioning is on, non-root-mounted values still cannot be `/docs`, `/docs/versions`, `/docs/v`, or any `/docs/v/...` path.
- `RazorDocs:Versioning:Enabled`
  - Turns on the published-version route contract and archive surface.
  - Does not switch the runtime into bundle mode.
- `RazorDocs:Versioning:CatalogPath`
  - Required when versioning is enabled.
  - Points to the JSON catalog that describes the published exact-version trees and the recommended release alias.
  - Relative paths resolve from the app content root.

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
  - Exact version string that should also be mounted at `/docs`.
  - Must point at a public, available version or the `/docs` entry falls back to the archive-style recovery surface.
- `versions[].version`
  - Exact version identifier such as `1.2.3` or `1.2.3-rc.1`.
- `versions[].label`
  - Optional reader-facing label shown in the archive. Defaults to `version`.
- `versions[].summary`
  - Optional archive summary copy.
- `versions[].exactTreePath`
  - Path to the exported stable `/docs` subtree for one exact release.
  - Relative paths resolve from the directory containing the catalog file.
  - RazorDocs can mount that same artifact at `/docs` for the recommended alias and at `/docs/v/{version}` for the exact release surface.
- `versions[].supportState`
  - Archive posture badge. Supported values are `Current`, `Maintained`, `Deprecated`, and `Archived`.
- `versions[].visibility`
  - Archive visibility policy. Supported values are `Public` and `Hidden`.
- `versions[].advisoryState`
  - Release-level warning badge. Supported values are `None`, `Vulnerable`, and `SecurityRisk`.

### Exact-version tree contract

Each `exactTreePath` directory is treated as a prebuilt static subtree for one exact release. It should be exported from the stable `/docs` surface, and at minimum it must include:

- `index.html` at the tree root
- `search.html` at the tree root
- `search-index.json` at the tree root
  - The payload must remain valid JSON with a top-level `documents` array so version-local search can load safely.
  - Every `documents[]` entry must include non-empty string `path` and `title` properties.
  - Missing or blank `path`/`title` values cause RazorDocs to reject the published release tree during startup validation.
- `search.css` at the tree root
- `search-client.js` at the tree root
- `minisearch.min.js` at the tree root
- any section, detail, partial, and asset routes that belong to the exported `/docs` surface for that release

RazorDocs does not regenerate these trees at request time. It resolves extensionless requests back to the exported `.html` files and rewrites stable-root HTML plus `search-index.json` payloads so the same artifact can serve both `/docs` and `/docs/v/{version}` honestly. Exporters should validate `search-index.json`, `search.css`, `search-client.js`, and `minisearch.min.js` before publishing because a missing runtime asset or a malformed search payload keeps that release unavailable until the artifact is fixed. Use the [RazorWire CLI](../ForgeTrust.Runnable.Web.RazorWire.Cli/README.md) or another static-export pipeline to publish those trees ahead of time.

### Archive ordering

- The public archive preserves the authored order of `versions[]` from the catalog.
- Use that list order when you want a specific narrative ordering that differs from plain lexical version sorting.

### Availability and failure behavior

- Version validation is best-effort and release-local.
- A missing or malformed `exactTreePath` marks only that release unavailable.
- Healthy published versions and the live preview surface continue to load.
- If the configured `recommendedVersion` is hidden, missing, or unavailable, RazorDocs does not mount it at `/docs`; the `/docs` route falls back to the archive-style recovery surface with a link to the live preview.

### Pitfalls

- Do not set `RazorDocs:Routing:DocsRootPath` to `/docs` when versioning is enabled. That collides with the stable published-release alias.
- Do not point `recommendedVersion` at a hidden or broken release tree.
- Do not assume `RazorDocs:Versioning:Enabled` means the runtime can read request-time bundles. This slice still serves the live preview from source and mounts published releases as static trees.
- Do not forget `search-index.json` in an exported release tree. A release without it is intentionally marked unavailable.

`RazorDocs:CacheExpirationMinutes` is interpreted as minutes. Use shorter values for source-backed development hosts where authors need edits to appear quickly; use longer values for production hosts when harvesters are expensive or the docs corpus changes only during deploys.

Pitfalls:

- Do not set `CacheExpirationMinutes` to `0` to disable caching. RazorDocs rejects zero and negative values because every request would rebuild the docs snapshot and search index.
- Do not set tiny positive values below `0.016666666666666666` minutes; the search-index `Cache-Control` `max-age` header cannot represent sub-second cache lifetimes.
- Do not set fractional-second values such as `0.333` minutes. RazorDocs rejects values that cannot round-trip to a whole-second `max-age`.
- Do not set huge finite values such as `double.MaxValue`. RazorDocs caps the value so the derived search-index `Cache-Control` `max-age` remains representable.
- The search-index response uses the same duration for its private `Cache-Control` `max-age`, so client refresh behavior stays aligned with server-side snapshot reuse.
- Manual refresh through `{DocsRootPath}/search-index.json?refresh=1` still invalidates the server snapshot generation immediately for authenticated users; it does not change the configured TTL for later entries. For example, when `RazorDocs:Routing:DocsRootPath` is `/docs/next`, use `/docs/next/search-index.json?refresh=1`.

## Contributor Provenance

RazorDocs can render a lightweight `Source of truth` strip directly under the page title and summary on details pages. The strip is evidence-driven:

- `View source` links to the authored source when RazorDocs can identify one safely.
- `Edit this page` links to an edit surface when the host configures one safely.
- `Last updated` renders as relative time with an exact machine-readable `<time datetime="...">` value behind it.

If a page has no trustworthy contributor evidence, RazorDocs omits the strip entirely instead of rendering placeholder copy.

### Host configuration

Contributor provenance is configured under `RazorDocs:Contributor`:

```json
{
  "RazorDocs": {
    "Mode": "Source",
    "Source": {
      "RepositoryRoot": "/path/to/repo"
    },
    "Contributor": {
      "Enabled": true,
      "DefaultBranch": "main",
      "SourceRef": "8b7c6d5",
      "SourceUrlTemplate": "https://github.com/forge-trust/Runnable/blob/{branch}/{path}",
      "SymbolSourceUrlTemplate": "https://github.com/forge-trust/Runnable/blob/{ref}/{path}#L{line}",
      "EditUrlTemplate": "https://github.com/forge-trust/Runnable/edit/{branch}/{path}",
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

- If `Enabled` is `false`, RazorDocs skips contributor rendering and does not enforce `DefaultBranch` or `{path}` template requirements at startup.
- If `Enabled` is `true` and `SourceUrlTemplate` or `EditUrlTemplate` is configured, `DefaultBranch` is required and RazorDocs fails options validation on startup when it is missing.
- If `Enabled` is `true` and `SourceUrlTemplate` or `EditUrlTemplate` is configured, that template must contain `{path}`. RazorDocs rejects startup when a template would collapse every page to one shared URL.
- If `Enabled` is `true` and `SymbolSourceUrlTemplate` is configured, the template must contain `{path}` and `{line}`, and unsupported `{token}` placeholders are rejected at startup. If it contains `{ref}`, RazorDocs uses `SourceRef` first and falls back to `DefaultBranch`; one of those values must be configured. If it contains `{branch}`, `DefaultBranch` must be configured.
- Templates expand both the branch and normalized source path segment-by-segment, so slash-separated refs stay readable while spaces and other special characters are still URL-escaped safely.
- Git-backed freshness runs during docs snapshot generation, not during view rendering. RazorDocs uses a bounded snapshot-time freshness budget so slow or wedged git lookups degrade to omitted timestamps instead of stretching one timeout across the whole docs corpus. If git is unavailable, shallow, or missing history for a page, RazorDocs omits only `Last updated`.
- Hosts that want `LastUpdatedMode: Git` in CI or export jobs must provide real history for the docs checkout. For GitHub Actions, use `actions/checkout` with `fetch-depth: 0` or another checkout shape that preserves commit history for the rendered files.

### C# symbol source links

Generated C# API pages can render small `Source` links beside documented types, enums, method overloads, and properties. These are symbol-level links, not page-level namespace links. Namespace API pages are synthetic and may contain declarations from many files, so RazorDocs only links a generated API symbol when the harvester captured an exact source path and 1-based declaration line for that rendered anchor.

`SymbolSourceUrlTemplate` is separate from `SourceUrlTemplate` because symbol links need `{line}` and page-level Markdown links do not. Prefer `{ref}` with `SourceRef` when publishing docs from CI so readers jump to the same code version used to build the docs:

```json
{
  "RazorDocs": {
    "Contributor": {
      "DefaultBranch": "main",
      "SourceRef": "8b7c6d5",
      "SymbolSourceUrlTemplate": "https://github.com/forge-trust/Runnable/blob/{ref}/{path}#L{line}"
    }
  }
}
```

Custom harvesters can populate `DocNode.SymbolSourceProvenance`, but RazorDocs only renders links for content that also includes the compatible placeholder emitted by the built-in C# harvester. The current placeholder contract is an implementation detail for generated API HTML:

```html
<span data-razordocs-symbol-source="anchor-id"></span>
```

RazorDocs expands or removes those placeholders during snapshot generation before HTML sanitization runs. If a placeholder has no safe href, if the source path is not repository-relative, if the line number is invalid, if duplicate placeholders make an anchor ambiguous, or if multiple provenance entries claim the same anchor, RazorDocs omits the symbol link. A missing link is better than a confident wrong line.

When a namespace README is merged into a generated namespace API page, the page-level strip still points to the README source and uses the label `Namespace intro source`. The generated API symbols on the same page use their own inline `Source` links.

### Page-level overrides

Authors can supply a nested `contributor:` block in inline Markdown front matter or in a paired sidecar such as `page.md.yml`:

```yaml
contributor:
  hide_contributor_info: true
  source_path_override: Web/ForgeTrust.Runnable.Web.RazorDocs/README.md
  source_url_override: https://github.com/forge-trust/Runnable/blob/main/Web/ForgeTrust.Runnable.Web.RazorDocs/README.md
  edit_url_override: https://github.com/forge-trust/Runnable/edit/main/Web/ForgeTrust.Runnable.Web.RazorDocs/README.md
  last_updated_override: 2026-04-22T23:19:00Z
```

Field behavior:

- `hide_contributor_info: true` suppresses the strip entirely for that page.
- `source_path_override` feeds template expansion and git freshness when the rendered page does not map cleanly to `DocNode.Path`. It must stay repository-relative; rooted paths and traversal segments are ignored.
- `source_url_override` and `edit_url_override` bypass template generation entirely. RazorDocs accepts only absolute `http`/`https` URLs or root-relative paths for these overrides.
- `last_updated_override` must stay a real timestamp. RazorDocs renders it through the same relative-time treatment as git-backed freshness.

### Automatic versus explicit provenance

RazorDocs is intentionally conservative about automatic provenance:

- Markdown pages use their harvested source path automatically.
- Harvested C# API symbols can get inline source links when `SymbolSourceUrlTemplate` is configured, but namespace-synthetic pages do not get one automatic page-level C# source link.
- Synthetic or merged pages can still opt into source, edit, or freshness evidence through explicit `contributor:` overrides.

This keeps RazorDocs from inventing fake precision for pages that do not have one trustworthy underlying source file.

### Pitfalls

- Do not configure source or edit templates without `DefaultBranch`. RazorDocs rejects that startup shape because local git state is too brittle to guess from.
- Do not configure source or edit templates without `{path}`. That shape cannot identify one source file per page, so RazorDocs rejects it at startup.
- Do not author free-text freshness copy in the provenance strip. Use `last_updated_override` for an exact timestamp, and use `trust.freshness` for broader lifecycle guidance.
- Do not expect shallow CI clones to populate `Last updated`. RazorDocs degrades safely by omitting freshness when history is unavailable. In GitHub Actions, prefer `actions/checkout` with `fetch-depth: 0` for pages that should surface git-backed freshness.
- Do not add `{line}` to `SourceUrlTemplate`; use `SymbolSourceUrlTemplate` for symbol links so Markdown page provenance keeps working.
- Do not invent additional `SymbolSourceUrlTemplate` tokens. RazorDocs rejects unsupported placeholders such as `{commit}` or `{linen}` instead of rendering silently broken links.
- Do not expect automatic edit links on namespace-synthetic API pages. Symbol links point to source browsing locations, while README-authored namespace intros keep the page-level edit link.

## Namespace README Intros

RazorDocs can merge an authored `README.md` into a generated namespace API page so teams can explain a namespace in prose without replacing the generated symbol list.

### Authoring contract

A README qualifies as a namespace intro only when all of these are true:

- The C# API harvester generated a namespace page at `Namespaces/{Dotted.Namespace}`.
- The authored file is named `README.md`.
- The README is harvested as a root documentation node, not as a child fragment.
- The README directory resolves to the same dotted namespace as the generated page.
- The path has an explicit docs-owned prefix before the namespace directory, currently a `docs/` segment or a `Namespaces/` segment.

Positive examples:

| README path | Merged target |
| --- | --- |
| `docs/ForgeTrust.Runnable.Web/README.md` | `Namespaces/ForgeTrust.Runnable.Web` |
| `Namespaces/ForgeTrust.Runnable.Web/README.md` | `Namespaces/ForgeTrust.Runnable.Web` |

Negative examples:

| README path | Behavior |
| --- | --- |
| `Web/ForgeTrust.Runnable.Web/README.md` | Stays a package README page. |
| `src/ForgeTrust.Runnable.Web/README.md` | Stays a source-adjacent README page if harvested. |
| `README.md` | Stays the repository-root docs landing source. |
| `docs/Unknown.Namespace/README.md` | Stays a normal README page unless a generated `Namespaces/Unknown.Namespace` page exists. |

### Merge behavior

- The generated namespace page keeps its `Namespaces/{Dotted.Namespace}` route.
- README HTML is inserted into the namespace page as the namespace intro.
- The standalone README node is removed after a successful merge so readers do not see duplicate pages.
- README metadata can override the namespace page metadata, but derived Markdown defaults are ignored when they would accidentally replace API-reference classification.
- README-relative links are resolved from the README source path before the standalone README page is removed.
- Links that target the removed README page itself are not rewritten to a published page. Avoid self-links such as `./README.md` inside namespace intros because the standalone README route disappears after merge.
- Contributor provenance points at the README source, while symbol-level source links still point at the generated API declarations.

### Decision guidance

Use a namespace README when the content is specifically about the namespace API surface: concepts, intended usage, lifecycle notes, or cross-type orientation for that namespace.

Use a package README when the content is about package adoption: installation, package-level configuration, examples, compatibility, and links to broader guides. Package READMEs such as `Web/ForgeTrust.Runnable.Web/README.md` and `src/ForgeTrust.Runnable.Web/README.md` do not automatically become namespace intros, even when the folder name matches a namespace. That boundary is intentional so package docs do not disappear into API pages by folder-name coincidence.

Future dual-use package and namespace docs should use an explicit opt-in contract instead of reopening implicit path matching. At the product-contract level, a future design could use a front matter flag that names the target namespace, a paired sidecar mapping a README to `Namespaces/{Dotted.Namespace}`, or a resolver extension point that receives an explicit namespace target. This repository should still prefer sidecar or resolver-based opt-in for authored `README.md` files because repository and package READMEs are expected to stay portable and free of inline front matter. Any future design should require the namespace name to be authored directly, preserve predictable package-doc behavior, and fail closed when the target namespace page does not exist.

### Pitfalls

- Do not move package READMEs under package folders expecting them to merge into namespace pages.
- Do not rely on the final folder name alone. A path needs a docs-owned prefix before the namespace directory.
- Do not expect a README to create a namespace API page. It only merges into a namespace page produced by the C# harvester.
- Do not include README self-links such as `./README.md` in a namespace intro. Link to surviving guide pages, generated namespace anchors, or package docs instead.
- Do not use namespace README merging as a general redirect or alias mechanism. Use explicit metadata and link authoring for those behaviors.

## Usage

Reference the package and add the module to your Runnable web application:

```csharp
await WebApp<RazorDocsWebModule>.RunAsync(args);
```

## Public Sections

RazorDocs now organizes public documentation around a fixed section-first model instead of a flat directory-first landing.

### Built-in sections

- `Start Here`
- `Concepts`
- `How-to Guides`
- `Examples`
- `API Reference`
- `Releases`
- `Troubleshooting`
- `Internals`

These sections back the current docs home, the sidebar shell, and the dedicated section routes under the current docs surface such as `/docs/sections/{slug}` or `/docs/next/sections/{slug}`.

### `nav_group` normalization and fallback rules

- `nav_group` can explicitly select a built-in public section by canonical label, slug, or alias.
- Invalid explicit `nav_group` values log a warning and fall back to RazorDocs-derived section assignment instead of creating ad hoc groups.
- Markdown docs with no explicit `nav_group` are derived into built-in sections using path and filename heuristics:
  - repository-root `README.md` and start-like names such as `quickstart` or `getting-started` fall into `Start Here`
  - `examples/` content falls into `Examples`
  - `releases/` content and root changelogs fall into `Releases`
  - concepts, architecture, explanation, and glossary-style paths fall into `Concepts`
  - troubleshooting, faq, debug, and error-oriented paths fall into `Troubleshooting`
  - internal-oriented paths fall into `Internals`
  - anything else falls into `How-to Guides`
- API reference content continues to use the canonical `API Reference` section.

### Section routes and landing docs

- The current docs surface exposes section routes such as `/docs/sections/start-here` when versioning is off or `/docs/next/sections/start-here` when versioning is on.
- Only canonical slugs are served directly; label- or alias-shaped section requests redirect to the canonical section route.
- When a section has an authored landing doc, RazorDocs redirects the section route to that page.
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
- If multiple docs in one section set `section_landing: true`, RazorDocs keeps the lowest `order` value, then the lowest canonical path, and logs a warning for the others.
- A section landing doc can also author `featured_page_groups`; RazorDocs uses those reader-intent groups for section-level “next steps” on the detail page and collapses the first resolved rows into section preview links surfaced on `/docs`.
- `HideFromPublicNav = true` always wins. Hidden pages do not appear in section routes, the sidebar, the docs home, or the public search index even if they declare a section or landing status.

## Docs Link Authoring

RazorDocs rewrites links inside harvested Markdown so authors can use source-friendly paths while readers stay on canonical docs-surface routes such as `/docs/...html` or `/docs/next/...html` with Turbo history support.

### Authoring contract

- Link to another harvested doc with its source path, such as `./guide.md`, `../CHANGELOG.md`, or `/releases/unreleased.md`.
- Link to an already canonical docs route only when the target is a harvested doc, such as `/docs/releases/unreleased.md.html` or `/docs/next/releases/unreleased.md.html`.
- Use ordinary site URLs, such as `/privacy.html` or `../status.html`, for non-doc pages. RazorDocs leaves those links untouched.
- Use browser-facing URLs for metadata fields that render plain anchors without content rewriting, such as `trust.migration.href`.

### Manifest-backed rewriting

During aggregation, RazorDocs builds a manifest from the harvested documentation nodes. Link rewriting consults that manifest before converting any source or canonical-looking link into the active docs surface.

This means a link is rewritten only when the target exists in the harvested docs set. A missing `./guide.md`, an ambiguous `/docs/missing.md.html`, or a normal site page like `../privacy.html` remains authored as-is instead of being guessed into a broken docs route.

### Pitfalls

- Do not rely on file extensions alone. A `.md`, `.cs`, or `.html` suffix does not make a link a RazorDocs target unless the target was harvested.
- If a doc link is not rewritten, first confirm the target file is included by the active harvester and not excluded by directory policy.
- Canonical docs-surface links are safe for exported docs, but source-relative Markdown links are usually easier to keep portable in GitHub and editor previews.

## Landing Curation

RazorDocs can turn the root docs landing into a curated reader-intent surface by reading `featured_page_groups` from the repository-root `README.md` metadata.

### Authoring contract

`featured_page_groups` is parsed as part of `DocMetadata`, so the metadata contract stays page-agnostic. RazorDocs uses those groups in two places:

- the root `README.md` metadata drives grouped proof-path rows on `/docs`
- any authored section landing doc can drive grouped section-level next-step rows and the section preview links shown on `/docs`

Authors can now supply that metadata in either of two places:

- Inline Markdown front matter at the top of the `.md` file
- A paired sidecar YAML file such as `README.md.yml` or `README.md.yaml`

Inline front matter remains the default authoring path for ordinary docs pages. Paired sidecars are the recommended escape hatch for portability-sensitive files such as `README.md`, where raw front matter renders poorly on GitHub and other plain Markdown surfaces.

```yaml
# README.md.yml
title: Runnable
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

- `intent` is the stable group identity. If omitted, RazorDocs derives one from `label`.
- `label` is the reader-facing group heading. If omitted, RazorDocs title-cases `intent`.
- `summary` explains when a reader should choose the group.
- `order` is optional on groups and pages. Lower values sort first, and ties preserve authored order.
- `pages` must contain the featured destinations for the group. Empty page lists are skipped.
- `question` is the reader-facing label shown on a row. If omitted, RazorDocs falls back to the destination page title.
- `path` accepts either the source path or canonical docs path for the destination page, including an exact `#fragment` suffix when the card should land on a specific section. RazorDocs normalizes forward-slash and backslash separators during resolution while preserving fragment identifiers, and the same resolver used by page details handles configured docs-root prefixes such as `/docs` or a custom live docs root.
- `supporting_copy` is optional landing-only text. If omitted, RazorDocs falls back to the destination page summary.

Author three to five groups for a broad landing page, and one to three pages per group. Prefer plain reader intents such as `understand`, `choose-package`, `see-it-working`, `release-risk`, and `api-reference`. Use custom intents when your product has domain-specific decisions that those defaults do not capture.

Preview locally from the repository root with the standalone docs host:

```bash
dotnet run --project Web/ForgeTrust.Runnable.Web.RazorDocs.Standalone -- --urls http://localhost:5189
```

Then open `http://localhost:5189/docs`. This is only the docs site running locally; the separate `razordocs preview --repo .` CLI idea is intentionally deferred.

### Fallback and visibility rules

- If the root `README.md` is missing, the landing stays on the neutral docs index.
- If `featured_page_groups` is missing, the landing uses the neutral docs index unless the Start Here public section can provide the built-in proof-path fallback.
- If `featured_page_groups: []` is authored inline, the explicit empty list is authoritative and suppresses sidecar fallback.
- If both `README.md.yml` and `README.md.yaml` exist for the same Markdown file, RazorDocs logs a warning and ignores both sidecars until the conflict is removed.
- If both sidecar metadata and inline front matter define the same field, inline front matter wins and the sidecar acts as fallback metadata only.
- Invalid sidecar YAML logs a warning and falls back to the inline/default metadata path instead of breaking the page harvest.
- If a featured path is missing, hidden from public navigation, or duplicated, RazorDocs skips it and logs a warning.
- If all featured entries are skipped, RazorDocs logs one final warning and falls back instead of rendering broken rows.
- The old flat `featured_pages` field is ignored and logs a migration warning. If both fields are present, `featured_page_groups` wins.

Diagnostics include the source file, field path when available, problem, cause, and fix. Common warnings are stale `featured_pages`, missing group identity, missing or null `pages`, flat-looking group entries, blank `path`, missing destination, hidden destination, duplicate destination, invalid YAML, sidecar extension conflicts, and all groups skipped after resolution.

### Pitfalls

- Do not create both `.yml` and `.yaml` sidecars for the same Markdown file. RazorDocs treats that as an authoring error and ignores both.
- Do not use a sidecar as a second secret metadata system. It supports the same `DocMetadata` schema as inline front matter, and it is best reserved for files whose Markdown needs to stay portable on other surfaces.
- Do not put `path` or `question` directly under a group. Page fields belong under `pages`.
- Prefer source-relative paths for authored curation when the docs may be exported or mounted under more than one route. Canonical `/docs/...html` paths are accepted for parity with browser links, but source paths stay easier to review and move with the file.
- README portability matters most at the repository and package level. In this repo, authored `README.md` files should stay free of inline front matter so GitHub renders them cleanly.

## Metadata-Driven Wayfinding

RazorDocs can render two kinds of page-local wayfinding on details pages without scraping rendered HTML after the fact:

- `On this page` links come from the harvested `DocNode.Outline` contract.
- `Previous` and `Next` proof-path links come from explicit metadata, not folder inference.

### Sequence contract

Use `sequence_key` together with `order` when a set of pages should behave like one proof path:

```yaml
sequence_key: razorwire-proof
order: 20
related_pages:
  - Web/ForgeTrust.Runnable.Web.RazorWire/README.md
  - Web/ForgeTrust.Runnable.Web.RazorWire/Docs/antiforgery.md
```

- `sequence_key` opts a page into a specific sequence. Pages do not join a sequence just because they share a folder.
- `order` determines the relative previous/next position inside that sequence.
- `related_pages` stays independent from sequencing and can point to source paths, canonical docs paths, or exact page titles.
- RazorDocs publishes authored sequence metadata to the current-surface search index for custom clients and integrations. The live source surface emits that payload at `{DocsRootPath}/search-index.json` (for example, `/docs/search-index.json` by default when versioning is off and `/docs/next/search-index.json` by default when versioning is on); exported exact-version trees carry their own `search-index.json` payload at the tree root. The `sequence_key` front-matter value becomes `sequenceKey`, `order` stays `order`, and `related_pages` stays separate as `relatedPages`; for example: `{ "sequenceKey": "razorwire-proof", "order": 20, "relatedPages": ["Web/ForgeTrust.Runnable.Web.RazorWire/README.md"] }`.

### Resolution rules

- Previous/next links render only when the current page has both `sequence_key` and `order`.
- RazorDocs only sequences navigable pages. Fragment-only anchor stubs and pages hidden from public navigation do not appear in proof-path navigation.
- Related pages are deduplicated against the current page and any resolved previous/next neighbors.

### Pitfalls

- Do not rely on filename prefixes or folder adjacency for proof-path behavior in this slice. Use explicit `sequence_key` values instead.
- Do not expect `related_pages` to imply ordering. Related links stay unordered beyond the authored list order.

## Metadata-Driven Page Type Display

RazorDocs treats `page_type` metadata as structured UI input, not just as opaque search metadata. The built-in landing cards, detail pages, and search results all normalize the same metadata through `DocMetadataPresentation.ResolvePageTypeBadge()`.

### Built-in normalization

- Known values such as `guide`, `example`, `api-reference`, `internals`, `how-to`, `start-here`, `troubleshooting`, `glossary`, and `faq` render with stable labels and intentional badge variants.
- Unknown values still render safely: RazorDocs normalizes whitespace, underscores, and dashes, then falls back to a neutral title-cased badge.
- Missing or blank `page_type` values render no badge at all instead of leaving empty chrome behind.

### Search payload contract

The current-surface `search-index.json` payload continues to emit the raw `pageType` metadata value and now also includes:

- `pageTypeLabel` for the normalized display label used by the built-in search UI
- `pageTypeVariant` for the built-in badge variant suffix used by CSS classes such as `docs-page-badge--guide`
- `publicSection` for the normalized built-in section slug when the page is publicly visible
- `publicSectionLabel` for the reader-facing section label
- `isSectionLanding` for authored section landing entry points

These fields let custom search clients stay visually aligned with the landing and detail experiences without re-implementing the mapping table.

## Custom Harvester Outline Contract

The built-in Markdown and C# harvesters now populate `DocNode.Outline` directly during harvest. Custom `IDocHarvester` implementations should do the same when they want:

- `On this page` links on details views
- heading metadata in the current-surface `search-index.json`
- stable behavior without re-parsing rendered HTML later

Each outline entry should provide the rendered fragment `Id`, the reader-facing `Title`, and the normalized heading `Level`. For visual parity with the built-in wayfinding UI, custom `IDocHarvester` implementations should populate `DocNode.Outline` only with entries that have a non-empty rendered fragment `Id` and non-empty `Title`; headings or generated sections missing either value are skipped by the built-ins. The Markdown harvester emits source-ordered H2-H3 headings by default, with titles normalized from inline heading text and IDs taken from the rendered heading fragment. The C# harvester emits level 2 entries for documented types and enums, and level 3 entries for method groups and properties. Matching those defaults keeps custom outlines aligned with the built-in `On this page` section and search heading metadata.

Public visibility note:

- `HideFromSearch = true` removes a page from the search payload directly.
- `HideFromPublicNav = true` also removes the page from the search payload because the public shell treats hidden pages as fully non-public.

## Trust Metadata For Release Notes And Policy Pages

RazorDocs can also render a top-of-page trust bar from nested `trust` metadata. Runnable uses this for its own release notes, upgrade policy, and changelog pages so the product doubles as a working example for consumers.

```yaml
trust:
  status: Unreleased
  summary: This page is provisional until the next tag is cut.
  freshness: Updated as changes land on main.
  change_scope: Repository-wide.
  migration:
    label: Read the upgrade policy
    href: /docs/releases/upgrade-policy.md.html
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
- `migration` is an optional label plus browser-facing `href` to the adoption guidance.
- `archive` explains where the durable tagged record or long-term home lives.
- `sources` is an optional list of provenance notes or upstream artifacts.

### Merge behavior

- Inline front matter and sidecar YAML both use the same nested `trust` schema.
- Inline metadata wins over sidecar metadata field by field.
- Explicit empty lists such as `sources: []` are authoritative and suppress fallback lists.

### Pitfalls

- Use a browser-facing `href` for `migration`, not a source path, because the trust bar renders a plain link without path rewriting.
- Keep private maintainer-only runbooks outside harvested docs. Hidden pages are removed from nav and search, but they are still public if linked directly.
- Do not turn the trust bar into marketing chrome. It should answer status, safety, and provenance questions quickly.

## Related Projects

- [ForgeTrust.Runnable.Web.RazorDocs.Standalone](../ForgeTrust.Runnable.Web.RazorDocs.Standalone/README.md) for the runnable/exportable host used in docs export and smoke testing
- [Back to Web List](../README.md)
- [Back to Root](../../README.md)

## Notes

- This package is the reusable documentation surface; `ForgeTrust.Runnable.Web.RazorDocs.Standalone` is the thin executable wrapper used for local hosting and export scenarios.
- The bundled RazorDocs UI already includes its generated stylesheet as a static web asset. The layout resolves the correct stylesheet path automatically from the host's root module shape for standalone/root-module hosts versus embedded application-part consumers.
- Consumers do not need to call `services.AddTailwind()` unless they also want Tailwind build/watch integration for their own host application's CSS.
- It depends on the Tailwind package family for RazorDocs package build-time styling generation and on the caching package for docs aggregation performance.
