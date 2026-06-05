# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.2`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- RazorWire CLI static export now recognizes the inline autoload marker that AppSurface Docs outlines emit for page navigation and materializes `/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js` in CDN exports. AppSurface Docs now mirrors the shared `razorwire:page-nav:active-change` event instead of owning duplicate outline scroll/hash tracking, so published docs rely on the same navigation runtime as package consumers.
- AppSurface Web browser status-page re-execution now preserves the original `401`, `403`, or `404` response status while rendering conventional browser recovery pages, so standalone docs 404 recovery keeps `NotFound` semantics across supported runtimes.
- Package consumers can now follow a package-first AppSurface Web quickstart from a fresh `dotnet new web` app, install `ForgeTrust.AppSurface.Web`, and verify the first route before choosing optional modules.
- AppSurface Docs Turbo-frame navigation now preserves cross-page heading fragments, so generated package chooser links land directly on the package-first quickstart section instead of the top of the target page.

### CI and package validation

- CI now includes a measured NuGet cache rollout for selected build, docs export, and code-quality jobs while keeping package-gate restores isolated.
- PackageIndex verification now ignores hidden local cache directories such as `.pnpm-store` and workspace `.nuget/packages` so local cache contents do not require package manifest entries.
- Fast non-package CI that does not compile Tailwind-consuming projects can now skip Tailwind runtime binary resolution while package validation and release paths force binary resolution on and fail before creating an empty runtime package.

### RazorWire package guidance

- RazorWire page navigation now keeps the active same-page link perceivable inside visible overflowing vertical nav surfaces, using the nav container's `scroll-padding-block` / `scroll-padding-top` / `scroll-padding-bottom` values as reveal insets while preserving document scroll.

### AppSurface Docs product example

- AppSurface Docs JavaScript public API harvesting now recognizes documented CSS property hooks such as RazorWire page-navigation scroll-padding contracts, so generated API references can describe browser styling insets without malformed-doclet diagnostics.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
