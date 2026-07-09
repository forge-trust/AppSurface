# Behavior Kit

Use the RazorWire behavior kit when a server-rendered page needs a small app-authored enhancement that must survive Turbo visits, Turbo frame replacement, partial DOM updates, and repeated bundle evaluation.

The behavior kit is for progressive enhancement on replaceable server DOM. Use RazorWire built-in managers first when they already own the behavior: page navigation, section copy, and form interactions. Use islands when the root is a module/component hydration boundary. Stimulus remains compatible app-owned JavaScript, but RazorWire does not require it.

## Setup

Behavior kit is eager-only in v1. Render it once in the shared layout before app bundles that register behaviors:

```cshtml
<rw:scripts behavior-kit="true" />
<script src="~/js/page-behaviors.js" asp-append-version="true"></script>
```

Plain `<rw:scripts />` does not lazy-load `behavior-kit.js`. RazorWire cannot infer app behavior registration from markup alone, and static export only preserves explicit eager behavior-kit scripts in v1. Generic `data-rw-behavior` marker loading is intentionally deferred.

## Register A Behavior

```js
window.RazorWire.behaviors.register({
  name: "demo.preview",
  selector: "[data-demo-preview]",
  connect(root, context) {
    const button = context.query("[data-demo-preview-button]");
    const output = context.query("[data-demo-preview-output]");
    if (!button || !output) return;

    button.addEventListener("click", () => {
      output.textContent = new Date().toLocaleTimeString();
    }, { signal: context.signal });
  }
});
```

`register(definition)` accepts:

- `name`: stable behavior name. Re-registering the same name with the same selector is idempotent and triggers a scan. Re-registering the same name with a different selector records `BehaviorRegistrationConflict` and keeps the first definition.
- `selector`: CSS selector for behavior roots.
- `connect(root, context)`: callback invoked once for each matching connected root. It may return an optional cleanup function.

The context includes:

- `signal`: abort signal fired before optional cleanup when the root disconnects or stops matching.
- `query(selector)` and `queryAll(selector)`: root-scoped helpers.
- `behaviorName` and `rootId`: stable diagnostic context for the live behavior/root pair.
- `diagnostic(message, fix, impact?, docs?)`: records an app behavior diagnostic using RazorWire's diagnostics shape.

## Lifecycle

`scan(root = document)` includes the supplied element itself when it matches the selector, then scans descendants. Already-connected behavior/root pairs are left alone, so repeated `scan()` calls, repeated bundle evaluation, Turbo events, and frame replacement do not attach duplicate listeners.

`prune()` disconnects behavior/root pairs whose root left the document or no longer matches the registered selector. Disconnect aborts `context.signal` first, then runs optional cleanup. If `connect()` throws after partial setup, RazorWire aborts the signal, discards the controller, records `BehaviorConnectFailed`, and lets a later `scan()` retry.

RazorWire listens for `turbo:render`, `turbo:load`, and `turbo:frame-load`. Call `window.RazorWire.behaviors.scan(element)` after app-owned DOM updates that happen outside those events.

## Diagnostics

`getDiagnostics()` returns objects with `code`, `message`, `impact`, `fix`, `docs`, and optional `behaviorName`, `selector`, and `rootId`. `clearDiagnostics()` empties the current diagnostics buffer, so later `getDiagnostics()` calls only include diagnostics recorded after the clear.

| Code | Meaning | Fix |
| --- | --- | --- |
| `BehaviorSelectorInvalid` | A behavior selector could not be queried. | Use a valid CSS selector for the behavior root. |
| `BehaviorRegistrationInvalid` | A behavior definition was malformed. | Pass `{ name, selector, connect }` to `window.RazorWire.behaviors.register(...)`. |
| `BehaviorRegistrationConflict` | The same behavior name was registered with an incompatible selector. | Keep names immutable, or use a unique behavior name for a different selector. |
| `BehaviorConnectFailed` | `connect()` threw while enhancing a root. | Fix the callback and use `context.signal` for event listeners so partial setup can be aborted. |
| `BehaviorCleanupFailed` | The optional cleanup callback threw during disconnect. | Make cleanup no-throw and prefer `context.signal` for listener cleanup. |
| `BehaviorAbortUnsupported` | `AbortController` / `AbortSignal` is unavailable. | Use a supported browser or load a compatible polyfill before `behavior-kit.js`. |

## Troubleshooting

- If `window.RazorWire.behaviors.register(...)` queues but nothing connects, make sure `behavior-kit.js` is loaded explicitly, for example with `<rw:scripts behavior-kit="true" />`.
- Do not attach unmanaged document-level listeners from repeated page bundles. Bind listeners to roots and pass `{ signal: context.signal }`.
- Do not use behavior kit to replace RazorWire form interactions, section copy, page navigation, or islands. Those surfaces already have package-owned lifecycle contracts.
- Static export copies explicit eager behavior-kit scripts. It does not synthesize behavior-kit assets from generic markup markers in v1.
