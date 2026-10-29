# ForgeTrust.AppSurface.Caching

Caching primitives for AppSurface applications built on top of `Microsoft.Extensions.Caching.Memory`.

## Overview

This package provides a small, focused caching layer for AppSurface modules. It is designed for scenarios where you want consistent memoization behavior, cache policies, and a module you can register into the AppSurface startup pipeline.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->
## Key Types

- **`AppSurfaceCachingModule`**: Registers the package services into the AppSurface module system.
- **`IMemo` / `Memo`**: Memoization helpers for caching computed values and async results.
- **`CachePolicy`**: A simple policy object for configuring expiration and cache behavior.

## Usage

Register the module in your application and inject `IMemo` where you want to cache repeated work:

```csharp
public sealed class MyModule : AppSurfaceCachingModule
{
}
```

Use memoization for expensive or repeated lookups:

```csharp
var result = await memo.GetAsync(
    () => LoadDocsAsync(),
    CachePolicy.Absolute(TimeSpan.FromMinutes(5)));
```

Use stale-while-revalidate for expensive values that should stay responsive after their freshness window expires:

```csharp
var result = await memo.GetAsync(
    () => LoadDocsAsync(),
    CachePolicy.AbsoluteWithStaleWhileRevalidate(
        freshDuration: TimeSpan.FromMinutes(5),
        staleDuration: TimeSpan.FromMinutes(5)));
```

## Notes

- The package builds on `Microsoft.Extensions.Caching.Memory`, so it works well for in-process application caching.
- `CachePolicy.AbsoluteWithStaleWhileRevalidate` returns the stale value immediately during the stale window and starts one background refresh for that cache key. If the background refresh fails, the stale value remains available until the stale window ends.
- Stale-while-revalidate is supported for absolute-expiration policies. Sliding expiration does not have a stable revalidation moment, so use `CachePolicy.AbsoluteWithStaleWhileRevalidate(...)` or `CachePolicy.Absolute(...).WithStaleWhileRevalidate(...)`.
- Public [`Memo`](Memo.cs) constructors use system UTC. Its internal clock constructor is reserved for cache-contract tests: supply a thread-safe UTC clock and configure the backing `MemoryCache` with the same clock. Advance them together to test the existing fresh, stale and failure-expiration boundaries; changing only the storage clock leaves stale-value classification on a different timeline. This seam does not change any duration or expose a public clock-configuration API.
- This package is intentionally lightweight and fits best when you want simple, application-level caching rather than a distributed cache abstraction.
