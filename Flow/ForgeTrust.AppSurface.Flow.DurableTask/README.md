# ForgeTrust.AppSurface.Flow.DurableTask

`ForgeTrust.AppSurface.Flow.DurableTask` maps AppSurface Flow definitions into durable orchestration decisions.

## Release Guidance

AppSurface has cut the first coordinated `v0.1.0` release candidate. Before installing this package from a prerelease feed, read the [v0.1.0 RC 1 release note](../../releases/v0.1.0-rc.1.md) for current release risk, migration guidance, and package readiness.

## What It Includes

- Passive `AppSurfaceFlowDurableTaskModule` that depends on `AppSurfaceFlowModule`.
- `IDurableTaskFlowRunner<TContext>` for evaluating one flow node and returning a durable decision.
- `IDurableTaskFlowClient<TContext>` and `IFlowResumeAuthorizer` for authorizing external resume events before hosts raise Durable Task events.
- `DurableTaskFlowDecision<TContext>` and `DurableTaskFlowStep<TContext>` for schedule, wait, completion, fault, timeout, and late-event behavior.
- `FlowContextSerializationValidator` with a `System.Text.Json` serializer implementation.
- `FlowRetryPolicy` carried on schedule decisions so hosts can translate retry intent into Durable Task retry options.

## What It Does Not Include

- Durable Task worker or client hosting setup.
- Storage provider registration.
- ASP.NET Core resume endpoints or authentication handlers.
- Semantic Kernel.

## Durable Boundary

Durable Task owns persistence, replay, timers, and external event delivery. This package owns the AppSurface mapping contract around those host responsibilities:

1. Resolve the typed flow definition by flow id and version.
2. Validate that the typed context can round-trip through the configured serializer.
3. Execute the current node once.
4. Map the node outcome to a durable decision.

`FlowWait<TContext>` becomes `WaitForExternalEvent` with optional timeout metadata. A host should race that external event against its durable timer. When an event arrives after the timer branch already won, `ResumeAsync` returns `IgnoreLateEvent` by default.

`AppSurfaceFlowDurableTaskOptions.NodeRetryPolicy` can attach one retry policy to scheduled node work. The adapter does not execute retries itself; the Durable Task host translates the policy into its worker/client retry options.

## Authorization

The default `DenyAllFlowResumeAuthorizer` rejects every resume event. Hosts must register their own `IFlowResumeAuthorizer` before exposing HTTP endpoints, queues, webhooks, or browser actions that resume durable flows.

```csharp
services.AddSingleton<IFlowResumeAuthorizer, MyResumeAuthorizer>();
```

Authorization should consider the flow id, version, durable instance id, waiting node id, event name, caller identity, and any app-specific metadata. Instance ids and event names are not authorization.

## Pitfalls

- Do not mutate Durable Task host state from `AppSurfaceFlowDurableTaskModule`; it is intentionally passive.
- Do not treat late events as success. The default behavior ignores them because delayed external events are expected in timer races.
- Do not register Semantic Kernel in this package. Agentic flow authoring belongs in samples or a future package, not the Durable Task adapter.
- Validate context serialization before starting durable instances so replay failures happen during local tests or startup verification, not halfway through a production process.
