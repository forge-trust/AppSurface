# Section Copy

Use RazorWire section copy when a server-rendered documentation page, reference page, or long-form article needs stable "copy link to this section" behavior without app-specific clipboard JavaScript.

## Section Copy in 3 Minutes

Render a button with `data-rw-section-copy` when the host owns the button markup:

```cshtml
<h2 id="install">Install</h2>
<button type="button"
        data-rw-section-copy="install"
        data-rw-section-copy-title="Install">
    Copy link
</button>
```

Or mark a heading/container with `data-rw-section-copy-target` when RazorWire should generate a plain button:

```cshtml
<h2 id="configuration" data-rw-section-copy-target="true">
    Configuration
</h2>
```

Plain `<rw:scripts/>` is enough. It emits a small detector that loads `section-copy.js` when the page contains `data-rw-section-copy` or `data-rw-section-copy-target`. Use `<rw:scripts section-copy="true" />` only when you intentionally want eager loading.

## API Shape

- `data-rw-section-copy="target-id"` belongs on a `button`. A leading `#` is accepted.
- `data-rw-section-copy-title="Title"` provides reader-facing copy for `aria-label`, live status, and fallback UI.
- `data-rw-section-copy-target="true"` belongs on a heading or section container with a document-unique `id`.
- `data-rw-section-copy-status="true"` marks an optional `aria-live="polite"` or `aria-live="assertive"` status region.
- `data-rw-section-copy-root="true"` optionally scopes generated status, timers, and cleanup. Without a root, RazorWire uses the document body.

RazorWire exposes `window.RazorWire.sectionCopyManager` with `scan()`, `prune()`, `getDiagnostics()`, and `clearDiagnostics()`. Call `scan()` after custom DOM updates that add section-copy markup outside normal Turbo render events.

## Runtime-Owned Hooks

The runtime writes stable data hooks that hosts may style or test:

- `data-rw-section-copy-enhanced="true"`
- `data-rw-section-copy-state="copied"` or `"fallback"`
- `data-rw-section-copy-message`
- `data-rw-section-copy-inserted="true"`
- `data-rw-section-copy-fallback="true"`
- `data-rw-section-copy-status-generated="true"`

Generated buttons are plain `button type="button"` elements with visible text `Copy link`. RazorWire does not add product-specific classes or icons. Host applications may decorate generated buttons after `scan()` while keeping the stable data hooks.

## Accessibility And Fallback

Successful copies announce through the configured live region or a generated visually-hidden polite status. RazorWire does not mutate authored visible button text.

When the Clipboard API is unavailable or denied, RazorWire renders an inline non-modal fallback dialog next to the source button. The fallback contains a readonly selected input and a close button. It closes on Escape, the close button, outside pointer press, or focus leaving the fallback. Escape returns focus to the source button when it is still connected.

## Static Export

Static export materializes `/_content/ForgeTrust.RazorWire/razorwire/section-copy.js` when exported HTML contains section-copy markup, even when the page uses lazy `<rw:scripts/>` loading and no literal section-copy script tag exists yet.

## Troubleshooting

- Blank `data-rw-section-copy` values are ignored. Set the attribute to a non-empty target id.
- Non-button `data-rw-section-copy` controls are ignored. Use buttons for copy actions and anchors for navigation.
- Duplicated target ids are ignored because the copied fragment would be ambiguous.
- Invalid percent encoding, such as a dangling `%`, is ignored. Use literal ids or valid encodings such as `api%20key`.
- A custom status region must include `aria-live="polite"` or `aria-live="assertive"`. Otherwise RazorWire creates an internal polite status region.
