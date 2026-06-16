# ForgeTrust.AppSurface.Caching

Caching primitives for AppSurface applications built on top of `Microsoft.Extensions.Caching.Memory`.

## Overview

This package provides a small, focused caching layer for AppSurface modules. It is designed for scenarios where you want consistent memoization behavior, cache policies, and a module you can register into the AppSurface startup pipeline.

## Release Guidance

AppSurface publishes coordinated `v0.1.0` release candidates. Before installing this package from a prerelease feed, read the [v0.1.0 RC 4 release note](../../releases/v0.1.0-rc.4.md) for current release risk, migration guidance, and package readiness.

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
- This package is intentionally lightweight and fits best when you want simple, application-level caching rather than a distributed cache abstraction.
