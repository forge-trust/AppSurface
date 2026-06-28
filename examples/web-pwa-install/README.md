# AppSurface Web PWA Install Proof

This example shows the one-line AppSurface Web PWA install path without adding a separate package.

```bash
dotnet run --project examples/web-pwa-install/WebPwaInstallExample.csproj -- --environment Development --port 5055
dotnet run --project Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj -- pwa verify --url http://localhost:5055
```

## What It Proves

- `WebOptions.Pwa` serves `/manifest.webmanifest`.
- `<appsurface:pwa-head />` emits manifest, theme, mobile, and icon metadata.
- `/_appsurface/pwa` explains local install posture during development.
- `/service-worker.js` is mapped only because the example explicitly opts into the starter offline strategy.

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
```
<!-- /appsurface:snippet -->

The starter service worker caches only the configured static asset list and offline fallback page. It does not cache POST responses, authenticated routes, arbitrary app navigations, or app data.
