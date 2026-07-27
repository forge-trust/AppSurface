# ForgeTrust.AppSurface.Aspire.Testing

Deterministic typed testing for AppSurface Aspire profiles.

## Choose This Package

Use this package when an AppHost enters through [`AspireApp<TModule>.RunAsync(args)`](../ForgeTrust.AppSurface.Aspire/README.md#minimal-shape) and an [`AspireProfile`](../ForgeTrust.AppSurface.Aspire/README.md#minimal-shape) selects the resource graph after asynchronous [CliFx](https://github.com/Tyrrrz/CliFx) dispatch. The package resolves that profile through the same AppSurface module and component path used at runtime, then returns a normal configurable Aspire testing builder without invoking the AppHost entry point.

Do not use it as a replacement for [Aspire's native testing API](https://learn.microsoft.com/dotnet/aspire/testing/overview). If an AppHost calls [`DistributedApplication.CreateBuilder(args)`](https://learn.microsoft.com/dotnet/api/aspire.hosting.distributedapplication.createbuilder) directly, use [`DistributedApplicationTestingBuilder.CreateAsync<TAppHost>()`](https://learn.microsoft.com/dotnet/api/aspire.hosting.testing.distributedapplicationtestingbuilder) instead.

## Compatibility

Version 1 of this preview is compiled and tested against `Aspire.Hosting` and `Aspire.Hosting.Testing` **13.4.4**, which is the package's minimum Aspire version. Consumers may select a later Aspire version without a NuGet exact-version conflict, but that combination is not AppSurface-verified. Advancing Aspire should include re-verifying the complete delegated `IDistributedApplicationTestingBuilder` surface, packed-package consumer compilation, failed-build cleanup, and build/start/disposal integration proof.

Aspire 13.4.4 does not expose or dispose the partial root service provider when host construction fails after provider creation. Immediately before building, this package decorates the verified singleton `IHost` factory so it can capture that provider and dispose it on a non-process-fatal build failure. A successful build transfers ownership unchanged to the returned `DistributedApplication`. If a consumer-selected Aspire version changes that registration shape, AppSurface emits a `System.Diagnostics.Trace` warning and continues through Aspire without the additional failed-build cleanup. This preserves potentially compatible builds while making the cleanup limitation explicit.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for the current publication status, compatibility guidance, and readiness.

## Install

Install the coordinated package with:

```bash
dotnet add package ForgeTrust.AppSurface.Aspire.Testing --prerelease
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
- A failed or cancelled build is terminal. A non-process-fatal failure releases activation services immediately and, if Aspire created its root provider before host construction failed, disposes that provider first. After catching a process-fatal build failure, the caller must dispose the builder to make a best-effort provider cleanup before activation cleanup; an explicit cleanup failure is then propagated. Cancellation observed after Aspire builds an application disposes that unreturned application first.
- `Dispose` and `DisposeAsync` are idempotent, and concurrent calls join the same cleanup. Disposal during an in-flight build is rejected; retry after the build task settles. After a successful build, builder disposal provides a fallback that disposes the application before activation services.
- Cached `Services`, `Configuration`, or resource collections cannot be invalidated. Mutating cached objects after build or disposal is unsupported caller behavior.

Factory validation, activation, composition, and cancellation failures remain primary even when factory-owned activation cleanup throws, including when that secondary cleanup failure is process-fatal. During `BuildAsync`, non-fatal cleanup does not replace the build or cancellation failure, while process-fatal cleanup propagates immediately. Explicit disposal without an earlier failure propagates its cleanup exception.

The failed-build provider capture is intentionally package-internal and tied to the Aspire registration shape verified by this release. Do not copy it into application code, schedule delayed cleanup, or dispose cached `Services`: those approaches cannot distinguish a failed partial build from a live application and can race successful ownership transfer. When overriding Aspire, configure an appropriate `TraceListener` if your test runner does not surface trace warnings and include failed-build cleanup in your compatibility proof.

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
