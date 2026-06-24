# AppSurface Web PWA Install Support

AppSurface Web owns the baseline install contract for Progressive Web Apps: manifest metadata, head tags, diagnostics, and explicit offline fallback wiring. It does not promise that every browser will show an install prompt. Browsers still decide when and how to surface installation.

## Quick Start

Enable PWA metadata from `WebOptions.Pwa`:

```csharp
await WebApp<MyRootModule>.RunAsync(
    args,
    options =>
    {
        options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews };
        options.Pwa.Enabled = true;
        options.Pwa.Name = "Contoso Field Notes";
        options.Pwa.ShortName = "Field Notes";
        options.Pwa.ThemeColor = "#2563eb";
        options.Pwa.BackgroundColor = "#ffffff";
        options.Pwa.Icons.Add(new PwaIcon { Source = "/icons/app-192.png", Sizes = "192x192" });
        options.Pwa.Icons.Add(new PwaIcon { Source = "/icons/app-512.png", Sizes = "512x512" });
    });
```

Add the TagHelper to an MVC/Razor layout:

```cshtml
<appsurface:pwa-head />
```

Verify a running app:

```bash
appsurface pwa verify --url https://app.example.com
```

During local development, open `/_appsurface/pwa` for an HTML checklist and `/_appsurface/pwa/status.json` for machine-readable diagnostics.

## API Shape

`WebOptions.Pwa` defaults to disabled. When `Enabled` is `true`, AppSurface validates the install contract during startup and maps the PWA endpoints before application MVC endpoints.

Required values when enabled:

| Option | Default | Notes |
|--------|---------|-------|
| `Name` | Empty | Full app name used by the manifest and head metadata. |
| `ShortName` | Empty | Short launcher name. |
| `StartUrl` | `/` | Must be an app-root-relative URL path. |
| `Scope` | `/` | Must be an app-root-relative URL path. Keep it at or above `StartUrl`. |
| `Display` | `Standalone` | Maps to `standalone`, `minimal-ui`, `fullscreen`, or `browser`. |
| `ThemeColor` | Empty | Hex color emitted to the manifest and `<meta name="theme-color">`. |
| `BackgroundColor` | Empty | Hex color emitted to the manifest. |
| `Icons` | Empty | Must include at least one `192x192` icon and one `512x512` icon. |

Optional values:

| Option | Default | Notes |
|--------|---------|-------|
| `ManifestPath` | `/manifest.webmanifest` | App-root-relative endpoint for generated manifest JSON. |
| `Diagnostics` | `DevelopmentOnly` | `Always`, `DevelopmentOnly`, or `Never` for `/_appsurface/pwa`. |
| `Offline.Enabled` | `false` | Controls whether AppSurface maps a service worker endpoint. |
| `Offline.ServiceWorkerPath` | `/service-worker.js` | App-root-relative service worker endpoint. |
| `Offline.OfflineFallbackPath` | Empty | Required when offline is enabled. |
| `Offline.StaticAssetPaths` | Empty | Static assets to precache with the offline fallback. |

All AppSurface-owned endpoint paths must be app-root-relative. Absolute URLs, protocol-relative URLs, backslash paths, traversal segments, query strings, and fragments are rejected before the app starts.

## Head Metadata

`<appsurface:pwa-head />` emits:

- `<link rel="manifest" href="/manifest.webmanifest">`
- `<meta name="theme-color" ...>`
- `<meta name="application-name" ...>`
- Apple mobile-web-app capable, title, and status-bar metadata.
- `<link rel="icon" sizes="..." type="..." href="...">` for each configured icon.

The helper respects `PathBase` and uses ASP.NET Core file versioning for icon hrefs when static assets provide a versioned path. If PWA support is disabled, it emits no output.

Minimal API apps, custom layout systems, and non-Razor frontends can copy the equivalent tags from development diagnostics. Keep the manifest link and theme color in the initial HTML response; browsers discover install metadata from the page head.

## Offline Strategy

Offline support is deliberately opt-in. By default, AppSurface maps no service worker and caches no routes.

The starter offline strategy only precaches the configured static asset list plus the configured offline fallback page. The generated service worker:

- Caches only same-origin `GET` requests listed in `Offline.StaticAssetPaths` plus `Offline.OfflineFallbackPath`.
- Returns the fallback page for failed navigations.
- Ignores `POST`, `PUT`, `PATCH`, `DELETE`, and other non-GET requests.
- Ignores cross-origin requests.
- Does not cache authenticated pages, arbitrary navigation routes, API responses, or app data by default.

That default is intentionally boring. Authenticated apps, admin tools, docs with release archives, and user-specific dashboards should design their own service worker when they need richer offline behavior. Do not enable broad route caching until the app has reviewed authentication, logout, tenant isolation, cache invalidation, and private data exposure.

## Diagnostics

Development diagnostics are available at:

- `/_appsurface/pwa`
- `/_appsurface/pwa/status.json`

The status JSON reports stable AppSurface diagnostics for startup-validatable configuration, manifest metadata, head-tag hints, and offline posture. Production hides diagnostics by default; use `PwaDiagnosticEndpointExposure.Always` only for intentionally public readiness checks.

## CLI Verification

`appsurface pwa verify --url <origin>` checks a running origin:

- HTTPS or localhost install-context acceptability.
- Root HTML manifest link presence.
- Manifest route reachability and `application/manifest+json` content type.
- Manifest fields, display mode, same-origin `start_url`, and same-origin `scope`.
- Required `192x192` and `512x512` icons.
- Icon reachability and basic image content type.
- Diagnostic endpoint posture.
- Service worker reachability when diagnostics report offline enabled.

Use JSON output when integrating the verifier into CI:

```bash
appsurface pwa verify --url https://app.example.com --json
```

The command emits stable diagnostic codes and exits nonzero when install-critical checks fail.

## Browser Caveats

Installability is a browser policy decision. AppSurface can provide correct metadata, HTTPS/localhost proof, diagnostics, and verification, but it cannot force Chrome, Edge, Safari, Firefox, Android, iOS, or desktop shells to expose the same prompt at the same time.

Common reasons a browser may still hide installation:

- The origin is not HTTPS and is not localhost.
- The page has not been visited with enough engagement for that browser.
- The icon asset is unreachable, too small, or not an image.
- The manifest link is missing from the HTML head.
- `start_url` or `scope` points outside the current origin.
- The browser requires additional platform-specific signals.

Use AppSurface diagnostics and `appsurface pwa verify` to prove the web contract first, then debug browser-specific prompt behavior from browser DevTools.

## Pitfalls

- Do not add a service worker just to make an app "more PWA." A bad service worker is harder to unwind than a missing one.
- Do not cache authenticated HTML pages or API data with the starter strategy.
- Keep `StartUrl` inside `Scope`; otherwise browsers may treat the manifest as inconsistent.
- Serve icons from stable same-origin paths. Versioned URLs are fine in head metadata, but manifest icon `src` values should remain app-root-relative.
- Keep diagnostics development-only unless the app deliberately wants public PWA readiness metadata.
- For apps behind a reverse proxy path base, verify through the externally visible URL, not only the inner Kestrel URL.
