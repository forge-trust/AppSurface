# ForgeTrust.AppSurface.Durable.PostgreSql

`ForgeTrust.AppSurface.Durable.PostgreSql` is the PostgreSQL storage protocol for
[`ForgeTrust.AppSurface.Durable`](../ForgeTrust.AppSurface.Durable/README.md). This first integration slice owns explicit
schema deployment, runtime-epoch fencing, atomic Work acceptance, scoped dispatch, leases, retry state, provider-effect
permits, cancellation, and scope disablement. It does not start a hosted worker.

The package is a public preview. Treat its schema and persisted history as versioned application infrastructure, not as
tables for application code to update directly.

## Choose this package when

- a domain transaction and durable Work acceptance must commit in the same PostgreSQL database;
- Work must survive process loss with lease-generation and runtime-epoch fencing;
- provider safety must be explicit and ambiguous external outcomes must suspend for reconciliation; or
- the application should own its durable control plane without an Azure service, sidecar, or separate broker.

Choose a larger workflow platform when you need arbitrary deterministic replay, child workflows, unbounded fan-out, or
a provider-independent storage abstraction. This package intentionally uses PostgreSQL as its sole source of truth.

## Schema deployment

`IDurableRuntimeSchemaManager` exposes four explicit operations:

- `GetStatusAsync` reports missing, compatible, inconsistent, too-old, and too-new stores;
- `GenerateScript` emits the ordered migration script for a deployment pipeline;
- `ApplyAsync` applies pending embedded migrations with an advisory lock; and
- `RotateRuntimeEpochAsync` fences a restored store before a replacement fleet starts.

Construct `PostgreSqlDurableRuntimeSchemaManager` with a migration-owner `NpgsqlDataSource`. Runtime application roles
must not own the schema or receive `BYPASSRLS`. Runtime startup validates compatibility; it never performs automatic DDL.

All five protocol migrations land with this schema slice because scope disablement is one atomic fence across Work,
Flow, and Schedule tables. The later engine slices build on that already-versioned protocol.

## Atomic Work acceptance

Use `PostgreSqlDurableWorkClient` when the runtime may own a short transaction. Use
`IDurableWorkTransactionWriter` and `PostgreSqlDurableWorkTransactionWriter` when an existing domain transaction must
also accept Work:

```csharp
await writer.EnqueueAsync(transaction, request, cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

The writer verifies that the supplied transaction targets the configured PostgreSQL host, port, and database. It never
opens, commits, rolls back, replaces, or disposes that transaction. A mismatched database fails before acceptance.

Requests require a registered Work contract and codec. The registration's provider-safety policy must exactly match the
request, and duplicate command identifiers return the original acceptance only when the canonical fingerprint matches.

## Resumable Flow persistence

`PostgreSqlDurableFlowClient` starts, queries, lists, resumes, cancels, and releases typed Flow instances. A start request
uses an explicitly registered Flow definition and the exact allowlisted context codec. Each accepted command persists a
canonical fingerprint so command-id reuse can return the original result without silently changing intent.

The Flow store advances exactly one explicit transition at a time. Its history records calls, results, event waits,
timers, suspensions, cancellations, and terminal outcomes with stable sequence identities. This is deliberate decision
orchestration, not arbitrary stack replay; ordinary asynchronous activity code remains supported outside the durable
authoring boundary.

Activity calls become durable Work with an immutable activity identity. Completion is projected back into the waiting
Flow only after Work fencing succeeds. A process crash between those operations is repaired from persisted state rather
than by repeating a provider effect.

`GetAsync` returns the authorized full snapshot. `ListAsync` is a bounded, payload-free inventory intended for recovery
and operator views. Release after restore requires exact revision, runtime-epoch, manifest, and wait-shape evidence; it
never invents a continuation for an incompatible definition.

Event delivery is application-authorized before it reaches the client. Instance and event identifiers locate rows but
are not authorization proof. Late, duplicate, mismatched-contract, and canceled waits are recorded deterministically.

## Durable scheduling

`PostgreSqlDurableScheduleClient` persists one-off and recurring definitions and exposes bounded queries, lifecycle
commands, recovery release, and next-occurrence explanation. Targets are registered durable Work or Flow contracts; the
client validates their exact codecs before accepting a schedule.

Supported definitions are `At`, `After`, `Every`, and full Cron expressions through Cronos 0.13.0. Cron evaluation
persists the evaluator version, time-zone identity, and UTC occurrence window. A later dependency or time-zone rule
change cannot reinterpret an occurrence that is already durable.

Recurring schedules default to:

- `ScheduleOverlapPolicy.QueueOne`, which coalesces overlap into at most one pending occurrence; and
- `ScheduleMisfirePolicy.RunOnce`, which emits one recovery occurrence after downtime and advances to the next future
  tick.

Those policies are configurable per schedule. Use bounded catch-up or bounded concurrency only when the target's
business and provider contracts can tolerate it. Updating a schedule increments its generation and invalidates
not-yet-started occurrences from the prior generation.

`PostgreSqlDurableScheduleProcessor` advances due occurrences in bounded transactions and atomically accepts their Work
or Flow targets. Run-slot rows preserve active and pending truth across crashes. Terminal target completion releases the
slot and activates or discards pending state according to the stored overlap policy.

## Registration and activation

Register storage explicitly with `AddAppSurfaceDurablePostgreSql`, supplying the runtime-role `NpgsqlDataSource`, the
current out-of-band runtime epoch, and optional process settings. Registration is passive: it adds clients, schema
validation, control surfaces, health, and `IDurableRuntimePump`, but starts no polling and applies no migrations.

Call `AddWorkerHost` only in a continuously live worker process. The hosted service repeatedly invokes the same bounded
pump available to jobs, functions, tests, and externally activated hosts. A scale-to-zero deployment must arrange an
external activator; PostgreSQL notifications are metadata-only wake hints and cannot start a nonexistent process.

The bounded pump processes Work, Flow, and Schedule surfaces with an item cap and time budget. It serializes passes per
runtime instance, alternates surface order to avoid starvation, validates schema and epoch compatibility before work,
and reports whether another immediate pass is useful.

## Runtime health and graceful drain

`IDurableRuntimeHealth.GetAsync` returns a payload-free snapshot containing schema/epoch compatibility, process and
worker ownership, hosted surfaces, heartbeat and last-successful-sweep timestamps, drain/pass markers, and bounded due
counts. It never exposes a scope id, aggregate id, provider key, or payload.

`IDurableRuntimeDrainControl.BeginDrainAsync` prevents later passes from claiming new aggregates. It does not cancel
provider I/O or revoke an effect permit. A custom host must begin drain, await its active pump call, and then terminate.
The hosted service resumes on startup and records a best-effort drain marker during shutdown.

Worker identity is persisted with a unique process-instance id. Reusing a worker id while another live process owns it
fails closed; a clean drained process hands ownership off immediately, while an unclean owner remains protected until
its heartbeat becomes stale.

## Control, reconciliation, and restore

`IDurableWorkControlClient` and `IDurableScopeControlClient` provide authorized reads, bounded payload-free inventories,
cancellation, and scope disablement. `IDurableWorkOperatorClient` exposes audited reconciliation, manual resolution,
safe retry, and recovery release commands. Operator requests require exact revision, actor, reason, and evidence fields;
they do not infer provider truth.

After a point-in-time restore, stop ingress and workers, rotate the out-of-band runtime epoch with the migration-owner
connection, start the replacement fleet in recovery posture, page each authorized Work/Flow/Schedule inventory,
reconcile every issued effect permit, release only exact-revision aggregates proven safe, and then resume activation.
Starting the pump is not a substitute for that audit.

## Execution and provider safety

Work dispatch uses short claims, renewable leases, revision checks, scope generations, and a store-wide runtime epoch.
External provider I/O must happen without holding a database connection or transaction. Completion is accepted only for
the exact claim identity that authorized the attempt.

Provider safety is not an exactly-once claim:

- `Idempotent` permits normal retry using the immutable activity-derived key;
- `ProviderKeyed` requires the provider to honor that stable key;
- `ReconcileBeforeRetry` suspends ambiguous outcomes until an operator records evidence; and
- `ManualResolution` never repeats the effect automatically.

Cancellation before the effect boundary prevents provider execution. Cancellation after a permit is issued cannot
prove the provider did nothing, so the runtime records evidence and follows the configured safety policy.

## Recovery and security pitfalls

- Rotate the out-of-band runtime epoch after point-in-time restore before releasing Work.
- Treat PostgreSQL notifications as latency hints only; due-state polling remains authoritative.
- Never put payloads, provider keys, or user-controlled text into worker identifiers or low-cardinality health metadata.
- Scope identifiers are authorization context, not proof by themselves; set and enforce row-level scope state inside the
  same transaction as every mutation.
- Do not retry an ambiguous provider effect merely because its lease expired.
- Do not release a suspended Flow after restore unless its manifest and active-wait shape still match persisted history.
- A scale-to-zero host needs an external activator; persisted schedules cannot wake a process that does not exist.
- Begin drain and await the active bounded pass before terminating a custom worker host.
- Never reuse the restored database's former runtime epoch or grant epoch rotation to the ordinary runtime role.

Operational codes are documented in the [`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md).

## Release Guidance

Use the [package chooser](../../packages/README.md) for adoption status and dependency guidance. Versioned publication
evidence and release policy live in the [release hub](../../releases/README.md).
