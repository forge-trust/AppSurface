# Portable durable execution

AppSurface Durable is a small, PostgreSQL-first runtime for work that must survive process restarts, typed Flows that
must resume at explicit transition boundaries, and schedules that must recover after downtime. It keeps the application
contract host-neutral while putting the authoritative work, Flow, timer, schedule, lease, and effect history in the
application's PostgreSQL database.

Start with:

- [`ForgeTrust.AppSurface.Durable`](ForgeTrust.AppSurface.Durable/README.md) for host-neutral work, Flow, payload,
  schedule, status, and control contracts;
- [`ForgeTrust.AppSurface.Durable.PostgreSql`](ForgeTrust.AppSurface.Durable.PostgreSql/README.md) for storage, migrations,
  row-level security, worker activation, restore, and operations; and
- the [runnable PostgreSQL tutorial](../examples/durable-postgresql/README.md) for source-generated codecs, enqueue,
  bounded pumping, control, and Cronos scheduling.

## Why this boundary

Compute portability and storage portability are separate. The core package does not know about PostgreSQL or hosted
workers, so reusable modules can publish typed durable contracts without choosing a deployment model. The PostgreSQL
package is intentionally concrete: one database is the control plane, external queues are optional wake hints, and a
bounded `IDurableRuntimePump` works in continuous workers, jobs, functions, or HTTP activation.

This preview does not expose deterministic `async`/`await` replay. Flow definitions persist one explicit node decision,
activity call, wait, timer, or event continuation at a time. That keeps v1 failure boundaries inspectable and avoids
serializing arbitrary stack state. A later authoring layer can compile `async`-shaped code into the same versioned
commands and events, but it must not reinterpret existing history or hide provider-effect ambiguity.

## Non-goals

The runtime does not provide exactly-once external effects, arbitrary code replay, child workflows, unbounded fan-out,
a general message bus, a multi-database transaction, automatic production DDL, or a provider-independent storage SPI.
Use an existing Durable Task adapter when its control plane is already acceptable, or a larger workflow platform when
those capabilities are the actual requirement.

Operational failures use the shared [`ASDURxxx` diagnostics catalog](../troubleshooting/durable-diagnostics.md). Schema
deployment uses the canonical [`appsurface durable schema`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-durable-schema)
commands.
