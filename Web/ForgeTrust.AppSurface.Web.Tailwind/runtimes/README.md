# ForgeTrust.AppSurface.Web.Tailwind Runtime Packages

Platform-specific runtime packages that carry the official standalone Tailwind CLI binaries used by `ForgeTrust.AppSurface.Web.Tailwind`.

## Overview

These packages exist so the main Tailwind package can depend on RID-specific binaries without bundling every executable into one package. Each runtime package contains the native Tailwind CLI for a single supported platform.

## Supported Packages

- `ForgeTrust.AppSurface.Web.Tailwind.Runtime.win-x64`
- `ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-x64`
- `ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64`
- `ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64`
- `ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-arm64`

> Note: Windows Arm64 hosts are supported via x64 emulation using `ForgeTrust.AppSurface.Web.Tailwind.Runtime.win-x64`. Tailwind `v4.1.18` does not publish a native Windows Arm64 standalone binary.

## Usage

Most consumers should install only `ForgeTrust.AppSurface.Web.Tailwind`. These runtime packages are implementation-detail dependencies that are restored automatically through the main package.

Install one directly only if you have a specialized packaging or build scenario that requires explicit control over the Tailwind CLI binary payload.

During build and pack, each runtime project downloads the official Tailwind checksum file and matching standalone binary, then verifies the binary hash before adding it to the package. The download step uses a shared user-level cache by default so local Git worktrees do not each carry their own full set of Tailwind executables. The default cache root is selected from `$XDG_CACHE_HOME/forgetrust/appsurface/tailwind`, `%LOCALAPPDATA%/ForgeTrust/AppSurface/Tailwind`, `$HOME/.cache/forgetrust/appsurface/tailwind`, or `%USERPROFILE%/.cache/forgetrust/appsurface/tailwind`, in that order. Set `TailwindDownloadCacheRoot` when CI should use a specific persistent cache volume or when a one-off build needs an isolated cache.

The download step retries transient network failures by default using `TailwindDownloadRetries` and `TailwindDownloadRetryDelayMilliseconds`. Keep those defaults for normal CI; tune them only when your build environment needs more patience for GitHub release asset downloads or intentionally wants fail-fast behavior.

## Maintainer CI behavior

`TailwindRuntimeBinaryResolutionEnabled` is an internal maintainer switch for repository CI. It defaults to `true`. Non-package restore/build/test jobs may set it to `false` to skip runtime binary download and checksum work only when they do not compile Tailwind-consuming projects, unless they also intentionally set `TailwindEnabled=false`. Runtime package creation fails when binary resolution is disabled. Package validation, release packaging, and manual `dotnet pack` commands must use `/p:TailwindRuntimeBinaryResolutionEnabled=true`.

Use `dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify-packages --package-version <prerelease>` as the primary package proof path. That workflow forces runtime binary resolution on before packing. Raw `dotnet pack` commands are advanced/manual and must pass `/p:TailwindRuntimeBinaryResolutionEnabled=true`.

Supported packaging overrides are `TailwindBaseUrl`, `TailwindSumsUrl`, `TailwindDownloadRetries`, and `TailwindDownloadRetryDelayMilliseconds`. Offline runtime packaging is not supported by setting `TailwindRuntimeBinaryResolutionEnabled=false`; use a reachable mirror URL and checksum URL, or retry package validation after the upstream download path recovers.

See [`eng/ci-critical-path.md`](../../../eng/ci-critical-path.md#tailwind-runtime-binary-resolution-in-ci) for the workflow matrix and copy-paste CI examples.
