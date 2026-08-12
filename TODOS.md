# Deferred work

## Named-canary snapshot follow-ups (#645)

- Consider POST exact-list batches, host-declared snapshot profiles, asynchronous jobs, or stored snapshots only if adopters show that the bounded synchronous `GET /_appsurface/canaries` contract is insufficient.
- Keep polling, retry/backoff, CI exit codes, and GitHub reporting in the caller/workflow follow-up tracked by #625; the AppSurface package remains a current-proof producer, not a deployment controller.
- Revisit schema versioning or a generated client only after a stable external consumer demonstrates a compatibility need.

## Durable runtime operations follow-ups (#641)

- Consider broker-backed activation only when a deployment requires scale-to-zero or a provider can preserve the existing bounded `IDurableRuntimePump.RunOnceAsync` contract. PostgreSQL remains authoritative; polling is the recovery path and metadata-only wake hints remain optional and advisory.
- Consider an application-owned authorized operator dashboard or HTTP control surface only after an adopter identifies a real workflow that the typed health and drain APIs cannot serve. Do not add a Durable-owned endpoint or second operational timeline by default.
- Consider independently configured per-surface worker hosts when production latency evidence shows that one sequential Work/Flow/Schedule pass cannot meet a service objective. Keep the common runtime kernel and PostgreSQL claims/fences rather than adding local parallel fan-out.

## Config-audit debug expansion follow-ups (#389)

- Consider targeted expansion of one already-known audit entry and a separately mapped or more strictly authorized debug endpoint only after operator evidence shows that the bounded report-wide expansion is insufficient. Preserve the known-entry boundary, current redaction pipeline, and explicit operator intent.

## Tenant-theme selection follow-ups (#705)

- Design a separate composition contract for tenant/context pair selection together with browser-local user mode preference only when an adopter needs both. It must define first-paint order, CSP behavior, static-export limits, failure semantics, and migration guidance; do not let two document-provider opt-ins silently compose.
- Revisit asynchronous selection only when a supported host context cannot be resolved before rendering without I/O. Keep tenant lookup and caching upstream from the Web rendering path by default.
- Consider an explicit compatible-provider/decorator marker only if enterprise adopters demonstrate that the strict custom-provider escape path blocks legitimate instrumentation or resilience wrappers.
