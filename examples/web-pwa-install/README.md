# AppSurface Web PWA Runtime Foundation Proof

This example shows the AppSurface Web install, independent application badging, offline, and shared push-worker foundation. Its explicit registration button proves browser worker activation. A separate accessible badging card keeps a synthetic in-app attention count authoritative while demonstrating accepted, unsupported, and sanitized rejected requests without claiming the home-screen icon changed. Without external keys, the sample still provides worker/helper readiness evidence; the optional push rail is reported as `not-configured`, while notification permission, subscription, and delivery remain unevaluated. In Development, externally configured keys activate the optional [`ForgeTrust.AppSurface.Web.Push`](../../Web/ForgeTrust.AppSurface.Web.Push/README.md) proof card with DevAuth personas, in-memory app-owned custody, direct-gesture subscription, and a truthful example-only protected host action.

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

The script writes schema-v3 CI-friendly verifier evidence to `examples/web-pwa-install/pwa-verify-v3.json` by default. Override that path with `APP_SURFACE_WEB_PWA_EVIDENCE`.

For a Bash schema-v3 proof on Linux or macOS, direct each requested surface to a named artifact:

```bash
mkdir -p artifacts
dotnet run --project Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj -- pwa verify \
  --surface push --base-url http://localhost:5055 --entry-path /account/resume \
  --expect-push enabled --json > artifacts/pwa-push-readiness.json
dotnet run --project Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj -- pwa verify \
  --surface all --base-url http://localhost:5055 --entry-path /account/resume \
  --expect-push enabled --json > artifacts/pwa-all-readiness.json
dotnet run --project Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj -- pwa verify \
  --surface push --base-url http://localhost:5055 --entry-path / \
  --expect-push disabled --json > artifacts/pwa-push-disabled.json
```

PowerShell parity is available on Windows, macOS, and Linux when `pwsh` is installed. It accepts explicit port and evidence-path inputs, starts the example for a bounded interval, cleans up the child host, and writes schema-v3 combined evidence:

```powershell
pwsh ./examples/web-pwa-install/verify.ps1 `
  -Port 5055 `
  -EvidencePath ./artifacts/pwa-verify-v3.json
```

The Bash and PowerShell proofs use Development configuration only and never generate, read, or require VAPID keys or other secrets. A nonzero `appsurface pwa verify` exit is authoritative even when the named JSON artifact exists.

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
- `appsurface pwa verify --surface push` checks worker/helper readiness plus the optional server-known push-rail posture; a zero-provider sample may report the rail as `not-configured` and still pass when worker/helper evidence is valid. `--surface all` combines that schema-v3 posture with install evidence. The entry path is used for registration-helper discovery on push and for both manifest and helper discovery on all.
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
options.Pwa.Badging.Enabled = true;
```
<!-- /appsurface:snippet -->

The starter service worker caches only the configured static asset list and offline fallback page. It does not cache POST responses, authenticated routes, arbitrary app navigations, or app data. Push handlers do not change that cache policy.

The capability ledger separates configuration from browser and delivery proof. “App badging configured” proves only that AppSurface mapped the helper and composed the worker adapter. “Push handlers configured” means only that the generated worker can receive the versioned AppSurface notification payload. A no-VAPID sample can therefore pass worker/helper readiness while reporting the optional rail as `not-configured`. Icon display, permission timing, subscription storage, recipients, VAPID delivery, and end-to-end browser evidence remain application or follow-up concerns.

For the full option reference, badging privacy and activation boundaries, push-only setup, custom-handler boundary, payload contract, and worker migration guidance, read [PWA install, badging, and push-worker support](../../Web/ForgeTrust.AppSurface.Web/Docs/pwa-install.md).

## Secure manual browser/device PR checklist

Use the sample's UI only for a reviewed manual browser/device proof. This checklist is intentionally separate from the server-known CLI readiness artifact:

- [ ] Use a disposable, access-controlled HTTPS host reachable only by the reviewers' test device(s); never expose a shared development host or real customer data.
- [ ] Run the [config-present preflight](../../Web/ForgeTrust.AppSurface.Web/Docs/pwa-install.md#push-readiness-evidence) and retain only the redacted `pwa-push-readiness.json` or `pwa-all-readiness.json` artifact.
- [ ] On desktop, use a direct user gesture to register the worker, request permission, subscribe, and exercise the host action; record supported, denied, unavailable, and failed outcomes separately.
- [ ] On an installed iOS/iPadOS Home Screen app, repeat the direct-gesture flow. Record unsupported or permission-denied outcomes without treating them as server configuration failures.
- [ ] Do not paste VAPID private keys, subscription endpoints, `p256dh`, `auth`, bearer tokens, payloads, or provider responses into issues, logs, screenshots, or artifacts.
- [ ] Mark browser receipt, notification display, click behavior, and provider delivery as manual observations only; they are not claims made by `appsurface pwa verify`.
- [ ] Set an explicit expiry for the disposable host and its access credentials, revoke access after the proof, remove temporary subscriptions and test data, and delete local artifacts that contain anything beyond the redacted verifier JSON.

## Browser-State Tests

The Node VM tests execute the exact static scripts served by the example and cover registration and badging proof-card states, exact worker matching, local digit-string validation, pending locking, canonical in-app state, duplicate-click containment, and the absence of permission or subscription calls:

```bash
node --test examples/web-pwa-install/test/*.test.mjs
```
