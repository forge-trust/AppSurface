# Durable Schedule protocol v1

## Purpose and delivery boundary

This document is the normative persistence and processing contract for the PostgreSQL
Schedule implementation. It completes the public `IDurableScheduleClient` preview
contract without starting a hosted scheduler. A caller invokes one bounded
`PostgreSqlDurableScheduleProcessor.ProcessDueAsync` pass from its own trusted
trigger.

Slice 5 starts with the Work bridge. `At`, `After`, and `Every` schedules whose
target is registered Work are supported by Gate A. A Flow target is not admitted
until a caller-owned Flow-start transaction seam has been proven separately. The
processor never performs provider I/O and never waits for Work or Flow completion.

## Authoritative time and immutable definitions

Schedule create and update commands capture one PostgreSQL
`transaction_timestamp()` value. It is persisted on the command and immutable
generation as `accepted_at_utc`. It is the only anchor for `After`, and the anchor
for an `Every` schedule when the caller did not supply one. `At` and explicitly
anchored `Every` schedules persist their UTC value unchanged. The later Work
acceptance timestamp is deliberately not used: the Work protocol owns a separate
`clock_timestamp()` sample.

Definitions are append-only generations. Updating an active or suspended definition
creates a new generation and never rewrites its target snapshot. Deletion is terminal
for that Schedule identity; a later update cannot revive it, so an operator creates a
new identity when the definition must be restored. The active `schedule_definition`
row records lifecycle state, revision, active generation, runtime epoch, scope
generation, and the materialization cursor. An occurrence identity is stable across
processor retries:

`(scope_id, schedule_id, generation, kind, first_nominal_utc)`.

## Occurrence materialization

The cursor is a durable UTC high-water mark. A processor evaluates nominal instants
in `(cursor_utc, cutoff_utc]`, where the cutoff comes from PostgreSQL. It writes the
occurrence disposition and cursor movement in one scoped transaction. A rollback
leaves neither durable fact; a retry sees the same occurrence identity and must
return the original target link rather than enqueue another Work.

`nominal` occurrences have one UTC instant. `recovery` and `coalesced` occurrences
have an inclusive range `[first_nominal_utc, last_nominal_utc]`. A pending coalesced
row may only move its last nominal instant forward. The schema reserves
`pending`, `claimed`, `materialized`, `skipped`, `superseded`, `canceled`, and
`suspended` as materialization states. Gate A writes only `pending`, `materialized`,
and `superseded`; it does not claim individual occurrence rows. A future gate that
introduces `claimed` must define its lease expiry and the only permitted return to
`pending` before that state is used.

For the default `QueueOne` plus `RunOnce` policy, a nonterminal target occupies the
Schedule-wide slot. Subsequent overlapping nominal instants are represented by at
most one pending coalesced occurrence. A generation update supersedes old unstarted
occurrences, but an already started old-generation target remains durable history and
continues to occupy its slot.

When a materialized Work target reaches terminal truth, the Work transaction checks
for a pending coalesced occurrence in the Schedule's active generation. If one exists,
it requeues the Schedule dispatch row in that same transaction. The next bounded
Schedule pass materializes the coalesced occurrence without waiting for another
interval. Retries, suspended Work, and any still-nonterminal Work do not release the
slot. This callback only changes dispatch eligibility; occurrence identity, cursor
movement, and Work acceptance remain owned by the Schedule processor.

## Clock and evaluator fences

A cutoff at or behind the cursor is a no-op. A forward cutoff inside the configured
safety window creates one declared recovery range and follows the persisted misfire
policy. An anomalously large forward cutoff suspends the Schedule without moving its
cursor. `ReleaseAfterRecovery` can release an old runtime epoch only; it cannot move
the cursor or clear an evaluator/clock suspension. Repair requires a public update
(creating a new generation) or delete/recreate after the operator corrects the cause.

Before evaluating a due Schedule, the scoped processor compares its persisted runtime
epoch and scope generation with the active store epoch and locked scope generation. A
mismatch suspends the Schedule before it moves the cursor or accepts Work. An old
runtime epoch can use `ReleaseAfterRecovery`, which rebinds the current scope
generation; a scope-generation-only mismatch requires a public update or
delete/recreate to establish a new generation.

Cron evaluation is intentionally outside Gate A. Before CronosV1 is admitted, its
grammar, evaluator version, time-zone rules fingerprint, deterministic `H` seed, and
DST behavior must be persisted and verified. A mismatch suspends instead of silently
reinterpreting history.

## Transactions, security, and target bridge

There are two non-overlapping transaction forms:

1. A trusted dispatcher claims one payload-free dispatch queue row under a lease and
   commits before accessing scoped data.
2. A scoped runtime transaction sets the existing local scope setting, validates
   store/runtime/scope/generation fences, records Schedule facts, and bridges a
   pending occurrence.

The dispatch queue exposes only dispatch ID, Schedule ID, scope routing ID, lease
state/generation, and dispatch revision. It contains no payload, target identity,
policy, due time, cursor, or display label. PostgreSQL RLS controls rows; exact
column grants and the execution-only `claim_schedule_dispatch` function control the
queue surface. The security-definer function uses due time internally but returns
only scope routing ID, Schedule ID, and dispatch revision to the dispatcher. A bridge
must prove the connection's `current_user` is the configured runtime role before it
sets local scope state. The function rejects blank or control-character lease owners
and lease durations that are null, non-positive, or longer than ten minutes before it
changes a row, so an untrusted dispatcher call cannot strand a Schedule in a
non-expiring or excessively long lease.

The Work bridge calls the existing
`PostgreSqlDurableWorkTransactionWriter.EnqueueAsync(NpgsqlTransaction, DurableWorkRequest)`
inside the occurrence-link transaction. It uses a deterministic target command,
activity, Work ID, and idempotency identity derived from the immutable occurrence.
The occurrence link is compare-or-return: if no link exists, write the exact accepted
Work identity and materialized state; otherwise return the stored tuple only when it
matches exactly. A mismatch is an idempotency conflict.

## Manual processing API

`PostgreSqlDurableScheduleProcessor` is provider-specific and passive. Its
`ProcessDueAsync(PostgreSqlDurableScheduleProcessRequest, CancellationToken)` call
processes at most `MaximumSchedules` schedules (default 1), returns zero counts for
an empty pass, and observes cancellation before starting the next lease. Cancellation
does not roll back a committed Schedule fact. Applications must not call it in an
ASP.NET request loop or register it as hosted work; Slice 6 owns hosted activation.

## Crash boundaries and proof

The TestHost proof names these barriers: `schedule-decision-before-commit`,
`schedule-decision-after-commit`, `schedule-bridge-before-commit`,
`schedule-link-after-commit`, and `schedule-work-after-acceptance`. Recovery must
produce one occurrence and one accepted Work identity. The Work provider safety mode
governs provider effects; Schedule never claims universal effect-exactly-once.

## Operations and compatibility

Migration `0004_schedule_protocol.sql` is forward-only. Runtime version 4 requires
it before Schedule operations run. A v3 process retains only its already-proven Work
and Flow paths. Run `SELECT appsurface_durable.ensure_schedule_history_partitions()`
as the migration owner before the pre-created next month begins. It creates the
current and next `schedule_history` partitions and applies forced RLS and exact child
policy shape. The role recipe validates exact role grants after child creation; archive
or prune detached history only after an audit transcript.

## Diagnostics and recovery

Schedule API validation and domain conflicts return durable problem results. Database
timeouts, disconnects, and SQLSTATE failures abort the transaction and require a
bounded retry only after rollback. Access-denied and non-runtime bridge-role failures
are not retriable. Clock or evaluator mismatches suspend before target acceptance;
repair them with definition update or delete/recreate, not recovery release.
