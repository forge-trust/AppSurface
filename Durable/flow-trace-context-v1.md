# Durable Flow trace context v1

This guide defines the internal, versioned W3C trace-context contract for PostgreSQL-backed durable Flow execution. It is an operational correlation feature: it is never authorization, scope routing, payload storage, or a substitute for PostgreSQL RLS.

> **Preview boundary:** Slice 4 supplies the deterministic persistence and process-loss proof described here. Public hosted-runtime activation remains Slice 6; applications must not treat the reference processor as a hosted worker.

## Contract

The internal `flow_trace_context` relation stores only validated W3C version-`00` fields and durable metadata:

| Field | Rule |
| --- | --- |
| `trace_context_id` | Storage-generated UUID, referenced through `(scope_id, trace_context_id)`. |
| `traceparent` | Exactly W3C version `00`, 55 characters, lower-case hexadecimal IDs, never all-zero. |
| `tracestate` | Optional opaque ASCII value, at most 512 characters. It is not parsed for routing. |
| `correlation_token` | Storage-generated UUID exported as the only identifier-valued durable tag. |
| `cause_kind` | One of `command_accepted`, `activity_scheduled`, `activity_completed`, `event_winner`, `timer_winner`, or `evaluation_committed`. |
| `contract_version` | `1`; a future W3C wire version requires a new durable contract version. |

No baggage, payload, scope, principal, Flow/command/event ID, exception text, or raw W3C value is exported as telemetry.

## Lifecycle and trust

- Command acceptance captures a valid ambient `Activity.Current` context. When a listener records the command producer Activity, the committed command Activity becomes the cause; otherwise the valid ambient context is retained.
- A Flow turn, timer, or child Work execution starts a fresh root Activity and supplies the persisted cause as an `ActivityLink` at creation time. Waits, scheduler delays, retries, and process gaps are never span duration.
- Start writes the Flow row, command/history evidence, immutable trace row, and composite trace pointers inside one transaction. A transaction rollback leaves no trace evidence.
- A valid `traceparent` with rejected `tracestate` keeps the parent and drops only state. An invalid `traceparent` drops both fields. Persisted corruption never stops Flow evaluation; it produces an unlinked execution.

## Diagnostics

| Code | Safe message | Behavior |
| --- | --- | --- |
| `ASDUR212` | Trace context invalid | Drop invalid `traceparent` and `tracestate`; continue without a link. |
| `ASDUR213` | Trace state rejected | Keep the valid `traceparent`, drop `tracestate`, and continue with a link. |

Diagnostics contain only the code, cause kind, and context status. Never log header values.

## Fixed telemetry attributes

Only these tags are emitted by Durable instrumentation:

- `appsurface.durable.trace.contract_version`
- `appsurface.durable.execution.kind`
- `appsurface.durable.trigger.kind`
- `appsurface.durable.flow.state`
- `appsurface.durable.outcome`
- `appsurface.durable.correlation_token`
- `appsurface.durable.context.status`

Configure the canonical `ForgeTrust.AppSurface` source through the [Observability package](../Observability/ForgeTrust.AppSurface.Observability/README.md) or a host-owned OpenTelemetry registration.

## Five-minute warm local proof

With the existing PostgreSQL reference-workload prerequisites running, use an in-process listener to verify that a resumed Flow Activity contains a link:

```csharp
using System.Diagnostics;

using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == "ForgeTrust.AppSurface",
    Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllData,
    ActivityStarted = activity =>
    {
        foreach (var link in activity.Links)
        {
            Console.WriteLine($"{activity.OperationName}: {link.Context.TraceId}/{link.Context.SpanId}");
        }
    }
};

// Start the Flow with a valid ambient Activity, drive the reference processor,
// then inspect the fresh flow/timer Activity and its link.
```

Run the reference proof with `./Durable/verify-postgresql.sh --quick --flow`. For the strict real-PostgreSQL suite, use `./Durable/verify-postgresql.sh --ci --flow`.

## Deployment order and retention

1. Apply `0004_schedule_protocol.sql`, then the reviewed forward-only `0005_flow_trace_context.sql` migration with the migration-owner connection. Never rename or reorder an applied migration.
2. Re-run [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) after both migrations so only the scoped runtime can read or write trace metadata; the global dispatcher has no access.
3. Deploy trace-aware binaries. Older supported binaries keep null trace pointers; a later trace-aware rollout interprets those rows as `context.absent` and never backfills.

Trace rows retain the same lifecycle as their linked Flow protocol records until an explicit archival policy is introduced.
