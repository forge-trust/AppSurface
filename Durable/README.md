# Portable durable execution

AppSurface Durable is a source-only public preview of portable durable contracts. It is split by audience:

- [`ForgeTrust.AppSurface.Durable`](ForgeTrust.AppSurface.Durable/README.md) is the application and reusable-module API
  for work, Flow, schedules, serialization, registration, and clients.
- [`ForgeTrust.AppSurface.Durable.Provider`](ForgeTrust.AppSurface.Durable.Provider/README.md) is the runtime-provider and
  operator SPI for claims, pumping, health, drain, recovery, and controlled repair.
- [`ForgeTrust.AppSurface.Durable.PostgreSql`](ForgeTrust.AppSurface.Durable.PostgreSql/README.md) is the first
  authoritative-store implementation. Slice 3 supplies explicit schema management and a manually driven Work engine;
  it starts no hosted worker.

All three packages are machine-held out of every publish plan until slices 4-6 prove Flow, Schedule, hosted runtime,
drain/recovery, and operational conformance in a coordinated release review. They can be built and packed directly for
contract verification, but they are not a supported NuGet release.

## Why this boundary

Reusable modules should describe durable intent without selecting storage or starting workers. Runtime providers need
public, testable contracts without friend access to the application package. The dependency therefore points one way:

`ForgeTrust.AppSurface.Durable.Provider` → `ForgeTrust.AppSurface.Durable`

`ForgeTrust.AppSurface.Durable.PostgreSql` → `ForgeTrust.AppSurface.Durable.Provider` → `ForgeTrust.AppSurface.Durable`

The application package registers only passive registries. A provider is selected explicitly by the host. The PostgreSQL
source preview adds explicit migrations and one-operation-at-a-time Work persistence, but no polling host, scheduling
execution, hosted service, endpoint, or telemetry implementation.

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

Operational failures use the shared [`ASDURxxx` diagnostics catalog](../troubleshooting/durable-diagnostics.md). Codes
for later hosted-runtime behavior remain reserved there.

For the PostgreSQL boundary, start with the [`slice 3 reference workload`](slice3-reference-workload.md), then use the
normative [`Work protocol v1`](work-protocol-v1.md). The
[`slice 3 reconstruction ledger`](slice3-reconstruction.md) accounts for every artifact in the superseded branch.

The [slice 2 API budget](api-budget.md) records which original public contracts were retained, moved, added,
internalized, or removed. The package test projects enforce the corresponding member-level API snapshots.
