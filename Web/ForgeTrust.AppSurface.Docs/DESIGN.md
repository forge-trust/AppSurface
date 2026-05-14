# RazorDocs Design Language

## Purpose

RazorDocs should feel like a focused documentation workspace, not a marketing site and not a generic SaaS dashboard. The UI should help people orient quickly, scan densely, and move deeper into docs with very little friction.

## Core Tone

- Quietly technical
- Editorial rather than card-heavy
- Dense, but never cramped
- Confident without looking flashy

If a new surface starts to feel like a feature grid, a landing page, or an AI-generated admin template, pull it back.

## Visual System

### Typography

- Primary typeface: `Outfit`
- Body copy should stay clean and readable with generous line-height
- Authored Markdown prose should use a shorter measure and stronger paragraph/list rhythm than generated API reference pages. Release notes and long guides must scan as editorial documents, not as one uninterrupted text column.
- Headings should feel compact and intentional, not oversized hero copy
- Search results should privilege readable title hierarchy over decorative framing

### Color

- Base surfaces: dark slate family
- Accent: cyan for focus, active state, and high-value calls to action
- Borders and separators should do most of the structure work
- Avoid adding extra accent colors unless the feature truly needs semantic differentiation

### Surfaces

- Prefer layered panels, separators, and subtle fills over heavy boxed cards
- Result lists should read like an editorial index with strong rows, not isolated product cards
- Keep shadows minimal; contrast and spacing should carry the layout

## Styling Boundary

RazorDocs uses multiple styling patterns because it is solving different ownership and stability problems.

Owned package chrome is local product UI. Harvested content is nested document output. Stateful search UI also needs selectors that both CSS and JavaScript can trust. Treating those surfaces as one blanket styling problem is how teams end up arguing about style purity instead of making the interface easier to maintain.

### Default Rule

Use this order when deciding where a new style belongs:

1. Reusable component contract or shared CSS and JavaScript hook: semantic class.
2. Unowned nested content rendered inside a package wrapper: wrapper-scoped semantic CSS.
3. One-off package chrome that RazorDocs owns directly: Tailwind utilities in markup.

`README.md` is the fast rulebook for this decision. This document explains why the rule exists and where contributors usually get tripped up.

### Why Ownership Beats Style Purity

- One-off owned chrome is easiest to read when the intent stays in the Razor markup that owns it.
- Harvested content is safest to style through a wrapper such as `.docs-content` because RazorDocs does not control each nested node or authoring shape.
- Markdown and generated API reference are both harvested content, but they are different reading jobs. Use `.docs-content--markdown` for prose rhythm and keep `.docs-content--api` on the wider base reference treatment.
- Reusable package components deserve semantic names even when RazorDocs owns the markup, because repeated UI contracts are easier to review and update when they have one stable selector.
- Search surfaces need semantic hooks because the stylesheet and `search-client.js` both rely on the same stable names across loading, empty, failure, and active-filter states.

### Edge Cases

#### Reusable owned package UI

Classes such as `docs-page-badge` and `docs-metadata-chip` are not a failure of utility-first styling. They are the right tool when a repeated package component needs one stable contract across multiple views and stylesheets.

Shared reusable primitives belong in the Tailwind entry stylesheet at `wwwroot/css/app.css`, which generates the package stylesheet loaded on every RazorDocs page. Search-specific state and result styling belongs in `wwwroot/docs/search.css`; do not make non-search pages depend on search assets for badges, metadata chips, or trust/provenance chrome.

#### Search workspace hooks

The search workspace renders semantic classes such as `docs-search-page`, `docs-search-page-filters-toggle`, and `docs-search-page-active-filters` directly in Razor, then extends those hooks in CSS and JavaScript. That is intentional. Shared hooks keep stateful UI readable and stable.

That does not mean every heading, paragraph, or fallback-link wrapper inside `Search.cshtml` needs its own semantic class. Keep the stateful container and interactive hook semantic, then use local utilities for one-off typography and spacing inside that single view.

#### Required `id` values

Some search controls still need unique `id` values such as `docs-search-page-input` and `docs-search-page-filters-panel`. Those support uniqueness, accessibility relationships, and DOM targeting. They do not replace semantic classes as the reusable styling contract.

### Anti-Patterns

Avoid these by default:

- adding semantic classes to static package chrome when local utilities are clearer
- forcing repeated package UI back into long utility strings when a shared component class is already the simpler contract
- pushing utility classes into harvested nested HTML that RazorDocs does not fully own
- treating shared CSS and JavaScript hook classes as a failure of Tailwind instead of a legitimate integration seam
- moving styles across the boundary without a concrete user-facing benefit

### Review Questions

When reviewing a change, ask:

- Does this surface need a reusable selector that more than one file depends on?
- Does RazorDocs fully own this markup, or is it styling nested harvested content?
- Will a future contributor understand where the style belongs without reading half the package?
- Is this change improving usability or maintainability, or just chasing stylistic consistency?

## Search Workspace

`/docs/search` is a search-first workspace.

- Keep the primary search input visually dominant
- Place filters directly under or adjacent to the query area
- Keep one main results stream, ranked by relevance
- Use badges and breadcrumbs to add context without fragmenting the scan path

### Starter State

The empty state should guide without feeling promotional.

- Include a one-sentence orientation
- Show clickable suggestion chips
- Explain that filters can also be used for browse mode before typing

### Results

Results should be a high-information list.

- Title is the strongest element
- Breadcrumbs come first and stay subtle
- Badges are compact metadata, not visual decoration
- Snippets should stay short and readable
- Highlight matches with restrained `<mark>` styling

### Filters

- Desktop: filters are visible within the page workspace
- Mobile: collapse filters behind a compact toggle with active-filter summary pills
- Disabled zero-result options should remain visible so the dataset shape stays legible

## State Design

Search has distinct states and they should look distinct.

- First load: show skeleton rows and clear “Loading search index...” messaging
- Refinement updates: keep prior results visible and use a subtle busy state
- No results: explain that the search worked, then offer recovery paths
- Failure: explain that search itself is unavailable, show retry, and provide fallback links

Do not reuse the no-results treatment for actual failures.

## Page-Local Navigation

`On this page` is a local map for the current document, not a second global navigation surface. The left sidebar owns the docs product hierarchy. The page outline owns the reader's position inside the current article.

Desktop details pages with an outline should use an article-first composition:

1. page title and current article content
2. quiet page-local outline rail
3. active section marker visible enough to scan without competing with reading

The persistent rail appears only on wide desktop (`>=1280px`) so the article column stays readable. Below that breakpoint, use one collapsed `On this page` control above the article. Do not render separate desktop and mobile outlines.

Visual rules:

- Keep the rail editorial and quiet: border/separator structure beats boxed cards.
- Use cyan for active and focus states only.
- Borrow the active-row treatment from the approved mockup direction: subtle row fill plus a cyan marker.
- Keep H2 links stronger and H3 links quieter/indented.
- Use small row radii only. Do not wrap the whole rail in a large rounded card.
- Preserve the existing RazorDocs global sidebar; do not replace it with icon-only chrome for this pattern.

Interaction rules:

- The active outline link uses `aria-current="location"`.
- Mobile outline links collapse the outline after navigation so the reader returns to content.
- JavaScript enhances server-rendered hash links. It must not create a hidden-only outline when scripts fail.

## Interaction Rules

- `/` focuses the visible search input when the user is not already typing
- `Cmd/Ctrl+K` opens the search workspace or focuses the advanced input
- Browser history should feel natural:
  - typing updates URL state with replace semantics
  - deliberate filter changes create navigable history entries
- Back/Forward must restore query, filters, and rendered results

## Accessibility

- Preserve semantic headings, labels, and live regions for status changes
- Loading, failure, and no-results states must remain understandable without visual context
- Focus should move predictably for shortcuts, retry, and suggestion chips
- Mobile layouts should keep search and results visible without forcing long filter scrolls

## Anti-Patterns

Avoid these by default:

- chunky boxed result cards
- oversized marketing hero treatments
- decorative gradients inside core reading surfaces
- multiple competing result columns
- hiding the corpus shape behind over-minimal filter UI
- introducing visual styles that do not already fit the dark-slate plus cyan system
