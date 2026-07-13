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

Operational codes are documented in the [`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md).

## Release Guidance

Use the [package chooser](../../packages/README.md) for adoption status and dependency guidance. Versioned publication
evidence and release policy live in the [release hub](../../releases/README.md).
