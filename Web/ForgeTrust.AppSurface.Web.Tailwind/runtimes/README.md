# ForgeTrust.AppSurface.Web.Tailwind Runtime Packages

These packages retain the original one-RID native Tailwind standalone executable
artifacts for direct or compatibility-oriented use. They are no longer dependencies of
ForgeTrust.AppSurface.Web.Tailwind's normal installation path.

## Status and supported packages

- ForgeTrust.AppSurface.Web.Tailwind.Runtime.win-x64
- ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-x64
- ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64
- ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64
- ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-arm64

Windows Arm64 uses the win-x64 binary under Windows x64 emulation. Tailwind 4.1.18 does
not publish a native Windows Arm64 standalone executable.

## When to use one

Normal consumers install only ForgeTrust.AppSurface.Web.Tailwind. Its main package
acquires the single build-host executable through its pinned release manifest and
verified cache.

Use a runtime package directly only when an existing specialized packaging workflow
requires a native payload under runtimes/<rid>/native. Direct use is a compatibility
choice: it does not change the main package's host-cache behavior, project-reference
output hygiene, or explicit CLI overrides.

## Maintainer guidance

The runtime projects remain independently packable so published companion artifacts are
not silently deleted, unlisted, or deprecated by the host-scoped delivery change.
Their build targets continue to download and verify the official standalone asset before
packing that direct native payload.

For the default package's cache, offline recovery, digest-manifest trust boundary, and
CI prewarming guidance, see the
[main Tailwind README](../README.md). Do not claim that normal main-package consumers
restore a runtime companion transitively.
