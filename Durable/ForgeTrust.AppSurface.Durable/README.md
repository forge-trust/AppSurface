# ForgeTrust.AppSurface.Durable

`ForgeTrust.AppSurface.Durable` defines the host-neutral contracts for durable work, resumable
[AppSurface Flow](../../Flow/ForgeTrust.AppSurface.Flow/README.md), and durable schedules. A storage integration owns the
authoritative store, migrations, bounded runtime pump, and optional hosted execution.

The package is a public preview. It deliberately does not promise stable history compatibility until a second consumer,
rolling-upgrade proof, restore proof, and the published capacity gate have all passed.

For the package-family rationale and scope, start at [Portable durable execution](../README.md).

## Choose this package when

- a domain transaction must durably accept provider work before it commits;
- a typed Flow must survive process restarts, timers, activity calls, and authorized resume events;
- `At`, `After`, `Every`, or full Cronos schedules must recover deterministically after downtime; or
- PostgreSQL must remain the only required durable infrastructure.

Do not choose it for arbitrary replayable application code, exactly-once provider effects, child workflows, fan-out, a
general message bus, or a multi-database transaction. Existing Durable Task adapters remain supported when their control
plane and storage boundary are already acceptable.

## Contract map

| Surface | Start here | Important default |
|---|---|---|
| Durable work | `IDurableWorkClient`, `DurableWorkRequest`, `AddDurableWork` | At-least-once execution; provider safety must be declared |
| Transactional handoff | Storage integration transaction writer | Uses the caller's exact domain transaction |
| Flow | `IDurableFlowClient`, `DurableFlowRegistration<TContext>` | Exactly one node decision is persisted at a time |
| Scheduling | `IDurableScheduleClient`, `DurableSchedule` | `QueueOne` overlap plus `RunOnce` misfire |
| Worker activation | `IDurableRuntimePump.RunOnceAsync` | Item-bounded, with a time budget for starting additional work |
| Runtime health | `IDurableRuntimeHealth`, `IDurableRuntimeDrainControl` | Payload-free heartbeat and explicit graceful drain |
| Serialization | `IDurablePayloadCodec<T>`, `DurablePayloadCodecRegistry` | Only explicitly registered, policy-approved payloads |

`AppSurfaceDurableModule` is passive. It registers host-neutral registries but performs no DDL, polling, network access,
or provider work.

## Register durable work

Register source-generated JSON codecs and a typed `IDurableWorkerExecutor<TWork,TResult>`:

```csharp
services.AddDurableWork<RevokeAccess, RevokeAccessResult, RevokeAccessExecutor>(
    "skoolit.access.revoke",
    "v1",
    DurableProviderSafety.ProviderKeyed,
    revokeAccessCodec,
    revokeAccessResultCodec);
```

`ProviderKeyed` means the executor must send `work.ExecutionIdentity.ProviderKey` to the provider. That key derives from
the immutable activity identity and never changes with `AttemptNumber` or `LeaseGeneration`. Choose:

- `Idempotent` only when repeating the provider call is safe without a key;
- `ProviderKeyed` when the provider deduplicates an immutable operation key;
- `ReconcileBeforeRetry` when a side-effect-free read must establish provider state first; or
- `ManualResolution` when an unknown response can never be retried automatically.

An exception or lost response after `EffectPermitRecorded` does not prove the remote effect failed. The runtime follows
the registered safety mode and suspends ambiguous work rather than inventing exactly-once behavior.

## Payload registration

`SystemTextJsonDurablePayloadCodec<T>` requires `JsonTypeInfo<T>` from a source-generated JSON context plus an
application policy delegate. The policy is part of the durable trust boundary: reject credentials, provider URLs, email
addresses, raw provider responses, child-sensitive text, and other values that were not explicitly approved for durable
storage.

Contract names and versions are durable identifiers. Reusing one identity for a different byte shape is a compatibility
break. Payloads are copied, SHA-256 hashed, and capped at 256 KiB by protocol v1; registrations should normally choose a
smaller contract-specific limit.

`DurableDataClassification` and `RetentionPolicyId` are immutable metadata snapshotted with each payload. A retention
policy id does not delete history by itself. Applications must map it to reviewed archival or deletion operations that
respect nonterminal aggregates, legal holds, provider-effect evidence, and required audit history. The preview does not
ship a generic purge API because deleting an effect permit or active wait by age alone would destroy authoritative
truth. It also does not yet provide a retirement-blocker query that proves no live aggregate references a codec or
contract version. A reviewed purge/archival contract and that blocker inventory are required before stable release;
until then, treat retention ids as metadata and retain historical registrations.

## Status and authorized control

Use `IDurableWorkControlClient` for scoped work status and optimistic cancellation, `IDurableScopeControlClient` for
scope-generation fencing, `IDurableWorkOperatorClient` for suspended-work recovery, and `IDurableScheduleClient` for
schedule inspection and lifecycle commands. The application must authorize the trusted scope and operator before
calling these APIs. Actor ids and reason codes are privacy-safe audit identifiers, not free-form notes, and every
mutation requires the current revision or generation.

`IDurableFlowClient.GetAsync` returns a scoped, payload-free snapshot containing definition identity, state, current
node, revision, timestamps, and a privacy-safe terminal code. Use that revision for cancellation or an audited
`ReleaseSuspensionAsync`; never expose durable context bytes merely to build an operator status page. A release succeeds
only for a recoverable suspension, or an exact-revision dormant nonterminal instance owned by an older runtime epoch,
whose persisted definition manifest and wait/timer/activity shape still match the active runtime. Direct old-epoch
release preserves the prior state and active wait; it does not require an event, timer, or activity callback merely to
trigger lazy suspension first.

The operator surface is deliberately narrower than an arbitrary retry button:

- `ReconcileAsync` runs only a registered side-effect-free reconciler for `ReconcileBeforeRetry` work;
- `ResolveAsync` records authorized applied or proven-not-applied provider truth for `ManualResolution` work and for
  canceled `Idempotent`/`ProviderKeyed` work suspended on an ambiguous permit;
- `RetrySafeAsync` releases suspended work only when its immutable provider policy and permit evidence make replay safe;
  this is an explicit audited override that consumes any preserved cancellation before replay; and
- `ReleaseAfterRecoveryAsync` directly re-fences exact-revision old-epoch nonterminal work, including future-due work
  that never reached lazy discovery, without erasing due time or effect evidence. Ambiguous attempts remain in the
  provider policy's fail-closed suspension; cancellation without an ambiguous permit terminalizes before effect.

Each suspended-work operator operation above has an idempotent command id in addition to actor, reason, and expected
work revision. The PostgreSQL provider persists a started command before side-effect-free provider reconciliation and
later completes it; database-only operator transitions commit their completed command atomically. A retry therefore
returns prior truth instead of silently repeating reconciliation.

That guarantee does not yet extend to `IDurableWorkControlClient.CancelAsync` or
`IDurableScopeControlClient.DisableAsync`. They require an optimistic revision or generation plus audited actor and
reason, but carry no `DurableCommandId`; a caller that loses the committed response must re-read authoritative state
rather than deduplicate the command. An idempotent control-command ledger and typed causal command fields are required
before stable release.

Recovery inventory is explicit and scope-authorized; it is not a hidden pump sweep. `IDurableWorkControlClient.ListAsync`
returns ordered payload-free pages of up to 500 items, optionally filtered by public state or limited to nonterminal
old-epoch rows. Each item includes provider policy, cancellation intent, revision, and `RequiresRecoveryRelease`, but
never the work payload, provider key, or result. `IDurableFlowClient.ListAsync` returns payload-free pages of up to 1,000
snapshots, with optional state and `RequiresRecoveryRelease` filters. Use a listed revision with the corresponding
audited release API. These recovery flags identify old-epoch candidates; they do not prove manifest compatibility,
valid wait shape, or resolved provider truth. The [schedule client](#schedules) separately lists bounded schedule pages,
with lifecycle-state and old-epoch filters but no target input. Use its authorized `GetAsync` path when the full schedule
snapshot or encoded target input is required. Starting the replacement pump still does not enumerate every dormant
old-epoch aggregate.

Do not grant support users direct table access or build operator mutations from raw SQL. The public clients preserve
row-level scope, command idempotency, revision checks, fencing, and append-only history. See the
[`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md) for safe failure and recovery guidance.

## Durable Flow

The native runtime consumes `IFlowTransitionEvaluator<TContext>`, the same one-node semantic boundary used by the
in-memory and Durable Task adapters. Register a definition, context codec, evaluator, and any
`DurableFlowActivityBinding<TContext,TWork,TResult>` values. An activity transition atomically persists:

1. the node decision and updated context;
2. a registered durable work command; and
3. the wait for that activity's typed result.

Flow nodes are transition functions, not provider executors. A process can die after evaluating a node but before its
decision commits, so a node must not perform external I/O or mutate external state. Put those effects behind
`FlowNodeOutcome<TContext>.Activity(...)`.

Every durable registration requires an application-owned implementation version in addition to the public Flow
version. The persisted definition fingerprint covers that token, authoring model, context codec identity and policy,
evaluator identity/version, graph topology, typed event bindings, and activity work/result bindings. Change the
implementation token whenever executable node behavior changes without a new Flow version. Reusing the same manifest
for changed code is unsafe; a mismatch suspends the instance and cannot be released by the incompatible registration.

```csharp
services.AddDurableFlow(
    definition,
    flowContextCodec,
    implementationVersion: "2026-07-12.1",
    activityBindings: [sendNoticeBinding],
    eventBindings: [new DurableFlowEventBinding<ApprovalReceived>(approvalCallsite, approvalCodec)]);
```

Typed external waits must use a `FlowEventCallsite<TPayload>` that is bound to the exact globally registered codec.
The persisted wait records contract name/version, classification, and retention policy. A missing, wrong, or
policy-invalid payload is rejected before consuming the event id. The string-based `Wait` overload is an explicit
no-payload contract, not an untyped JSON escape hatch.

Generated `[FlowNode]` authoring warns with `ASFLOWA007` when a node directly reads `DateTime.Now`/`UtcNow`,
`DateTimeOffset.Now`/`UtcNow`, `Guid.NewGuid`, `Random`, or `Stopwatch.GetTimestamp`. Pass those values through persisted
context or resume contracts. Definition tests can also call `DurableFlowDeterminismVerifier.VerifyAndThrowAsync` to
evaluate one persisted input twice and compare canonical durable decisions. That harness samples the supplied branch;
it does not prove all possible inputs deterministic.

External events are application-authorized calls. Scope, instance id, event name, and event id are not authorization by
themselves. V1 accepts an event only while the exact wait is active; `NotWaitingYet` leaves the event id unconsumed so the
caller can retry. V1 intentionally has no early-event inbox.

## Schedules

Create schedule shapes with `DurableSchedule.At`, `.After`, `.Every`, or `.Cron`. Cron uses the versioned
`CronDialect.CronosV1`, its documented five- or six-field grammar, an IANA time zone, and deterministic `H` expansion.
Persisted UTC occurrences remain authoritative if dependency or time-zone rules later change.

The default composition is:

- `ScheduleMisfirePolicy.RunOnce`: downtime produces one recovery occurrence and advances to the next future tick;
- `ScheduleOverlapPolicy.QueueOne`: an active run retains at most one coalesced follow-up run.

Policies are per schedule. Use bounded `CatchUp` or bounded `AllowConcurrent` only when the target's business and
provider contracts make those choices safe. Updating a definition increments its generation and invalidates all
not-yet-started prior-generation occurrences; an already-running occurrence may finish.

`IDurableScheduleClient.ListAsync` is the preview's bounded schedule inventory: callers page within one authorized
scope, with a default page size of 100 and a maximum of 1,000, and may filter by lifecycle state or
`RequiresRecoveryRelease`. Returned list items are payload-free: they include target identity and provider policy but
not the encoded target input. Use authorized `GetAsync` for the full snapshot needed to inspect or edit a schedule.
After restore, use the listed id and revision with `ReleaseAfterRecoveryAsync`; that command accepts an exact-revision
old-epoch `Active` or `Paused` schedule, or an `ASDUR108` suspension that records one of those prior states. It preserves
pending/coalesced occurrence state and never resurrects a deleted schedule.

## Runtime health and graceful drain

`IDurableRuntimeHealth.GetAsync` returns a payload-free, low-cardinality `DurableRuntimeHealthSnapshot` suitable for an
application-owned health endpoint. It reports schema and epoch compatibility, the configured worker and process-instance
identities, hosted surfaces, heartbeat and last-successful-sweep timestamps, drain and active-pass markers, plus the
count and oldest age of due dispatches for those surfaces. It never returns a scope, aggregate id, provider key, or
payload. The selected storage integration defines registration and operational behavior.

The health snapshot is the preview's current observability API. The runtime does not yet emit a supported `Meter`,
metrics instrument set, or `ActivitySource` trace model. Stable release requires privacy-bounded metrics and tracing;
hosts should not infer those signals from high-cardinality scope or aggregate identifiers in the meantime.

`Healthy` requires a compatible schema and runtime epoch plus a current worker heartbeat. `NotStarted` and `Stale` use
[`ASDUR404`](../../troubleshooting/durable-diagnostics.md#schema-and-activation); incompatible schema or epoch uses the
corresponding `ASDUR108`/`ASDUR400`-series code. [`ASDUR405`](../../troubleshooting/durable-diagnostics.md#schema-and-activation)
means the configured worker id is still owned by another live process instance or an overlapping pass was attempted.
`Draining` is intentional state rather than a failure code.

`IDurableRuntimeDrainControl.BeginDrainAsync` prevents later bounded passes from claiming new aggregates. It does not
cancel provider I/O, revoke an effect permit, or wait for a pass already in flight. A custom host must begin drain, await
its active `RunOnceAsync` call, and only then terminate. The same worker id cannot hand off while that persisted pass is
active unless the owner becomes stale; a clean drained owner hands off immediately after the marker clears. `ResumeAsync`
is for startup or an aborted deployment, not for bypassing schema or restore fencing. The PostgreSQL hosted worker
performs resume on startup and a best-effort drain marker on shutdown.

## Runtime and recovery pitfalls

- PostgreSQL notifications and external queues are wake hints. The periodic sweep is the correctness path.
- A host scaled to zero needs an external activator calling `IDurableRuntimePump.RunOnceAsync` or schedules will not be
  timely. Notifications cannot start a nonexistent process, and the heartbeat stale bound must exceed the expected
  activator cadence if that process reports runtime health.
- Never hold a database connection or transaction while calling a provider.
- Disabling a scope or rotating the runtime epoch fences claims again at the effect boundary, not only at discovery.
- Runtime epoch rotation is a store-level compare-and-swap deployment action. Old clients fail before any scoped
  mutation; the new fleet suspends old-epoch rows while preserving active waits and timers.
- A point-in-time database restore requires stopping workers, rotating the out-of-band epoch, reconciling effects, and
  explicitly releasing safe aggregates from the bounded Work, Flow, and schedule inventories. Flow release restores `Ready`,
  event/timer/activity waits, or `CancelPending` only after compatibility and wait-shape validation. Restored pending
  rows must not run blindly, and the pump never substitutes for the authorized inventory and recovery audit.
- Automatic startup DDL is not supported. Apply numbered PostgreSQL migrations explicitly before compatible workers
  claim work.

## Release Guidance

Use the [package chooser](../../packages/README.md) for adoption status and dependency guidance. Versioned publication
evidence and release policy live in the [release hub](../../releases/README.md).
