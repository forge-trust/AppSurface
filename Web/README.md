# Web Projects

This directory contains libraries and tools specifically for web application development within the AppSurface ecosystem.

Need install guidance first? Start with the [AppSurface v0.1 package chooser](../packages/README.md), then come back here for the deeper web-surface breakdown.

## Contents

- [**ForgeTrust.AppSurface.Web**](./ForgeTrust.AppSurface.Web/README.md) – The core web bootstrapping library, including [protected preview named deploy canary evaluation with bounded response evidence and fixed completion telemetry](./ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints), conventional browser status pages, and opt-in production 500 pages.
- [**ForgeTrust.AppSurface.Web.OpenApi**](./ForgeTrust.AppSurface.Web.OpenApi/README.md) – Modular OpenAPI generation with development-only endpoint exposure by default.
- [**ForgeTrust.AppSurface.Docs**](./ForgeTrust.AppSurface.Docs/README.md) – Reusable docs package for harvesting and serving repository documentation.
- [**ForgeTrust.AppSurface.Docs.Standalone**](./ForgeTrust.AppSurface.Docs.Standalone/README.md) – AppSurface host for exporting and serving AppSurface Docs.
- [**ForgeTrust.RazorWire**](./ForgeTrust.RazorWire/README.md) – Reactive web components, real-time streaming, CDN-default static export, and convention-based failed form UX.
- [**ForgeTrust.AppSurface.Web.Scalar**](./ForgeTrust.AppSurface.Web.Scalar/README.md) – Scalar API Reference UI integration gated by Scalar and OpenAPI endpoint exposure.
- `ForgeTrust.AppSurface.Web.Tests` – Test suite for web components.

## JavaScript workspace

Browser assets in this directory are managed by the `Web/` pnpm workspace. Run these commands from the repository root:

```bash
pnpm --dir Web install --frozen-lockfile
pnpm --dir Web run assets:typecheck
pnpm --dir Web run assets:test
pnpm --dir Web run assets:build
pnpm --dir Web run assets:verify
```

`ForgeTrust.AppSurface.Docs/assets` owns the TypeScript source for generated Docs browser assets. Generated package outputs remain committed under `ForgeTrust.AppSurface.Docs/wwwroot/docs` because Razor Class Library delivery, embedded fallback resources, route aliases, and static export all depend on those paths. Do not edit generated `wwwroot/docs/search-client.js` or `wwwroot/docs/minisearch.min.js` by hand; edit the source or pinned package version, rebuild, then verify.

`ForgeTrust.RazorWire/assets` owns the TypeScript source for the RazorWire browser runtime and island loader. Generated package outputs remain committed at `ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.js` and `ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.islands.js` so existing static-web-asset paths, embedded fallback resources, and package consumers keep working without migration. Use `pnpm --dir Web run assets:razorwire:typecheck`, `assets:razorwire:test`, `assets:razorwire:build`, and `assets:razorwire:verify` for focused runtime work; see [RazorWire Runtime Contract Pipeline](./ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md) for ownership, diagnostics, and pack-time guard details.

---
[🏠 Back to Root](../README.md)
