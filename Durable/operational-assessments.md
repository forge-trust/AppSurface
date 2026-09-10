# Adopt Durable operational assessments

This is the task guide for upgrading an existing PostgreSQL worker or external activator to the additive Durable
operational-assessment contract. Start here if you need to decide whether activation is currently authorized, whether
one pump invocation actually entered application execution, or how to roll the package back safely. The package
reference remains in the [Durable](README.md), [Provider](ForgeTrust.AppSurface.Durable.Provider/README.md), and
[PostgreSQL](ForgeTrust.AppSurface.Durable.PostgreSql/README.md) READMEs.

## Audience and boundary

| You are | Start with | The provider decides | Your application still owns |
| --- | --- | --- | --- |
| Existing worker host | [existing-host recipe](#existing-host-recipe) | Store observation, schema/epoch compatibility, admission, pass bookkeeping | Process lifetime, traffic readiness, retry policy, authorization |
| External activator | [authoritative admission](#authoritative-admission) | Whether this invocation entered `RunPassAsync` | Wake transport, endpoint policy, retry schedule |
| Diagnostics-only consumer | [health predicates](#health-predicates) | Current provider control-plane assessment | Application liveness and user-facing status mapping |
| Schema owner | [migration and roles](#migration-and-role-reconciliation) | Required schema and role boundary | Review, apply, and audit credentials |
| Custom pump implementer | [custom composition](#custom-composition) | The four-kind public algebra | One shared singleton under both pump interfaces |
| Rollback operator | [rollback](#rollback-to-v020-preview8) | Reader/writer compatibility range | Stop/disable activation and deploy the selected binary |

This case is provider-first. Apply only migrations embedded in the exact package you are deploying, and verify
`PostgreSqlDurableRuntimeSchemaManager.RequiredVersion` before rollout. The #794 package embeds
`0010_runtime_health_observation.sql`; registration remains passive and never applies it.

## Health predicates

`DurableRuntimeHealthSnapshot` exposes four computed predicates. Use their names instead of reconstructing policy from
`SchemaCompatible`, `EpochCompatible`, timestamps, or exception text:

```csharp
var health = await healthClient.GetAsync(cancellationToken);

Console.WriteLine(
    $"state={health.State}; observed={health.WasStoreObserved}; " +
    $"can-enable={health.CanEnableActivation}; " +
    $"can-attempt={health.CanAttemptPump}; ready={health.IsReady}");
```

| Predicate | Meaning | Important non-meaning |
| --- | --- | --- |
| `WasStoreObserved` | The provider published a complete authoritative-store assessment | `false` does not mean the store is incompatible; it means it was unavailable to observe |
| `CanEnableActivation` | This assessment authorizes activation under schema and epoch rules | Not a permanent deployment toggle or application-liveness result |
| `CanAttemptPump` | A host precheck says a new pass is appropriate now | Advisory only; it must not be a check-then-act gate |
| `IsReady` | The runtime control plane is healthy and compatible | Does not prove HTTP readiness, dependency liveness, or item success |

`NotStarted` and `Stale` may enable activation and may attempt a pass so the provider can establish or take over a
heartbeat. `Draining` may remain activation-compatible while refusing a new pass. `Unavailable` fails closed for
activation because compatibility was not established. `Healthy` with either compatibility flag false is also not
activatable; synthetic snapshots must fail closed through the computed predicates.

`ObservedAtUtc` is a timestamp for the assessment: database statement time for an observed store, process time for an
`Unavailable` assessment. Use `WasStoreObserved` as the provenance signal.

### Replace handwritten readiness

Delete component-level expressions such as:

```csharp
// Old: duplicates provider policy and misclassifies an unobserved store.
var canEnable = health.SchemaCompatible && health.EpochCompatible;
var ready = health.State == DurableRuntimeHealthState.Healthy
    && health.SchemaCompatible
    && health.EpochCompatible;
```

Use the computed contract instead:

```csharp
// New: the provider owns state, compatibility, and observation semantics.
var canEnable = health.CanEnableActivation;
var canAttempt = health.CanAttemptPump; // Display/precheck context only.
var ready = health.IsReady;
var observed = health.WasStoreObserved;
```

Search the host for other uses of `SchemaCompatible`, `EpochCompatible`, and `DurableRuntimeHealthState`. Keep the raw
fields only where they are displayed as diagnostic evidence; do not use them to recreate activation or readiness
policy. Add `Unavailable = 5` to exhaustive enum/serialization handling.

## Existing-host recipe

Update `ForgeTrust.AppSurface.Durable`, `ForgeTrust.AppSurface.Durable.Provider`, and
`ForgeTrust.AppSurface.Durable.PostgreSql` to the same #794 package version. Keep the existing PostgreSQL registration,
separate dispatcher/runtime data sources, and explicit `AddWorkerHost()` choice. The new consumer resolves the
admission-aware interface alongside the legacy interface, but calls admission directly. It does not read health and
then separately gate the call:

```csharp
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

static async Task RunOnePassAsync(
    IServiceProvider services,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var request = new DurableRuntimePumpRequest(
        maximumItems: 32,
        timeBudget: TimeSpan.FromSeconds(10),
        surfaces: DurableRuntimeSurface.All);

    // Authoritative admission owns the check and the transition into one bounded pass.
    var attempt = await services
        .GetRequiredService<IDurableRuntimePumpAdmission>()
        .TryRunOnceAsync(request, cancellationToken);

    switch (attempt.Kind)
    {
        case DurableRuntimePumpAttemptKind.Completed:
            var result = attempt.Result!;
            logger.LogInformation(
                "Durable pass completed: discovered={Discovered}, processed={Processed}, failed={Failed}",
                result.Discovered, result.Processed, result.Failed);
            // Completed means terminal pass bookkeeping completed, not that every item succeeded.
            break;
        case DurableRuntimePumpAttemptKind.Refused:
            logger.LogDebug("Durable pass was refused before RunPassAsync.");
            break;
        case DurableRuntimePumpAttemptKind.Unavailable:
            logger.LogWarning("Durable store was not observed; problem={ProblemCode}.", attempt.ProblemCode);
            break;
        case DurableRuntimePumpAttemptKind.Incompatible:
            logger.LogError("Durable store rejected this runtime; problem={ProblemCode}.", attempt.ProblemCode);
            break;
    }
}
```

The four-kind switch is intentionally exhaustive. `Completed` always has a non-null result, including a zero-valued
empty pass. `Refused`, `Unavailable`, and `Incompatible` have no result and prove only that this invocation did not
enter `RunPassAsync`; they say nothing about a previous invocation whose response was lost, another process, or
item-level external effects. Caller cancellation, exceptions escaping application execution, malformed provider
state, and finalization failures propagate. `Try` does not mean “never throws.”

Existing hosts may continue using `IDurableRuntimePump.RunOnceAsync`; its source and binary shape remain supported.
External activators should adopt `IDurableRuntimePumpAdmission` when they need execution certainty.

Expected output is one and only one attempt classification:

```text
Durable pass completed: discovered=1, processed=1, failed=0
```

An empty admitted pass is still `Completed` with zero counts. A refusal prints only `Refused`; an observation failure
prints `Unavailable` with `ASDUR103`; a schema/epoch rejection prints `Incompatible` with the corresponding stable
problem code. Exceptions and cancellation do not become an output kind.

## Custom composition

The PostgreSQL implementation must be one singleton behind both public interfaces so local pass serialization cannot
be bypassed. Pre-register a complete replacement before provider registration, then assert the resolved identities:

```csharp
var customPump = new MyDurablePump(/* custom provider dependencies */);
services.AddSingleton(customPump);
services.AddSingleton<IDurableRuntimePump>(customPump);
services.AddSingleton<IDurableRuntimePumpAdmission>(customPump);

services.AddAppSurfaceDurablePostgreSql(
    dispatcherDataSource,
    runtimeDataSource,
    workOptions,
    scheduleOptions);

using var provider = services.BuildServiceProvider();
if (!ReferenceEquals(
        provider.GetRequiredService<IDurableRuntimePump>(),
        provider.GetRequiredService<IDurableRuntimePumpAdmission>()))
{
    throw new InvalidOperationException(
        "The legacy and admission-aware pump registrations must share one singleton.");
}
```

Assert reference equality in composition tests. Replacing only the legacy interface or only the admission interface
is unsupported for concurrent use because it can create independent process-local slots. A custom provider may use
an in-process fake for contract tests, but a fake must not be described as PostgreSQL admission or used to certify a
real store. `MyDurablePump` must implement both interfaces over one process slot; its admission method must perform the
authoritative pre-execution decision rather than delegating through a separate check. The
[packed PostgreSQL consumer](packed-consumers/PostgreSqlProvider/Program.cs) compiles this registration pattern,
exercises all four legal outcomes, and records zero execution calls for each returned non-completed outcome.

## Diagnostics and recovery

The public contract deliberately keeps outage detail coarse. PostgreSQL diagnostics should attach only low-cardinality
operation, phase, cause, and code fields; never SQL, exception text, connection strings, role names, payload ids,
scope ids, or aggregate ids.

| Public evidence | Cause | Safe fix | Retry safety | Execution certainty |
| --- | --- | --- | --- | --- |
| Health or admission `Unavailable` / `ASDUR103` | Transport, pool exhaustion, or connectivity failure | Check the selected data source, network, pool capacity, and database availability; retry by host policy | This invocation did not enter `RunPassAsync`; a new attempt is policy-controlled |
| Health or admission `Unavailable` / `ASDUR103` | Provider deadline or query/backlog pressure | Inspect the full health query plan, exact due-row aggregation, pool wait, and command bounds before increasing budgets | This invocation did not enter `RunPassAsync`; do not infer incompatibility |
| Health or admission `Unavailable` / `ASDUR103` | Permission denied (`42501`) | Rerun and verify the canonical [role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql); do not retry blindly | This invocation did not enter `RunPassAsync` |
| `Incompatible` / `ASDUR400`–`ASDUR403` | Observed schema is missing, old, too new, or inconsistent | Review status, generate a forward-only script, apply it as migration owner, rerun roles | This invocation did not enter `RunPassAsync` |
| `Incompatible` / `ASDUR108` | Observed runtime epoch does not authorize this process | Follow the authorized epoch-rotation/recovery procedure | This invocation did not enter `RunPassAsync` |
| `Refused` | Local overlap, closed gate, draining, active store pass, or lost worker generation | Wait for the current pass, finish shutdown, or replace/reconcile the stale worker as appropriate | This invocation did not enter `RunPassAsync`; the prior/concurrent invocation may have |
| Exception before execution | Malformed data, undefined object, unclassified provider failure, or caller cancellation | Preserve and diagnose the original exception; fix the provider/store contract or cancellation owner | Do not classify or silently retry from exception text |
| Exception after execution begins | Application failure, finalization failure, or cleanup failure | Inspect durable state and the original exception; cleanup is bounded and secondary | Execution may have begun; do not treat it as a safe refusal |

## Migration and role reconciliation

The #794 rollout is migration-first and forward-only:

1. Drain/stop every durable worker and Schedule writer, disable external activators, and hold the reviewed maintenance
   window through migration 10. Its partial lease-expiry index uses transactional `CREATE INDEX`, so Schedule writes
   can block for the duration of the index build. The migration acquires its Schedule lock with `NOWAIT` and bounds
   the index statement at five minutes; lock contention or a longer build rolls the transaction back for diagnosis
   and a later retry.
2. Generate and review the exact script from the installed version.
3. Verify that the configured migration owner already owns the schema-9
   `runtime_due_dispatch_health(integer)` function. If ownership drifted, run
   [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql)
   with the intended migration owner before applying the migration. Migration 0010 deliberately fails before DDL
   rather than replacing a function owned by another principal.
4. Apply `0010_runtime_health_observation.sql` with that migration owner, taking schema 9 to schema 10.
5. Rerun the role recipe
   to reconcile object ownership, `PUBLIC EXECUTE`, and the four restricted login leaves.
6. Run schema `status` and `preflight`, verify the active epoch and StoreId, then smoke-test the old supported reader.
7. Deploy the #794 binary, exercise health and both pump interfaces, and re-enable activation.

The role recipe remains required after the migration even when the function signature is unchanged. It must leave the
migration owner as the owner of package objects/functions, revoke `PUBLIC EXECUTE`, grant the runtime role only the
reviewed aggregate observation/heartbeat capabilities, and keep dispatcher, runtime, and retention roles distinct
restricted login leaves. Registration stays passive: it never applies this migration or repairs grants at startup.

With `APPSURFACE_DURABLE_MIGRATION_CONNECTION` naming the migration-owner connection, the review/apply sequence is:

```console
$ dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- \
    durable schema status \
    --connection-env APPSURFACE_DURABLE_MIGRATION_CONNECTION
# Review: installed 9; required 10; upgrade required.

$ dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- \
    durable schema script --from-version 9 \
    --output /tmp/appsurface-durable-9-to-10.sql
# Review the generated file; it must contain only migration 0010 for this upgrade.

# If the schema-9 function is not already owned by appsurface_durable_owner, run the canonical role recipe here
# before applying the migration. Healthy installations skip this repair-only invocation.

$ dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- \
    durable schema apply \
    --connection-env APPSURFACE_DURABLE_MIGRATION_CONNECTION \
    --apply
# Expected: Durable schema: 9 -> 10; applied: 0010.

$ psql --host <database-host> --dbname <database-name> --username appsurface_durable_owner \
    -v migration_owner_role=appsurface_durable_owner \
    -v dispatcher_role=appsurface_durable_dispatcher \
    -v runtime_role=appsurface_durable_runtime \
    -v retention_operator_role=appsurface_durable_retention \
    -f Durable/configure-postgresql-roles.sql

$ dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- \
    durable schema preflight \
    --connection-env APPSURFACE_DURABLE_MIGRATION_CONNECTION
```

Treat the comments as expected checkpoints, not captured output from this working tree. Command wording is canonical in
the [`durable schema` CLI guide](../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands);
operators must review actual output and the generated SQL before applying it.

### Compatibility matrix

| Runtime | Schema 9 | Schema 10 with reconciled roles |
| --- | --- | --- |
| #794 package | Upgrade required; do not activate | Supported |
| `v0.2.0-preview.8` | Supported | Supported rollback reader/writer; mandatory smoke test |
| Pre-`0009` package | Stop before role reconciliation | Unsupported |

## Rollback to v0.2.0-preview.8

The named supported binary rollback target is `v0.2.0-preview.8`, after the migration owner has completed the role
recipe. The additive migration remains in place; do not delete migration rows or run a destructive down-migration.

| Binary | Schema 9 | Schema 10 | Action |
| --- | --- | --- | --- |
| #794 binary | Upgrade required | Current/compatible | Apply migration and roles before activation |
| `v0.2.0-preview.8` | Current/compatible | Compatible reader/writer: its published range includes 10 and migration 0010 preserves the function signature | Stop activation, deploy this binary, smoke-test status/health/heartbeat/real Work, then decide whether to continue |
| Pre-`0009` binary | Not a supported post-role-recipe rollback | Not supported | Keep stopped; repair forward or restore the reviewed role posture |

If a schema-10 function defect is found, stop activation and use a reviewed corrective forward migration. Never edit
`0005` or `0010` in place and never restore broad dispatcher access as a rollback shortcut.

## Cancellation and shutdown

Before and during `RunPassAsync`, caller cancellation propagates. Immediately after `RunPassAsync` returns, provider
finalization owns a fresh reserve token that is not linked to caller cancellation. This prevents a caller disconnect
from rewriting completed application work as a canceled pass. A finalization deadline still propagates as a pump-level
failure because execution may have begun; failed-pass cleanup gets a separate bounded reserve and cannot mask the
original exception.

For a hosted loop, let `H = HostOptions.ShutdownTimeout`, `T = TimeBudgetPerPass`, and `R = ShutdownReserve`. Validate
`T + (2 * R) <= H` without overflow and cancel an active pass no later than `shutdownStart + H - (2 * R)`. A
finalization cancellation must not be swallowed merely because the earlier pass token was canceled.

## First-run proof and evidence

The one-command local proof is:

```bash
bash examples/durable-postgresql/run-local-proof.sh
```

It creates a disposable loopback PostgreSQL 16.5 container, applies the current checked-in migrations through schema
10 using the explicit CLI path, runs the canonical role recipe, initializes a development epoch, runs the real
Work/Flow/Schedule example, resolves both pump interfaces to the same PostgreSQL singleton, calls authoritative
admission directly, and checks that worker startup performs no DDL.

The measurement harness is [`Durable/evidence/measure-issue-794-adoption.sh`](evidence/measure-issue-794-adoption.sh).
It records command boundaries and real elapsed times only when invoked. The checked-in evidence file deliberately
marks unrun five-journey targets as `not-run`; it contains no fabricated timing measurements.
