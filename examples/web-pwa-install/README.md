# AppSurface Web PWA Runtime Foundation Proof

This example shows the AppSurface Web install, independent application badging, offline, and push-worker foundation without adding a separate package. Its explicit registration button proves browser worker activation. A separate accessible badging card keeps a synthetic in-app attention count authoritative while demonstrating accepted, unsupported, and sanitized rejected requests without claiming the home-screen icon changed. Notification permission, push subscription, and delivery remain unconfigured.

```bash
dotnet run --project examples/web-pwa-install/WebPwaInstallExample.csproj -- --environment Development --port 5055
dotnet run --project Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj -- pwa verify \
  --base-url http://localhost:5055 \
  --entry-path /account/resume \
  --expect-start-url / \
  --expect-scope / \
  --expect-display standalone \
  --expect-theme-color '#2563eb' \
  --expect-background-color '#ffffff' \
  --expect-icon 192x192 \
  --expect-icon 512x512 \
  --json
```

Or run the bundled proof script:

```bash
examples/web-pwa-install/verify.sh
```

The script writes CI-friendly verifier evidence to `examples/web-pwa-install/pwa-verify.json` by default. Override that path with `APP_SURFACE_WEB_PWA_EVIDENCE`.

## What It Proves

- `WebOptions.Pwa` serves `/manifest.webmanifest`.
- `<appsurface:pwa-head />` emits manifest, theme, mobile, and icon metadata.
- `/_appsurface/pwa` explains local install posture during development.
- `appsurface pwa verify` can prove a non-root entry path, exact manifest values, expected icon declarations, and JSON evidence.
- `/service-worker.js` combines the opted-in offline and push handlers.
- `<appsurface:pwa-head />` loads the inert registration helper when push is enabled.
- The proof card loads its state machine from a content-versioned static asset and calls only `window.AppSurface.Pwa.register()`. It never requests permission, subscribes through `PushManager`, or sends a notification.
- The independent application-badge card calls `window.AppSurface.Pwa.badging.set(count)` and `.clear()`, retains a visible accessible synthetic attention count, and never treats `"accepted"` as proof of icon visibility.
- The CLI output remains install/worker evidence. It does not independently verify browser badging support or display.

## Configuration

`examples/web-pwa-install/Program.cs`

<!-- appsurface:snippet id="web-pwa-options" file="examples/web-pwa-install/Program.cs" marker="web-pwa-options" lang="csharp" -->
```csharp
options.StartupTimeout = TimeSpan.FromSeconds(60);
options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews };
options.Pwa.Enabled = true;
options.Pwa.Name = "AppSurface PWA Field Notes";
options.Pwa.ShortName = "Field Notes";
options.Pwa.ThemeColor = "#2563eb";
options.Pwa.BackgroundColor = "#ffffff";
options.Pwa.Icons.Add(new PwaIcon { Source = "/icons/app-192.svg", Sizes = "192x192", Type = "image/svg+xml" });
options.Pwa.Icons.Add(new PwaIcon { Source = "/icons/app-512.svg", Sizes = "512x512", Type = "image/svg+xml" });

options.Pwa.Offline.Enabled = true;
options.Pwa.Offline.OfflineFallbackPath = "/offline.html";
options.Pwa.Offline.StaticAssetPaths = ["/icons/app-192.svg", "/icons/app-512.svg", "/offline.html"];
options.Pwa.Push.Enabled = true;
options.Pwa.Badging.Enabled = true;
```
<!-- /appsurface:snippet -->

The starter service worker caches only the configured static asset list and offline fallback page. It does not cache POST responses, authenticated routes, arbitrary app navigations, or app data. Push handlers do not change that cache policy.

The capability ledger separates configuration from browser and delivery proof. “App badging configured” proves only that AppSurface mapped the helper and composed the worker adapter. “Push handlers configured” means only that the generated worker can receive the versioned AppSurface notification payload. Icon display, permission timing, subscription storage, recipients, VAPID delivery, and end-to-end browser evidence remain application or follow-up concerns.

For the full option reference, badging privacy and activation boundaries, push-only setup, custom-handler boundary, payload contract, and worker migration guidance, read [PWA install, badging, and push-worker support](../../Web/ForgeTrust.AppSurface.Web/Docs/pwa-install.md).

## Browser-State Tests

The Node VM tests execute the exact static scripts served by the example and cover registration and badging proof-card states, exact worker matching, local digit-string validation, pending locking, canonical in-app state, duplicate-click containment, and the absence of permission or subscription calls:

```bash
node --test examples/web-pwa-install/test/*.test.mjs
```
