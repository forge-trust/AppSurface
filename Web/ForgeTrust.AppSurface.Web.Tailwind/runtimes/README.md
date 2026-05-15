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

During build and pack, each runtime project downloads the official Tailwind checksum file and matching standalone binary, then verifies the binary hash before adding it to the package. The download step retries transient network failures by default using `TailwindDownloadRetries` and `TailwindDownloadRetryDelayMilliseconds`. Keep those defaults for normal CI; tune them only when your build environment needs more patience for GitHub release asset downloads or intentionally wants fail-fast behavior.
