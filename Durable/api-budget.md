# Durable slice 2 API budget

This ledger records how the public surface from the original durable-contract commit was treated when the contract was
rebuilt as three preview packages. The checked-in
[Durable](https://github.com/forge-trust/AppSurface/blob/main/Durable/ForgeTrust.AppSurface.Durable/PublicAPI.Shipped.txt),
[Provider](https://github.com/forge-trust/AppSurface/blob/main/Durable/ForgeTrust.AppSurface.Durable.Provider/PublicAPI.Shipped.txt), and
[PostgreSQL](https://github.com/forge-trust/AppSurface/blob/main/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PublicAPI.Shipped.txt) snapshots are the exhaustive member-level
source of truth; this ledger explains the intentional package and visibility decisions.

## Retained adopter API

Every public type in the `ForgeTrust.AppSurface.Durable` snapshot remains public in that package unless it appears in
the additions below. These are the adopter-facing Work, Flow, Schedule, serialization, registration, and client
contracts. No adopter-facing public type was removed or internalized.

## Moved provider API

Every public type in the `ForgeTrust.AppSurface.Durable.Provider` snapshot other than `DurableProviderWorkAdapter` was
retained from the original contract and moved from the adopter assembly into the Provider package. This includes the
runtime pump, runtime health, drain, claimed-work, control, recovery, and operator families. The move establishes the
one-way package dependency Provider to Durable and replaces the original internal/friend provider seam.

## Added public API

- `DurableCommandFingerprint` and `DurableCommandFingerprintMatch` add versioned semantic command identity.
- `DurableWorkExecutionContext` and `DurablePreparedWork` expose the adopter side of provider-neutral Work execution.
- `DurableProviderWorkAdapter` exposes the provider side of the Work identity transition without friend access.

## Internalized or removed implementation seams

Canonicalization, identifier validation, provider validation, and fingerprint construction remain internal helpers;
they are implementation details behind validated public constructors. The original internal schedule/provider friend
seam was removed. No public type was deleted outright.

## Compatibility rule

All three packages remain source-only public previews and are machine-blocked from publication. The PostgreSQL slice-3 source
provider supplies Work conformance and restore fencing, but publication remains held until slices 4-6 prove Flow,
Schedule, hosted runtime, drain/recovery, and coordinated operations. Changes may still be made, but every public member change must update the
appropriate deterministic API snapshot and this ledger when it changes a type's audience, package, or visibility.

## Slice 3 PostgreSQL API

The source-only `ForgeTrust.AppSurface.Durable.PostgreSql` package adds thirteen public types in three deliberate
families. Its checked-in `PublicAPI.Shipped.txt` is the exhaustive member inventory.

- Schema deployment: `IDurableRuntimeSchemaManager`, `PostgreSqlDurableRuntimeSchemaManager`, schema status,
  compatibility, apply/activation/rotation results, and the schema exception. These operations require a
  migration-owner data source and never run automatically at runtime startup.
- Work acceptance: `IDurableWorkTransactionWriter`, `PostgreSqlDurableWorkTransactionWriter`, and
  `PostgreSqlDurableWorkClient`. The writer preserves caller ownership of the exact Npgsql transaction; the client
  owns only its short convenience transaction.
- Construction policy: `PostgreSqlDurableWorkOptions` and `PostgreSqlDurableWakeNotificationMode` make StoreId,
  runtime epoch, and default-disabled wake hints explicit without exposing internal claim/store operations.

Discovery, recovery, claim, renew, preparation failure, permit, completion, cancellation, scope disablement,
operator reconciliation, manual resolution, safe retry, recovery release, and stale-observation types remain internal.
Slice 6 must prove the smallest hosting and operator SPI before any of those types become public.

## Slice 4 PostgreSQL Flow API

Slice 4 adds exactly one public PostgreSQL runtime type:
`PostgreSqlDurableFlowClient : IDurableFlowClient`. Its constructor requires the scoped `NpgsqlDataSource`, exact
`IDurableFlowRegistry`, explicit `IDurablePayloadCodecRegistry`, and the existing
`PostgreSqlDurableWorkOptions`. Reusing the options type avoids a duplicate public configuration surface for StoreId,
runtime epoch, and wake-hint behavior.

The Flow processor, dispatch candidates/results, lease settings, barrier observer, command store, decision writer,
timer resolver, child-Work acceptance seam, and Work-to-Flow projector remain internal. Hosted activation, health,
drain, and public operator SPI decisions remain slice 6.

The existing Flow list continuation-token shape is unchanged. Its validated preview bound increases from 200 to 512
characters so the versioned base64url keyset token can carry an exact UTC update key and instance identity without
truncation. Tokens remain opaque, payload-free, scope-local query inputs and reject unknown versions or malformed
encodings.

## Slice 5 PostgreSQL Schedule API

Slice 5 adds five public PostgreSQL schedule types:
- `PostgreSqlDurableScheduleClient : IDurableScheduleClient` handles definition management, lifecycle commands, list pagination, and next-occurrence explanations.
- `PostgreSqlDurableScheduleOptions` configures the required runtime role, maximum clock advance, and dispatch lease duration.
- `PostgreSqlDurableScheduleProcessRequest` specifies the lease owner and maximum schedules per pass.
- `PostgreSqlDurableScheduleProcessResult` reports claimed schedules, recorded occurrences, materialized Work targets, and suspended schedules.
- `PostgreSqlDurableScheduleProcessor` drives manual, bounded evaluation passes using separate dispatcher and runtime data sources.

## Slice 6 PostgreSQL runtime operations API

Slice 6 adds the explicit PostgreSQL composition surface rather than widening the portable Provider contracts:

- `AppSurfaceDurablePostgreSqlOptions` configures process-local worker identity, bounded Pass policy, polling,
  liveness, shutdown reserve, and advisory wake-hint mode.
- `AppSurfaceDurablePostgreSqlServiceCollectionExtensions.AddAppSurfaceDurablePostgreSql` registers passive storage,
  clients, schema manager, pump, health, and drain from explicit dispatcher/runtime data sources and existing Work and
  Schedule options. It opens no connection, applies no migration, and installs no `IHostedService`.
- `AppSurfaceDurablePostgreSqlBuilder.AddWorkerHost` and `AddAppSurfaceDurableWorkerHost` are the deliberate opt-in
  for one host adapter. They retain `IDurableRuntimePump` as the external-activator escape hatch rather than creating a
  second execution API.
- `AppSurfaceDurablePostgreSqlModule` declares only the passive Durable module dependency. It never guesses
  credentials or activates a worker.

The runtime pump, health, and drain interfaces remain owned by `ForgeTrust.AppSurface.Durable.Provider`. PostgreSQL
claim stores, heartbeat generation operations, listener state, execution wrapper, and hosted service stay internal so
#685 can instrument the execution wrapper without duplicating trace context or broadening the public API.
