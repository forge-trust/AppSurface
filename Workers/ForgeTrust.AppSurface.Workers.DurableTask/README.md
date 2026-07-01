# ForgeTrust.AppSurface.Workers.DurableTask

`ForgeTrust.AppSurface.Workers.DurableTask` maps AppSurface durable worker contracts into Durable Task-facing decisions.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

## What It Includes

- Passive `AppSurfaceWorkersDurableTaskModule` that depends on `AppSurfaceWorkersModule` and `AppSurfaceFlowDurableTaskModule`.
- `IDurableTaskWorkerChainRunner<TWork,TResult,TProjection>` for mapping claim, completion, projection repair, wait, timeout, stale-signal, and retry outcomes to Durable Task-facing decisions.
- `DurableTaskWorkerDecision<TWork,TResult,TProjection>` and `DurableTaskWorkerDecisionKind` for schedule-executor, wait, repair-projection, complete, fault, late-signal, retry, and timeout behavior.
- `AppSurfaceWorkersDurableTaskOptions` for attaching retry intent to executor and projection-repair scheduling decisions.

## What It Does Not Include

- Durable Task worker or client hosting setup.
- Durable Task storage provider registration.
- EF Core, Postgres, or any AppSurface-owned queue/scheduler runtime.
- ASP.NET Core endpoints, authentication handlers, webhooks, UI, or Semantic Kernel.

## Durable Boundary

Durable Task owns persistence, replay, timers, activities, and external event delivery. This package owns the AppSurface worker mapping contract around those host responsibilities:

1. Claim outcomes map to executor scheduling only when the app-owned contract returns `Claimed`.
2. Completion outcomes map to projection repair only when a fresh terminal fact was recorded; duplicate completion completes without carrying a projection payload.
3. Projection repair maps to completion with a projection payload, stale-signal, retry, or fault decisions and never schedules executor activity.
4. Wait and timeout decisions carry event and timeout metadata for host orchestration code to translate into Durable Task APIs.

`FlowRetryPolicy` values describe retry intent only. The adapter does not execute retries; the Durable Task host translates the policy into activity or timer behavior.

## Pitfalls

- Do not register EF/Postgres as an AppSurface worker runtime. Use app-owned stores for product state and durable facts, or Durable Task storage for orchestration history.
- Do not call executors from projection repair. Repair is derived from durable completion facts.
- Do not expose resume endpoints without host-owned authentication and authorization.
- Do not treat instance ids, event names, or work ids as authorization decisions by themselves.
