# ForgeTrust.AppSurface.Durable.PostgreSql

`ForgeTrust.AppSurface.Durable.PostgreSql` is the native PostgreSQL store and worker integration for
[`ForgeTrust.AppSurface.Durable`](../ForgeTrust.AppSurface.Durable/README.md). It keeps durable work, one-node Flow
history, timers, schedules, occurrences, claims, effect permits, and terminal facts in the same PostgreSQL database as
the application's transactional handoff.

This package is a public preview. PostgreSQL is the only required control plane. `LISTEN`/`NOTIFY`, an external queue,
or an HTTP activator may reduce wake-up latency, but the bounded database sweep remains authoritative.

The [durable PostgreSQL example](../../examples/durable-postgresql/README.md) is a buildable tutorial covering
source-generated codecs, registration, explicit development schema apply, work control, a bounded pump, and full Cronos
schedules.

## Install and prerequisites

```bash
dotnet add package ForgeTrust.AppSurface.Durable
dotnet add package ForgeTrust.AppSurface.Durable.PostgreSql
```

Use a supported PostgreSQL server and four deliberately different database security postures:

- **migration owner** — owns `appsurface_durable`, applies numbered migrations, and is not used by the app;
- **runtime role** — non-owner, no `BYPASSRLS`, permitted to perform scoped runtime reads/writes, and allowed to execute
  only the security-definer function that records the first active epoch;
- **dispatcher role** — may read only the payload-free dispatch catalog when deployment policy separates discovery.
- **epoch operator** — a deployment/recovery principal that may rotate only active epoch metadata and append epoch
  history after all old workers have stopped.

The package does not create deployment-specific roles. Provision and grant them through the application's normal
infrastructure workflow. Never run the worker as the schema owner merely to bypass an RLS error.

The example's [`roles.example.sql`](../../examples/durable-postgresql/roles.example.sql) shows the required post-migration
shape: compatibility metadata is read-only; mutable protocol tables receive only `SELECT`/`INSERT`/`UPDATE`; operator
commands are inserted completed for atomic transitions or move only from started to completed around reconciliation;
histories receive only append-and-read access; and no runtime table receives `DELETE`. An optional dispatcher receives
only schema usage and `SELECT` on the payload-free dispatch catalog. Review explicit grants for every table added by a
future migration instead of using broad default table privileges. Application operators are not PostgreSQL operators:
give them no direct durable-table grants, authorize them in the application, and use the audited control clients.

The runtime role receives `EXECUTE` on `appsurface_durable.initialize_runtime_epoch(uuid)`, but no `UPDATE` on
`store_metadata` and no access to `runtime_epoch_history`. The `SECURITY DEFINER` function can set only the initially
empty active epoch and append its bootstrap fact. Later rotation requires the separate epoch operator's column-scoped
metadata update plus append/read access to epoch history; do not grant that role to an ordinary application worker.

## Apply numbered migrations explicitly

Runtime startup validates compatibility and never performs DDL. Use the existing
[`appsurface durable schema`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-durable-schema) deployment commands:

```bash
export APPSURFACE_DURABLE_CONNECTION='<migration-owner connection string>'
appsurface durable schema status
appsurface durable schema script --from-version 0 --output ./artifacts/appsurface-durable.sql
appsurface durable schema apply --apply
appsurface durable schema preflight
```

Or call `PostgreSqlDurableRuntimeSchemaManager` directly with a migration-owner `NpgsqlDataSource`:

```csharp
var manager = new PostgreSqlDurableRuntimeSchemaManager(migrationDataSource);
var script = manager.GenerateScript(fromVersion: 0);
var applied = await manager.ApplyAsync(cancellationToken);
await manager.ValidateAsync(cancellationToken);
```

`GenerateScript` is deterministic and protected by the same package advisory lock as `ApplyAsync`. `ValidateAsync` is
read-only and fails before claim loops start when the store is missing, inconsistent, too old, or outside the package's
reader/writer compatibility range.

Migration [`0005_runtime_health.sql`](Migrations/0005_runtime_health.sql) adds the payload-free `runtime_heartbeat`
record used for worker ownership, active-pass fencing, liveness, and graceful drain. It does not add an automatic health
endpoint or startup DDL. Apply it with the migration owner before deploying a runtime that uses the
[health and drain contracts](../ForgeTrust.AppSurface.Durable/README.md#runtime-health-and-graceful-drain); the runtime
role needs the explicit `SELECT`/`INSERT`/`UPDATE` grant shown in the example's
[`roles.example.sql`](../../examples/durable-postgresql/roles.example.sql).

Do not reverse-migrate authoritative history during rollback. Disable new ingress, drain with a compatible worker, and
deploy a forward fix. Contracting old columns or history shapes belongs in a later release after every supported reader
and writer has moved forward.

## Register storage and execution

Register allowlisted typed work first, then add passive PostgreSQL storage with a non-owner runtime data source and a
stable epoch stored outside that database:

```csharp
services.AddDurableWork<CleanupWork, CleanupResult, CleanupExecutor>(
    "example.cleanup",
    "v1",
    DurableProviderSafety.ProviderKeyed,
    cleanupWorkCodec,
    cleanupResultCodec);

var durable = services.AddAppSurfaceDurablePostgreSql(
    runtimeDataSource,
    runtimeEpoch,
    options =>
    {
        options.WorkerId = "cleanup-worker-a";
        options.HeartbeatStaleAfter = TimeSpan.FromSeconds(30);
    });
```

This registration adds clients, status/control surfaces, schema validation, and `IDurableRuntimePump`, but performs no
DDL and starts no background execution. A continuously live worker host must explicitly call `durable.AddWorkerHost()`.
A scale-to-zero host should leave hosted execution disabled and arrange external activation of bounded
`RunOnceAsync` passes instead.

## Atomic domain transaction handoff

`IDurableWorkTransactionWriter` receives the application's exact active `NpgsqlTransaction`:

```csharp
await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

await householdRepository.RevokeAccessAsync(householdId, transaction, cancellationToken);
var accepted = await transactionWriter.EnqueueAsync(transaction, request, cancellationToken);

if (!accepted.IsSuccess)
{
    throw new InvalidOperationException(accepted.Problem!.Problem);
}

await transaction.CommitAsync(cancellationToken);
```

The writer never opens, commits, rolls back, replaces, or disposes that transaction. The accepted work and history rows
are the transactional outbox record; there is no second outbox state machine. A metadata-only notification may be
issued inside the transaction, but a dropped notification only delays the next sweep.

`CommandId` and `IdempotencyKey` are separate deduplication identities. Reusing either with the exact semantic request
returns the original `WorkId`, command, revision, and acceptance time. Reusing either with different contract bytes or
policy is a conflict. The provider key is separately derived from the immutable `ActivityId`; it is never the caller's
idempotency key, attempt number, or lease generation.

## Claims, effects, and cancellation

Discovery reads only opaque scope/aggregate ids, due time, state, revision, and priority. Each candidate then starts a
new transaction, executes `SET LOCAL appsurface_durable.scope_id`, conditionally claims the exact revision and scope
generation, and reads payload only after the scoped claim succeeds.

The claim also snapshots the Work row's configured lease-renewal cadence. The pump renews on that persisted cadence,
bounded by the current lease expiry, rather than deriving a cadence from host health settings or the remaining lease.
Every renewal is still capped by the Work row's persisted maximum lease lifetime; changing runtime options cannot extend
an already-accepted Work item's effect window.

Before invoking a provider executor, the runtime commits `EffectPermitRecorded` with activity, attempt, lease, scope,
and runtime-epoch fencing. It then releases the database connection before provider I/O. A stale worker cannot renew,
complete, cancel, or acquire another permit, but its result is retained as a stale observation for reconciliation.
Executor cancellation is cooperative: the pump cancels the token after cancellation, scope disable, epoch mismatch, or
lease loss, then awaits the invocation. Lease and epoch fencing prevent a late result from becoming durable truth, but
they cannot kill provider code that ignores the token or performs unbounded I/O. Executors must apply their own bounded
provider-call timeouts and honor cancellation promptly.

Cancellation with no unresolved permit becomes `CanceledBeforeEffect`. Cancellation during an active permitted attempt
becomes `CancelPending`; cancellation with unresolved historical permit evidence enters the provider policy's resolvable
suspension immediately. Lease renewal propagates active-attempt cancellation into the provider executor's token without
canceling renewal or completion recording. A known success is preserved as `SucceededAfterCancelRequested`, a proven
no-effect result may cancel, and an unknown provider result suspends. Cancellation never deletes evidence that an
external effect did or may have happened.
For canceled `Idempotent` or `ProviderKeyed` ambiguity, `ResolveAsync` can record applied/not-applied proof without replay;
`RetrySafeAsync` instead acts as an explicit audited override and consumes cancellation before making replay eligible.

Disabling a scope atomically projects every nonterminal Work row: work without an ambiguous permit becomes
`CanceledBeforeEffect`; permitted unknown work enters the provider policy's resolvable suspension with cancellation
preserved; an in-progress reconciliation remains fenced. Already-suspended work is evaluated by the same evidence-first
rule rather than preserved merely because it is suspended. A late completion from the disabled generation is recorded
only as a stale observation, never as terminal truth and never through a terminal callback.

Cancellation queries unresolved `granted` or `ambiguous` permits across every attempt. An in-progress reconciliation is
preserved; an in-flight permitted attempt remains `CancelPending`; other unresolved evidence routes to the immutable
provider policy's suspension. Every other nonterminal state, including a no-evidence compatibility suspension, becomes
`CanceledBeforeEffect` with its lease cleared. A side-effect-free reconciliation that proves `NotApplied` cannot return
to `RetryWait` and repeat a mutation behind a canceled parent Flow. `RetrySafeAsync` remains the explicit override for
replay-safe `Idempotent` or `ProviderKeyed` work.

Suspended work is recovered through `IDurableWorkOperatorClient`, not raw SQL. Reconcile, manual resolution, safe retry,
and restore release each require an application-authorized scope, privacy-safe actor and reason codes, an idempotent
command id, and the expected work revision. PostgreSQL records the operator command before provider reconciliation and
marks it completed with the resulting state and revision; retrying the same completed command returns its prior outcome.
None of these operations grants a general replay escape hatch or deletes effect permits.

Work cancellation and scope disable are narrower preview controls: they require the expected revision or generation,
actor id, and reason code, but do not yet accept a `DurableCommandId` or persist a response-deduplication ledger. After
post-commit response loss, re-read state before deciding whether to issue another control command. Idempotent control
commands with typed causal fields remain a stable-release requirement; do not generalize the suspended-work operator
ledger guarantee to these two controls.

## Flow and external events

`PostgreSqlDurableFlowClient` implements `IDurableFlowClient`. Starting a Flow persists its definition id/version,
implementation manifest fingerprint, context contract, first command, history, and dispatch row atomically. A pump
claim evaluates exactly one node through the registered `IDurableFlowRegistry` and commits that transition before
another node can run. A missing registration, changed implementation token, evaluator version, topology, event binding,
or activity codec suspends rather than interpreting old history with new code.

An Activity transition accepts the work row and activity wait in the same transaction. Its typed terminal result resumes
the same callsite and node. Expected business failure belongs in the typed result; exhausted technical failure or an
ambiguous provider effect suspends the Flow.

`RaiseEventAsync` accepts an authorized event only while its exact wait is active. If the Flow is not waiting yet, the
result is `NotWaitingYet` and the event id remains unused. Event delivery and timeout firing condition on the same Flow
revision; exactly one continuation wins and the other observation remains in history.

`CancelAsync` requires the expected Flow revision plus privacy-safe actor and reason codes. Accepted, race-lost, and
already-terminal cancellation commands retain that audit identity. Cancellation before an activity effect cancels both
work and Flow atomically; unresolved provider truth remains `CancelPending` until the terminal work fact is known.

`GetAsync` is the operator-safe status surface. It applies the trusted scope through RLS and returns only id/version,
state, node, revision, timestamps, and terminal code—never context, event, activity, or transition payload bytes. Use its
revision with `ReleaseSuspensionAsync` after application authorization and external reconciliation.

Recoverable suspension preserves its prior state and authoritative active wait/timer rows. Release validates the exact
registered manifest, command schema, active scope, optimistic revision, and expected wait shape before re-fencing the
row to the store's active runtime epoch. The same audited release can directly re-fence an exact-revision dormant
nonterminal row from an older epoch without first delivering an event, firing a timer, or completing an activity merely
to trigger lazy suspension. Direct release preserves `Ready`, event-wait, future-timer, activity-wait, and
cancel-pending state; future due times and active wait identities remain authoritative. Current-epoch non-suspended,
terminal, incompatible-manifest, and state-shape-mismatched rows are not adopted. A suspended activity can accept an
operator-proven typed result while remaining suspended; release then makes it `Ready` exactly once. If cancellation was
pending, an applied child result releases the parent directly to `Canceled`. Provider ambiguity that has not been
resolved remains suspended. When an operator establishes terminal child truth after runtime-epoch rotation, the same
transaction first fences the older parent and then projects that typed result or proven pre-effect cancellation. Flow
release therefore restores the projected `Ready` state, or reports the already-terminal canceled parent, without a
synthetic second callback.

## Scheduling

`PostgreSqlDurableScheduleClient` implements the full `IDurableScheduleClient` create/update/pause/resume/delete/get/list
and explanation surface. Cronos is isolated behind `CronDialect.CronosV1`; schedules persist the raw expression, grammar,
IANA zone, evaluator version, deterministic `H` seed, time-zone rules fingerprint, and next computed UTC occurrence.

An authoritative occurrence and run-slot ledger enforces per-schedule overlap and misfire behavior. The default
`QueueOne + RunOnce` composition creates one recovery occurrence after downtime, coalesces it into one pending slot when
a run is active, and releases that slot after any terminal outcome. Update increments generation and invalidates old
not-started occurrences; delete prevents every not-started occurrence from beginning.

Long-downtime recovery is bounded by count and time budgets. It uses previous/next occurrence calculations rather than
enumerating millions of missed ticks.

## Worker hosting

`IDurableRuntimePump.RunOnceAsync` is the single bounded execution primitive. Register PostgreSQL storage without hosted
execution when Cloud Run, a job runner, or another external activator will call the pump. Opt into the package's critical
hosted loop only for a continuously running worker host.

A host scaled to zero with no activator cannot provide timely schedules. Notifications do not change that fact. Health
checks should expose the last successful sweep, schedule lag, incompatible schema, and drain state without using scope,
instance, work, or schedule ids as metric labels.

## Runtime health and graceful drain

`AddAppSurfaceDurablePostgreSql` registers the host-neutral
[`IDurableRuntimeHealth` and `IDurableRuntimeDrainControl`](../ForgeTrust.AppSurface.Durable/README.md#runtime-health-and-graceful-drain)
contracts whether or not hosted execution is enabled. `IDurableRuntimeHealth.GetAsync` reads PostgreSQL schema and epoch
compatibility, the configured worker heartbeat, last completed sweep, drain and active-pass markers, and due-dispatch
lag for the configured `HostedSurfaces`. Lag includes due available work and expired leased dispatches on those surfaces.
The snapshot is payload-free and omits all scope and aggregate ids; mapping it to ASP.NET Core health/readiness policy
remains application-owned.

This health snapshot is the implemented observability surface. The package does not yet publish a supported `Meter`,
runtime metrics instruments, or `ActivitySource` spans. Privacy-bounded metrics and tracing remain a stable-release
requirement; do not use scope, work, Flow, or schedule ids as labels to fill that gap.

`HeartbeatStaleAfter` defaults to 15 seconds, must be longer than `IdlePollingInterval`, and may be configured up to one
hour. PostgreSQL time is authoritative for heartbeat comparisons. A pump pass records its active marker and refreshes
the heartbeat during long provider work at no more than one third of `HeartbeatStaleAfter` between writes;
`LastSuccessfulSweepAtUtc` advances only after the bounded pass completes. Alert on
[`ASDUR404`](../../troubleshooting/durable-diagnostics.md#schema-and-activation) when a worker never
completes a pass or its heartbeat exceeds the bound. [`ASDUR405`](../../troubleshooting/durable-diagnostics.md#schema-and-activation)
means another non-stale process instance still owns the same `WorkerId` or the same instance attempted overlapping
passes. Configure a unique privacy-safe worker id for every simultaneously live replica; do not shorten the stale bound
to make identity collisions appear safe.

`BeginDrainAsync` persists `draining=true`; later `RunOnceAsync` calls return without claiming work. It deliberately
does not revoke permits, cancel provider calls, or wait for the pass recorded by `IsPassActive`. A custom host should
begin drain, await its in-flight pass, verify that `IsPassActive` cleared, and then stop. The same `WorkerId` cannot
transfer while that pass remains active unless the owner first becomes stale; a clean drained owner whose pass cleared
hands off immediately. `TimeBudgetPerPass` stops the pump from beginning additional items; it is not a hard deadline for
an already-started executor or Flow evaluator. An invocation that ignores cancellation keeps the pass and graceful drain
active until it returns even though fencing rejects stale completion. The opt-in hosted worker calls
`ResumeAsync` on startup and writes a best-effort drain marker in its shutdown path. If that final write fails it logs
`ASDUR404`; another same-epoch process must wait for the stale bound rather than assuming the prior provider call ended.

Scale-to-zero changes activation, not correctness. Leave `AddWorkerHost` disabled and invoke bounded pump passes from an
external scheduler or wake endpoint until `HasMore` is false. PostgreSQL `NOTIFY` cannot wake a nonexistent process. If
the activated process publishes runtime health, configure `HeartbeatStaleAfter` above the expected activation cadence;
an idle database with no activator should correctly report `NotStarted` or `Stale`, not `Healthy`.

## Point-in-time restore runbook

A database restore cannot transparently replay external effects:

1. stop every old worker and disable new ingress;
2. restore the PostgreSQL database;
3. choose a new out-of-band epoch and configure the replacement fleet without starting claims;
4. use an epoch-operator data source to compare-and-swap the restored store's active epoch:

   ```csharp
   var manager = new PostgreSqlDurableRuntimeSchemaManager(epochOperatorDataSource);
   await manager.RotateRuntimeEpochAsync(
       expectedRestoredEpoch,
       replacementEpoch,
       "recovery-operator",
       "point-in-time-restore",
       cancellationToken);
   ```

5. start in recovery mode so nonterminal work, Flow, waits, timers, and schedules remain suspended;
6. build the authorized recovery set from application-owned Work/Flow ids and bounded schedule pages;
7. reconcile every permitted external effect using its immutable provider key;
8. release only aggregates proven safe under the new epoch; and
9. resume workers and ingress after the recovery audit is complete.

An old worker's epoch cannot begin a guarded transaction, renew, or complete after the rotation. Never reuse the
restored database's former epoch, and never give the ordinary runtime role direct epoch-update authority.

`ReleaseAfterRecoveryAsync` does not require future-due or otherwise dormant Work to reach lazy discovery first. It
accepts only the exact revision of an old-epoch nonterminal aggregate, preserves its due time and permit evidence, and
keeps ambiguous attempts in the provider policy's resolvable suspension. A preserved cancellation never becomes an
implicit replay: proof may resolve it, or an explicit audited safe-retry command must consume it.

The first guarded runtime mutation bootstraps an empty store epoch once. Thereafter, every work and Flow mutation checks
the store-level active epoch before taking scope or aggregate locks. A stale fleet fails with `ASDUR108`; only the active
fleet may suspend old rows, reconcile their external truth, and invoke audited work, Flow, or schedule release APIs.

Recovery inventory is not a hidden database sweep. `IDurableWorkControlClient.ListAsync` provides ordered, payload-free
pages of up to 500 items, with public-state and old-epoch-only filters. `IDurableFlowClient.ListAsync` provides
payload-free pages of up to 1,000 snapshots with state and `RequiresRecoveryRelease` filters. Both return the revision
needed by an audited release, while their recovery flags remain candidate signals rather than proof of safe replay.
`IDurableScheduleClient.ListAsync` provides bounded authorized schedule pages (100 by default, 1,000 maximum), including
payload-free target identity, state, revision, and recovery filtering. Use authorized schedule `GetAsync` when the full
snapshot and encoded target input are required. Starting the pump does not enumerate every dormant old-epoch aggregate;
page each authorized scope, reconcile provider truth, and issue exact-revision Work, Flow, or schedule releases.

## Testing consumers

The package test project contains a Testcontainers PostgreSQL harness and uses real transactions, roles, RLS, pooled
connections, contention, migration ranges, and state transitions. Consumer tests should follow the same shape:

1. start a disposable supported PostgreSQL container;
2. apply migrations with the owner connection;
3. create a non-owner runtime role and data source;
4. run domain mutation plus transactional enqueue;
5. kill or interrupt execution at acceptance, claim, permit, provider-result, and terminal-commit boundaries; and
6. assert database invariants rather than relying on timing sleeps.

There is intentionally no in-memory provider advertised as PostgreSQL-conformant. A public testing-support package is
deferred until a second consumer proves which fixtures belong in a stable API.

For actionable failure codes and safe fixes, use the
[`ASDURxxx` durable diagnostics catalog](../../troubleshooting/durable-diagnostics.md).
