# Page Navigation

RazorWire page navigation enhances same-page section links without turning your page into a client-side app. Use it when a server-rendered page already has normal anchors and sections, but needs active section state, reduced-motion-aware navigation, optional compact panel close behavior, and Turbo/frame lifecycle cleanup.

The no-JavaScript fallback is ordinary hash navigation. RazorWire owns behavior and state attributes. Your app owns layout, breakpoints, colors, spacing, and icons.

## Page Navigation in 3 Minutes

1. Confirm your layout renders `<rw:scripts/>`.
1. Add one page-nav root with same-page hash links.
1. Add matching section `id` values and app CSS for active state or sticky-header offset.

```html
<nav data-rw-page-nav aria-label="Page sections">
  <a href="#overview" data-rw-page-nav-link>Overview</a>
  <a href="#pricing" data-rw-page-nav-link>Pricing</a>
</nav>

<section id="overview">
  <h2>Overview</h2>
</section>

<section id="pricing">
  <h2>Pricing</h2>
</section>
```

That is enough for active link state. The active link receives `aria-current="location"` and `data-rw-page-nav-active="true"`.

For sticky headers, prefer CSS before JavaScript offsets:

```css
section[id] {
  scroll-margin-top: 5rem;
}

[data-rw-page-nav] {
  scroll-padding-block: 0.75rem;
}

[data-rw-page-nav-link][data-rw-page-nav-active="true"] {
  font-weight: 700;
}
```

## Razor TagHelpers

RazorWire also provides native-element attribute TagHelpers. They normalize Razor markup into the same public `data-rw-*` contract as raw HTML.

```cshtml
<nav rw-page-nav aria-label="Page sections">
    <a href="#overview" rw-page-nav-link>Overview</a>
    <a href="#pricing" rw-page-nav-link>Pricing</a>
</nav>
```

Rendered HTML:

```html
<nav aria-label="Page sections" data-rw-page-nav="true">
  <a href="#overview" data-rw-page-nav-link="true">Overview</a>
  <a href="#pricing" data-rw-page-nav-link="true">Pricing</a>
</nav>
```

Optional compact panel wiring:

```cshtml
<nav rw-page-nav aria-label="Page sections">
    <button rw-page-nav-toggle="page-sections-panel"
            type="button"
            aria-expanded="true">
        Sections
    </button>

    <div rw-page-nav-panel id="page-sections-panel">
        <a href="#overview" rw-page-nav-link>Overview</a>
        <a href="#pricing" rw-page-nav-link>Pricing</a>
    </div>
</nav>
```

RazorWire keeps `aria-expanded` and neutral panel state attributes in sync. Your CSS decides when a closed panel is hidden:

```css
[data-rw-page-nav-enhanced="true"]
[data-rw-page-nav-panel-state="closed"] {
  display: none;
}
```

Do not hide the panel by default without an enhancement guard. Without JavaScript, the links must stay visible or otherwise reachable.

## Attribute Reference

| Attribute | Target | Required | Behavior |
| --- | --- | --- | --- |
| `data-rw-page-nav="true"` | `nav`, `aside`, or container | Yes | Marks one independent page-navigation root. |
| `data-rw-page-nav-link="true"` | `a[href^="#"]` | Yes | Marks links managed inside the nearest page-nav root. |
| `data-rw-page-nav-toggle="true"` | `button` | No | Marks an optional compact-panel toggle. |
| `data-rw-page-nav-panel="true"` | Panel container | No | Marks the optional panel controlled by the toggle. |
| `aria-controls` | Toggle | Recommended with panel | Points to the controlled panel `id`. |
| `aria-expanded` | Toggle | Optional | Initial open/closed state. Defaults to open when omitted. |

TagHelper aliases:

| Razor attribute | Rendered attributes |
| --- | --- |
| `rw-page-nav` | `data-rw-page-nav="true"` |
| `rw-page-nav-link` | `data-rw-page-nav-link="true"` |
| `rw-page-nav-toggle` | `data-rw-page-nav-toggle="true"` and optional `aria-controls` when the value is not empty |
| `rw-page-nav-panel` | `data-rw-page-nav-panel="true"` |

## State Contract

| State | User-visible behavior | Attributes |
| --- | --- | --- |
| Root enhanced | Runtime found and bound the root. | `data-rw-page-nav-enhanced="true"` |
| Active link | Link points to the current section. | `aria-current="location"`, `data-rw-page-nav-active="true"` |
| Inactive link | Link is not current. | `aria-current` and `data-rw-page-nav-active` removed |
| Panel open | Optional panel should be presented by app CSS. | `aria-expanded="true"` on toggle, `data-rw-page-nav-panel-state="open"` |
| Panel closed | Optional panel may be hidden by app CSS after enhancement. | `aria-expanded="false"` on toggle, `data-rw-page-nav-panel-state="closed"` |

## Active Link Visibility

When the active link sits inside a visible vertical page-navigation surface that overflows, RazorWire keeps that link perceivable inside the nav surface. This orientation behavior is a progressive enhancement on top of ordinary hash links: without JavaScript, links still navigate to their sections, but RazorWire cannot synchronize active visibility.

RazorWire resolves the reveal surface from the active link upward to the nearest page-nav root. It uses the first ancestor that is rendered, vertically scrollable, and overflowing. Hidden, collapsed, zero-size, horizontal-only, clipped non-scrollable, and non-overflowing ancestors are skipped silently. RazorWire never calls `scrollIntoView()` for this nav reveal and never scrolls the document viewport as part of active-link orientation.

Customize reveal insets with CSS on the scrollable nav surface:

```css
[data-rw-page-nav] {
  max-block-size: min(28rem, calc(100vh - 8rem));
  overflow-y: auto;
  scroll-padding-block: 0.75rem;
}
```

Use `scroll-padding-top` / `scroll-padding-bottom` or logical `scroll-padding-block` when sticky nav headers, fades, or compact chrome would otherwise crowd the active link. These insets affect only active-link reveal inside the nav surface. Target-section offsets still belong on the sections with `scroll-margin-top`.

The manager also exposes `window.RazorWire.pageNavigationManager.syncActiveLinkVisibility()` for advanced integrations that perform custom DOM replacement or layout work outside Turbo. It re-checks the current active link without changing active state and scrolls only the eligible nav container when needed.

## Accessibility and Navigation Rules

| Surface | Requirement |
| --- | --- |
| Root | Use semantic `nav` or provide an accessible label such as `aria-label="Page sections"`. |
| Links | Use plain same-page anchors. Modified clicks, downloads, non-`_self` targets, and external links are not hijacked. |
| Active state | One valid link per root receives `aria-current="location"`. Server-rendered stale current state is cleaned up. |
| Active visibility | Overflowing vertical nav surfaces may use `scroll-padding-block` to keep the active link clear of sticky or compact chrome. |
| Toggle | Use a real `button`. RazorWire updates `aria-expanded`; your label stays app-owned. |
| Panel | Keep no-JS content reachable. Apply closed styling only after `data-rw-page-nav-enhanced="true"`. |
| Motion | RazorWire respects `prefers-reduced-motion: reduce`. Do not rely on animation to explain state. |
| Focus | RazorWire does not move focus after a section click. Native link focus remains predictable. |

## Diagnostics

Runtime markup mistakes do not throw in production. In development diagnostics, RazorWire records concise entries on `window.RazorWire.pageNavigationManager.getDiagnostics()`.

Example:

```text
RazorWire page navigation: data-rw-page-nav-toggle controls "page-sections-panel", but no element with that id exists.
Impact: panel close behavior is disabled for this nav root.
Fix: add id="page-sections-panel" to the panel or remove the toggle value.
Docs: Web/ForgeTrust.RazorWire/Docs/page-navigation.md#troubleshooting
```

## Bootstrap Migration

| Bootstrap-style behavior | RazorWire replacement |
| --- | --- |
| `.page-scroll` click handler | Normal `href="#section"` plus `data-rw-page-nav-link` or `rw-page-nav-link`. |
| `data-bs-spy="scroll"` | `data-rw-page-nav` root with active state reflected on managed links. |
| Active classes | Style `[data-rw-page-nav-active="true"]` or `[aria-current="location"]`. |
| `.navbar-collapse` close after link click | Optional page-nav toggle/panel close. This is not full Bootstrap collapse parity. |
| Sticky header offset | Prefer CSS `scroll-margin-top` on target sections. |
| Active item reveal in a tall nav | Let the nav surface overflow vertically and customize insets with `scroll-padding-block`. |

RazorWire does not emulate Bootstrap classes, breakpoints, off-canvas menus, tabs, accordions, or routers. If the navigation becomes a full app menu or disclosure system, use app JavaScript or a dedicated UI library instead.

Active-link reveal is intentionally narrow. It is not Bootstrap parity, not horizontal/carousel reveal, not animated reveal, not focus relocation, not a new menu or disclosure system, and not an app-specific visual context row. Apps can still listen for `razorwire:page-nav:active-change` when they need product-specific chrome around the active link.

## Static Export

The markup remains static HTML. If a static export includes a page-nav root and uses RazorWire enhancement, the exported tree must include the standard `<rw:scripts/>` output and the package `page-navigation.js` asset. The standard scripts output lazy-loads that asset when page-navigation roots are present, so archive validation should confirm the asset remains available when page navigation appears in exported content.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `window.RazorWire.pageNavigationManager` is undefined | No page-navigation root has been rendered yet, `<rw:scripts/>` is missing, or a custom script pipeline omitted the package asset. | Render `<rw:scripts/>` once in the layout and keep `page-navigation.js` available, or include the package asset after the core runtime in a custom pipeline. |
| No active link changes | Links are missing `data-rw-page-nav-link` or targets lack matching `id` values. | Add link markers and stable section IDs. |
| Active link is clipped in a tall nav | The nav surface is not a visible vertical overflowing container, or the active link is inside a hidden/collapsed panel. | Put `overflow-y: auto` and a constrained block size on the visible nav surface, then tune `scroll-padding-block`. |
| Panel does not close | Toggle is missing `aria-controls`, controls a missing id, or no panel is marked. | Add `rw-page-nav-toggle="panel-id"` and `id="panel-id"` on the panel. |
| Links disappear without JavaScript | App CSS hides the panel before enhancement. | Scope closed-panel CSS under `[data-rw-page-nav-enhanced="true"]`. |
| Sticky header covers targets | Browser scrolls target to the top of the viewport. | Add `scroll-margin-top` to target sections. |
| External links are not enhanced | RazorWire only manages same-page hash links. | This is expected. Keep external navigation ordinary. |
