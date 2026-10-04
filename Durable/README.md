# Portable durable execution

The [schema-11 heartbeat retention guide](heartbeat-retention-operations.md) is the start page for automatic cleanup of stale runtime identities, its 24-hour default, migration, release proof, and recovery.

For schema-11 activation, the [complete runtime-set preflight contract](heartbeat-retention-operations.md#complete-runtime-set-preflight-and-proof-checklist)
requires the same reviewed manifest and independent migration-owner role for every runtime credential. It defines the
planning, candidate, published-package, and deployment receipt gates; the local example is instructional and does not
substitute for matched package proof.

AppSurface Durable is a public-preview package family for portable durable contracts. It is split by audience:

- [`ForgeTrust.AppSurface.Durable`](ForgeTrust.AppSurface.Durable/README.md) is the application and reusable-module API
  for work, Flow, schedules, serialization, registration, and clients.
- [`ForgeTrust.AppSurface.Durable.Provider`](ForgeTrust.AppSurface.Durable.Provider/README.md) is the runtime-provider and
  operator SPI for claims, pumping, health, drain, recovery, and controlled repair.
- [`ForgeTrust.AppSurface.Durable.PostgreSql`](ForgeTrust.AppSurface.Durable.PostgreSql/README.md) is the first
  authoritative-store implementation. Slices 3–6 supply explicit schema management, Work, Flow, Schedule, and an
  explicitly opted-in hosted runtime.
- [`ForgeTrust.AppSurface.Durable.Testing`](ForgeTrust.AppSurface.Durable.Testing/README.md) provides production-backed
  deterministic test builders, provider fakes, pump histories, host-scenario observations, and typed Work observations.
  It does not simulate persistence or replace real-provider conformance tests.

All four packages participate in the coordinated prerelease publish plan. They remain preview contracts: adopt them
only with the reviewed schema, role, recovery, and operational evidence described below, and do not treat the preview
as production support.

For #765 retry timing choices, start with the [execution-policy v1 reference](execution-policies-v1.md). It compares
legacy completion-relative backoff, deadline-only execution, and fixed acceptance-relative attempt plans, then links the
source-compiled adopter example, diagnostics, and schema-12 rollout procedure.

## Deterministic retry-plan timeline

A planned Work's authoritative acceptance instant is the database insertion time, sampled inside the caller-owned
transaction. The commit makes the row visible; it does not move the schedule's anchor.

```text
accepted_at = A

slot (zero-based)        0          1           2           3            4          cutoff
attempt (one-based)      1          2           3           4            5          exclusive
earliest eligibility     A          A+5m        A+20m       A+60m        A+180m     A+240m
                         |----------|-----------|-----------|-------------|
caller-owned transaction [insert at A -------------------------------- commit at A+25m]
```

The final offset is strictly before the exclusive circuit cutoff: 180 minutes < 240 minutes. A late commit consumes
part of the window before a worker can see the Work. A delayed first claim or worker downtime does not rebase offsets
or skip slots; downtime alone executes nothing. Only a provider-authorized safe sequential retry transition can
advance to the next slot. For example, if attempt 1 safely fails at A+30m, attempt 2's fixed A+5m slot is already
overdue and may be immediately eligible. Each subsequent slot still requires its own safe sequential transition. The
plan sets earliest eligibility, not punctual execution.

The source-linked Core example below checks the immutable offsets and one-slot validation using public policy facts.
It does not simulate a database claim or external I/O. For authoritative behavior, see the
[packed real-PostgreSQL five-slot proof](packed-consumers/PostgreSqlPolicyConsumer/PlannedRetryPostgreSqlConsumerTests.cs),
the [overdue-slot boundary test](ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlDurableWorkExecutionPolicyTests.cs#L11),
and the [caller-owned delayed-commit proof](ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlDurableWorkExecutionPolicyTests.cs#L735).
The [execution-policy reference](execution-policies-v1.md#validate-the-fixed-schedule-in-the-adopter) describes the
provider boundary and rollout.

<!-- appsurface:snippet id="durable-execution-policy-timing-proof" file="Durable/packed-consumers/Adopter/TypedWorkDefinitionProof.cs" marker="durable-execution-policy-timing-proof" lang="csharp" -->
```csharp
internal static void VerifyExecutionPolicyTimingProof()
{
    var acceptedAt = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var plan = new DurableAttemptPlan(
        "attempt-plan-v1",
        [TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20),
         TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180)],
        TimeSpan.FromMinutes(240));
    var expectedOffsets = new[]
    {
        TimeSpan.Zero,
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(20),
        TimeSpan.FromMinutes(60),
        TimeSpan.FromMinutes(180),
    };
    var expectedAttemptNumbers = new[] { 1, 2, 3, 4, 5 };
    var expectedEligibility = new[]
    {
        acceptedAt,
        acceptedAt.AddMinutes(5),
        acceptedAt.AddMinutes(20),
        acceptedAt.AddMinutes(60),
        acceptedAt.AddMinutes(180),
    };
    var exclusiveCutoff = acceptedAt.Add(plan.MaximumCircuitDuration);
    if (!plan.ElapsedOffsets.SequenceEqual(expectedOffsets)
        || plan.MaximumCircuitDuration != TimeSpan.FromMinutes(240)
        || plan.ElapsedOffsets[^1] >= plan.MaximumCircuitDuration)
    {
        throw new InvalidOperationException("The fixed five-slot plan must keep every slot before its exclusive circuit cutoff.");
    }

    for (var slot = 0; slot < expectedOffsets.Length; slot++)
    {
        var eligibility = acceptedAt.Add(plan.ElapsedOffsets[slot]);
        if (eligibility != expectedEligibility[slot]
            || expectedAttemptNumbers[slot] != slot + 1
            || eligibility >= exclusiveCutoff)
        {
            throw new InvalidOperationException("Zero-based offsets must map to one-based attempts at fixed acceptance-relative times.");
        }
    }

    // Schedule arithmetic only: these observations do not simulate database claims or provider I/O.
    var callerCommitAt = acceptedAt.AddMinutes(25);
    var delayedFirstClaimAt = acceptedAt.AddMinutes(30);
    var downtimeRecoveryAt = acceptedAt.AddMinutes(90);
    var nextSlotAfterSafeAttemptOne = expectedEligibility[1];
    var overdueSlotsAfterDowntime = expectedEligibility.Count(time => time < downtimeRecoveryAt);
    if (!(acceptedAt < callerCommitAt && callerCommitAt < delayedFirstClaimAt)
        || nextSlotAfterSafeAttemptOne != acceptedAt.AddMinutes(5)
        || nextSlotAfterSafeAttemptOne >= delayedFirstClaimAt
        || overdueSlotsAfterDowntime != 4
        || expectedEligibility[4] <= downtimeRecoveryAt)
    {
        throw new InvalidOperationException("Late commit and downtime must not rebase or skip fixed slots; only safe sequential retries advance them.");
    }
}
```
<!-- /appsurface:snippet -->

<!-- appsurface:snippet id="durable-execution-policy-one-slot" file="Durable/packed-consumers/Adopter/TypedWorkDefinitionProof.cs" marker="durable-execution-policy-one-slot" lang="csharp" -->
```csharp
internal static void VerifyOneSlotPolicyValidation()
{
    static DurableWorkRetryPolicy CreateRetryPolicy(int maximumAttempts) => new(
        maximumAttempts: maximumAttempts,
        maximumElapsedTime: TimeSpan.FromHours(1),
        initialRetryDelay: TimeSpan.FromMinutes(1),
        maximumRetryDelay: TimeSpan.FromMinutes(1),
        leaseDuration: TimeSpan.FromMinutes(1),
        renewalCadence: TimeSpan.FromSeconds(15),
        maximumLeaseLifetime: TimeSpan.FromMinutes(5),
        backoffAlgorithm: "exponential-v1");

    var oneSlotPlan = new DurableAttemptPlan(
        "attempt-plan-v1",
        [TimeSpan.Zero],
        TimeSpan.FromMinutes(30));
    var oneAttemptPolicy = DurableWorkExecutionPolicy.ForAttemptPlan(
        CreateRetryPolicy(maximumAttempts: 1),
        oneSlotPlan);
    var acceptedPlan = oneAttemptPolicy.AttemptPlan;
    if (oneAttemptPolicy.RetryPolicy.MaximumAttempts != 1
        || acceptedPlan is null
        || acceptedPlan.ElapsedOffsets.Count != 1
        || acceptedPlan.ElapsedOffsets[0] != TimeSpan.Zero)
    {
        throw new InvalidOperationException("A one-slot plan permits exactly one execution and no planned retry.");
    }

    var retryCountMismatchRejected = false;
    try
    {
        _ = DurableWorkExecutionPolicy.ForAttemptPlan(
            CreateRetryPolicy(maximumAttempts: 2),
            oneSlotPlan);
    }
    catch (ArgumentException)
    {
        retryCountMismatchRejected = true;
    }

    if (!retryCountMismatchRejected)
    {
        throw new InvalidOperationException("A one-slot plan must reject a retry policy that permits another attempt.");
    }
}
```
<!-- /appsurface:snippet -->

For the internal W3C causal-link contract, safe telemetry attributes, deployment order, and reference proof, read
[Durable Flow trace context v1](flow-trace-context-v1.md). It supplies persistence and crash-proof seams now; it does
not make Slice 4 a hosted runtime.

For the champion-tier upgrade path, start with the [Durable operational-assessment adoption guide](operational-assessments.md).
It explains the computed health predicates, direct authoritative admission, exhaustive attempt handling, diagnostics,
schema `9 -> 10` rollout, complete PostgreSQL role-pair reconciliation, and the supported `v0.2.0-preview.8`
binary rollback boundary. For the canonical manifest and exact grants, use the
[PostgreSQL provider role-recipe reference](ForgeTrust.AppSurface.Durable.PostgreSql/README.md#role-recipe-contract)
and its [two-pair local walkthrough](../examples/durable-postgresql/README.md#version-1-role-pair-walkthrough).

For a passive external wake host, start with the canonical [external activation reference](external-activation-v1.md)
and its [authenticated PostgreSQL example](../examples/durable-external-activation/README.md). The service composes
provider health with one authoritative admission attempt, a separate cooperative request budget, closed outcomes, and
bounded activity tags; the host still owns routes, authorization, HTTP policy, probes, and deployment limits.

For host and module tests, use the [Durable Testing package guide](ForgeTrust.AppSurface.Durable.Testing/README.md)
for its six health-state defaults, authoritative pump-admission scenario, timeout handling, history retention, and
privacy boundaries. Pair it with PostgreSQL conformance when a test needs evidence about stored or recovered state.

## Why this boundary

Reusable modules should describe durable intent without selecting storage or starting workers. Runtime providers need
public, testable contracts without friend access to the application package. The dependency therefore points one way:

`ForgeTrust.AppSurface.Durable.PostgreSql` → `ForgeTrust.AppSurface.Durable.Provider` → `ForgeTrust.AppSurface.Durable`

The application package registers only passive registries. A provider is selected explicitly by the host. The PostgreSQL
provider adds explicit migrations (`0001_work_shared`, `0002_forced_rls`, `0003_flow_protocol`,
`0004_schedule_protocol`, `0005_runtime_heartbeat`, `0006_flow_trace_context`, `0007_flow_retention`,
`0008_flow_repair`, `0009_work_contract_discovery`, `0010_runtime_health_observation`, and
`0011_runtime_heartbeat_retention`, and `0012_work_execution_policy`) plus one-operation-at-a-time Work, Flow, and
Work-first Schedule persistence with versioned W3C causal evidence, verified retention, evidence-first Flow repair, and
opt-in execution deadlines and fixed attempt plans. Schema 12 is the reader/writer compatibility floor for opted-in
execution policies; legacy rows retain their existing timing and fingerprint behavior. PostgreSQL registration remains passive; an
application explicitly adds one bounded polling host through
[`AddWorkerHost()`](ForgeTrust.AppSurface.Durable.PostgreSql/README.md#run-a-worker-host) only where it intends
continuous activation. It adds no public endpoint, dashboard, or automatic migration.

The minimum supported PostgreSQL compatibility floor is PostgreSQL 16 or newer (`server_version_num >= 160000`); CI and default strict proof
use `postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877`.

## Slice 7 discovery and reconciliation

Slice 7 remains a public-preview surface. The documentation below describes its discovery and reconciliation contract;
it does not replace deployment review or confer production support.

Storage registration is passive. The PostgreSQL provider opens no worker and installs no hosted service until the host
explicitly calls [`AddWorkerHost()`](ForgeTrust.AppSurface.Durable.PostgreSql/README.md#run-a-worker-host). Startup validates the
stored schema version and active runtime epoch, fails closed on incompatibility, and never applies DDL or silently
advances schema history.

The forward-only deployment order is:

1. `0001_work_shared.sql`
2. `0002_forced_rls.sql`
3. `0003_flow_protocol.sql`
4. `0004_schedule_protocol.sql`
5. `0005_runtime_heartbeat.sql`
6. `0006_flow_trace_context.sql`
7. `0007_flow_retention.sql`
8. `0008_flow_repair.sql`
9. `0009_work_contract_discovery.sql`
10. `0010_runtime_health_observation.sql`
11. `0011_runtime_heartbeat_retention.sql`
12. [`0012_work_execution_policy.sql`](https://github.com/forge-trust/AppSurface/blob/codex/make-it-so-765-retry-deadlines/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/Migrations/0012_work_execution_policy.sql)
13. [`Durable/configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql)

The preferred production flow is to generate and review the Durable schema script offline, drain and stop every pre-`0009`
worker, apply the reviewed migrations in the order above (including `0012_work_execution_policy.sql`), apply the canonical
role recipe, and run schema status/preflight before enabling the
worker host. The [`durable schema` CLI commands](../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands)
make those checks discoverable. `apply --apply` is an explicit migration-owner operation only; deployments normally
pass `--connection-env APPSURFACE_DURABLE_MIGRATION_CONNECTION`, and it is never a startup side effect. Offline and
online commands accept no connection-string argument and never print connection strings.

For recovery, inspect status first, produce a corrected and reviewed forward-only script, then retry the intended
operation. Never delete or rewrite migration history. The [`durable-postgresql` example](../examples/durable-postgresql/README.md)
is a local proof of the boundaries above, not production operations guidance.

The role recipe takes a complete version-1 `role_pairs_json` manifest on every run. Each pair explicitly chooses
`full` or `work_only`; omission is not retirement. Since schema 11, the reviewed deployment manifest is the authority
for the complete pair set, and the recipe refuses omissions it can observe in package ACLs or policy targets. Compare
the exact reviewed manifest file with the prior release record because a privileged actor could erase every catalog
trace of a former pair. The recipe is transactional, but policy DDL can wait briefly on active work. See the
[operator rollout and rollback order](operational-assessments.md#migration-and-role-reconciliation) and the
[complete runtime-set preflight checklist](heartbeat-retention-operations.md#complete-runtime-set-preflight-and-proof-checklist).

Typed Work authoring is documented in the [typed Work definition migration guide](migrations/typed-work-definitions-v1.md).
It is a syntax and rollout guide; the existing PostgreSQL workload remains the evidence for acceptance and terminal
completion.

## Scale and transport boundary

PostgreSQL is the first planned authoritative provider, not the definition of AppSurface Durable. The adopter contracts
describe accepted Work, Flow, Schedule, payload, and external-effect semantics without selecting a database, polling
loop, queue, or broker. The Provider SPI likewise describes bounded activation and fenced execution without exposing a
broker acknowledgement as durable truth.

A deployment may evolve in two distinct ways:

- a wake-only broker or notification may activate `IDurableRuntimePump`; the authoritative provider still discovers,
  claims, fences, and completes eligible work, and a periodic pass remains the recovery path for lost notifications;
- a future broker-backed provider may implement the Provider SPI directly when it can preserve the same acceptance,
  revision, execution-identity, provider-effect, schedule, and recovery contracts.

Slice 2 intentionally does not define a targeted broker-dispatch token or general event-bus API. Those shapes require a
concrete broker and deployment need. Queue delivery alone must never authorize execution, prove completion, or replace
the provider's authoritative history.

The preview persists explicit Work and Flow decisions rather than arbitrary `async` stack state. It also makes no
exactly-once claim for external effects. Provider safety, immutable execution identity, revision fences, and versioned
command fingerprints make ambiguity observable and fail closed.

Operational failures use the shared [`ASDURxxx` diagnostics catalog](../troubleshooting/durable-diagnostics.md), including
the fixed hosted-runtime liveness and worker-generation codes.

For the PostgreSQL boundary, start with the [`slice 3 reference workload`](slice3-reference-workload.md), [`slice 4 reference workload`](slice4-reference-workload.md), [`Schedule protocol v1`](schedule-protocol-v1.md), and [Durable Flow trace context v1](flow-trace-context-v1.md), then use the
normative [`Work protocol v1`](work-protocol-v1.md) and [`Flow protocol v1`](flow-protocol-v1.md). The
[`slice 3 reconstruction ledger`](slice3-reconstruction.md) and [`slice 4 reconstruction ledger`](slice4-reconstruction.md) account for every artifact in the superseded branches.

Terminal Flow evidence now has a [verified retention lifecycle](ForgeTrust.AppSurface.Durable.PostgreSql/README.md#verified-flow-retention): a bounded per-Flow assessment, immutable manifest, reproducible archive package, receipt/source correspondence proof, optional hold, and separately authorized idempotent purge. It is intentionally not an age-based deletion feature. The application owns authorization, archive transport, encryption, availability, policy duration, and compliance.

The [slice 2 API budget](api-budget.md) records which original public contracts were retained, moved, added,
internalized, or removed. The package test projects enforce the corresponding member-level API snapshots.
