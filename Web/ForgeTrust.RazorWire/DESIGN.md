# RazorWire Generated UI Design Contract

## Purpose

RazorWire helps server-rendered ASP.NET Core apps update page fragments without asking every app to invent a separate frontend runtime. That means RazorWire occasionally owns small generated UI nodes: enhancement markers, form feedback, stream connection affordances, and other package-created fragments that support RazorWire behavior.

This contract defines how those RazorWire-owned generated nodes should look, behave, and be overridden by host applications. It is deliberately narrower than a product design system.

## Scope

This contract applies only to UI that the `ForgeTrust.RazorWire` package creates or explicitly owns.

Examples in scope:

- package-generated form feedback
- package-owned stream or island status affordances
- package-owned fallback UI around enhanced RazorWire behavior
- documented attributes and custom properties on RazorWire-generated nodes

Examples out of scope:

- arbitrary app markup inside a RazorWire form
- view components, partials, or layouts authored by the host app
- sample-app visual styling
- RazorDocs documentation chrome
- consumer-owned Tailwind, Bootstrap, or design-system classes

If a node is authored by the application, RazorWire should not impose a visual opinion on it. RazorWire may add behavior attributes or script hooks where the feature requires them, but the host application owns the element's design.

## RazorWire Is Not RazorDocs

RazorDocs owns a full documentation workspace with its own editorial chrome, navigation, search, and package-specific design language. RazorWire does not.

RazorWire-generated UI should inherit from the application around it. It should feel like a quiet enhancement inside the host app, not like a branded AppSurface component transplanted onto the page.

Use the RazorDocs design language only when working on RazorDocs. For RazorWire, this file is the package-level source of truth.

## Visual Posture

RazorWire-owned UI should be:

- calm: use restrained color and no dramatic motion
- compact: fit naturally near the element or form that needs feedback
- app-inheriting: rely on `font`, `color`, and inherited spacing where practical
- accessible: visible focus, readable contrast, predictable keyboard flow
- low chrome: no heavy cards, promotional styling, or ornamental decoration
- resilient: wrap cleanly on narrow screens and inside constrained forms

Generated UI should explain state, not compete with the app's primary interface.

## Styling Surface

RazorWire-generated UI should expose two styling layers:

1. Stable `data-rw-*` attributes for selectors and behavior.
2. CSS custom properties for host-controlled visual defaults.

Prefer data attributes over package-specific class names for generated nodes. Data attributes make ownership explicit, avoid collisions with host CSS naming, and give consumers predictable selectors without implying that RazorWire is shipping a general-purpose component library.

Generated UI should use a small custom-property set with package defaults. A feature may define additional properties when it needs them, but it should document the full surface next to the feature.

Recommended shared properties:

```css
:root {
  --rw-ui-font: inherit;
  --rw-ui-surface: transparent;
  --rw-ui-text: currentColor;
  --rw-ui-muted-text: color-mix(in srgb, currentColor 72%, transparent);
  --rw-ui-border: color-mix(in srgb, currentColor 24%, transparent);
  --rw-ui-accent: #2563eb;
  --rw-ui-danger: #b42318;
  --rw-ui-radius: 0.375rem;
  --rw-ui-gap: 0.5rem;
}
```

Package CSS should keep defaults modest and overrideable. Do not require a build step, sample-app Tailwind setup, or package-wide visual theme for RazorWire-generated UI to work.

## Override Model

Consumers should be able to override generated UI at three levels:

- Global: set `--rw-ui-*` properties on `:root`, `body`, or an application shell.
- Form-level: set custom properties or supported `data-rw-*` attributes on the form or nearest generated RazorWire container.
- Target-level: override a specific generated node by selecting its documented `data-rw-ui` and target metadata.

Use the narrowest override that matches the decision. Global overrides are good for product-wide color and radius alignment. Form-level overrides are better when one workflow needs a different density or state color. Target-level overrides are for one-off corrections around a specific generated node.

Feature docs must name the supported attributes and custom properties before consumers are expected to depend on them.

## Example Generated Component

A future generated form-failure summary should follow this shape: scoped data attributes, semantic roles, readable text, and no dependence on host CSS classes.

```html
<div
  data-rw-ui="form-failure"
  data-rw-severity="error"
  data-rw-target="payment-step"
  role="status"
  aria-live="polite"
>
  <p data-rw-ui="form-failure-title">We could not submit this form.</p>
  <ul data-rw-ui="form-failure-list">
    <li data-rw-ui="form-failure-item">Refresh the page and try again.</li>
  </ul>
</div>
```

The package-owned default CSS should stay close to this level of opinion:

```css
[data-rw-ui="form-failure"] {
  display: grid;
  gap: var(--rw-ui-gap, 0.5rem);
  color: var(--rw-ui-danger, #b42318);
  font: var(--rw-ui-font, inherit);
}
```

## Theming Override Example

The host app can align generated feedback with its own design system without replacing the RazorWire behavior.

```css
:root {
  --rw-ui-danger: #9f1239;
  --rw-ui-radius: 0.25rem;
}

.checkout-form {
  --rw-ui-gap: 0.375rem;
}

.checkout-form [data-rw-ui="form-failure"][data-rw-target="payment-step"] {
  padding-block: 0.5rem;
  border-block-start: 1px solid var(--rw-ui-danger);
}
```

In this example, `.checkout-form` is a host-owned selector on the form or nearest generated RazorWire container. It could be a class, ID, or app-specific data attribute. The override changes the presentation of a generated node. It does not require replacing RazorWire scripts, copying sample-app classes, or changing unrelated app markup.

## Accessibility Baseline

Every RazorWire-generated UI feature should document and verify:

- semantic role and accessible name when the node communicates state
- `aria-live` behavior for asynchronous feedback
- keyboard reachability for interactive generated controls
- visible focus treatment for generated focusable elements
- contrast against inherited and default surfaces
- mobile wrapping without clipped text or horizontal scrolling
- behavior when JavaScript enhancement fails or is unavailable

Generated feedback should stay close to the interaction that caused it. When focus should move, the feature must document the focus rule and provide tests for it.

## Anti-Patterns

Avoid these by default:

- global toast systems for local form failures
- modal takeovers for recoverable inline feedback
- sample-app Tailwind dependencies in package-generated UI
- a package-wide RazorWire visual theme
- hard-coded colors that cannot be overridden with custom properties
- generated UI that restyles consumer-authored form fields or layout
- hidden state changes that screen readers cannot observe
- animation that is required to understand the UI state

If a feature seems to need one of these, write down the product reason before adding it. Most RazorWire-generated UI should be smaller and quieter than the host page around it.

## Review Questions

Before adding or changing generated UI, ask:

- Does RazorWire own this node, or is the app the real owner?
- Which `data-rw-*` attributes and custom properties are part of the supported contract?
- Can a host app make this match its own design system without replacing package code?
- Does the UI remain understandable with JavaScript disabled, slow streams, or failed form submissions?
- Are accessibility behavior, defaults, and pitfalls documented next to the feature?
