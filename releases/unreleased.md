# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.5`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

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
- Add release-facing changes here.

## Migration watch

- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.
