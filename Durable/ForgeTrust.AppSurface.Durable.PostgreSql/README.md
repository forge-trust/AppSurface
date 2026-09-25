# ForgeTrust.AppSurface.Durable.PostgreSql

> **Public preview:** this package supplies explicit PostgreSQL schema management, Work, Flow, Schedule, an explicitly
> opted-in bounded runtime host, and durable W3C causal evidence. Storage registration itself starts no worker or hosted
> service.

Choose this package when an application must commit its domain mutation and durable Work acceptance in the same
PostgreSQL transaction, and when process-loss recovery must use explicit leases, runtime/scope fences, effect permits,
and provider-safety policy. Choose a larger workflow platform for arbitrary deterministic replay, child workflows, or
unbounded fan-out. PostgreSQL is this provider's sole durable truth.

The verified database floor is PostgreSQL 16+; default verification and strict CI tests are pinned to PostgreSQL 16.5 via `postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877`.

The package references the adopter-facing
[`ForgeTrust.AppSurface.Durable`](../ForgeTrust.AppSurface.Durable/README.md) contracts and the
[`ForgeTrust.AppSurface.Durable.Provider`](../ForgeTrust.AppSurface.Durable.Provider/README.md) SPI. Neither package
depends on PostgreSQL.

## Slice 7 discovery and reconciliation

This provider is a public preview. Registration is passive: `AddAppSurfaceDurablePostgreSql` installs storage and runtime
services but does not
start a worker, open a connection, or apply DDL. Continuous processing requires the explicit
[`AddWorkerHost()` opt-in](#run-a-worker-host). Startup validates the installed schema and active epoch and never
applies DDL or advances migration history.

The production migration order is `0001_work_shared.sql`, `0002_forced_rls.sql`, `0003_flow_protocol.sql`,
`0004_schedule_protocol.sql`, `0005_runtime_heartbeat.sql`, `0006_flow_trace_context.sql`,
`0007_flow_retention.sql`, `0008_flow_repair.sql`, `0009_work_contract_discovery.sql`, and
`0010_runtime_health_observation.sql`, followed by the matching released provider package's
`contentFiles/any/any/configure-postgresql-roles.sql` role recipe. The package recipe is byte-identical to the
canonical [`Durable/configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql)
source, as checked by packed-consumer verification. Prefer generating the Durable schema script offline, reviewing it,
applying the forward-only migrations, running that package recipe with the complete reviewed manifest, and completing
schema status/preflight before enabling the worker host. The
[`durable schema` command family](../../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands)
keeps scripts offline while its online commands accept no connection-string argument and never print connection
strings. Its explicit `apply --apply` path resolves only a named migration-owner environment variable, normally
`--connection-env APPSURFACE_DURABLE_MIGRATION_CONNECTION`.

For reconciliation, check status first, create a corrected and reviewed forward-only script, and retry. Never delete
or rewrite migration history. The [`durable-postgresql` example](../../examples/durable-postgresql/README.md) is a
local proof of these boundaries, not production operations guidance.

## Operational assessment

The [operational-assessment adoption guide](../operational-assessments.md) is the complete existing-host and
external-activator recipe. PostgreSQL health exposes the provider-neutral `WasStoreObserved`, `CanEnableActivation`,
`CanAttemptPump`, and `IsReady` predicates. Use `CanAttemptPump` only as context; call
`IDurableRuntimePumpAdmission.TryRunOnceAsync` directly so health and admission cannot become a check-then-act pair.

| Attempt | Public fields | Meaning and remedy |
| --- | --- | --- |
| `Completed` | Result required, no problem code | Application execution returned and terminal bookkeeping completed. Inspect counts; completion is not item-level success. |
| `Refused` | No result, no problem code | Local overlap, closed process gate, drain, active store pass, or worker-generation refusal. Wait or reconcile as appropriate. |
| `Unavailable` | No result, `ASDUR103` | The store was not observed. Transport/pool, provider-deadline, and permission (`42501`) causes have different remedies. |
| `Incompatible` | No result, `ASDUR108` or `ASDUR400`–`ASDUR403` | The store was observed and rejected the runtime. Follow schema or authorized epoch recovery. |

The returned non-completed outcomes certify that this invocation did not enter `RunPassAsync`; they do not certify
the status of another process or a lost response. Exceptions before or after execution retain their original semantics.
After `RunPassAsync` returns, caller cancellation no longer controls bounded successful-sweep finalization.

### Composition requirement

The legacy `IDurableRuntimePump` and admission-aware `IDurableRuntimePumpAdmission` must resolve to one concrete
singleton when a provider implements both. A custom replacement must register the same instance under both interfaces
before provider registration. Replacing only one interface can create independent process-local slots and is unsupported
for concurrent activation. Use an in-process fake only to compile and exercise the public algebra; it is not evidence
of PostgreSQL admission or store health.

```csharp
var customPump = new MyDurablePump(/* custom provider dependencies */);
services.AddSingleton<IDurableRuntimePump>(customPump);
services.AddSingleton<IDurableRuntimePumpAdmission>(customPump);
services.AddAppSurfaceDurablePostgreSql(
    dispatcherDataSource,
    runtimeDataSource,
    workOptions,
    scheduleOptions);
```

Resolve both interfaces after building the provider and assert `ReferenceEquals` before concurrent use. The complete
[custom-composition recipe](../operational-assessments.md#custom-composition) includes that assertion, and the
[packed consumer](../packed-consumers/PostgreSqlProvider/Program.cs) compiles and runs the public four-kind contract.

### Diagnostics matrix

| Cause | Stable evidence | Safe operator action |
| --- | --- | --- |
| Transport or pool | `Unavailable` / `ASDUR103`, cause `Transport` | Check connectivity, pool capacity, and database availability; retry by host policy |
| Provider deadline | `Unavailable` / `ASDUR103`, cause `ProviderDeadline` | Inspect exact due aggregation, query plan, backlog, pool wait, and command bounds |
| Permission | `Unavailable` / `ASDUR103`, cause `PermissionDenied` / SQLSTATE `42501` | Rerun and verify the canonical [role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql); do not retry blindly |
| Schema incompatibility | `Incompatible` / `ASDUR400`–`ASDUR403` | Generate/review/apply forward migrations as migration owner, then rerun roles and preflight |
| Epoch mismatch | `Incompatible` / `ASDUR108` | Follow the authorized epoch rotation or restore procedure |

Structured events `4110` (health unavailable), `4111` (admission unavailable), `4112`/`4113` (refusal), and `4114`
(failed-pass cleanup) contain only operation, phase, cause, code, and documentation anchor as applicable. They do not
expose SQL, exception text, credentials, role names, payload identifiers, scope identifiers, or aggregate identifiers.

### Migration 9 -> 10 and rollback

For the #794 provider release, drain and stop every durable worker and Schedule writer, keep pre-`0009` workers
stopped, generate and review the exact script, and verify that the configured migration owner owns the schema-9
`runtime_due_dispatch_health(integer)` function. Repair owner drift with the canonical role recipe before migration
if needed. Then apply migration `0010_runtime_health_observation` from schema 9 to 10 with that owner through the
generated script or explicit CLI apply command, rerun the role recipe
for post-migration reconciliation, run status and preflight, smoke-test `v0.2.0-preview.8`, and deploy the new binary.
Registration never applies the migration.

Migration history is immutable. A schema-10 defect is repaired by a reviewed corrective forward migration. Binary
rollback to `v0.2.0-preview.8` keeps schema 10 in place and requires status, health, heartbeat, and real Work smoke
tests under the reconciled roles. A pre-`0009` binary is not a supported rollback target after the role recipe removes
broad dispatcher access. See the [adoption guide](../operational-assessments.md#migration-and-role-reconciliation)
for the complete rollout boundary and local-proof behavior.

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
- `ApplyAsync` applies pending known migrations under one session advisory lock. Migration 0010 receives a 330-second
  client allowance so its five-minute server-side index-build deadline remains authoritative even when the
  migration-owner data source has a shorter default command timeout; earlier migrations retain that configured
  command timeout;
- `InitializeRuntimeEpochAsync` activates the first epoch exactly once; and
- `RotateRuntimeEpochAsync` compare-and-swaps the active epoch after restore or an authorized recovery event.

Runtime mutations take a shared, transaction-scoped advisory fence before validating the active epoch. Schema changes
and epoch rotation take the exclusive package lock, so they wait for in-flight runtime transactions and prevent an old
epoch from committing new durable state after rotation.

Runtime roles never own schema or apply DDL. Apply the ordered migrations in numeric order:
`0001_work_shared.sql`, `0002_forced_rls.sql`, `0003_flow_protocol.sql`, `0004_schedule_protocol.sql`,
`0005_runtime_heartbeat.sql`, `0006_flow_trace_context.sql`, `0007_flow_retention.sql`,
`0008_flow_repair.sql`, `0009_work_contract_discovery.sql`, and `0010_runtime_health_observation.sql`.

`0009_work_contract_discovery.sql` introduces registry-scoped Work discovery and the payload-free
discovery function `appsurface_durable.discover_work_dispatch(text[], text[], integer)`. Its contract-lookup index and
`0010_runtime_health_observation.sql`'s partial Schedule lease-expiry index use transactional `CREATE INDEX`, which can
block concurrent writes to their respective tables. Schedule each migration in a reviewed maintenance window after
draining every affected runtime and writer host; the package's checksum-bound transactional migration protocol cannot
use `CREATE INDEX CONCURRENTLY`. Migration `0010` takes its Schedule table lock with `NOWAIT` and bounds the index
statement at five minutes, so an active writer or unexpectedly long build fails and rolls the migration back instead
of silently extending the write outage. Keep writers stopped, inspect the cause, and retry the same forward migration.
The embedded `.sql` resource is a migration fragment: its `SET LOCAL` depends on the transaction wrapper emitted by
`GenerateScript` or created by `ApplyAsync`. Do not execute that fragment directly with `psql`.
Apply schema is forward-only; rolling application code back does not authorize destructive schema rollback. Execute
generated SQL with a client that stops on the first error; `psql` callers must pass `-v ON_ERROR_STOP=1`.

## Typed Work-exit rollout

PostgreSQL is the V1 provider for the opt-in [typed executor exit contract](../ForgeTrust.AppSurface.Durable/README.md#exit-aware-work-for-a-proven-pre-effect-retry). It calls the Provider SPI's `InvokeExitAsync` only after
recording the existing effect permit, then translates the fact once through the existing completion path—no schema,
migration, SQL function, or retry-delay surface is added.

| Executor exit | PostgreSQL completion fact | Resulting provider-owned behavior |
|---|---|---|
| `Succeeded(result)` | `Succeeded` | Existing success or cancel-requested-success rules. |
| `RetryBeforeEffect(code)` | `ProvenNoEffect` | Marks the permit `proven_no_effect` and applies existing retry delay, attempt, deadline, cancellation, history, dispatch, Flow, and Schedule rules. |
| `FailedTerminal(code)` | `FailedTerminal` | Preserves the post-permit safety matrix; `ProviderKeyed` is suspended because the effect may have occurred. |
| `AmbiguousExternalOutcome(code)` | `AmbiguousExternalOutcome` | Preserves the existing ambiguity/recovery path and never retries blindly. |
| Uncaught exception, cancellation, lease loss, codec failure, or legacy-boundary mismatch | `AmbiguousExternalOutcome` / `ASDUR106` | Existing failure behavior; only an explicit retry-before-effect fact can use `ProvenNoEffect`. |

Roll out an exit-aware Work in this order: deploy Provider/PostgreSQL binaries that invoke exits; confirm every
eligible worker has restarted and captured a capable registry snapshot; register a **new immutable Work version** with
`AddDurableWorkExit`; then accept that version. Old registries must not discover it. For rollback, stop accepting the
new version but keep capable workers until its accepted Work is terminal or deliberately suspended. See the
[protocol's typed-exit rules](../work-protocol-v1.md#typed-executor-exits) and [exit-aware diagnostics](../../troubleshooting/durable-diagnostics.md#exit-aware-work-codes).

Create host principals outside migrations. Use [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) to
grant the migration-owner, payload-free dispatcher, scoped-runtime, and scoped-retention-operator capabilities. Service roles must not receive
ownership or `BYPASSRLS`. Transaction-local scope context is defense in depth, not a replacement for application
authorization. The recipe fails before granting privileges when role names alias each other or a service role can
inherit the migration owner, `SUPERUSER`, or `BYPASSRLS`. It refuses any manifest service role that already owns a
package schema object before transferring package ownership to the migration owner; repair that ownership through a
separately reviewed administrator operation, then rerun the complete manifest. Existing direct,
inherited, or `PUBLIC` schema, relation, column, and sequence privileges outside the documented allowlist cause the
transactional recipe to fail and roll back; remove those host-managed grants before retrying. The runtime role is
deliberately fully trusted for the unscoped `runtime_heartbeat` table: the health component owns the
worker-generation fence through its row lock and compare-and-swap predicates, so applications must not expose that
credential to untrusted callers.

Apply schema migrations before the normal post-migration role-recipe run. A migration can add package relations, but
the recipe owns the reviewed grants for existing service roles; running it second is required before Flow runtime or
dispatcher connections can use the new relations. Migration 0010 also verifies that its schema-9 function is already
owned by the configured migration principal. If that preflight exposes historical owner drift, run the canonical
recipe once as a repair before retrying migration 0010, then run it again after the migration for normal reconciliation.

## Run a worker host

After the schema is current, the recovery epoch is initialized, and the role recipe has run, compose exactly one
runtime-role and one payload-free dispatcher-role data source. The warm path is designed to reach one hosted Work
completion in five minutes; it never performs DDL at application startup.

Custom [`IDurableWorkRegistry`](../ForgeTrust.AppSurface.Durable/README.md#public-api-by-audience) implementations
must expose a complete, stable `RegisteredContracts` snapshot before resolving `IDurableRuntimePump`. PostgreSQL
snapshots the list once to define that host's discovery authority, rejects default/duplicate or more than 10,000 pairs
with [`ASDUR119`](../../troubleshooting/durable-diagnostics.md#asdur119), and does not observe later registry mutation. An empty
registry is valid and makes Work passes quiescent. Restart
the host after changing the registered contracts. This is an execution-isolation boundary among trusted worker hosts,
not an authorization boundary within the dispatcher credential: callers that possess that credential may invoke its
payload-free discovery function with valid contract identities. Keep the exact dispatcher credential out of untrusted
code, just as for the unscoped runtime-heartbeat credential.

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

For the Source Lifecycle lane, bind its own `work_only` dispatcher and runtime data sources, and restrict both its
continuous host and every direct pump request to Work. `HostedSurfaces` defaults to `All`, so set it explicitly:

```csharp
services.AddAppSurfaceDurablePostgreSql(
        sourceDispatcherDataSource,
        sourceRuntimeDataSource,
        sourceWorkOptions,
        sourceScheduleOptions,
        options =>
        {
            options.WorkerId = "source-worker-01";
            options.HostedSurfaces = DurableRuntimeSurface.Work;
        })
    .AddWorkerHost();

// Resolve from the built host, after service registration is complete.
var sourceAttempt = await host.Services
    .GetRequiredService<IDurableRuntimePumpAdmission>()
    .TryRunOnceAsync(
        new DurableRuntimePumpRequest(
            maximumItems: 32,
            timeBudget: TimeSpan.FromSeconds(10),
            surfaces: DurableRuntimeSurface.Work),
        cancellationToken);
```

Use the same Work-only selection for direct calls and recovery passes; never rely on the hosted setting to constrain an
independently constructed `DurableRuntimePumpRequest`. Flow or Schedule selection fails closed under the source
dispatcher profile. This prevents accidental cross-surface execution, while still not making contract selectors or
runtime `scope_id` values lane authorization.

`AddAppSurfaceDurablePostgreSql` resolves Work, Flow, Schedule, schema, pump, health, drain, and
`IFlowRepairOperatorClient` services but installs no `IHostedService`, opens no connection, and applies no migration.
`AddWorkerHost()` is the standard continuous activation path. On startup it validates schema compatibility and the
active epoch, but never applies DDL or advances migration history. It also validates that `TimeBudgetPerPass +
(2 * ShutdownReserve)` fits inside `HostOptions.ShutdownTimeout`; an invalid store or host configuration fails closed.

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
an application-owned authorized health endpoint. `Healthy` is the only ready state; every other state is intentionally
not ready, including `NotStarted`, `Stale`, `Draining`, `Incompatible`, and `Unavailable`. Snapshots contain aggregate counts and fixed codes only—never payload,
scope, aggregate, connection, or trace values. At shutdown, local admission closes synchronously before the host
persists drain; already-permitted Work follows its ordinary cancellation/recovery path rather than inventing a result.

For a cold path, first drain and stop every pre-`0009` worker because the role recipe intentionally removes its raw
`dispatch` access. Apply every pending forward-only migration through `0010_runtime_health_observation.sql` with the migration owner, then use the matching released provider package's
`contentFiles/any/any/configure-postgresql-roles.sql` with the complete reviewed manifest. Verify the active epoch and StoreId, deploy with `AddWorkerHost()` disabled, then enable it. Never destructively roll
back a migration. After the role recipe runs, a pre-`0009` worker is not a compatible application rollback target because its dispatcher
credential no longer has raw `dispatch` access; keep that worker stopped and roll forward to a `0009`-compatible binary instead. Do not
restore the broad grant as a rollback shortcut.

### Role recipe contract

Run the canonical [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql)
with `psql` as a principal that can transfer ownership and grant privileges. Every invocation supplies the complete,
reviewed version-1 manifest; it is not an add-pair command. The checked-in
[fictional two-pair manifest](https://github.com/forge-trust/AppSurface/blob/main/examples/durable-postgresql/role-pairs.example.json) contains role names only,
never passwords or connection strings:

For deployments, use the matching released provider NuGet package's
`contentFiles/any/any/configure-postgresql-roles.sql` after applying that release's migrations. The repository
verification gate [`Durable/verify-packed-consumers.sh`](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-packed-consumers.sh) checks that this packaged file
is byte-identical to the canonical source recipe. Record the exact package version with the manifest evidence; do not
extract the recipe from an unrelated package version or run a workspace recipe against a database at another schema
version. The source-checkout path is for the disposable local proof only.

```json
{
  "version": 1,
  "pairs": [
    { "dispatcher": "forwarding_dispatcher", "runtime": "forwarding_runtime", "dispatcher_profile": "full" },
    { "dispatcher": "source_dispatcher", "runtime": "source_runtime", "dispatcher_profile": "work_only" }
  ]
}
```

Version 1 has exactly `version` and `pairs` at the top level, with `version: 1` and 1–32 entries. Each pair has exactly
`dispatcher`, `runtime`, and explicit `dispatcher_profile` (`full` or `work_only`); there is no default. Unknown or
duplicate JSON properties, unknown fields, malformed types, empty or over-limit sets, invalid/aliased roles, and names
that resolve with PostgreSQL truncation are rejected before mutation. Names are data and the recipe quotes resolved
identifiers. Pair roles and the explicitly supplied migration owner and retention operator are all distinct.

Store the reviewed manifest in deployment configuration with the role declarations and hash the exact UTF-8 bytes of
the file passed to `psql`. Record its path, SHA-256 digest, pair names, and profiles in release/certificate evidence;
whitespace and pair-order changes alter the digest and require review. Compare the supplied file with the prior reviewed
release record on every deployment. The locked catalog check rejects omitted roles still visible in managed policy
targets or package ACLs and refuses contradictory or ambiguous catalog state. Catalog inference cannot detect a former
pair after a privileged actor has erased every trace; the previous deployment manifest is independent evidence for that
case. A separately designed database registry would be needed for database-only historical proof.

Every pair role must be a distinct restricted `LOGIN` leaf: no membership edge, ownership, grant option,
`SUPERUSER`, `CREATEDB`, `CREATEROLE`, `REPLICATION`, or `BYPASSRLS`. Create and rotate credentials through the
deployment secret system; the recipe never accepts or changes passwords. The migration owner remains separate and may
be `NOLOGIN`.

| Dispatcher profile | Direct privileges | Intended lane |
| --- | --- | --- |
| `full` | Schema `USAGE`; Work discovery and Schedule claim `EXECUTE`; direct `SELECT` on payload-free `flow_dispatch` | Existing forwarding pair; preserves Work, Flow, and Schedule discovery behavior. |
| `work_only` | Schema `USAGE`; Work discovery `EXECUTE` only | Source lane; no direct Durable table, column, or sequence access and no Flow/Schedule function execution. |

Both profiles retain the established runtime grant set for their paired runtime. That runtime grant set, transaction-local
`scope_id` checks, shared StoreId/epoch, and Work registration selection do not create a database-enforced lane row
partition. The Work discovery function's caller-supplied contract arrays select routing candidates; they do not
authenticate a contract or prevent a credential holder from requesting another valid Work contract's routing metadata.
Use a separate store or a separately designed PostgreSQL partition when independent row-level authorization or a
separate failure domain is required. The three operational choices and their costs are summarized in the
[adoption guide](../operational-assessments.md#shared-store-role-pair-choice).

Omitting an installed pair is an error, never retirement. The recipe preserves healthy policy OIDs, targets,
expressions, ACLs, owners, and effective grants on an identical rerun; it rejects unexpected extra principals and broader
privilege drift. Missing narrow grants for a listed profile may be restored. Narrowing `full` to `work_only` is refused;
an explicitly reviewed `work_only` to `full` expansion requires renewed privilege proof and a new certificate. Pair
retirement and profile narrowing need a separately reviewed procedure. A failure rolls back recipe changes. The advisory
lock serializes catalog reconciliation, while policy DDL may briefly wait on active work; use the bounded drain and
maintenance procedure in the [adoption guide](../operational-assessments.md#migration-and-role-reconciliation).

For the executable two-pair psql command, identical rerun, omission refusal, and source privilege check, see the
[version-1 local walkthrough](../../examples/durable-postgresql/README.md#version-1-role-pair-walkthrough).

The `appsurface_durable` schema is package-reserved. The recipe serializes with migrations and runtime transactions,
then transfers every table, partition, sequence, view, materialized view, foreign table, and package function in that
schema to the migration owner. Do not place application-owned objects there.

| Principal | Allowed privileges |
| --- | --- |
| Migration owner | Owns package schema and package objects and retains the resulting owner authority needed for reviewed migrations/reconciliation. It remains separate from every service role and retention operator. |
| `full` dispatcher | Schema `USAGE`; `SELECT` on the payload-free `flow_dispatch` relation; `EXECUTE` on Work discovery and Schedule claim functions. It receives routing IDs, due time, priority, and revision only, never raw `dispatch` columns. |
| `work_only` dispatcher | Schema `USAGE` and `EXECUTE` only on `discover_work_dispatch(text[], text[], integer)`; no direct Durable table, column, or sequence privilege, and no Flow/Schedule function execution. |
| Runtime reads | Schema `USAGE`; table `SELECT` on package metadata, scoped Work, Flow, Schedule, and Flow trace-context relations. Work/Flow `discovery` reads are scope-filtered by transaction-local RLS or dispatcher function policy. |
| Runtime inserts | Table `INSERT` on scoped Work, Flow, Schedule, and Flow trace-context relations. |
| Runtime updates | Reviewed column-level `UPDATE` on mutable Work, Flow instance/wait/timer, Schedule definition/occurrence/dispatch, and dispatch fields; no table-wide update grant. |
| Runtime sequences | `USAGE` and `SELECT` on every sequence in the package schema. |
| Runtime heartbeat | Unscoped `SELECT` and `INSERT`, plus reviewed column-level `UPDATE`, on `runtime_heartbeat` for `IDurableRuntimeHealth`. Its forced RLS policy intentionally uses `USING (true)` and `WITH CHECK (true)`; keep this fully trusted runtime credential out of untrusted callers. |
| Retention operator | Scope-filtered Flow/Work-reference and retention-evidence reads; `EXECUTE` only on the owner-run manifest and lifecycle capabilities. It has no direct lifecycle/source `INSERT`, `UPDATE`, `DELETE`, sequence, dispatcher-discovery, Schedule, worker-host, or migration access. |
| `PUBLIC` | No effective privilege on the package schema, relations, columns, sequences, or functions. Canonical revokes are reapplied and verified in the recipe transaction. |

Neither service credential receives schema `CREATE`, table-wide `UPDATE`, `DELETE`, `TRUNCATE`, `REFERENCES`,
`TRIGGER`, or `MAINTAIN`; the dispatcher receives no sequence privileges. Forced RLS remains an additional scope fence,
not the reason destructive privileges are safe. The recipe also rejects disabled or unforced RLS and any policy whose
name, command, role target, permissiveness, `USING`, or `WITH CHECK` expression differs from the reviewed migration.

Before the pre-created next month starts, a migration-owner operation must run
`SELECT appsurface_durable.ensure_schedule_history_partitions();`. It retains the current and next Schedule-history
partitions and reapplies their forced RLS policy. Runtime and dispatcher roles cannot execute this maintenance function.

## Verified Flow retention

Register retention only after the schema is current and the complete role-pair manifest recipe has completed. Supply a dedicated
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

The application supplies command identity, actor, reason, and the expected lifecycle sequence for every mutation. A
minimal orchestration shape is:

```csharp
var assessmentResult = await retention.AssessAsync(
    new DurableRetentionAssessmentRequest(scopeId, flowInstanceId),
    cancellationToken);

if (!assessmentResult.IsSuccess ||
    assessmentResult.Value.Status != DurableRetentionAssessmentStatus.Safe)
{
    // Stop for Blocked or Indeterminate; do not create a manifest.
    return;
}

var manifestResult = await retention.CreateManifestAsync(
    new DurableRetentionManifestCreateRequest(
        new DurableCommandId("retention-manifest-command"),
        assessmentResult.Value),
    cancellationToken);
if (!manifestResult.IsSuccess)
{
    return;
}

var manifest = manifestResult.Value.Manifest;
var packageResult = await retention.BuildArchivePackageAsync(
    scopeId,
    manifest.ManifestId,
    cancellationToken);
if (!packageResult.IsSuccess)
{
    return;
}

var package = packageResult.Value;
await archiveStore.WriteAsync(package, cancellationToken);
var receipt = await archiveStore.CreateReceiptAsync(package, cancellationToken);
var sequence = manifest.LifecycleSequence;

var receiptResult = await retention.RecordArchiveReceiptAsync(
    new DurableRetentionRecordArchiveReceiptRequest(
        scopeId, manifest.ManifestId, new DurableCommandId("retention-receipt-command"),
        actorId, "verified-flow-retention", sequence, receipt),
    cancellationToken);
if (!receiptResult.IsSuccess)
{
    return;
}

var verifyResult = await retention.VerifyArchiveAsync(
    new DurableRetentionVerifyArchiveRequest(
        scopeId, manifest.ManifestId, new DurableCommandId("retention-verify-command"),
        actorId, "verified-flow-retention", sequence + 1),
    cancellationToken);
if (!verifyResult.IsSuccess)
{
    return;
}

var purgeResult = await retention.PurgeAsync(
    new DurableRetentionPurgeRequest(
        scopeId, manifest.ManifestId, new DurableCommandId("retention-purge-command"),
        actorId, "verified-flow-retention", sequence + 2),
    cancellationToken);
if (!purgeResult.IsSuccess)
{
    return;
}
```

Treat each `DurableOperationResult` as a checked boundary: handle `IsSuccess == false`, preserve the returned
command outcome for retries, and stop on `Blocked`, `Indeterminate`, source changes, stale lifecycle sequences, or
holds. The archive write and receipt creation are external application steps; the provider never treats a receipt as
proof that external bytes are durable or legally adequate.

`DFA1` package bytes are returned before external I/O. The archive receipt is an opaque adopter assertion, not a URI,
availability check, encryption proof, or compliance determination. Source-correspondence verification rebuilds the
canonical package and compares its SHA-256 and frozen closure digest. Applications must document and operate their own
archive store, key management, retention policy, legal holds, performance/WAL evidence, and recovery objectives.

## Typed Work codec sharing with Flow

A [typed Work definition](../ForgeTrust.AppSurface.Durable/README.md#typed-work-definitions) can share its input codec
with a Flow context. Register the Work binding and Flow once per identity. Flow startup accepts the definition-owned
view and the provider-owned canonical view of the same captured source; matching metadata on an unrelated codec is
insufficient. `PostgreSqlDurableFlowClient.StartAsync` decodes through the selected allowlisted codec and validates its
result before writing the start command. Subsequent evaluations use the selected codec for context decode and encode.
If a custom registry returns the original source of a definition-owned view, Flow retains that view's captured
payload checks around the same source. Selecting the raw source cannot bypass classification or retention checks.
See the [migration guide](../migrations/typed-work-definitions-v1.md#reuse-lookup-and-concurrency) for exact lookup,
static definition reuse and deployment boundaries. This authoring change requires no schema migration.

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

## Repair an ASDUR211 child-effect suspension

Apply the additive [`0008_flow_repair.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/Migrations/0008_flow_repair.sql) migration after `0007_flow_retention.sql`, rerun the
[`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) recipe, and deploy a compatible public-preview
binary before enabling repair callers. Existing suspensions that lack the V1 descriptor identity intentionally return
`ASDUR218`; the migration does not invent a digest from an incomplete legacy shape.

An authorized host resolves `IFlowRepairOperatorClient`, calls `GetAssessmentAsync` with its trusted scope and Flow
id, chooses a current payload-free candidate, then submits its matching static request factory. A completed-effect
candidate proves a retained typed Work result; a no-effect candidate proves a named `manual_resolve` command with
`resolution_kind = proven_not_applied`. The repair transaction locks scoped evidence, records one terminal repair
command, and returns a receipt for an applied or exact-duplicate request. It never calls a Work executor. A stale
assessment returns a refusal or race result instead of guessing.

`ReleaseSuspensionAsync` is not a repair shortcut: V1 child-effect descriptors fail with `ASDUR211`. If no candidate
is offered, retain the suspension, follow the relevant `ASDUR218`–`ASDUR220` remediation, and do not use direct SQL
to change Work, wait, or Flow state. Rollback is code-level: disable repair callers or return to a compatible binary
while retaining the forward-only additive schema.

Read the normative [`Work protocol v1`](../work-protocol-v1.md), [`Flow protocol v1`](../flow-protocol-v1.md), [Durable Flow trace context v1](../flow-trace-context-v1.md), the
[`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md), the
[`slice 3 reconstruction ledger`](../slice3-reconstruction.md), and the [`slice 4 reconstruction ledger`](../slice4-reconstruction.md) for exact behavior, safe responses, and lineage.

## Verification

From the repository root, `./Durable/verify-postgresql.sh --quick` runs focused Work proof, `./Durable/verify-postgresql.sh --quick --flow` runs focused Flow proof, and `./Durable/verify-postgresql.sh --quick --schedule` runs the real PostgreSQL Work-first Schedule proof. `--ci` runs the complete strict real-PostgreSQL suite; `--ci --flow` performs its compatibility preflight before that suite, while `--ci --schedule` also runs the complete suite without a Schedule-specific filter.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->
