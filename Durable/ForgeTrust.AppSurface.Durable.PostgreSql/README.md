# ForgeTrust.AppSurface.Durable.PostgreSql

> **Source-only public preview:** this package supplies explicit PostgreSQL schema management and a manually driven Work
> engine, and a Flow engine. It is excluded from publish plans until slices 4-6 prove Flow, Schedule, hosted runtime, drain/recovery, and
> coordinated operations. It starts no worker or hosted service.

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

Run the source-evaluator [`slice 3 reference workload`](../slice3-reference-workload.md) or [`slice 4 reference workload`](../slice4-reference-workload.md). They apply schema explicitly,
accept Work and start Flows atomically with domain mutations, force-terminate a separate process at the committed
timer-winner checkpoint, verify the remaining recovery boundaries transactionally with fresh processors, and prove safe
recovery for every provider-safety class and Flow state transition. They are not hosted-runtime demonstrations.

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

Runtime roles never own schema or apply DDL. The package has three migrations: Work/shared state (`0001_work_shared.sql`), forced RLS and privilege revocation (`0002_forced_rls.sql`), and Flow protocol persistence (`0003_flow_protocol.sql`). Schedule and runtime-heartbeat schema belong to slices 5-6. Applied schema is forward-only;
rolling application code back does not authorize destructive schema rollback. Execute generated SQL with a client that
stops on the first error; `psql` callers must pass `-v ON_ERROR_STOP=1`.

Create host principals outside migrations. Use [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) to
grant the migration-owner, payload-free dispatcher, and scoped-runtime capabilities. Runtime roles must not receive
ownership or `BYPASSRLS`. Transaction-local scope context is defense in depth, not a replacement for application
authorization. The recipe fails before granting privileges when role names alias each other or a service role can
inherit the migration owner, `SUPERUSER`, or `BYPASSRLS`. It also transfers every package table, sequence, and view to
the migration owner so pre-existing object ownership cannot preserve runtime DDL authority. Existing direct,
inherited, or `PUBLIC` schema, relation, column, and sequence privileges outside the documented allowlist cause the
transactional recipe to fail and roll back; remove those host-managed grants before retrying.

Apply schema migrations before rerunning the role recipe. A migration can add package relations, but the recipe owns
the reviewed grants for existing service roles; running it second is required before Flow runtime or dispatcher
connections can use the new relations.

### Role recipe contract

Run the recipe with `psql` as a principal that can transfer ownership and grant privileges:

```console
psql -v ON_ERROR_STOP=1 \
  -v migration_owner_role=appsurface_durable_owner \
  -v dispatcher_role=appsurface_durable_dispatcher \
  -v runtime_role=appsurface_durable_runtime \
  -f Durable/configure-postgresql-roles.sql "$CONNECTION_STRING"
```

The dispatcher and runtime values identify the exact non-human credentials used to connect, not reusable capability
groups. Both must be distinct `LOGIN` leaf roles with no memberships in either direction and without `SUPERUSER`,
`CREATEDB`, `CREATEROLE`, `REPLICATION`, or `BYPASSRLS`. Create and rotate their credentials through the deployment
secret system; the recipe never accepts or changes passwords. Neither service credential may own any database or hold
grant options on the `appsurface_durable` schema or its objects. The migration owner remains separate and may be
`NOLOGIN`.

The `appsurface_durable` schema is package-reserved. The recipe serializes with migrations and runtime transactions,
then transfers every table, partition, sequence, view, materialized view, and foreign table in that schema to the
migration owner. Do not place application-owned objects there.

| Principal | Allowed privileges |
| --- | --- |
| Dispatcher | Schema `USAGE`; global table `SELECT` on payload-free `dispatch` and `flow_dispatch` only. |
| Runtime reads | Schema `USAGE`; table `SELECT` on package metadata, scoped Work relations, and all six scoped Flow relations. `flow_dispatch` is scope-filtered by transaction-local RLS. |
| Runtime inserts | Table `INSERT` on scoped Work relations and all six scoped Flow relations. |
| Runtime updates | Reviewed column-level `UPDATE` on mutable Work, Flow instance/wait/timer, and dispatch fields; no table-wide update grant. |
| Runtime sequences | `USAGE` and `SELECT` on every sequence in the package schema. |

Neither service credential receives schema `CREATE`, table-wide `UPDATE`, `DELETE`, `TRUNCATE`, `REFERENCES`,
`TRIGGER`, or `MAINTAIN`; the dispatcher receives no sequence privileges. Forced RLS remains an additional scope fence,
not the reason destructive privileges are safe. The recipe also rejects disabled or unforced RLS and any policy whose
name, command, role target, permissiveness, `USING`, or `WITH CHECK` expression differs from the reviewed migration.

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
Slice 6 must prove the adopter-facing hosting and operator-control boundary before those operations become public API;
applications must not depend on internal PostgreSQL types in the meantime.

Read the normative [`Work protocol v1`](../work-protocol-v1.md), [`Flow protocol v1`](../flow-protocol-v1.md), the
[`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md), the
[`slice 3 reconstruction ledger`](../slice3-reconstruction.md), and the [`slice 4 reconstruction ledger`](../slice4-reconstruction.md) for exact behavior, safe responses, and lineage.

## Release Guidance

From the repository root, `./Durable/verify-postgresql.sh --quick` runs focused Work proof, `./Durable/verify-postgresql.sh --quick --flow` runs focused Flow proof, and `--ci` / `--ci --flow` run the strict
real-PostgreSQL gates. The [`package chooser`](../../packages/README.md) is the generated adoption/publication source, and
the [`release hub`](../../releases/README.md) owns coordinated release policy.
