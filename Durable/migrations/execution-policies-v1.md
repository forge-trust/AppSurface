# Durable Work execution-policy migration v1

This checklist upgrades an existing Durable PostgreSQL store for immutable attempt plans and absolute execution deadlines. Read the [execution-policy reference](../execution-policies-v1.md) first to select legacy, deadline-only, or planned behavior. This migration changes the compatible package floor; it is not a zero-downtime rolling upgrade for old workers.

## Compatibility boundary

Schema `12` adds nullable timing facts while leaving legacy rows on their existing completion-relative retry behavior. The compatibility floor for readers and writers is `12`. Every producer, dispatcher, operator, runtime, release/preflight tool, and future worker that can touch the store must understand the new schema before it is applied. A pre-floor binary must fail compatibility validation before it can process rows.

| Before migration | After migration |
|---|---|
| All producers and workers understand only legacy retry fields | All connecting packages pass the schema-12 reader/writer floor |
| Stop submissions that could create new execution-policy Work; drain old workers and activators | Migration owner applies the reviewed forward-only migration and role recipe |
| Verify current schema, runtime epoch, and complete role set | Verify schema status, checksums, compatibility ranges, runtime epoch, and preflight under each runtime credential |
| Keep historical Work registrations available | Deploy compatible packages everywhere, then accept a new immutable Work version |

Do not apply DDL from application startup. Generate and review the schema script with the matching PostgreSQL package, apply it only with the migration-owner credential, and use the same released package's role recipe. Preserve migration `0001` through `0011` bytes and history. Do not lower the reader/writer floor in a later migration or make an old package appear compatible by editing metadata.

## Added facts and unchanged legacy rows

Migration `0012` adds nullable Work fields `execution_policy_schema`, `attempt_plan_version`, `attempt_plan_offsets`, `maximum_circuit_microseconds`, `execution_not_after`, `execution_deadline_reached_at`, `execution_admission_closed_at`, and `execution_admission_closed_reason`. Deadline-only and planned Work use `work-execution-v1`; the attempt-plan triple is populated only for planned Work. Legacy rows keep all opt-in fields null. Existing retry and lease columns remain the single stored retry-policy representation.

The closure records have separate persistence rules. `execution_admission_closed_at` and
`execution_admission_closed_reason` preserve the first observed cutoff and are never overwritten. The internal
`execution_deadline_reached_at` independently preserves the first authoritative observation at or after
`execution_not_after`; it is still recorded if circuit or planned-slot exhaustion closed admission first. These are
storage facts, not additional public API members.

Related additions persist one-use invocation admission, history retention-policy identity for quarantined observations, and a payload-free dispatch discovery hint. Actual `due_at` remains the execution eligibility timestamp. The production `work_execution_now()` clock is `VOLATILE SECURITY INVOKER`; the isolated fixture's controlled clock is a separate test-only implementation and must not appear in deployed migration SQL.

No row is backfilled with an execution policy, plan, or deadline. Existing v1 request fingerprints remain byte-for-byte stable. New opt-in requests use v2 fingerprint semantics; the server-generated acceptance timestamp is not part of request identity. An identical duplicate still resolves to its original row after expiry; changing timing facts under the same command/idempotency identity conflicts.

## Adoption and rollback

1. Select existing Work versions that should remain legacy. Define a new immutable Work version for changed timing semantics and keep its old registration available for historical rows.
2. Stop new submissions during the floor change. Drain old producers, readers, dispatchers, runtimes, operator tools, and scheduled activators that cannot read/write schema 12.
3. Generate, inspect, and apply migration `0012` as migration owner. Apply the matching role recipe and preserve the forced-RLS and migration-owner boundaries.
4. Deploy the compatible Durable, Provider, PostgreSQL, and dependent operator packages to every host that can connect to the store. Check schema status and epoch health under the real runtime credentials before activation.
5. Run the strict PostgreSQL compatibility gate and the fresh-feed [packed consumer gate](https://github.com/forge-trust/AppSurface/blob/codex/make-it-so-765-retry-deadlines/Durable/verify-packed-consumers.sh). The packed PostgreSQL proof must use the controlled fixture clock, execute real SQL against the pinned server, and report zero skipped tests.
6. Register and accept the new Work version only after every reader and worker is compatible. Inspect the stored policy/deadline through the authorized Work inspection API; logs and metrics remain payload-free.

To roll back application behavior, stop accepting the new version and retain schema-12-capable packages until accepted Work is drained or reconciled. Keep compatible code available for recovery. Never reintroduce the pre-floor package, delete or rewrite migration history, extend a deadline, or infer “no effect” from expiry. See the [schema and epoch deployment procedure](../ForgeTrust.AppSurface.Durable.PostgreSql/README.md#explicit-schema-and-epoch-deployment) and the [diagnostic actions](../../troubleshooting/durable-diagnostics.md#execution-deadline-and-attempt-plan-diagnostics).
