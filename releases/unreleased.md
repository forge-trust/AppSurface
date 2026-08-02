# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.5`. It stays provisional until the next tag is cut.

## What is taking shape

- Applications can now persist PostgreSQL-backed `At`, `After`, and `Every` Schedules for registered durable Work, then run a bounded, externally triggered due pass without introducing a hosted scheduler. The Work-first gate keeps each occurrence's Work acceptance and Schedule facts in one transaction, coalesces overlapping QueueOne runs, and suspends rather than silently crossing clock, runtime-epoch, or scope-generation safety fences. Start with the [Schedule protocol](../Durable/schedule-protocol-v1.md) and the [PostgreSQL provider guide](../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md).

## Included in the next coordinated version

### Release and docs surface

- [`ForgeTrust.AppSurface.Web`](../Web/ForgeTrust.AppSurface.Web/README.md) and
  [`ForgeTrust.AppSurface.Web.Push`](../Web/ForgeTrust.AppSurface.Web.Push/README.md) now provide privacy-safe,
  schema-versioned PWA push-readiness posture. Web diagnostics contain either a fixed, redacted VAPID key identifier,
  SHA-256 public-key fingerprint, and package-route mapping bit, or an explicit unavailable/not-configured state.
  The optional Push package contributes when validated active VAPID configuration is present and reports route mapping
  as a separate readiness bit; no private keys, endpoints, subscriptions, payloads, or provider exception text are published.
- [`appsurface pwa verify`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-pwa-verify) now preserves its
  schema-v2 install default while `--surface push|all` emits schema-v3 server-known readiness evidence. It verifies
  worker/helper discovery, direct JavaScript responses, headers, and cache behavior, and clearly marks browser,
  permission, subscription, notification, and delivery observations as not evaluated.
- [`ForgeTrust.AppSurface.Web` named canaries](../Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints)
  now include a bounded protected aggregate snapshot at `GET /_appsurface/canaries`. Operators can select registered
  canaries by exact name or durable tag, receive ordinal partial outcomes under explicit concurrency and deadline caps,
  and parse a privacy-safe envelope with fixed telemetry. The feature does not add triggers, retries, polling, readiness
  effects, or authorization-policy ownership; hosts retain those decisions.
- The PostgreSQL durable schema adds the Schedule ledger, payload-free dispatch leases, forced-RLS history partitions, and a reviewed role recipe. Operators can use the [migration and role setup guidance](../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#explicit-schema-and-epoch-deployment) before enabling the manual processor.

## Migration watch

- Apply `0004_schedule_protocol.sql` with the migration-owner workflow before constructing Schedule clients or processors. Runtime credentials must remain distinct non-owner, non-`BYPASSRLS` dispatcher and scoped-runtime roles; use [`configure-postgresql-roles.sql`](../Durable/configure-postgresql-roles.sql) rather than granting table access directly.
- Schedule history partitions cover the current and following UTC months. Before the boundary is crossed, an operator must run `appsurface_durable.ensure_schedule_history_partitions()` as the migration owner; a missing partition fails writes visibly rather than routing data elsewhere.
