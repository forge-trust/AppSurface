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

### Deterministic freshness transitions

The existing `Memo(IMemoryCache)` and `Memo(IMemoryCache, TimeSpan failureCacheDuration)` constructors use `TimeProvider.System`. The explicit `Memo(IMemoryCache, TimeSpan failureCacheDuration, TimeProvider)` overload lets a host or test control the timestamps used to store and compare fresh/stale windows. The cache and clock must be nonnull, and the failure duration must be positive. It borrows both dependencies and retains the existing cache policies and cancellation semantics.

Use the explicit overload when verifying phase boundaries or coordinating an application's existing clock. Configure the supplied `MemoryCache` with an `ISystemClock` adapter backed by that **same** provider through `MemoryCacheOptions.Clock`. `Memo` does not replace the cache's expiration clock. Two independent clocks can evict an entry while Memo still considers it stale, or classify an entry as expired while its cache still retains it. Move a custom clock forward only; ordinary constructors require no adapter or configuration change.

Clock advancement changes logical freshness, not task completion. Hold/release the actual factory independently, join caller tasks, and verify both a stale caller's immediate old value and an expired caller's awaited failure. Increasing a delay or TTL does not establish which phase a scheduled continuation observed. The [Docs snapshot regression control](../../Web/ForgeTrust.AppSurface.Docs.Tests/RecommendationSnapshotTests.cs) uses the real Memo and MemoryCache with a shared clock to check these cases without a wall-clock race.

[`Memo.DrainBackgroundRefreshesAsync(CancellationToken)`](Memo.cs) joins a snapshot of original refresh tasks already registered by completed stale reads. Completion means those tasks ended, including failed or cancelled refreshes; it does not mean a refresh succeeded or supply a cached value. Stale-refresh errors remain observed, while expired caller refreshes continue to propagate their own failures. Caller cancellation stops only the drain wait; accepted factories remain owned by their original tasks. No timeout or fresh TTL is created.

For deterministic observation or graceful shutdown, stop new requests, await already accepted read calls, drain their background snapshot under your existing cancellation/deadline, then dispose Memo and its borrowed cache. Reads accepted later are outside that snapshot, and synchronous `Dispose` does not promise an asynchronous join. The [Memo ownership controls](../ForgeTrust.AppSurface.Caching.Tests/MemoTests.cs) hold a real refresh to verify pending drainage, cancellation without factory cancellation, and publication of the refreshed value after its original join.

- The package builds on `Microsoft.Extensions.Caching.Memory`, so it works well for in-process application caching.
- `CachePolicy.AbsoluteWithStaleWhileRevalidate` returns the stale value immediately during the stale window and starts one background refresh for that cache key. If the background refresh fails, the stale value remains available until the stale window ends.
- Stale-while-revalidate is supported for absolute-expiration policies. Sliding expiration does not have a stable revalidation moment, so use `CachePolicy.AbsoluteWithStaleWhileRevalidate(...)` or `CachePolicy.Absolute(...).WithStaleWhileRevalidate(...)`.
- This package is intentionally lightweight and fits best when you want simple, application-level caching rather than a distributed cache abstraction.
