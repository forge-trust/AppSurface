# Portable durable execution

AppSurface Durable is a source-only public preview of portable durable contracts. It is split by audience:

- [`ForgeTrust.AppSurface.Durable`](ForgeTrust.AppSurface.Durable/README.md) is the application and reusable-module API
  for work, Flow, schedules, serialization, registration, and clients.
- [`ForgeTrust.AppSurface.Durable.Provider`](ForgeTrust.AppSurface.Durable.Provider/README.md) is the runtime-provider and
  operator SPI for claims, pumping, health, drain, recovery, and controlled repair.
- [`ForgeTrust.AppSurface.Durable.PostgreSql`](ForgeTrust.AppSurface.Durable.PostgreSql/README.md) is the first
  authoritative-store implementation. Slices 3–6 supply explicit schema management, Work, Flow, Schedule, and an
  explicitly opted-in hosted runtime.

All three packages are machine-held out of every publish plan pending coordinated release evidence. They can be built
and packed directly for contract verification, but they are not a supported NuGet release.

For the internal W3C causal-link contract, safe telemetry attributes, deployment order, and reference proof, read
[Durable Flow trace context v1](flow-trace-context-v1.md). It supplies persistence and crash-proof seams now; it does
not make Slice 4 a hosted runtime.

## Why this boundary

Reusable modules should describe durable intent without selecting storage or starting workers. Runtime providers need
public, testable contracts without friend access to the application package. The dependency therefore points one way:

`ForgeTrust.AppSurface.Durable.PostgreSql` → `ForgeTrust.AppSurface.Durable.Provider` → `ForgeTrust.AppSurface.Durable`

The application package registers only passive registries. A provider is selected explicitly by the host. The PostgreSQL
source preview adds explicit migrations (`0001_work_shared`, `0002_forced_rls`, `0003_flow_protocol`,
`0004_schedule_protocol`, `0005_runtime_heartbeat`, `0006_flow_trace_context`, and `0007_flow_repair`) plus
one-operation-at-a-time Work, Flow, and Work-first Schedule persistence with versioned W3C causal evidence and
evidence-first Flow repair. PostgreSQL registration remains passive;
an application explicitly adds one bounded polling host only where it intends continuous activation. It adds no public
endpoint, dashboard, or automatic migration.

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

The [slice 2 API budget](api-budget.md) records which original public contracts were retained, moved, added,
internalized, or removed. The package test projects enforce the corresponding member-level API snapshots.
