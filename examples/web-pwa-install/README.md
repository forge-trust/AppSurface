# AppSurface Web PWA Worker Foundation Proof

This example shows the AppSurface Web install, offline, and shared push-worker foundation. Without external keys, its registration button proves worker activation while leaving permission, subscription, and delivery unconfigured. In Development, externally configured keys activate the optional [`ForgeTrust.AppSurface.Web.Push`](../../Web/ForgeTrust.AppSurface.Web.Push/README.md) proof card with DevAuth personas, in-memory app-owned custody, direct-gesture subscription, and a truthful example-only protected host action.

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
- With explicit external Web Push configuration, the Development-only safe-rail card manually demonstrates Admin policy success, Viewer rejection, cookie antiforgery, app-owned custody, and gesture-bound subscribe/unsubscribe behavior. Its protected host action runs the package sender against the Development-only proof transport; it makes no network request and claims no browser delivery.

## Optional Web Push safe-rail proof

Generate a key pair only through the explicit example command, then store it outside the repository:

```bash
dotnet run --project examples/web-pwa-install -- --generate-vapid-keys
dotnet user-secrets init --project examples/web-pwa-install
dotnet user-secrets set --project examples/web-pwa-install WebPush:Keys:Primary:PublicKey "<public>"
dotnet user-secrets set --project examples/web-pwa-install WebPush:Keys:Primary:PrivateKey "<private>"
dotnet user-secrets set --project examples/web-pwa-install WebPush:Keys:Primary:Subject "mailto:push@example.test"
dotnet user-secrets set --project examples/web-pwa-install WebPush:AllowedPushServiceOrigins:0 "https://fcm.googleapis.com"
```

Use the browser-specific exact origin: Chromium commonly uses `https://fcm.googleapis.com`, Firefox uses `https://updates.push.services.mozilla.com`, and Safari uses `https://web.push.apple.com`. Review and add a changed vendor origin explicitly; never use an arbitrary-HTTPS fallback.

Run in Development, open `/_appsurface/dev-auth/`, select **Push Admin**, and return to `/`. **Enable notifications** is the only action that may prompt. The Viewer persona receives `403`. Subscription custody is persona-keyed and process-local. **Run protected host-action proof** invokes the package sender against a deterministic Development-only HTTP 201 proof transport while the separate **Push delivery** row remains `Not proven`; no network request or browser delivery occurs. Never use the sample custody, proof transport, or DevAuth implementation in production.

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
```
<!-- /appsurface:snippet -->

The starter service worker caches only the configured static asset list and offline fallback page. It does not cache POST responses, authenticated routes, arbitrary app navigations, or app data. Push handlers do not change that cache policy.

The capability ledger separates configuration from browser and delivery proof. “Push handlers configured” means only that the generated worker can receive the versioned AppSurface notification payload. Permission timing, subscription storage, recipients, VAPID delivery, and end-to-end browser evidence remain application or follow-up concerns.

For the full option reference, push-only setup, custom-handler boundary, payload contract, and worker migration guidance, read [PWA install and push-worker support](../../Web/ForgeTrust.AppSurface.Web/Docs/pwa-install.md).

## Browser-State Tests

The Node VM tests execute the exact static script served by the example and cover every proof-card state, exact worker matching, duplicate-click containment, and the absence of permission or subscription calls:

```bash
node --test examples/web-pwa-install/test/*.test.mjs
```
