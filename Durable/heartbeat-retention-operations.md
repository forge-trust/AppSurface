# PostgreSQL runtime heartbeat retention: schema 11

Start here when adding automatic cleanup of old `runtime_heartbeat` identities to an existing [Durable PostgreSQL provider](ForgeTrust.AppSurface.Durable.PostgreSql/README.md). The table contains one row per configured worker ID. A stopped process cannot remove its row, and deployments that create new IDs can accumulate stale rows. The provider keeps 24 hours of heartbeat history by default, then removes eligible rows in bounded batches. Cleanup manages capacity; it does not determine Work correctness or readiness.

The [local PostgreSQL proof](../examples/durable-postgresql/README.md#one-command-local-proof) demonstrates one 500-row deletion, current-row survival, and a healthy Work pass on a disposable database. The [schema CLI](../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands) handles explicit migration and preflight. Application startup never applies DDL.

## Configure the provider

`AddAppSurfaceDurablePostgreSql` owns the validated settings. The provider's package pump signals maintenance immediately after its first admitted pass commits the current heartbeat. It does not wait for pruning before executing Work. Later maintenance continues without more passes until the worker host stops or the provider is disposed. A service that never admits a pass does not begin maintenance. Replacing the package pump with a custom `IDurableRuntimePump` bypasses this trigger; custom-pump owners must schedule cleanup deliberately or use the package pump.

```csharp
services.AddAppSurfaceDurablePostgreSql(
    dispatcherDataSource,
    runtimeDataSource,
    workOptions,
    scheduleOptions,
    options =>
    {
        options.WorkerId = "worker-1";
        options.EnableHeartbeatMaintenance = true; // default
        options.HeartbeatRetention = TimeSpan.FromHours(24); // default
        options.HeartbeatMaintenanceCadence = TimeSpan.FromHours(24); // default
        options.HeartbeatPruneBatchSize = 500; // default
    });
```

Set `EnableHeartbeatMaintenance = false` in a new provider registration to pause cleanup during an incident; passes and heartbeat admission continue. Re-enable it in a later registration to resume. These are startup options, not a live toggle. Retention accepts 24 hours through 3,650 days and must exceed `HeartbeatStaleAfter`. Cadence accepts one hour through 30 days; batch size accepts 1 through 5,000. Keep the 24-hour default when older identities have little diagnostic value; increase it only when your incident workflow needs a longer lookback and capacity evidence supports it. PostgreSQL time defines the cutoff, so host clock drift does not change eligibility.

The first due call begins at admission. A full batch schedules another call no sooner than one minute after the previous start; a partial or zero result returns to the configured cadence. Failures retry no sooner than one hour. At the default cap, a sustained backlog can clear at most 500 rows per minute, or 720,000 per day; 100,000 rows require about 200 calls, starting from minute 0 through minute 199 plus SQL execution time. Database outages and churn above that rate extend catch-up. Observe actual churn, deletion rate, pool pressure, and renewal latency before changing the batch size.

The function uses `last_heartbeat_at < cutoff`, an ordered index, `FOR UPDATE SKIP LOCKED`, a cluster-wide nonblocking advisory lock, and an exact worker ID/instance exclusion. `pass_active` is ignored because abrupt process loss can leave it true. Renewal that locks first is skipped. Pruning that locks first may make a writer wait; the old row may be deleted, and takeover must retry or re-register. Prune calls have a short server and client time budget; the release proof must keep renewal p95 below five seconds and maximum below the default 15-second stale bound. PostgreSQL `DELETE` creates dead tuples and does not immediately shrink files. Observe live/dead tuple estimates, relation and index size, autovacuum, and sustained rate during rollout.

## Deploy schema 11

This is a **downtime migration**. `0011_runtime_heartbeat_retention.sql` creates a normal transactional index, which can lock the heartbeat table. It cannot use `CREATE INDEX CONCURRENTLY` inside the package's checksum-bound transaction. The migration's five-second `lock_timeout` limits acquisition of the table lock, not the full index-build duration. The package migration advisory lock has a separate 30-second acquisition deadline with 75–125 ms jittered attempts. CLI `apply` allows 390 seconds for the overall operation and 330 seconds for the index-build command. Measure build duration on a representative table before choosing CLI `apply` or a reviewed generated script executed under the same package lock.

1. Pack and stage the schema-11-capable package while the old deployment remains active. Pin the **immediately previous published PostgreSQL provider artifact**, its SHA-256, version, and source commit. Run its actual Work and health path on schema 11 in the release lane. The current proof passes with `0.2.0-preview.8` on schema 11; verify that it is still the immediately previous artifact at publication time.
2. Drain and stop old runtimes and writers. Inspect status and generate the reviewed migration script. A pre-migration `preflight` failure for pending 0011 is an **expected downtime finding**, not a passing activation gate.
3. Apply 0011 with the migration-owner credential, then run the canonical [role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) with the four named roles. The recipe transfers function/schema ownership, revokes other execute grants, grants the exact prune signature to the runtime role, and installs the migration-owner heartbeat policy needed for the `SECURITY DEFINER` function under forced row level security.
4. Run schema status and **passing** preflight with the restricted runtime-role connection. Verify version 11, function owner/security/search path/ACL, runtime role isolation, and valid `(last_heartbeat_at, worker_id)` btree index before activation.
5. Deploy the new binary and resume activation. Record the 100,000-stale/1,000-recent scale plan, index/buffer and deletion timing, live/dead tuples, relation/index bytes, autovacuum behavior, pool occupancy, and renewal p50/p95/max for both lock orders.

```bash
# Connection values live in environment variables; do not put them in command arguments.
appsurface durable schema status --connection-env APPSURFACE_DURABLE_MIGRATION_CONNECTION
appsurface durable schema script --from-version 10 --output /tmp/appsurface-schema-0011.sql
# Review the generated file and schedule the downtime window.
appsurface durable schema apply --connection-env APPSURFACE_DURABLE_MIGRATION_CONNECTION --apply
psql -v ON_ERROR_STOP=1 \
  -v migration_owner_role=appsurface_durable_owner \
  -v dispatcher_role=appsurface_durable_dispatcher \
  -v runtime_role=appsurface_durable_runtime \
  -v retention_operator_role=appsurface_durable_retention \
  -f Durable/configure-postgresql-roles.sql "$APPSURFACE_DURABLE_ROLE_RECIPE_CONNECTION"
appsurface durable schema status --connection-env APPSURFACE_DURABLE_RUNTIME_CONNECTION
appsurface durable schema preflight --connection-env APPSURFACE_DURABLE_RUNTIME_CONNECTION
```

If your deployment runs `appsurface durable schema preflight` before `apply`, expect a nonzero pending-0011 downtime diagnostic and stop the old workers before applying. Investigate any other preflight failure; do not mask its exit code. `APPSURFACE_DURABLE_ROLE_RECIPE_CONNECTION` must name a principal able to transfer schema ownership and reconcile grants, as described in the [role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql).

The CLI `apply` path is appropriate only if the measured 0011 index build fits its 330-second migration-command deadline and the full operation fits 390 seconds. For a larger table, use the reviewed generated script through a client with a deliberately sized operation deadline and `ON_ERROR_STOP=1`; keep the script's package advisory lock and transaction wrapper intact. Do not execute the embedded migration fragment directly. Do not run `apply` and the generated script concurrently. The same SQL migration must be recorded once in `schema_migration`.

## Recover and diagnose

Schema migrations are forward-only. If the five-second table-lock wait or the package advisory-lock acquisition times out, keep activation closed, inspect the blocker without printing another session's SQL or credentials, and retry the same reviewed forward operation in a maintenance window. The current exact `0.2.0-preview.8` binary has passed the schema-11 Work/health test, so a failed new-package deployment may redeploy that **same pinned binary** after the release lane repeats the proof. If the immediately previous binary changes or its proof fails before publication, set migration 0011's minimum reader and writer versions to 11, keep activation closed after commit, and roll forward. Never drop the index/function, lower the schema version, or infer compatibility for arbitrary older binaries.

The runtime role is deliberately trusted for the unscoped heartbeat table. The `SECURITY DEFINER` function permits repeated bounded deletion of eligible history using caller-supplied exclusion arguments. Forced row level security remains enabled: the runtime policy covers direct heartbeat admission and renewal, while a separate exact migration-owner policy lets the function owner see and delete eligible rows. Those arguments are a self-pruning guard, not authentication. A compromised runtime credential can remove all old diagnostic rows through repeated calls; isolate it from request-controlled code. Runtime must not own the schema, inherit another role, hold grant option, or have direct `DELETE`/`TRUNCATE`. The [role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) and post-migration preflight are the grant boundary. A dedicated maintenance credential is a separate design if an adopter cannot accept this trust model.

Maintenance emits `appsurface.durable.runtime_heartbeat.prune.attempts`, `.deleted`, and `.failures` on the `ForgeTrust.AppSurface` meter. Stable outcomes are `deleted`, `zero_or_contended`, `failed`, and `cancelled`; zero does not distinguish no eligible rows from an advisory-lock refusal. `disabled` is configuration state, not an SQL attempt. Logs and metrics must not use worker IDs, instance IDs, connection names, exception text, or credentials as attributes. Repeated failures do not change readiness by themselves. Check connection reachability, the exact function grant, and schema preflight; then retry after repair or pause maintenance through a new provider registration. The [Durable diagnostics catalog](../troubleshooting/durable-diagnostics.md) maps these findings to safe actions.
