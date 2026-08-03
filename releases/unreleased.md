# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.5`. It stays provisional until the next tag is cut.

## What is taking shape

- [`appsurface canary poll`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-canary-poll) now turns one existing protected named-canary evaluation into a read-only, caller-owned deployment proof. It validates an application base URL and environment-only marker/credential sources before dispatch, preserves path bases, disables redirects and hidden client timeouts, parses the preview compatibility core by property name, and gives `pending` plus recoverable transport failures an explicit bounded polling lifecycle. Safe text and JSON outcomes expose only the canary name, attempts, elapsed time, diagnostic, bounded reason/summary, next action, and documentation URL; raw credentials, markers, headers, URLs, and response bodies never render. `pass` alone exits `0`; semantic canary failures, protocol failures, transient exhaustion, deadlines, and cancellation use stable nonzero exits. The tool remains a caller rail, not a canary trigger, readiness probe, deployment controller, identity broker, or composite Action.
- `ForgeTrust.AppSurface.Theming` and the explicit `ForgeTrust.AppSurface.Web` Razor adapter now provide immutable semantic theme pairs with native System/Light/Dark browser output. Docs is part of the MVP: its default `AppSurfaceDark` path can consume the shared pair while preserving its public density, chrome, Graphite compatibility, short-hex overrides, static published-tree output, and internal token ownership. RazorWire styling remains limited to generated form-error nodes and uses the nonce-bearing critical stylesheet under strict CSP. Package discovery, quickstart, diagnostics, Docs migration, test commands, and deliberate non-goals ship with the feature; user preference persistence, tenant selection, shared Graphite, remote packs, and adoption telemetry remain separate policy work.
- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- The [release tool](../tools/ForgeTrust.AppSurface.Release/README.md) now treats checked-in versioned sidecars as explicitly `prepared`, then derives a transient `tagged` projection only after an annotated tag binds the prepared sidecar, manifest, and evidence digests. The new `tag-message` and `inspect` commands validate the tag object, tagger, base-branch reachability, V1/V2 evidence, package surface, and preparation commit before publishing. Docs publication uses the validated projection only in a disposable checkout and retains its inspect proof; prepared source files never acquire tag or GitHub Release claims.
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
- Coordinated package documentation now follows the release that was current when that documentation tree was published: current docs use the stable [`releases/current.md`](./current.md) pointer, while historical trees retain their original versioned release notes. The [release tool](../tools/ForgeTrust.AppSurface.Release/README.md) records and validates that contract through [versioned manifest and evidence V2 artifacts](./README.md#release-evidence-bundle), preserving V1 evidence compatibility and rejecting incomplete, conflicting, or unknown package release-link declarations.
- Add release-facing changes here.

## Migration watch

- Apply `0004_schedule_protocol.sql` with the migration-owner workflow before constructing Schedule clients or processors. Runtime credentials must remain distinct non-owner, non-`BYPASSRLS` dispatcher and scoped-runtime roles; use [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) rather than granting table access directly.
- Schedule history partitions cover the current and following UTC months. Before the boundary is crossed, an operator must run `appsurface_durable.ensure_schedule_history_partitions()` as the migration owner; a missing partition fails writes visibly rather than routing data elsewhere.
- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.
