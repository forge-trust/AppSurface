# ForgeTrust.AppSurface.Durable.PostgreSql

> **Source-only public preview:** this package supplies explicit PostgreSQL schema management, Work, Flow, Schedule,
> an explicitly opted-in bounded runtime host, and durable W3C causal evidence. It remains excluded from publish plans
> until coordinated release review proves operational conformance. Storage registration itself starts no worker or hosted
> service.

Choose this package when an application must commit its domain mutation and durable Work acceptance in the same
PostgreSQL transaction, and when process-loss recovery must use explicit leases, runtime/scope fences, effect permits,
and provider-safety policy. Choose a larger workflow platform for arbitrary deterministic replay, child workflows, or
unbounded fan-out. PostgreSQL is this provider's sole durable truth.

The verified database target is PostgreSQL 17.5.

The package references the adopter-facing
[`ForgeTrust.AppSurface.Durable`](../ForgeTrust.AppSurface.Durable/README.md) contracts and the
[`ForgeTrust.AppSurface.Durable.Provider`](../ForgeTrust.AppSurface.Durable.Provider/README.md) SPI. Neither package
depends on PostgreSQL.

## First proof

Run the source-evaluator [`slice 3 reference workload`](../slice3-reference-workload.md) and
[`slice 4 reference workload`](../slice4-reference-workload.md) to apply schema explicitly, accept Work and start Flows
atomically with domain mutations, force-terminate a separate process at the committed timer-winner checkpoint, and verify
the remaining recovery boundaries transactionally with fresh processors. The Work-first
[`Schedule protocol`](../schedule-protocol-v1.md) remains a bounded Gate A Work-only protocol check; its deterministic
crash-proof evidence remains deferred. [Durable Flow trace context v1](../flow-trace-context-v1.md) documents the
value-free causal evidence and process-loss link proof. For the runtime activation boundary, use the worker-host path
below.

## Explicit schema and epoch deployment

Construct `PostgreSqlDurableRuntimeSchemaManager` with a migration-owner `NpgsqlDataSource`:

- `GetStatusAsync` reports StoreId, nullable active epoch, migration state, and reader/writer compatibility;
- `GenerateScript` produces deterministic forward-only SQL from an exact reviewed installed version; generated SQL is
  not safe to rerun after a selected migration commits;
- `ApplyAsync` applies pending known migrations under one session advisory lock;
- `InitializeRuntimeEpochAsync` activates the first epoch exactly once; and
- `RotateRuntimeEpochAsync` compare-and-swaps the active epoch after restore or an authorized recovery event.

Runtime mutations take a shared, transaction-scoped advisory fence before validating the active epoch. Schema changes
and epoch rotation take the exclusive package lock, so they wait for in-flight runtime transactions and prevent an old
epoch from committing new durable state after rotation.

Runtime roles never own schema or apply DDL. The package has seven migrations: Work/shared state (`0001_work_shared.sql`),
forced RLS and privilege revocation (`0002_forced_rls.sql`), Flow protocol persistence (`0003_flow_protocol.sql`), the
Work-first Schedule ledger (`0004_schedule_protocol.sql`), the payload-free runtime heartbeat
(`0005_runtime_heartbeat.sql`), value-free Flow trace context (`0006_flow_trace_context.sql`), and verified one-Flow
retention lifecycle evidence (`0007_flow_retention.sql`). Applied schema is
forward-only; rolling application code back does not authorize destructive schema rollback. Execute generated SQL with a
client that stops on the first error; `psql` callers must pass `-v ON_ERROR_STOP=1`.

Create host principals outside migrations. Use [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) to
grant the migration-owner, payload-free dispatcher, scoped-runtime, and scoped-retention-operator capabilities. Service roles must not receive
ownership or `BYPASSRLS`. Transaction-local scope context is defense in depth, not a replacement for application
authorization. The recipe fails before granting privileges when role names alias each other or a service role can
inherit the migration owner, `SUPERUSER`, or `BYPASSRLS`. It also transfers every package table, sequence, and view to
the migration owner so pre-existing object ownership cannot preserve runtime DDL authority. Existing direct,
inherited, or `PUBLIC` schema, relation, column, and sequence privileges outside the documented allowlist cause the
transactional recipe to fail and roll back; remove those host-managed grants before retrying. The runtime role is
deliberately fully trusted for the unscoped `runtime_heartbeat` table: the health component owns the
worker-generation fence through its row lock and compare-and-swap predicates, so applications must not expose that
credential to untrusted callers.

Apply schema migrations before rerunning the role recipe. A migration can add package relations, but the recipe owns
the reviewed grants for existing service roles; running it second is required before Flow runtime or dispatcher
connections can use the new relations.

## Run a worker host

After the schema is current, the recovery epoch is initialized, and the role recipe has run, compose exactly one
runtime-role and one payload-free dispatcher-role data source. The warm path is designed to reach one hosted Work
completion in five minutes; it never performs DDL at application startup.

```csharp
using ForgeTrust.AppSurface.Durable.PostgreSql;

var workOptions = new PostgreSqlDurableWorkOptions(
    runtimeEpoch,
    expectedStoreId,
    PostgreSqlDurableWakeNotificationMode.Enabled);

services.AddAppSurfaceDurablePostgreSql(
        dispatcherDataSource,
        runtimeDataSource,
        workOptions,
        new PostgreSqlDurableScheduleOptions("appsurface_durable_runtime"),
        options =>
        {
            options.WorkerId = "orders-worker-01"; // unique for every live replica
            options.MaximumItemsPerPass = 32;
            options.TimeBudgetPerPass = TimeSpan.FromSeconds(10);
            options.ShutdownReserve = TimeSpan.FromSeconds(5);
        })
    .AddWorkerHost();
```

`AddAppSurfaceDurablePostgreSql` resolves Work, Flow, Schedule, schema, pump, health, and drain services but installs
no `IHostedService`, opens no connection, and applies no migration. `AddWorkerHost()` is the standard continuous
activation path. It validates schema compatibility, the active epoch, and that `TimeBudgetPerPass + ShutdownReserve`
fits inside `HostOptions.ShutdownTimeout`; an invalid store or host configuration fails closed.

The host calls the same [`IDurableRuntimePump`](../ForgeTrust.AppSurface.Durable.Provider/README.md#activation-and-broker-evolution)
used by an external activator. A Pass runs at most `MaximumItemsPerPass` committed Turns and rotates Work, Flow, and
Schedule after each selected-surface attempt. Empty and deferred surfaces advance the cursor without consuming that
item budget. PostgreSQL remains authoritative for discovery, claims, leases, effect permits, Flow transitions,
Schedule facts, row-level security, and recovery epochs. Deploy separately identified workers when a long Work
invocation needs stronger latency isolation from Flow or Schedule.

Enabled notification mode creates one dedicated `LISTEN appsurface_durable_wake` connection. It coalesces and discards
metadata-only notification payloads; polling remains the recovery path for lost, duplicate, delayed, or unavailable
hints. A receipt never authorizes a claim.

Resolve [`IDurableRuntimeHealth`](../ForgeTrust.AppSurface.Durable.Provider/README.md#public-api-by-audience) through
an application-owned authorized health endpoint. `Healthy` is the only ready state; `NotStarted`, `Stale`, `Draining`,
and `Incompatible` are intentionally not ready. Snapshots contain aggregate counts and fixed codes only—never payload,
scope, aggregate, connection, or trace values. At shutdown, local admission closes synchronously before the host
persists drain; already-permitted Work follows its ordinary cancellation/recovery path rather than inventing a result.

For a cold path, apply `0005_runtime_heartbeat.sql` with the migration owner, rerun
[`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql),
verify the active epoch and StoreId, deploy with `AddWorkerHost()` disabled, then enable it. Roll back application code
by disabling the worker host and deploying a previous compatible binary; never destructively roll back a migration. If
Issue #685 supplies an intervening migration first, renumber this migration to the next contiguous number while preserving
its content and rerun the role recipe.

### Role recipe contract

Run the recipe with `psql` as a principal that can transfer ownership and grant privileges:

```console
psql -v ON_ERROR_STOP=1 \
  -v migration_owner_role=appsurface_durable_owner \
  -v dispatcher_role=appsurface_durable_dispatcher \
  -v runtime_role=appsurface_durable_runtime \
  -v retention_operator_role=appsurface_durable_retention \
  -f Durable/configure-postgresql-roles.sql "$CONNECTION_STRING"
```

The dispatcher, runtime, and retention-operator values identify exact non-human credentials used to connect, not reusable capability
groups. All three must be distinct `LOGIN` leaf roles with no memberships in either direction and without `SUPERUSER`,
`CREATEDB`, `CREATEROLE`, `REPLICATION`, or `BYPASSRLS`. Create and rotate their credentials through the deployment
secret system; the recipe never accepts or changes passwords. Neither service credential may own any database or hold
grant options on the `appsurface_durable` schema or its objects. The migration owner remains separate and may be
`NOLOGIN`.

The `appsurface_durable` schema is package-reserved. The recipe serializes with migrations and runtime transactions,
then transfers every table, partition, sequence, view, materialized view, foreign table, and package function in that
schema to the migration owner. Do not place application-owned objects there.

| Principal | Allowed privileges |
| --- | --- |
| Dispatcher | Schema `USAGE`; global table `SELECT` on payload-free `dispatch` and `flow_dispatch`; `EXECUTE` only on the constrained Schedule claim function. It receives Schedule routing IDs and revision, never raw `schedule_dispatch` columns. |
| Runtime reads | Schema `USAGE`; table `SELECT` on package metadata, scoped Work, Flow, Schedule, and Flow trace-context relations. Dispatch reads are scope-filtered by transaction-local RLS. |
| Runtime inserts | Table `INSERT` on scoped Work, Flow, Schedule, and Flow trace-context relations. |
| Runtime updates | Reviewed column-level `UPDATE` on mutable Work, Flow instance/wait/timer, Schedule definition/occurrence/dispatch, and dispatch fields; no table-wide update grant. |
| Runtime sequences | `USAGE` and `SELECT` on every sequence in the package schema. |
| Runtime heartbeat | Unscoped `SELECT` and `INSERT`, plus reviewed column-level `UPDATE`, on `runtime_heartbeat` for `IDurableRuntimeHealth`. Its forced RLS policy intentionally uses `USING (true)` and `WITH CHECK (true)`; keep this fully trusted runtime credential out of untrusted callers. |
| Retention operator | Scope-filtered Flow/Work-reference and retention-evidence reads; `EXECUTE` only on the owner-run manifest and lifecycle capabilities. It has no direct lifecycle/source `INSERT`, `UPDATE`, `DELETE`, sequence, dispatcher-discovery, Schedule, worker-host, or migration access. |

Neither service credential receives schema `CREATE`, table-wide `UPDATE`, `DELETE`, `TRUNCATE`, `REFERENCES`,
`TRIGGER`, or `MAINTAIN`; the dispatcher receives no sequence privileges. Forced RLS remains an additional scope fence,
not the reason destructive privileges are safe. The recipe also rejects disabled or unforced RLS and any policy whose
name, command, role target, permissiveness, `USING`, or `WITH CHECK` expression differs from the reviewed migration.

Before the pre-created next month starts, a migration-owner operation must run
`SELECT appsurface_durable.ensure_schedule_history_partitions();`. It retains the current and next Schedule-history
partitions and reapplies their forced RLS policy. Runtime and dispatcher roles cannot execute this maintenance function.

## Verified Flow retention

Register retention only after the schema is current and the four-role recipe has completed. Supply a dedicated
retention-operator data source; it must not be the dispatcher or runtime source:

```csharp
services.AddAppSurfaceDurablePostgreSql(
    dispatcherDataSource,
    runtimeDataSource,
    workOptions,
    scheduleOptions);
services.AddAppSurfaceDurablePostgreSqlFlowRetention(retentionOperatorDataSource);
```

Migration `0007_flow_retention.sql` installs PostgreSQL's trusted [`pgcrypto`](https://www.postgresql.org/docs/current/pgcrypto.html) extension in `public` to recompute
canonical SHA-256 source-item evidence inside the owner-run capabilities. The migration owner therefore needs database
permission to install that extension on first use, or an operator must preinstall `pgcrypto` in `public` before applying
the migration. A pre-existing installation in another schema is rejected with an explicit migration error; move it to
`public` before retrying. The capabilities call the extension by schema-qualified identity; it is not resolved through a
caller-owned search path.

Resolve `IDurableFlowRetentionClient` only behind an application-authorized operator boundary. The lifecycle is
`AssessAsync` → `CreateManifestAsync` → `BuildArchivePackageAsync` → external write → `RecordArchiveReceiptAsync` →
`VerifyArchiveAsync` → optional `SetHoldAsync` → `PurgeAsync`. Assessment has no universal age policy: callers select
one terminal Flow and supply a maximum of 10,000 closure items and 64 MiB package bytes. The owner-run PostgreSQL
capabilities validate scope and lifecycle state, lock the source closure during verification and purge, retain the Flow
identity and command ledger, clear terminal payload fields, and delete
only manifest-covered Flow history, resolved waits, terminal timers, and terminal dispatch rows. A retry returns the
persisted command outcome; a changed source, stale sequence, active child Work, repair-required Flow, or hold rejects
the operation without a partial delete.

The retention login receives no direct mutation grants. `CreateManifestAsync` calls
`appsurface_durable.create_flow_retention_manifest`, while receipt, verify, hold, and purge call
`appsurface_durable.apply_flow_retention_lifecycle`. Both capabilities require the transaction's scoped identity,
serialize command replay, validate lifecycle sequencing, and compare every live source item's server-computed SHA-256
against the immutable manifest before verification or deletion. The application still owns authorization and the opaque
external-archive receipt: PostgreSQL proves source correspondence, not external storage availability or legal adequacy.

`DFA1` package bytes are returned before external I/O. The archive receipt is an opaque adopter assertion, not a URI,
availability check, encryption proof, or compliance determination. Source-correspondence verification rebuilds the
canonical package and compares its SHA-256 and frozen closure digest. Applications must document and operate their own
archive store, key management, retention policy, legal holds, performance/WAL evidence, and recovery objectives.

## Options reuse across Work and Flow

Create `PostgreSqlDurableWorkOptions` from the non-empty StoreId and explicitly active epoch returned by deployment. The options object is shared across Work and Flow operations:

<!-- appsurface:snippet id="durable-postgresql-options-reuse" file="Durable/packed-consumers/PostgreSqlProvider/PostgreSqlReadmeProof.cs" marker="durable-postgresql-options-reuse" lang="csharp" -->
```csharp
internal static PostgreSqlDurableWorkOptions CreateSharedOptions(
    Guid runtimeEpoch,
    Guid expectedStoreId)
{
    // PostgreSqlDurableWorkOptions is reused directly across Work and Flow operations to guarantee
    // consistent ExpectedStoreId, active RuntimeEpoch, notification modes, and schema compatibility validation.
    return new PostgreSqlDurableWorkOptions(
        runtimeEpoch,
        expectedStoreId,
        PostgreSqlDurableWakeNotificationMode.Disabled);
}
```
<!-- /appsurface:snippet -->

## Accept Work

<!-- appsurface:snippet id="durable-postgresql-accept-work" file="Durable/packed-consumers/PostgreSqlProvider/PostgreSqlReadmeProof.cs" marker="durable-postgresql-accept-work" lang="csharp" -->
```csharp
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace DurablePostgreSqlConsumer;

internal static class PostgreSqlReadmeProof
{
    internal static async ValueTask<DurableOperationResult<DurableWorkAcceptance>> AcceptAsync(
        NpgsqlDataSource dataSource,
        IDurableWorkRegistry workRegistry,
        Guid runtimeEpoch,
        Guid expectedStoreId,
        NpgsqlTransaction transaction,
        DurableWorkRequest request,
        CancellationToken cancellationToken)
    {
        var options = new PostgreSqlDurableWorkOptions(
            runtimeEpoch,
            expectedStoreId,
            PostgreSqlDurableWakeNotificationMode.Disabled);

        var writer = new PostgreSqlDurableWorkTransactionWriter(dataSource, workRegistry, options);
        var accepted = await writer.EnqueueAsync(transaction, request, cancellationToken);
        if (!accepted.IsSuccess)
        {
            await transaction.RollbackAsync(cancellationToken);
            return accepted;
        }

        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    // docs:snippet durable-postgresql-options-reuse:start
    internal static PostgreSqlDurableWorkOptions CreateSharedOptions(
        Guid runtimeEpoch,
        Guid expectedStoreId)
    {
        // PostgreSqlDurableWorkOptions is reused directly across Work and Flow operations to guarantee
        // consistent ExpectedStoreId, active RuntimeEpoch, notification modes, and schema compatibility validation.
        return new PostgreSqlDurableWorkOptions(
            runtimeEpoch,
            expectedStoreId,
            PostgreSqlDurableWakeNotificationMode.Disabled);
    }
    // docs:snippet durable-postgresql-options-reuse:end
}
```
<!-- /appsurface:snippet -->

The proof helper owns transaction completion: it rolls back when acceptance returns a domain problem and commits only
after successful Work acceptance. The writer itself uses the exact active `NpgsqlTransaction`; it never opens a second
connection, commits, rolls back, replaces, or disposes the caller transaction. Caller rollback removes both the domain
mutation and Work acceptance. Use
`PostgreSqlDurableWorkClient` with the same data source, registry, and options only when the package may own a short
acceptance transaction.

## Work-first Schedule pass

`PostgreSqlDurableScheduleClient` persists the existing `IDurableScheduleClient` contract. The Gate A provider admits
`At`, `After`, and `Every` schedules whose target is registered Work; `QueueOne` and `RunOnce` are their defaults. It
captures one PostgreSQL `transaction_timestamp()` for each generation. `After` and unanchored `Every` derive their
first nominal time from that stored value, never from caller time or the later Work `accepted_at` value.

`PostgreSqlDurableScheduleProcessor` is intentionally passive. Construct it with a dispatcher data source and a
separate runtime-role data source, then invoke one bounded pass:

```csharp
var processor = new PostgreSqlDurableScheduleProcessor(
    dispatcherDataSource,
    runtimeDataSource,
    workRegistry,
    workOptions,
    new PostgreSqlDurableScheduleOptions("appsurface_durable_runtime"));

var pass = await processor.ProcessDueAsync(
    new PostgreSqlDurableScheduleProcessRequest("orders-schedule-pass", maximumSchedules: 8),
    cancellationToken);
```

The dispatcher can only lease the narrow Schedule queue through a security-definer claim function. It has no raw
`schedule_dispatch` table read, so due time and cadence remain inside the function; its result contains only scope,
Schedule ID, and revision. Before the processor sets scoped RLS state or bridges an occurrence, it verifies
`current_user` equals `PostgreSqlDurableScheduleOptions.RuntimeRole`. The Work bridge uses the existing caller-owned
`PostgreSqlDurableWorkTransactionWriter`, so the occurrence link and one Work acceptance commit or roll back together.
An empty pass returns zero counts. Cancellation stops before the next lease and cannot undo a previously committed fact.
Do not call the processor in a request loop. The worker host above is the standard continuous activation path; manual
processor calls remain useful only for focused tests or an explicitly designed external activator.

The Schedule processor also compares persisted runtime-epoch and scope-generation fences before evaluating a due row.
A mismatch suspends the Schedule before it can move a cursor or accept Work. Use `ReleaseAfterRecovery` only for an old
runtime epoch; a scope-generation mismatch must be repaired with a public update or delete/recreate. The dispatcher
claim function rejects blank/control-character owners and null or non-positive durations before leasing, preventing malformed
or overlong calls from stranding a row. Schedule discovery leases are capped at ten minutes.

For the admitted default `QueueOne` policy, one nonterminal target occupies the Schedule-wide slot. Later nominal
instants coalesce into one pending occurrence. When that Work reaches a terminal state, the Work transaction requeues
the Schedule dispatch row; the next manual pass materializes the pending occurrence immediately rather than waiting
for another interval. Retries and suspended Work intentionally retain the slot. See the normative
[`QueueOne occurrence rules`](../schedule-protocol-v1.md#occurrence-materialization) for transaction ownership and
generation behavior.

`ListAsync` returns payload-free Schedule inventory ordered by Schedule ID. When the requested page is not terminal, it
returns a provider-issued continuation token; send that token back unchanged with the same scope and filters to obtain
the next page. The token is an opaque cursor, not an authorization grant, and changing it can only change the caller's
position within the already RLS-scoped inventory.

CronosV1, Flow targets, and non-default overlap or misfire policies are intentionally rejected by this increment. Cron
needs pinned evaluator/time-zone evidence; Flow has no caller-owned start transaction seam; `Skip`, bounded concurrency,
and catch-up need occurrence-state semantics that arrive in a later gate. A Schedule that observes a database-clock
advance beyond its configured safety window suspends instead of moving its cursor. `ReleaseAfterRecovery` only releases
an old-epoch fence; it cannot clear a clock/evaluator suspension or rewrite a cursor. Repair with a public definition
update or delete/recreate after the underlying cause is corrected.

Endpoint/database matching is a configuration guard. Durable identity is `ExpectedStoreId`, which the writer reads
through the supplied transaction. Notifications default to disabled; when enabled, they are payload-free latency hints
and never replace authoritative discovery.

## Failure and effect safety

Local preflight and expected domain outcomes leave an otherwise active transaction usable. PostgreSQL errors, timeout,
network loss, server cancellation, or an aborting SQLSTATE require caller rollback. Savepoints are unsupported.
The API method being called is the operation context: failures are not wrapped in a generic provider exception that
would hide the concrete Npgsql type. Missing or incompatible schema failures expose safe `Status`; when PostgreSQL
reveals missing schema during Work acceptance, `InnerException` preserves the original `PostgresException`, stack, and
SQLSTATE. Only the outer durable message and status are safe to log. Never log or serialize the inner exception's
server-controlled message, detail, hint, SQL text, object names, or other fields; project only its concrete type and
SQLSTATE.

External provider I/O happens only after an exact-fence permit commits and never while a database connection or
transaction is held. `Idempotent` and `ProviderKeyed` work can recover safely; `ReconcileBeforeRetry` and
`ManualResolution` suspend ambiguous outcomes until evidence authorizes a transition. The package never claims
exactly-once external effects and never converts unknown post-permit truth to failed terminal.

The source provider implements audited reconciliation, manual-resolution, safe-retry, and recovery-release transitions
as internal conformance behavior. Recovery release atomically moves an exact ambiguous permit to the newly authorized
runtime epoch with its Work. When the current attempt has no exact ambiguous permit, release safely makes the Work
retryable and leaves historical permits unchanged. When an expected exact permit cannot move with the Work, the entire
release rolls back so later proof remains possible.
The runtime pump, health, drain, and host composition are now public through the Provider SPI and PostgreSQL
registration extensions. Applications must still keep authorization around all operator/control APIs and must not
depend on internal PostgreSQL claim/store types.

Read the normative [`Work protocol v1`](../work-protocol-v1.md), [`Flow protocol v1`](../flow-protocol-v1.md), [Durable Flow trace context v1](../flow-trace-context-v1.md), the
[`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md), the
[`slice 3 reconstruction ledger`](../slice3-reconstruction.md), and the [`slice 4 reconstruction ledger`](../slice4-reconstruction.md) for exact behavior, safe responses, and lineage.

## Release Guidance

From the repository root, `./Durable/verify-postgresql.sh --quick` runs focused Work proof, `./Durable/verify-postgresql.sh --quick --flow` runs focused Flow proof, and `./Durable/verify-postgresql.sh --quick --schedule` runs the real PostgreSQL Work-first Schedule proof. `--ci` runs the complete strict real-PostgreSQL suite; `--ci --flow` performs its compatibility preflight before that suite, while `--ci --schedule` also runs the complete suite without a Schedule-specific filter. The [`package chooser`](../../packages/README.md) is the generated adoption/publication source, and
the [`release hub`](../../releases/README.md) owns coordinated release policy.
