# ForgeTrust.AppSurface.Workers

`ForgeTrust.AppSurface.Workers` defines host-neutral contracts for durable worker chains and projection repair.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

## What It Includes

- `AppSurfaceWorkersModule`, a passive module that declares no queue, storage, endpoint, or background worker infrastructure.
- `IDurableWorkerProjectionContract<TWork,TResult,TProjection>` for claim, completion, single-projection repair, and bounded pending-projection repair.
- `IDurableWorkerExecutor<TWork,TResult>` for the side-effecting executor activity that host runtimes schedule only after a claim succeeds.
- `DurableWorkerEnvelope<TPayload>`, `DurableWorkerCorrelation`, and `DurableWorkerDiagnostic` for typed outcomes and privacy-safe repair diagnostics.
- `DurableWorkerExecutionIdentity` for native runtimes that must keep immutable activity/provider identity separate from attempt, lease, scope, and restore generations.
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

The native [AppSurface Durable runtime](../../Durable/ForgeTrust.AppSurface.Durable/README.md) reuses
`IDurableWorkerExecutor<TWork,TResult>` and the typed envelope, but it does not call this package's legacy
`TryClaimAsync` or `CompleteAsync` projection-authority methods. PostgreSQL remains the sole claim, effect-permit, and
terminal-fact authority.

## Native execution identity

`DurableWorkerEnvelope<TPayload>.ExecutionIdentity` is optional for compatibility with existing adapters. The native
runtime always supplies it before provider I/O. Its `ActivityId` and `ProviderKey` remain stable across retries; its
`AttemptNumber`, `LeaseGeneration`, `ScopeGeneration`, and `RuntimeEpoch` fence one exact execution observation.

Do not derive a provider idempotency key from `Correlation.AttemptId`, an attempt number, or a lease generation. Doing
so turns a retry into a different provider operation and defeats `ProviderKeyed` safety. Projection repair may inspect
execution identity, but must never treat it as authorization to invoke the executor again.

## Privacy-Safe Diagnostics

Metadata and diagnostics are meant to be durable and visible in repair reports, so they must contain only safe values:

- Stable reason codes and opaque identifiers.
- Counts, states, and bounded labels.
- Safe problem, cause, and fix guidance.

Do not put provider URLs, OAuth tokens, credentials, raw payloads, prompts, model output, email bodies, attachments, child-sensitive text, or other unclassified sensitive content into metadata.

## Projection Repair

Projection repair is a recovery path, not an executor path. Repair code should read durable completion facts, update visible state, and return `Reconciled`, `Noop`, `StaleFence`, `Conflict`, or `Unrecoverable` outcomes. It should not call `IDurableWorkerExecutor<TWork,TResult>`.

Use `DurableWorkerProjectionRepairRequest` to keep repair scans bounded by staleness and batch size.
