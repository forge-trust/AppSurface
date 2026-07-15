# ForgeTrust.AppSurface.Aspire.Testing

Deterministic typed testing for AppSurface Aspire profiles.

## Choose This Package

Use this package when an AppHost enters through `AspireApp<TModule>.RunAsync(args)` and an `AspireProfile` selects the resource graph after asynchronous CliFx dispatch. The package resolves that profile through the same AppSurface module and component path used at runtime, then returns a normal configurable Aspire testing builder without invoking the AppHost entry point.

Do not use it as a replacement for Aspire's native testing API. If an AppHost calls `DistributedApplication.CreateBuilder(args)` directly, use `DistributedApplicationTestingBuilder.CreateAsync<TAppHost>()` instead.

## Compatibility

Version 1 of this preview is compiled and tested against exactly `Aspire.Hosting` and `Aspire.Hosting.Testing` **13.4.4**. Keep the AppHost SDK and Aspire testing package on that patch line. Advancing Aspire requires an AppSurface package release that re-verifies the complete delegated `IDistributedApplicationTestingBuilder` surface, packed-package consumer compilation, failed-build cleanup, and build/start/disposal integration proof.

Publication is currently blocked by a pinned Aspire 13.4.4 failure-path defect: if Aspire host construction throws after creating its internal service provider, `DistributedApplicationBuilder.Build()` does not expose or dispose that partial host. AppSurface still disposes the profile activation host and preserves the build exception, but it cannot reach Aspire's leaked partial provider. The package remains blocked until the pinned dependency disposes that state or exposes a supported cleanup seam.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for the current publication blocker, compatibility guidance, and readiness.

## Install

After the publication blocker above is cleared, install the coordinated package with:

```bash
dotnet add package ForgeTrust.AppSurface.Aspire.Testing
```

The test project must also reference the AppHost project so its generated `Projects.*` marker, public module, and public profile types are available.

## Build, Start, Probe, Dispose

```csharp
using Aspire.Hosting.Testing;
using AspireAppHostExample;
using ForgeTrust.AppSurface.Aspire.Testing;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

await using var builder = await AppSurfaceAspireTestingBuilder.CreateAsync<
    Projects.AspireAppHostExample,
    ExampleModule,
    QaProfile>(timeout.Token);

builder.Services.AddLogging();

await using var application = await builder.BuildAsync(timeout.Token);
await application.StartAsync(timeout.Token);
await application.ResourceNotifications.WaitForResourceHealthyAsync("web", timeout.Token);

using var client = application.CreateHttpClient("web", "http");
using var response = await client.GetAsync("/", timeout.Token);
response.EnsureSuccessStatusCode();
```

The declaration order is deliberate: C# disposes `application` before `builder`. The application owns started Aspire resources; the builder owns the unstarted AppSurface activation host that supplies the selected profile and its constructor dependencies. The builder retains fallback ownership of the built application, so disposing the builder first disposes and awaits the application before releasing activation services. Explicit application-first disposal remains preferred because it keeps stop and disposal failures at the application call site.

`CreateAsync` pins activation and Aspire identity to `TAppHost`. AppSurface activation receives empty arguments so no command or hosted service runs. The selected profile's `PassThroughArgs` become Aspire builder arguments. The generated marker's public `ProjectPath` supplies the AppHost directory, and the dashboard is disabled for the test builder by default.

## Profile Contract

The marker, module, and profile must be public, closed types in the same AppHost assembly. The marker must be the generated `Projects.*` type with one public static readable `string ProjectPath`; the module must be a concrete `IAppSurfaceHostModule`; and the profile must be a concrete `AspireProfile` with CliFx `[Command]` metadata.

Typed tests support constructor-injected services, `PassThroughArgs`, `GetDependencies()`, and `GetComponents()`. They intentionally reject profiles containing `[CommandOption]` or `[CommandParameter]` properties because no CliFx binding phase runs. Move test-varying graph choices into constructor services or known Aspire pass-through arguments. String profile selection, cross-assembly profile discovery, readiness policy, and a higher-level owned fixture are deferred.

## Builder Lifecycle

- Customize `Configuration`, `Services`, and resources before `BuildAsync`.
- Call `BuildAsync` exactly once. Concurrent or repeated builds fail with `InvalidOperationException`.
- After a successful build, every builder member is rejected; inspect or customize the graph before `BuildAsync` and use the returned application afterward.
- A failed or cancelled build is terminal and releases activation services. Cancellation observed after Aspire builds an application disposes that unreturned application first.
- `Dispose` and `DisposeAsync` are idempotent, and concurrent calls join the same cleanup. Disposal during an in-flight build is rejected; retry after the build task settles. After a successful build, builder disposal provides a fallback that disposes the application before activation services.
- Cached `Services`, `Configuration`, or resource collections cannot be invalidated. Mutating cached objects after build or disposal is unsupported caller behavior.

Factory validation, activation, composition, build, and cancellation failures remain the primary exception. Non-fatal cleanup failures do not replace that failure; process-fatal exceptions propagate immediately. Explicit disposal without another primary failure propagates its cleanup exception.

## Troubleshooting

| What you see | Cause | Fix |
| --- | --- | --- |
| AppHost marker validation failed | `TAppHost` is not the generated marker or `ProjectPath` is invalid. | Pass the AppHost project's generated `Projects.*` type and ensure its project directory exists. |
| Type validation failed | Marker, module, and profile are not public, closed, concrete where required, and co-located. | Keep all three public types in the AppHost assembly and add `[Command]` to the profile. |
| Command-bound members are rejected | The profile relies on CliFx option or positional binding. | Use constructor-injected configuration or `PassThroughArgs`; typed member binding is not supported in v1. |
| Profile activation failed | DI could not create the profile or one of its dependencies. | Register the missing dependency through the AppHost module and keep concrete components discoverable in the AppHost assembly. |
| Builder already built or faulted | The one-build contract was violated. | Create a fresh testing builder for each application instance. |
| Entry point exited without building | Aspire's entry-point factory observed asynchronous profile dispatch before graph construction. | Use this typed factory; it composes the profile directly and never executes the entry point. |

---
[📂 Back to Aspire List](../README.md) | [🏠 Back to Root](../../README.md)
