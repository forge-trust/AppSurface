# ForgeTrust.AppSurface.Workers

`ForgeTrust.AppSurface.Workers` defines host-neutral contracts for durable worker chains and projection repair.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

## What It Includes

- `AppSurfaceWorkersModule`, a passive module that declares no queue, storage, endpoint, or background worker infrastructure.
- `IDurableWorkerProjectionContract<TWork,TResult,TProjection>` for claim, completion, single-projection repair, and bounded pending-projection repair.
- `IDurableWorkerExecutor<TWork,TResult>` for the side-effecting executor activity that host runtimes schedule only after a claim succeeds.
- `DurableWorkerEnvelope<TPayload>`, `DurableWorkerCorrelation`, and `DurableWorkerDiagnostic` for typed outcomes and privacy-safe repair diagnostics.
- Stable outcome and retryability enums for durable adapters and application logs.
- `DurableWorkerMetadataSafety` for rejecting unsafe diagnostic metadata before it becomes durable or user-visible.

## What It Does Not Include

- Durable Task worker or client hosting.
- EF Core, Postgres, or any queue/scheduler runtime.
- Storage schemas or migrations.
- ASP.NET Core endpoints, authentication handlers, webhooks, UI, or Semantic Kernel.

## Contract Shape

Worker chains split durable behavior into three app-owned responsibilities:

1. `TryClaimAsync` decides whether executor activity may be scheduled.
2. `CompleteAsync` records the terminal execution fact produced by executor activity.
3. `ReconcileProjectionAsync` and `ReconcilePendingProjectionsAsync` update visible projections from durable completion facts without re-running executor activity.

This package does not decide where the facts live. A host can store them in its existing database, emit them from Durable Task activities, or adapt another persistence layer later. The first AppSurface runtime adapter is DurableTask-first; EF/Postgres runtime ownership is intentionally out of scope.

## Privacy-Safe Diagnostics

Metadata and diagnostics are meant to be durable and visible in repair reports, so they must contain only safe values:

- Stable reason codes and opaque identifiers.
- Counts, states, and bounded labels.
- Safe problem, cause, and fix guidance.

Do not put provider URLs, OAuth tokens, credentials, raw payloads, prompts, model output, email bodies, attachments, child-sensitive text, or other unclassified sensitive content into metadata.

## Projection Repair

Projection repair is a recovery path, not an executor path. Repair code should read durable completion facts, update visible state, and return `Reconciled`, `Noop`, `StaleFence`, `Conflict`, or `Unrecoverable` outcomes. It should not call `IDurableWorkerExecutor<TWork,TResult>`.

Use `DurableWorkerProjectionRepairRequest` to keep repair scans bounded by staleness and batch size.
