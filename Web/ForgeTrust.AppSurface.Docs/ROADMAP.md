# RazorDocs Roadmap

This note keeps the in-repo RazorDocs plan aligned with the phased GitHub roadmap.

## Product Direction

`RazorDocs` is being shaped as a `.NET`-native documentation framework for libraries and frameworks that unifies authored guides, examples, and source-derived API reference in one experience for human and agentic readers.

`AppSurface` is the first flagship consumer of this work. The initial phases should make AppSurface's documentation more trustworthy for evaluators while also proving the reusable RazorDocs product surface.

## Phase 1: Metadata Foundation

Status: shipped

Phase 1 focused on the shared metadata and indexing layer:

- front matter and structured doc metadata
- metadata defaults for markdown and harvested API docs
- namespace README merging
- metadata-backed search payloads
- metadata-aware navigation and breadcrumb behavior

This phase creates the foundation for later landing-page, search, and wayfinding work without hardcoding repo-specific behavior into views.

## Phase 2: Evaluator Experience

GitHub milestone: [RazorDocs Phase 2: Evaluator Experience](https://github.com/forge-trust/AppSurface/milestone/1)

Phase 2 is the first visible product slice built on top of the metadata foundation. The goal is to help an evaluating engineer answer trust questions quickly and move from high-level orientation into concrete examples and API details while also closing the baseline platform gaps that keep RazorDocs from feeling competitive as a serious docs product.

This phase should now ship in four moves:

1. turn the root docs landing into a metadata-driven trust-routing surface
2. extend the same curation and landing pattern to section or pillar entry points such as `Start Here`, `Examples`, and `Troubleshooting`
3. add the core docs-platform capabilities evaluators and maintainers expect, including versioning, contributor workflow affordances, canonical routes, and locale support
4. keep the resulting experience legible in navigation and search instead of bolting those capabilities on as isolated admin features

Primary issues:

- [#101](https://github.com/forge-trust/AppSurface/issues/101) Rebuild public docs navigation around user intent
- [#102](https://github.com/forge-trust/AppSurface/issues/102) Add canonical doc slugs with legacy-safe redirects
- [#106](https://github.com/forge-trust/AppSurface/issues/106) Redesign docs search UI with filters and richer results
- [#107](https://github.com/forge-trust/AppSurface/issues/107) Create Start Here, troubleshooting, and glossary content foundations
- [#109](https://github.com/forge-trust/AppSurface/issues/109) Add deep-page wayfinding with TOC, related links, and next steps
- [#123](https://github.com/forge-trust/AppSurface/issues/123) Add evaluator-first docs landing with featured proof paths
- [#124](https://github.com/forge-trust/AppSurface/issues/124) Render metadata-driven page type badges across landing, details, and search
- [#125](https://github.com/forge-trust/AppSurface/issues/125) Add featured page curation for evaluator questions
- [#126](https://github.com/forge-trust/AppSurface/issues/126) Add a concise RazorDocs product explainer to the flagship docs experience
- [#142](https://github.com/forge-trust/AppSurface/issues/142) Add docs versioning with version-aware navigation and search
- [#143](https://github.com/forge-trust/AppSurface/issues/143) Add contributor workflow features like edit links and last-updated metadata
- [#144](https://github.com/forge-trust/AppSurface/issues/144) Add i18n with locale-aware routing and search

Recommended shipping emphasis:

1. featured page curation
2. evaluator-first landing
3. versioning plus version-aware navigation and search
4. deep-page wayfinding
5. contributor workflow affordances plus canonical routes
6. locale-aware docs routing and search
7. section and pillar landing follow-through using the same metadata contract
8. concise RazorDocs product explainer

## Phase 3: Platform Breadth, Relevance, and Telemetry

GitHub milestone: [RazorDocs Phase 3: Platform Breadth, Relevance, and Telemetry](https://github.com/forge-trust/AppSurface/milestone/2)

Phase 3 deepens the platform breadth after the evaluator-facing experience is visible. The goal is to improve search intelligence and telemetry while also expanding RazorDocs into a more complete documentation framework with adjacent content types, richer authoring, and explicit extension and deployment seams.

Primary issues:

- [#104](https://github.com/forge-trust/AppSurface/issues/104) Expand search index with metadata and visibility controls
- [#105](https://github.com/forge-trust/AppSurface/issues/105) Tune search ranking for intent-based relevance
- [#108](https://github.com/forge-trust/AppSurface/issues/108) Add config-gated search telemetry and docs metrics
- [#145](https://github.com/forge-trust/AppSurface/issues/145) Add docs-adjacent content types for release notes, blog, and standalone pages
- [#146](https://github.com/forge-trust/AppSurface/issues/146) Add extension surfaces and production docs platform plumbing
- [#147](https://github.com/forge-trust/AppSurface/issues/147) Add richer authoring primitives for interactive technical docs

## Notes

- These phases are intended to be additive. Phase 2 should consume the Phase 1 foundation rather than reworking it.
- Phase 1 made the metadata pipeline page-agnostic on purpose. Phase 2 should first consume that seam from the repo-root `README.md`, then reuse it for non-root landing pages without introducing a parallel content system.
- `AppSurface` remains the first proof site, but the roadmap should favor reusable RazorDocs capabilities over one-off customizations.
- Future RazorDocs styling work should follow the package-level styling boundary contract documented in `README.md` and explained in `DESIGN.md`, rather than reopening Tailwind-versus-semantic-CSS debates from scratch.
- Future phases can expand into more explicit agent-facing features once the evaluator experience and search relevance are working well.
