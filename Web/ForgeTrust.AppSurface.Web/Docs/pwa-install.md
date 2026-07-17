# AppSurface Web PWA Install, Badging, and Push-Worker Support

AppSurface Web owns a small, composable [Progressive Web App](https://developer.mozilla.org/en-US/docs/Web/Progressive_web_apps) foundation: install metadata, application-icon badging requests, explicit offline behavior, push-event plumbing, service-worker registration metadata, and privacy-safe diagnostics. Each capability is opt-in. Badging does not own an attention count or prove that an icon changed. Enabling push does not request notification permission, create a subscription, choose recipients, or send a message.

Use the [executable PWA example](../../../examples/web-pwa-install/README.md) to see install metadata, an independent accessible badging proof, offline behavior, push handlers, and explicit browser registration together. Use [`appsurface pwa verify`](../../../Cli/ForgeTrust.AppSurface.Cli/README.md) for install and worker server-known readiness evidence; its schema does not include badging evidence.

## Quick Starts

### Install metadata

Configure manifest metadata in `WebOptions.Pwa`, then add `<appsurface:pwa-head />` to an MVC or Razor layout:

```csharp
options.Pwa.Enabled = true;
options.Pwa.Name = "Contoso Field Notes";
options.Pwa.ShortName = "Field Notes";
options.Pwa.ThemeColor = "#2563eb";
options.Pwa.BackgroundColor = "#ffffff";
options.Pwa.Icons.Add(new PwaIcon { Source = "/icons/app-192.png", Sizes = "192x192", Type = "image/png" });
options.Pwa.Icons.Add(new PwaIcon { Source = "/icons/app-512.png", Sizes = "512x512", Type = "image/png" });
```

`Pwa.Enabled` controls install metadata only. It is not a master switch for offline or push behavior.

### Badging only

Enable the default-off browser adapter without enabling install metadata, offline behavior, push, or a worker:

```csharp
options.Pwa.Badging.Enabled = true;
```

Add `<appsurface:pwa-head />` to the page head, then call the helper after it has loaded:

```javascript
const run = async () => {
    const result = await window.AppSurface.Pwa.badging.set(3);
    if (result === "unsupported") {
        // Keep the in-app attention state available.
    }
};

if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", run, { once: true });
} else {
    void run();
}
```

Use an authoritative aggregate count, never a delta such as “add one.” Reconcile from application state when the page returns to the foreground. Keep the same state visible and accessible inside the application because the browser exposes no badge readback and may not display the request.

### Push-only worker

An app that already owns its install experience can map AppSurface's default push handlers without adding a manifest or cache strategy:

```csharp
options.Pwa.Enabled = false;
options.Pwa.Scope = "/";
options.Pwa.Push.Enabled = true;
```

Add `<appsurface:pwa-head />` so AppSurface can load the external registration helper, then call it from an application-owned button or other deliberate interaction:

```javascript
const registration = await window.AppSurface.Pwa.register();
if (registration === null) {
    // This browser does not expose service workers.
}
```

The call only invokes [`navigator.serviceWorker.register()`](https://developer.mozilla.org/en-US/docs/Web/API/ServiceWorkerContainer/register) with the configured worker URL, scope, and `updateViaCache: "none"`. It never calls `Notification.requestPermission()` or `PushManager.subscribe()`.

### Add push handlers to an existing AppSurface PWA

Keep the existing install and offline configuration, then enable push:

```csharp
options.Pwa.Enabled = true;
options.Pwa.Offline.Enabled = true;
options.Pwa.Offline.OfflineFallbackPath = "/offline.html";
options.Pwa.Offline.StaticAssetPaths = ["/offline.html", "/icons/app-192.png", "/icons/app-512.png"];
options.Pwa.Push.Enabled = true;
```

AppSurface generates one worker containing both capabilities. Push failures are contained independently and do not change offline fetch behavior.

## Capability and Ownership Boundaries

| Capability | AppSurface Web | Application or follow-up package |
|---|---|---|
| Install metadata and head tags | Generates and validates them when `Pwa.Enabled` is `true`. | Chooses product identity, icons, and install UX. |
| Offline fallback | Generates the explicitly configured narrow cache/fetch strategy. | Decides whether private routes or data may ever be cached. |
| Push worker | Generates default push/click handlers or imports a custom handler. | Chooses whether and when push is appropriate. |
| Worker registration | Exposes inert `window.AppSurface.Pwa.register()`. | Calls it at an intentional point in the product journey. |
| Permission | None. | Owns user education, timing, preferences, and `Notification.requestPermission()`. |
| Subscription | None. | Owns `PushManager.subscribe()`, endpoint custody, reconciliation, identity, and persistence. |
| Delivery | None. | Owns VAPID keys, recipients, sending, provider results, retries, and abuse controls. |
| Navigation intent | Default adapter accepts only a safe same-scope destination. | Owns product-specific destinations or a custom handler policy. |
| Application-icon badging | Exposes sanitized page and active-worker set/clear adapters. | Owns the authoritative aggregate, reconciliation, privacy policy, and accessible in-app state. |

The [push-worker foundation case](https://github.com/forge-trust/AppSurface/issues/631) establishes worker and registration plumbing. [Push delivery](https://github.com/forge-trust/AppSurface/issues/632) owns subscriptions and server-side delivery. [Push verification](https://github.com/forge-trust/AppSurface/issues/633) owns browser compatibility and delivered-notification proof. “Push enabled” in server diagnostics therefore does not mean permission granted, subscription created, or notification delivered.

## API Reference

### Install metadata

`WebOptions.Pwa` is always available. Install requirements are validated only when `Enabled` is `true`.

| Option | Default | Behavior |
|---|---|---|
| `Enabled` | `false` | Maps the manifest and emits install metadata. Does not control offline or push. |
| `Name` | Empty | Required full application name when install metadata is enabled. |
| `ShortName` | Empty | Required launcher name when install metadata is enabled. |
| `StartUrl` | `/` | Validated only for install metadata; must remain inside `Scope`. |
| `Scope` | `/` | Manifest scope and effective worker registration scope. Validated whenever install metadata or a worker capability is active. Active worker scopes reject percent escapes so registration and notification destinations share one unambiguous path spelling. |
| `Display` | `Standalone` | Emits `standalone`, `minimal-ui`, `fullscreen`, or `browser`. |
| `ThemeColor` | Empty | Required install color emitted to the manifest and page head. |
| `BackgroundColor` | Empty | Required install color emitted to the manifest. |
| `Icons` | Empty | Requires `192x192` and `512x512` size tokens for install metadata. |
| `ManifestPath` | `/manifest.webmanifest` | Generated manifest endpoint when install metadata is enabled. The PathBase-adjusted value remains validated and reported in diagnostics for additive older-tool compatibility whenever any PWA surface is active. |
| `DiagnosticsExposure` | `DevelopmentOnly` | `Always`, `DevelopmentOnly`, or `Never` for AppSurface PWA diagnostics. |
| `DiagnosticsPath` | `/_appsurface/pwa` | Base path for the diagnostics HTML endpoint and its `/status.json` child. The path is adjusted beneath `PathBase` and rejects percent escapes. |

### Shared worker and push

| Option | Default | Behavior |
|---|---|---|
| `Worker.ServiceWorkerPath` | `/service-worker.js` | Canonical generated worker endpoint shared by offline and push. Percent escapes are rejected so routing, metadata, and static-shadow checks use one representation. |
| `Worker.RegistrationHelperPath` | `/_appsurface/pwa/register.js` | External registration-helper endpoint. Emitted in head metadata only when push is enabled. Percent escapes are rejected. |
| `Push.Enabled` | `false` | Adds push behavior and activates the shared worker independently of install/offline settings. |
| `Push.HandlerScriptPath` | `null` | Uses the strict AppSurface v1 adapter. A configured same-origin path is loaded with `importScripts()` and replaces the default push/click handlers. |

`Offline.ServiceWorkerPath` remains a compatibility alias for `Worker.ServiceWorkerPath`. Setting either one alone selects the worker path; setting both to the same value is valid. Conflicting explicit values fail startup regardless of assignment or configuration-binding order. New code should use `Worker.ServiceWorkerPath`.

### Application-icon badging

| Option or API | Default | Behavior |
|---|---|---|
| `Badging.Enabled` | `false` | Maps the page helper. When a worker is already active for offline or push, also installs the identical adapter in that worker. Badging alone does not create a worker. |
| `Badging.HelperPath` | `/_appsurface/pwa/badging.js` | Content-versioned page-helper endpoint. It must be an app-root-relative generated route and is adjusted beneath `PathBase` when emitted. |
| `AppSurface.Pwa.badging.set(count)` | — | Accepts a finite, nonnegative safe integer. Zero uses the clear path. Resolves to `"accepted"` or `"unsupported"`. |
| `AppSurface.Pwa.badging.clear()` | — | Requests an explicit clear, falling back to the native zero form only when needed. Resolves to `"accepted"` or `"unsupported"`. |

`"accepted"` means only that the selected native request resolved. It does not mean the app is installed, permission was granted, the badge is visible, or the operating system rendered the requested number. `"unsupported"` means the needed native method is absent in the current page or worker. The API never reads, stores, retries, sequences, or reports a displayed badge value.

| Configuration | Page adapter | Worker adapter |
|---|---|---|
| Badging only | Yes | No worker is created. |
| Badging + offline | Yes | Yes, after the new worker activates. |
| Badging + default or custom push | Yes | Yes, before push handlers are installed. |
| Badging disabled | No | No; existing generated worker behavior is unchanged. |

Invalid counts reject with `TypeError("ASPWAJS040")`. Native set failures reject with sanitized `DOMException("ASPWAJS041", "InvalidStateError")`; clear failures use `ASPWAJS042`. A value-free `Error` is used when `DOMException` cannot be constructed. Branch on the bounded `error.message` when product UI needs to distinguish these outcomes; do not log the complete native exception:

```javascript
try {
    await window.AppSurface.Pwa.badging.set(authoritativeCount);
} catch (error) {
    const code = error instanceof Error ? error.message : "ASPWAJS041";
    showBadgingFailure(code);
}
```

Custom worker handlers can call `self.AppSurface.Pwa.badging`, but must attach their own asynchronous work to `event.waitUntil()`. Page-helper deployment and active-worker deployment are intentionally non-atomic: normal service-worker update and activation rules mean an older active worker may temporarily lack the adapter.

### Offline behavior

| Option | Default | Behavior |
|---|---|---|
| `Offline.Enabled` | `false` | Adds the starter cache/fetch strategy and activates the shared worker independently of install/push settings. |
| `Offline.ServiceWorkerPath` | `/service-worker.js` | Compatibility alias described above. |
| `Offline.OfflineFallbackPath` | Empty | Required when offline is enabled. |
| `Offline.StaticAssetPaths` | Empty | Same-origin assets to precache with the fallback. |

The starter strategy caches only configured static assets and the fallback, intercepts only the requests documented by that strategy, and never broadens its cache because push is enabled. A configured static-asset pathname is looked up only in the worker-owned cache with the query string ignored, so content-versioned requests such as `/app.css?v=hash` can use the precached `/app.css` while offline. Push-only mode installs no fetch listener, creates no cache, stores no application data, and retires caches previously owned by the AppSurface worker when moving away from offline mode.

Install metadata and offline support preserve AppSurface's existing automatic static-file middleware behavior because they commonly reference web-root assets. A custom `Push.HandlerScriptPath` also enables that middleware so the handler may be deployed from the web root. Default push-only mode serves only generated worker/helper endpoints and does not enable global static-file middleware; enable it explicitly if the application separately needs unrelated web-root content.

All AppSurface endpoint and asset paths are app-root-relative. Startup rejects schemes, protocol-relative URLs, whitespace, backslashes, controls, route parameters, malformed escapes, recursively encoded traversal, query strings, and fragments where those values are not explicitly supported. Generated manifest, diagnostics, worker, and helper endpoint paths reject all percent escapes so routing, emitted metadata, and static-shadow checks cannot disagree about decoding. Worker, helper, manifest, diagnostics, and fallback routes must be distinct. AppSurface also fails when a generated worker or helper path would be shadowed by a file in the web root; it cannot reliably infer arbitrary application middleware or endpoints registered later.

Browsers match service-worker scopes as raw URL prefixes. `/app` therefore also covers `/application`; use `/app/` when the application intends a path-segment boundary. AppSurface validation, the default click adapter, and CLI scope checks follow those browser prefix semantics.

## Head Metadata and Browser Helpers

`<appsurface:pwa-head />` composes the metadata needed by active capabilities:

- Install enabled: manifest, theme, application, Apple mobile-web-app, and icon tags.
- Offline enabled: worker path and scope metadata, without loading the registration helper.
- Push enabled: a versioned external helper script carrying the PathBase-adjusted worker URL and scope as encoded metadata.
- Badging enabled: a separate versioned external badging helper, whether or not a worker capability is active.
- Neither active: no output.

The helper safely claims only a compatible `window.AppSurface.Pwa.register` namespace. It does not overwrite conflicting globals, and duplicate loading with identical metadata is harmless. Frozen objects, proxies, throwing accessors, forged helper brands, and incompatible namespace values are contained instead of escaping an exception into the host page.

`register()` accepts no arguments and returns `Promise<ServiceWorkerRegistration | null>`. `null` means service workers are unsupported. Registration rejection remains a rejected promise so application UI can show a truthful failure state. The helper contains no permission or subscription behavior.

The content-versioned helper URL is cached immutably only when its version matches the served content; unversioned or stale-version requests receive `no-cache`. Per-page script metadata carries the effective worker URL and scope. Under a strict [Content Security Policy](https://developer.mozilla.org/en-US/docs/Web/HTTP/CSP), allow the same-origin external helper through `script-src`; keep the application-owned registration interaction in an allowed external script or apply the host's normal nonce/hash policy.

Minimal API apps, custom layouts, and non-Razor frontends can copy the exact tags from development diagnostics. Do not construct the helper metadata by string concatenation.

## Default Push Payload v1

The default adapter accepts one strict JSON object:

```json
{
  "version": 1,
  "title": "Field notes are ready",
  "body": "Open today's notes.",
  "iconPath": "/icons/app-192.png",
  "badgePath": "/icons/badge-96.png",
  "tag": "field-notes-ready",
  "destinationPath": "/notes/today?source=push"
}
```

`version` and `title` are required. Optional properties must be omitted rather than set to `null`; unknown or incorrectly typed fields are rejected. The complete UTF-8 JSON document is limited to 3,993 bytes, the conservative version-1 plaintext ceiling derived from [RFC 8291](https://www.rfc-editor.org/rfc/rfc8291.html). Providers may enforce a smaller limit.

| Field | Limit | Validation |
|---|---:|---|
| `version` | Exact integer `1` | Selects this contract. |
| `title` | 1–256 UTF-16 code units | Required non-empty string. |
| `body` | 1–2,048 UTF-16 code units | Optional non-empty string. |
| `iconPath`, `badgePath` | 1–1,024 UTF-16 code units | Optional app-root-relative asset path resolved beneath `PathBase`. |
| `tag` | 1–128 UTF-16 code units | Optional non-empty notification tag. |
| `destinationPath` | 1–1,024 UTF-16 code units | Optional app-root-relative path, with at most one query string and no fragment, restricted to the effective worker scope. |

Invalid payloads are discarded with a value-free diagnostic. The adapter stores only the normalized destination in notification data. On click it closes the notification, independently revalidates that destination, focuses the first exact matching window client, or opens the safe URL. A notification without a destination closes without navigation or an invalid-destination diagnostic. A failed focus receives one open fallback. Payloads and destination values are never logged.

Set `Push.HandlerScriptPath` when the default schema or navigation policy is not appropriate:

```csharp
options.Pwa.Push.Enabled = true;
options.Pwa.Push.HandlerScriptPath = "/workers/contoso-push.js";
```

The generated worker imports that same-origin script instead of installing AppSurface's default push and notification-click listeners. A load or top-level evaluation failure produces only `ASPWAJS030`; shared lifecycle and any offline handlers continue to install. Script evaluation is not transactional: listeners or other top-level side effects created before an exception cannot be rolled back. Validate and initialize first, then register listeners only after the handler is ready. The application script owns payload validation, notification content, click behavior, privacy, and tests. AppSurface still owns shared lifecycle, worker headers, scope, registration metadata, and diagnostics.

## Endpoints, Headers, and PathBase

The worker and helper support `GET` and `HEAD`; `HEAD` returns the same headers without a body.

| Endpoint | Content type | Cache and security behavior |
|---|---|---|
| Worker | `text/javascript; charset=utf-8` | `Cache-Control: no-cache`, `X-Content-Type-Options: nosniff`, and `Service-Worker-Allowed` matching the PathBase-adjusted scope. |
| Registration helper | `text/javascript; charset=utf-8` | Current content-versioned requests receive immutable caching; unversioned or stale-version requests receive `no-cache`. All responses include `X-Content-Type-Options: nosniff`. |
| Badging helper | `text/javascript; charset=utf-8` | Uses the same exact-version immutable caching contract as the registration helper and includes `X-Content-Type-Options: nosniff`. |
| Manifest | `application/manifest+json` | Generated only when install metadata is enabled. |

App-root-relative settings do not include `PathBase`. A host mounted at `/tenant-a` turns `/service-worker.js` into `/tenant-a/service-worker.js` and `/` scope into `/tenant-a/`. Validate and verify the externally visible URL; do not configure `/tenant-a` twice.

## Diagnostics and CLI Verification

Development diagnostics are available at `/_appsurface/pwa` and `/_appsurface/pwa/status.json` unless `DiagnosticsExposure` changes that policy. The status JSON distinguishes server-known capabilities with `workerEnabled`, `workerPath`, `pushEnabled`, `workerScope`, `registrationHelperPath`, `badgingEnabled`, and `badgingHelperPath` while preserving `manifestPath`, `offlineEnabled`, `serviceWorkerPath`, `configuredServiceWorkerPath`, and `offlineFallbackPath` for older tools. Disabled badging is explicit as `badgingEnabled: false` and `badgingHelperPath: null`.

- Offline or combined mode keeps the legacy active `serviceWorkerPath` value.
- Push-only mode reports the active worker through the new worker fields; legacy offline worker fields are `null`.
- No-worker mode retains `configuredServiceWorkerPath` so compatible CLI versions can prove the endpoint is absent.

Diagnostics report only server-known configuration and helper routing. They never claim that the current browser supports badging or that a visible badge exists. `appsurface pwa verify` remains on its existing schema and does not independently verify badging in this stage. When push configuration is observed, the verifier reports that registration, permission, subscription, and delivery were not evaluated. It does not turn server configuration into browser proof.

Server validation keeps generated paths unambiguous. `ASPWA026` is secure-context guidance for a badging-only configuration, not a browser-support verdict. `ASPWA027` rejects an invalid `Badging.HelperPath`; generated-route collisions continue to use `ASPWA023`, and a web-root file shadowing the active helper fails startup with `ASPWA024` when static-file middleware is otherwise enabled.

```bash
appsurface pwa verify \
  --base-url https://app.example.com \
  --entry-path /account/resume \
  --expect-start-url / \
  --expect-scope / \
  --expect-display standalone \
  --expect-icon 192x192 \
  --expect-icon 512x512 \
  --json
```

Browser diagnostics are stable and value-free:

| Code | Meaning |
|---|---|
| `ASPWAJS001` | Invalid helper metadata. |
| `ASPWAJS002` | Browser namespace conflict. |
| `ASPWAJS003` | Service-worker registration rejected. |
| `ASPWAJS010` | Invalid push payload. |
| `ASPWAJS011` | Notification display failed. |
| `ASPWAJS020` | Invalid notification-click destination. |
| `ASPWAJS021` | Client focus/open failed. |
| `ASPWAJS030` | A shared lifecycle or custom-handler import failure was contained. |
| `ASPWAJS040` | A badging count was not a finite, nonnegative safe integer. |
| `ASPWAJS041` | A native set-badge request failed. |
| `ASPWAJS042` | A native clear-badge request failed. |

Warnings never include payloads, destination URLs, subscription endpoints, identifiers, or exception text.

## Migration and Pitfalls

- Moving or disabling a registered worker is staged browser-state migration, not only a server setting change. First deploy cleanup code from the currently registered path to unregister or replace the old registration and retire owned caches. Only after clients receive that deployment should you stop mapping or move the old path.
- Moving from an older or #631 offline worker to push-only at the same worker path is supported: the push-only worker deletes the legacy `appsurface-pwa-v1` cache and caches in that worker's path-derived namespace, then installs no fetch handler.
- AppSurface does not guess historical custom worker paths. An app that changes a path owns cleanup for registrations at the previous path.
- Do not cache authenticated HTML, APIs, tenant data, or logout-sensitive content with the starter offline strategy.
- Keep `StartUrl` inside `Scope`, and keep the worker scope no broader than the application surface that owns its behavior.
- Use HTTPS or localhost. Service-worker availability and install prompts remain browser policy decisions.
- A visible application badge generally requires an installed web-app identity. AppSurface cannot prove a host-owned manifest, installation state, or icon visibility. On iOS and iPadOS, browser policy may also tie badging to notification permission; AppSurface does not request it.
- Operating systems may hide, coerce, cap, or replace requested counts. There is no readback API. Treat the accessible in-app state as authoritative and reconcile aggregate state on foreground/resume rather than applying deltas.
- Choose privacy-safe aggregates. Even an icon count can disclose product activity to someone who can see the device.
- Do not confuse default-push `badgePath` with application-icon badging: `badgePath` selects notification artwork and does not set an app badge count.
- A newly deployed page helper can be available before an older active worker updates. Avoid assuming page and worker capability activation is atomic.
- Do not call registration “push ready.” Permission, subscription, server delivery, and end-to-end browser evidence are separate capabilities.
- A custom handler is executable code with the worker's authority. Serve it from a stable same-origin path, review its payload/navigation trust boundaries, and test failure branches.
- Keep diagnostics development-only unless the app intentionally exposes sanitized readiness metadata.
- Under reverse-proxy hosting, verify the external `PathBase`, CSP, worker scope, helper URL, and `Service-Worker-Allowed` header together.
