# ForgeTrust.AppSurface.Durable.PostgreSql

> **Source-only public preview:** this package supplies explicit PostgreSQL schema management and a manually driven Work
> engine. It is excluded from publish plans until slices 4-6 prove Flow, Schedule, hosted runtime, drain/recovery, and
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

Run the source-evaluator [`slice 3 reference workload`](../slice3-reference-workload.md). It applies schema explicitly,
accepts Work atomically with a domain mutation, terminates a separate process at committed checkpoints, and proves safe
recovery for every provider-safety class. It is not a hosted-runtime demonstration.

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

Runtime roles never own schema or apply DDL. The package has two migrations: Work/shared state, then forced RLS and
privilege revocation. Flow, Schedule, and runtime-heartbeat schema belong to slices 4-6. Applied schema is forward-only;
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
secret system; the recipe never accepts or changes passwords. Neither service credential may own the database or hold
grant options. The migration owner remains separate and may be `NOLOGIN`.

The `appsurface_durable` schema is package-reserved. The recipe serializes with migrations and runtime transactions,
then transfers every table, partition, sequence, view, materialized view, and foreign table in that schema to the
migration owner. Do not place application-owned objects there.

| Principal | Allowed privileges |
| --- | --- |
| Dispatcher | Schema `USAGE`; table `SELECT` on `dispatch` only. |
| Runtime reads | Schema `USAGE`; table `SELECT` on `store_metadata`, `schema_migration`, `scope`, `work`, `dispatch`, `work_operator_command`, `effect_permit`, `scope_history`, and `work_history`. |
| Runtime inserts | Table `INSERT` on `scope`, `work`, `dispatch`, `work_operator_command`, `effect_permit`, `scope_history`, and `work_history`. |
| Runtime updates | Column `UPDATE` on `scope(generation, state, updated_at)`, `work(state, due_at, updated_at, terminal_at, cancellation_requested_at, attempt_number, lease_generation, lease_owner, lease_started_at, lease_expires_at, runtime_epoch, revision, result_contract_id, result_schema_version, result_codec_id, result_classification, result_retention_policy_id, result_payload, result_sha256, terminal_code)`, `dispatch(due_at, state, expected_revision, updated_at)`, `work_operator_command(status, resulting_state, resulting_revision, completed_at)`, and `effect_permit(status, observed_at, details, runtime_epoch)`. |
| Runtime sequences | `USAGE` and `SELECT` on every sequence in the package schema. |

Neither service credential receives schema `CREATE`, table-wide `UPDATE`, `DELETE`, `TRUNCATE`, `REFERENCES`,
`TRIGGER`, or `MAINTAIN`; the dispatcher receives no sequence privileges. Forced RLS remains an additional scope fence,
not the reason destructive privileges are safe.

## Accept Work

Create `PostgreSqlDurableWorkOptions` from the non-empty StoreId and explicitly active epoch returned by deployment:

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

Read the normative [`Work protocol v1`](../work-protocol-v1.md), the
[`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md), and the
[`slice 3 reconstruction ledger`](../slice3-reconstruction.md) for exact behavior, safe responses, and lineage.

## Release Guidance

From the repository root, `./Durable/verify-postgresql.sh --quick` runs focused local proof and `--ci` runs the strict
real-PostgreSQL gate. The [`package chooser`](../../packages/README.md) is the generated adoption/publication source, and
the [`release hub`](../../releases/README.md) owns coordinated release policy.
