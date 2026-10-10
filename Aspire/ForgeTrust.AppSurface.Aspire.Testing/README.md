# ForgeTrust.AppSurface.Aspire.Testing

Deterministic typed testing for AppSurface Aspire profiles.

## Choose This Package

Use this package when an AppHost enters through [`AspireApp<TModule>.RunAsync(args)`](../ForgeTrust.AppSurface.Aspire/README.md#minimal-shape) and an [`AspireProfile`](../ForgeTrust.AppSurface.Aspire/README.md#minimal-shape) selects the resource graph after asynchronous [CliFx](https://github.com/Tyrrrz/CliFx) dispatch. The package resolves that profile through the same AppSurface module and component path used at runtime, then returns a normal configurable Aspire testing builder without invoking the AppHost entry point.

Do not use it as a replacement for [Aspire's native testing API](https://learn.microsoft.com/dotnet/aspire/testing/overview). If an AppHost calls [`DistributedApplication.CreateBuilder(args)`](https://learn.microsoft.com/dotnet/api/aspire.hosting.distributedapplication.createbuilder) directly, use [`DistributedApplicationTestingBuilder.CreateAsync<TAppHost>()`](https://learn.microsoft.com/dotnet/api/aspire.hosting.testing.distributedapplicationtestingbuilder) instead.

## Compatibility

Version 1 of this preview is compiled and tested against `Aspire.Hosting` and `Aspire.Hosting.Testing` **13.4.4**, which is the package's minimum Aspire version. Consumers may select a later Aspire version without a NuGet exact-version conflict, but that combination is not AppSurface-verified. Advancing Aspire should include re-verifying the complete delegated `IDistributedApplicationTestingBuilder` surface, packed-package consumer compilation, failed-build cleanup, and build/start/disposal integration proof.

Aspire 13.4.4 does not expose or dispose the partial root service provider when host construction fails after provider creation. Immediately before building, this package decorates the verified singleton `IHost` factory so it can capture that provider and dispose it on a non-process-fatal build failure. A successful build transfers ownership unchanged to the returned `DistributedApplication`. If a consumer-selected Aspire version changes that registration shape, AppSurface emits a `System.Diagnostics.Trace` warning and continues through Aspire without the additional failed-build cleanup. This preserves potentially compatible builds while making the cleanup limitation explicit.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

This AppHost-oriented package follows the coordinated AppSurface release policy.
Before using a prerelease build in an AppHost, development, or test environment,
check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md) for publication status,
compatibility guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Configured Graph-Only Proof

Use `CreateConfiguredAsync` when one test needs to choose constructor-visible profile inputs or adjust the Aspire graph before composition. The public options type is `ForgeTrust.AppSurface.Aspire.Testing.AppSurfaceAspireTestingOptions`. The public entry point is `Task<AppSurfaceAspireProfileTestingBuilder> AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<TAppHost, TModule, TProfile>(AppSurfaceAspireTestingOptions options, CancellationToken cancellationToken = default)`. It returns an inspectable graph without building or starting an Aspire application. The [complete portable program](../ForgeTrust.AppSurface.Aspire.Testing.Tests/Fixtures/PortableConsumer/Program.cs.fixture) contains every using directive, module, profile, immutable scenario, component, probe, assertion helper, and entry point; its matching [project template](../ForgeTrust.AppSurface.Aspire.Testing.Tests/Fixtures/PortableConsumer/Consumer.csproj.template) records the tested package baseline. Read and copy the fixture as a whole when you want the complete example rather than assembling a partial snippet.

### Prepared project and version selection

Use the .NET 10 SDK and a `net10.0` test project. Add a project reference from the test project to the real AppHost project so the generated public `Projects.*` marker is available alongside the public module and command-decorated profile. For example:

```xml
<ItemGroup>
  <ProjectReference Include="../src/MyProduct.AppHost/MyProduct.AppHost.csproj" />
</ItemGroup>
```

In the actual consumer call, pass the AppHost's generated marker in place of the fixture's local `ConsumerAppHost` stand-in. The marker, module, and profile must be public, closed types in the same AppHost assembly. See the [AppHost project-reference guidance](../ForgeTrust.AppSurface.Aspire/README.md#apphost-project-references) for the AppHost-side library/resource distinction.

The portable template targets `net10.0` and uses Aspire.Hosting, Aspire.Hosting.AppHost, and Aspire.Hosting.Testing 13.4.4 with Microsoft.Extensions.Hosting and Microsoft.Extensions.DependencyInjection 10.0.8. That is the documented fixture baseline, not a request to upgrade an existing consumer. Select an exact published AppSurface package version through the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md), following the Release Guidance above; replace the template's `{{PACKAGE_VERSION}}` placeholder with that version rather than using a floating version.

### What the portable fixture proves

The fixture uses an immutable `ConsumerScenario` record. `ConfigureActivationServices` replaces the module's default scenario and supplies the probe before the `ConsumerProfile` constructor runs. `ConfigureActivationConfiguration` supplies a value the profile constructor and component can observe. After profile activation, `ConfigureAspireBuilder` customizes the same builder that profile composition receives. The probe checks that the selected scenario reached the constructor, the Aspire hook ran once before composition, and `await using` releases the activation probe once. The fixture declares the returned builder with `await using`, so leaving the graph-only scope releases the activation host; when you also build an application, declare it after the builder so reverse declaration-order cleanup disposes the application first, as shown in the [Build/Start/Probe example](#build-start-probe-dispose).

The test evaluates the resource's environment annotations through [`ExecutionConfigurationBuilder`](https://github.com/microsoft/aspire/blob/v13.4.4/src/Aspire.Hosting/ApplicationModel/ExecutionConfigurationBuilder.cs) using the builder's Run execution context and an explicit `NullLogger.Instance`. The logger prevents graph-only evaluation from looking up a resource logger in an application provider that has not been built. It does not call `Build` or `BuildAsync`, start a resource, contact Stripe, require Docker, or use credentials. The graph contains `skoolit-web` for both scenario values, with these exact child settings:

| Child setting | Receipts enabled | Receipts disabled |
| --- | --- | --- |
| `Skoolit__Inbox__MonitoringReceipt__Enabled` | `true` | `false` |
| `Skoolit__Inbox__MonitoringReceipt__Cohort` | `SeededInternal` | `Disabled` |
| `Skoolit__Inbox__MonitoringReceipt__SeededInternalHouseholdIds__0` | `household_dev_billing` | empty string |

The fixture also verifies that the scenario selection does not change any of the four `SKOOLIT_QA_*` process variables. The fresh packed consumer test compiles and runs the complete fixture with warnings treated as errors. Its output marker is:

```text
PORTABLE_GRAPH_PROOF SkoolitSourceCommit=a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e ProviderAssembly=Microsoft.Extensions.DependencyInjection Aspire=13.4.4 Hosting=10.0.8 receiptsOn=true/SeededInternal/household_dev_billing receiptsOff=false/Disabled/
```

For the Skoolit #866 consumer scenario, keep all four selectors in one consumer-owned immutable per-call value and inject it through `ConfigureActivationServices`. Preserve the existing parsing contract:

| Selector | Selection semantics |
| --- | --- |
| `SKOOLIT_QA_FORWARDING_SENDER_SCENARIO` | Trim input; absent, blank, or `inactive` selects Inactive; `unavailable` selects Unavailable; every other value is rejected. Product forwarding stays enabled for Inactive and disabled for Unavailable. |
| `SKOOLIT_QA_MONITORING_RECEIPT_ENABLED` | Only case-insensitive `true` enables receipts; absent, `1`, and all other values select false. |
| `SKOOLIT_QA_SOURCE_ONLY_EVIDENCE` | Enabled unless the value is exactly `0`; absent, blank, `false`, and `1` remain enabled. Preserve the source-only flags and `source-outcomes-v6` capability annotations. |
| `SKOOLIT_QA_STRIPE_SANDBOX_ENABLED` | Case-insensitive `true` or exact `1` enables the sandbox; absent and other values select false. Opt-in retains the existing test-mode configuration resolution through the optional local-secret provider. |

The consumer's QA fixture should supply an explicit scenario before constructor activation so graph generation does not reread ambient selector values. Keep direct-run fallback separate: snapshot the existing four selector values once when its component is constructed, then use that immutable snapshot during generation. The graph-only receipt proof does not start a sandbox or make a live Stripe call.

### Hook order, provider boundary, and lifecycle

| Order | Phase | What is available |
| --- | --- | --- |
| 1 | Module configuration | AppHost module configuration callbacks and normal framework defaults run first. |
| 2 | `ConfigureActivationConfiguration` | Runs after module configuration callbacks and before activation configuration is built and profile dependencies are resolved. Add constructor-visible configuration here. |
| 3 | Module/framework service registrations, then `ConfigureActivationServices` | Runs after module and framework registrations and before the selected activation provider is built. Inject immutable scenarios or constructor dependencies here. |
| 4 | Profile activation | The selected activation provider constructs the profile and its constructor-injected components. |
| 5 | Aspire builder creation, then `ConfigureAspireBuilder` | Runs after profile activation and before composition. The hook and composition receive the same `Preparing` wrapper. |
| 6 | Profile composition | Components resolve and add resources to the graph; successful factory return changes the wrapper to Active. |
| 7 | Caller-owned build | After the factory returns, inspect or customize the graph, then call `BuildAsync` once only if an application is needed. Start and probe are later, optional application-lifecycle steps. |

`AppSurfaceAspireTestingOptions` exposes three nullable init-only synchronous delegates:

| Option property | Delegate type | Phase |
| --- | --- | --- |
| `ConfigureActivationConfiguration` | `Action<IConfigurationBuilder>` | After module configuration, before activation configuration is built. |
| `ConfigureActivationServices` | `Action<IServiceCollection>` | After module/framework service registrations, before the activation provider and profile constructor. |
| `ConfigureAspireBuilder` | `Action<IDistributedApplicationBuilder>` | After profile activation, before graph composition. |

Use the existing [`CreateAsync` lifecycle](#build-start-probe-dispose) when module registrations and current profile inputs are enough. Use `CreateConfiguredAsync` when a per-call value or configuration must reach constructors, or when a graph change must happen before composition. `AppSurfaceAspireTestingOptions` is required and non-null; each init-only callback defaults to null. The factory snapshots delegate references at entry and invokes each supplied callback at most once when its phase is reached. A failure or cancellation stops later phases; callbacks are never retried. Passing null options throws `ArgumentNullException` before cancellation or type validation; null callbacks simply skip their hook.

The activation service collection/provider and Aspire application's service collection/provider are distinct. `ConfigureActivationServices` runs before profile construction. The returned builder's `Services` property configures the later Aspire application and cannot change an already-constructed profile. The selected activation service-provider factory and provider identity are preserved. A later `ConfigureContainer` registration, including an Autofac module, follows that container's precedence and may win over an earlier service-collection descriptor; the activation hook is not a universal final override.

Hooks are synchronous. Cancellation is checked around callbacks and phase transitions, but cannot interrupt a callback already running; a callback-thrown `OperationCanceledException` remains unwrapped. Nonfatal phase failures identify the phase and participating types, preserve the original exception as `InnerException`, and omit configuration values. Capture immutable values. Do not use `async void`, call `BuildServiceProvider`, run the AppHost entry point, start the activation host or an Aspire resource, or use these hooks to replace the module's required parameterless constructor or CLI member binding.

During configured factory preparation, the Aspire hook and profile composition share one `Preparing` wrapper; its configuration, services, resources, and resource-builder creation are available for graph customization. Concrete `BuildAsync` and `Dispose`/`DisposeAsync`, `IDistributedApplicationTestingBuilder.BuildAsync`, `IDistributedApplicationBuilder.Build`, and the `IDisposable`/`IAsyncDisposable` disposal routes are rejected with `InvalidOperationException`. The same rejection applies through the owning `IDistributedApplicationBuilder` reached from a guarded resource builder's `ApplicationBuilder` property. Rejection does not transfer or release ownership. A callback may catch that rejection and continue; if it escapes the callback or composition, factory cleanup aborts the unbuilt graph and activation, and captured preparing references become terminal. On success, the returned builder is Active and caller-owned for its single explicit build; factory creation itself never builds or starts the application. After a successful build, resource builders expose the already-built native Aspire context so deferred SDK callbacks, including normal project argument configuration, can still read it. Direct wrapper access remains rejected after build, and resource-builder mutation remains unsupported; after wrapper disposal, guarded resource backreferences reject access again.

Configured activation uses an internal Core host-builder factory seam to observe ownership from the beginning. Idempotent leases cover configuration providers returned by source construction and reload-token registrations; an idempotent `IHost` factory decoration avoids recursive disposal while preserving the selected raw service-provider object. As a result, configured activation's `IConfigurationRoot.Providers` may expose private proxy providers. Do not depend on their concrete types or use that collection to infer original source identity. Configuration roots explicitly built inside callbacks and allocations a custom source/provider factory creates but does not return remain caller-owned. Host-owned returned resources follow host disposal; factory cleanup preserves the original creation failure if secondary cleanup also fails. Configuration roots retain their usual synchronous disposal policy. Providers that support only `IAsyncDisposable` remain in the activation owner until its awaited cleanup drains them once.

## Prepared-Project Walk

The aspirational onboarding target is 2–5 minutes for a project that already has the .NET 10 SDK, its AppHost/test project, and a selected package version. The packed test compiles and executes the example; the human walkthrough target remains unmeasured. When the maintainer performs the walk, record prerequisites, whether package restore was cold or warm, human elapsed time, and whether the constructor-to-graph result was understood; report restore/build automation time separately. First-time SDK setup and AppHost generation remain unmeasured and outside this prepared-project target.

The Skoolit consumer migration still needs a named QA acceptance owner, a target consumer branch, the fresh packed feed, and maintainer QA. This portable package fixture is not evidence that the consumer migration is complete or unblocked.

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
- `Dispose` and `DisposeAsync` are idempotent, and independent concurrent calls join the same cleanup. A recursive call from a service already being disposed returns without waiting on that same cleanup flow. Disposal during an in-flight build is rejected; retry after the build task settles. After a successful build, builder disposal provides a fallback that disposes the application before activation services.
- Cached `Services`, `Configuration`, or resource collections cannot be invalidated. Mutating cached objects after build or disposal is unsupported caller behavior.

Factory validation, activation, composition, and cancellation failures remain primary even when factory-owned activation cleanup throws, including when that secondary cleanup failure is process-fatal. During `BuildAsync`, non-fatal cleanup does not replace the build or cancellation failure, while process-fatal cleanup propagates immediately. Explicit disposal without an earlier failure propagates its cleanup exception.

The failed-build provider capture is intentionally package-internal and tied to the Aspire registration shape verified by this release. Do not copy it into application code, schedule delayed cleanup, or dispose cached `Services`: those approaches cannot distinguish a failed partial build from a live application and can race successful ownership transfer. When overriding Aspire, configure an appropriate `TraceListener` if your test runner does not surface trace warnings and include failed-build cleanup in your compatibility proof.

## Troubleshooting

| What you see | Cause | Fix |
| --- | --- | --- |
| AppHost marker validation failed | `TAppHost` is not the generated marker or `ProjectPath` is invalid. | Pass the AppHost project's generated `Projects.*` type and ensure its project directory exists. |
| Type validation failed | Marker, module, and profile are not public, closed, concrete where required, and co-located. | Keep all three public types in the AppHost assembly and add `[Command]` to the profile. |
| Command-bound members are rejected | The profile relies on CliFx option or positional binding. | Use constructor-injected configuration or `PassThroughArgs`; typed member binding is not supported in v1. |
| Activation configuration failed | The configuration callback failed before profile construction. | Check the overlay and configuration sources in `ConfigureActivationConfiguration`; constructor-visible values belong in activation configuration, not the later Aspire `Configuration` property. |
| Activation services failed | The activation service hook could not complete registration. | Confirm module and framework registrations first; register constructor inputs without building a second provider. Check selected provider and later container-registration precedence. |
| Profile activation failed | DI could not create the profile or one of its dependencies. | Register the missing dependency through the AppHost module or `ConfigureActivationServices` and keep concrete components discoverable in the AppHost assembly. |
| Aspire builder failed | Aspire builder creation or `ConfigureAspireBuilder` failed before composition. | Check `PassThroughArgs`, the generated marker/project directory, and the hook's graph customization. |
| Profile composition failed | Component enumeration, resolution, or graph generation failed. | Inspect profile dependencies and components; select test-varying graph behavior through the immutable activation scenario. |
| Preparing lifecycle call rejected | Build or disposal was attempted before configured preparation finished. | Catch the expected rejection only when preparation should continue, or wait until the factory returns before building or disposing. An escaping rejection aborts the factory. |
| Cancellation arrives during a callback | A synchronous callback is still running after cancellation was requested. | Keep hooks short and synchronous. Cancellation is observed at phase boundaries and does not preempt a callback. |
| Builder already built or faulted | The one-build contract was violated. | Create a fresh testing builder for each application instance. |
| Entry point exited without building | Aspire's entry-point factory observed asynchronous profile dispatch before graph construction. | Use this typed factory; it composes the profile directly and never executes the entry point. |

---
[📂 Back to Aspire List](../README.md) | [🏠 Back to Root](../../README.md)
