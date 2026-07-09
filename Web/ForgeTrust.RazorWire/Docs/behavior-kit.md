# Behavior Kit

Use RazorWire Behavior Kit when a server-rendered app needs small app-authored JavaScript that follows RazorWire's page lifecycle. Behavior Kit is for progressive enhancement, not for replacing your frontend architecture.

Behavior Kit has two lanes:

- Root-scoped behaviors enhance replaceable DOM roots such as buttons, panels, or widgets.
- Lifecycle behaviors run once per logical browser visit for page-owned work such as PWA display-mode telemetry.

Behavior Kit is explicit in v1. Render it with `<rw:scripts behavior-kit="true" />`; plain `<rw:scripts />` does not lazy-load `behavior-kit.js`.

## Root Behaviors

Register root behaviors with a stable name, a CSS selector, and a connect callback:

```html
<section data-task-preview>
  <button type="button" data-expand>Expand</button>
</section>

<script>
window.RazorWire.behaviors.register({
  name: "app.task-preview",
  selector: "[data-task-preview]",
  connect(root, context) {
    const button = context.query("[data-expand]");
    button?.addEventListener("click", () => {
      root.toggleAttribute("data-expanded");
    }, { signal: context.signal });
  }
});
</script>
```

RazorWire connects each matching root once, reconnects new roots after Turbo page and frame updates, and aborts `context.signal` before cleanup when a root leaves the document.

## Lifecycle Behaviors

Register lifecycle behaviors when there is no honest DOM root. The default events are `initial` and `turbo:load`:

```html
<script>
window.RazorWire.behaviors.registerLifecycle({
  name: "app.pwa-display-mode",
  connect(context) {
    const standalone =
      window.matchMedia("(display-mode: standalone)").matches ||
      window.matchMedia("(display-mode: fullscreen)").matches ||
      window.matchMedia("(display-mode: minimal-ui)").matches ||
      window.navigator.standalone === true;

    document.dispatchEvent(new CustomEvent("app:pwa-display-mode-seen", {
      detail: {
        displayMode: standalone ? "installed" : "browser",
        renderKind: context.renderKind,
        url: context.url
      }
    }));
  }
});
</script>
```

Set `frames: true` only when the behavior should also run for `turbo:frame-load`. Same-URL revisits still run on later lifecycle passes; repeated registration during the same pass does not double-count.

## Choose The Right Lane

| Need | Use |
| --- | --- |
| Package-owned page navigation, section copy, or form mechanics | Built-in RazorWire managers |
| App-owned behavior tied to a root element | `register({ name, selector, connect })` |
| App-owned telemetry or browser sampling tied to a logical visit | `registerLifecycle({ name, connect })` |
| Component/module hydration | RazorWire islands |
| Large app-specific frontend behavior | App-owned JavaScript or a frontend framework |

## Diagnostics

Call `window.RazorWire.behaviors.getDiagnostics()` in development tests or browser debugging. Diagnostics use `code`, `message`, `impact`, `fix`, and `docs`.

| Code | Meaning |
| --- | --- |
| `BehaviorSelectorInvalid` | A root behavior selector cannot be scanned. |
| `BehaviorRegistrationConflict` | The same behavior name was registered with incompatible options. |
| `BehaviorDiagnostic` | A root or lifecycle behavior reported an app-owned diagnostic through `context.diagnostic(...)`. |
| `BehaviorConnectFailed` | A root or lifecycle callback threw while connecting. |
| `BehaviorCleanupFailed` | A cleanup callback threw while disconnecting. |
| `BehaviorAbortUnsupported` | The browser cannot provide `AbortController` / `AbortSignal`. |
| `BehaviorLifecycleEventInvalid` | A lifecycle registration requested an unsupported event. |
| `BehaviorKitNotLoaded` | App code called the core stub before eager `behavior-kit.js` loaded. |

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| A behavior never connects | `<rw:scripts behavior-kit="true" />` is missing, or app code calls `scan()` before `behavior-kit.js` initializes. The queue-backed stub preserves early `register(...)` and `registerLifecycle(...)` calls. | Render `<rw:scripts behavior-kit="true" />` in the layout and call `scan()` only after the eager script has loaded. |
| A click handler fires twice | App code registered unmanaged document listeners outside Behavior Kit. | Move the listener into `connect` and bind with `{ signal: context.signal }`. |
| Page telemetry misses Turbo visits | The work was modeled as a root behavior on `body`. | Use `registerLifecycle` so logical visits, not body replacement, drive the sample. |
| A behavior conflicts after hot reload or repeated bundle evaluation | The same name changed selector, events, or frame options. | Keep names and options stable, or choose a new behavior name. |
