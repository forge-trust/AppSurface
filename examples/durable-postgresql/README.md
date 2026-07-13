# Durable PostgreSQL example

This example is the smallest end-to-end host for the native
[`ForgeTrust.AppSurface.Durable.PostgreSql`](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md) runtime. It
registers source-generated payload codecs and a typed worker, applies schema only through an explicit development
command, accepts work, runs a bounded pump, exposes application-authorized status and cancellation, and creates full
[Cronos](https://github.com/HangfireIO/Cronos) schedules.

Use this as a tutorial and composition reference, not as a production operator CLI. It demonstrates status and
cancellation, while reconciliation, manual resolution, safe retry, and post-restore release remain the narrower
`IDurableWorkOperatorClient` surface described in the
[host-neutral reference](../../Durable/ForgeTrust.AppSurface.Durable/README.md#status-and-authorized-control). Production
schema changes belong in the
[`appsurface durable schema`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-durable-schema) deployment workflow,
and production control commands must sit behind application authorization.

## 1. Start a development PostgreSQL server

The commands below use a disposable local PostgreSQL 17 container:

```bash
docker run --rm --name appsurface-durable-postgres \
  -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=appsurface_durable_example \
  -p 54329:5432 \
  postgres:17
```

In a second terminal, configure the migration owner and explicitly apply the numbered schema. The example refuses this
write unless the environment is `Development`:

```bash
export DOTNET_ENVIRONMENT=Development
export APPSURFACE_DURABLE_MIGRATION_CONNECTION='Host=localhost;Port=54329;Database=appsurface_durable_example;Username=postgres;Password=postgres'

dotnet run --project examples/durable-postgresql -- schema-status
dotnet run --project examples/durable-postgresql -- schema-apply-dev
```

Runtime startup never performs DDL. Outside local development, generate, review, and apply the migration script with the
[AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-durable-schema) using a dedicated migration
owner.

The current script includes
[`0005_runtime_health.sql`](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/Migrations/0005_runtime_health.sql),
which creates the payload-free worker heartbeat, active-pass, and drain record. The role template grants the runtime
only `SELECT`/`INSERT`/`UPDATE` on that table; health support does not justify schema ownership or startup DDL.

## 2. Grant a non-owner runtime role

Run the included [`roles.example.sql`](roles.example.sql) template after migrations, then create or reuse a separately
managed login and grant it the runtime group role:

```bash
psql "$APPSURFACE_DURABLE_MIGRATION_CONNECTION" \
  --set=ON_ERROR_STOP=1 \
  --file=examples/durable-postgresql/roles.example.sql

psql "$APPSURFACE_DURABLE_MIGRATION_CONNECTION" \
  --set=ON_ERROR_STOP=1 \
  --command="CREATE ROLE appsurface_durable_example LOGIN PASSWORD 'durable_dev' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS"

psql "$APPSURFACE_DURABLE_MIGRATION_CONNECTION" \
  --set=ON_ERROR_STOP=1 \
  --command="GRANT appsurface_durable_runtime TO appsurface_durable_example"
```

Then configure the runtime connection and a stable recovery epoch:

```bash
export APPSURFACE_DURABLE_CONNECTION='Host=localhost;Port=54329;Database=appsurface_durable_example;Username=appsurface_durable_example;Password=durable_dev'
export APPSURFACE_DURABLE_RUNTIME_EPOCH="$(uuidgen)"
export APPSURFACE_DURABLE_SCOPE='tutorial-tenant'
```

Keep the epoch outside the durable database and stable across normal restarts. Rotate it only as part of the
[restore runbook](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#point-in-time-restore-runbook).
The first guarded runtime transaction calls a narrowly granted `SECURITY DEFINER` function to record that initial active
epoch. The runtime role cannot update epoch metadata later.

Database roles and application operators are deliberately different:

- the migration owner applies numbered DDL and is never the application connection;
- the runtime role is a non-owner without `BYPASSRLS`; it can read compatibility metadata, append history, and perform
  only the explicit current-record mutations required by protocol v1, including starting and completing audited operator
  command rows, with no table `DELETE` grant or direct epoch-metadata update;
- the optional dispatcher role can read only the payload-free dispatch catalog;
- the epoch operator is a deployment/recovery role with column-scoped epoch-update and append-only epoch-history access;
  it is not granted to the tutorial's runtime login; and
- application operators receive no direct durable-table grant. Authorize their trusted scope, actor id, and reason code
  before calling `IDurableWorkControlClient` or `IDurableScheduleClient`.

## 3. Enqueue, pump, and inspect work

The registration in [`Program.cs`](Program.cs) uses `AddDurableWork` and `AddAppSurfaceDurablePostgreSql`. Enqueue one
operation, copy the returned work id, run one bounded pass, and inspect its terminal fact:

```bash
dotnet run --project examples/durable-postgresql -- enqueue resource-42 cleanup-command-42
dotnet run --project examples/durable-postgresql -- pump
dotnet run --project examples/durable-postgresql -- status <work-id>
```

`pump` calls `IDurableRuntimePump.RunOnceAsync`; it is the same bounded primitive used by hosted and external activation.
To see audited optimistic control, enqueue another item and cancel it before pumping:

```bash
dotnet run --project examples/durable-postgresql -- enqueue resource-43 cleanup-command-43
dotnet run --project examples/durable-postgresql -- cancel <work-id> 1
```

The revision is required so an operator cannot overwrite newer truth. Actor and reason values are stable privacy-safe
codes, not free-form notes. A cancellation after an effect permit does not erase the possibility that the provider
already applied the operation.

To exercise the narrower manual-resolution path, the example also registers a `ManualResolution` variant. The reserved
safe code `simulate-unknown` throws after the effect permit to model a lost provider response:

```bash
dotnet run --project examples/durable-postgresql -- \
  enqueue-manual simulate-unknown manual-command-1
dotnet run --project examples/durable-postgresql -- pump
dotnet run --project examples/durable-postgresql -- status <work-id>
dotnet run --project examples/durable-postgresql -- \
  resolve-not-applied <work-id> <suspended-revision> manual-resolution-1
```

The pump reports one safe failure and exits with code `2`; status then reports `Suspended`. `resolve-not-applied` is an
educational stand-in for application-authorized provider proof. It writes a completed idempotent operator command, marks
the effect permit proven not applied, and releases the work for retry. A production operator must obtain real provider
evidence before issuing that command; a timeout or human guess is not proof.

## 4. Create a full cron schedule

Create a five-field weekday schedule in an IANA time zone:

```bash
dotnet run --project examples/durable-postgresql -- \
  schedule weekday-cleanup resource-42 '0 9 * * MON-FRI' America/New_York

dotnet run --project examples/durable-postgresql -- schedule-status weekday-cleanup
```

Six-field expressions are also available when the grammar is explicit:

```bash
dotnet run --project examples/durable-postgresql -- \
  schedule frequent-cleanup resource-42 '*/15 * * * * *' Etc/UTC seconds
```

The example calls `ExplainNextOccurrencesAsync` before creation and prints evaluated UTC instants. `DurableSchedule.Cron`
persists the raw expression, `CronDialect.CronosV1`, grammar, IANA zone, and evaluator metadata. Its configurable defaults
are `ScheduleOverlapPolicy.QueueOne` plus `ScheduleMisfirePolicy.RunOnce`: one active run can retain one coalesced
follow-up, and downtime creates one recovery occurrence instead of replaying every missed tick.

## 5. Choose an activation model

For a continuously live worker process, opt into the critical hosted loop:

```bash
dotnet run --project examples/durable-postgresql -- worker
```

The hosted worker resumes its heartbeat session on startup and writes a best-effort drain marker when Ctrl+C stops the
host. It will not let one process instance overlap two bounded passes. The registered `IDurableRuntimeHealth` snapshot
can be mapped into an application-owned health endpoint without exposing payloads or aggregate ids:

```csharp
var health = await services.GetRequiredService<IDurableRuntimeHealth>()
    .GetAsync(cancellationToken);

Console.WriteLine(
    $"state={health.State}, code={health.ProblemCode ?? "none"}, "
    + $"lastSweep={health.LastSuccessfulSweepAtUtc:O}, due={health.DueDispatchCount}, "
    + $"draining={health.IsDraining}, passActive={health.IsPassActive}");
```

The default 15-second heartbeat stale bound is intentionally longer than the one-second idle polling interval. Use a
unique privacy-safe `WorkerId` for each live replica. `ASDUR404` means activation never completed or the heartbeat went
stale; `ASDUR405` means another live process still owns that identity or an overlapping pass was attempted. See the
[`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md#schema-and-activation) for the safe
operator response.

For Cloud Run, jobs, functions, or another scale-to-zero host, do not call `AddWorkerHost`. Arrange an external scheduler
or wake endpoint that invokes the same bounded `RunOnceAsync` operation until `HasMore` is false. PostgreSQL notifications
can reduce latency while a process is alive, but they cannot wake a process that no longer exists and are never the
correctness path. If an externally activated process publishes runtime health, configure `HeartbeatStaleAfter` above
the activation cadence; otherwise `Stale` is the honest verdict between passes.

Custom pump hosts should call `IDurableRuntimeDrainControl.BeginDrainAsync`, await their active `RunOnceAsync`, verify
`IsPassActive` is false, and only then terminate. Drain does not cancel provider I/O or revoke an effect permit. The
same worker identity cannot hand off while its pass remains active unless the owner becomes stale; a clean drained
identity hands off immediately after its pass marker clears. During long provider work, the runtime refreshes the
heartbeat at intervals no greater than one third of the stale bound.

## Code and data-contract tour

[`DurableExampleContracts.cs`](DurableExampleContracts.cs) uses a `JsonSerializerContext`; no assembly-qualified type
name or reflection-based serializer becomes durable protocol. Each codec fixes:

- a stable contract name and version;
- `DurableDataClassification.Operational` for opaque safe codes;
- a value policy that rejects unapproved bytes;
- a 1 KiB contract-specific payload limit; and
- the application-owned `operations-30d` retention policy id.

The retention id is a durable classification snapshot, not an automatic deletion timer. The application must map it to
reviewed archival or deletion jobs that preserve active aggregates, provider-effect evidence, legal holds, and required
audit history. Use `ApprovedApplication` only for application values that an explicit policy approves for durable
storage; never persist credentials, provider response bodies, secret URLs, email bodies, or child-sensitive text by
default.

`ResourceCleanupExecutor` declares `ProviderKeyed` safety and uses the immutable `ExecutionIdentity.ProviderKey`. A real
provider integration must send that exact key on every attempt. Attempt numbers and lease generations are fencing
facts, not provider operation identities.

## Production pitfalls

- Do not generate a new runtime epoch on every deployment; that fences legitimate in-flight work.
- Do not run the application as schema owner or grant `BYPASSRLS` to silence a policy error.
- Do not apply migrations automatically during service startup.
- Do not hold an `NpgsqlConnection` or transaction while calling an external provider.
- Do not assume a thrown provider call means no effect happened; follow the registered provider-safety policy.
- Do not remove historical codec, work, or Flow registrations while nonterminal history still names that version.
- Do not treat a schedule id, work id, Flow id, or scope string as authorization.
- After a point-in-time restore, stop old workers, rotate the out-of-band epoch, reconcile effect permits, and explicitly
  release only proven-safe work before resuming ingress.
- Do not assume the replacement pump inventories recovery work. Page the authorized Work and Flow `ListAsync` surfaces
  for payload-free old-epoch candidates, and the schedule `ListAsync` surface for schedule recovery. Treat every
  recovery flag as an inventory hint, reconcile provider truth, and use the listed revision for the exact release.
- Do not treat graceful drain as cancellation. It prevents later passes but deliberately lets already-started provider
  truth reach an authoritative completion or suspension.

Continue with the [host-neutral contract reference](../../Durable/ForgeTrust.AppSurface.Durable/README.md), the
[PostgreSQL operations reference](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md), and the
[`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md).
