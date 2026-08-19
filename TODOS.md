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

## JavaScript class harvesting follow-ups (#301)

- Define derived-class and inherited-member documentation semantics only when a package author needs them. The follow-up must decide source identity, rendered hierarchy, stable anchors, search behavior, and diagnostics before locally declared derived-class members can be harvested; do not partially publish an inheritance model.

## Fine-grained harvest observatory follow-ups (#343)

- **What:** Consider development-only file/relative-path progress labels only after a real maintainer workflow proves aggregate parser phases and counts are insufficient. **Why:** Source identity materially expands the privacy boundary without improving the default #343 proof surface. **Pros:** Gives a future troubleshooting workflow more local context. **Cons:** Requires explicit opt-in, operator authorization, and a fresh redaction review. **Context:** #343 deliberately publishes only phase, source-unit counts, documents, and a rolling built-in rate; source identity must never become the default live-stream payload. **Depends on / blocked by:** Evidence from an adopter that aggregate progress is insufficient.
- **What:** Consider a public optional custom-harvester progress contract after an external `IDocHarvester` adopter supplies a concrete integration. **Why:** The internal package-owned session solves today’s need without locking an unproven callback API. **Pros:** Could eventually give ecosystem harvesters parity. **Cons:** Commits compatibility, publication bounds, redaction, and versioning rules for a public contract. **Context:** #343 keeps custom harvesters status-only. **Depends on / blocked by:** A real external adopter and separate public-API/privacy review.
- **What:** Consider a historical rate chart or event-history stream only if current-state observability proves inadequate for a documented operational workflow. **Why:** A maintainer may later need retrospective analysis rather than current-state proof. **Pros:** Enables trend investigation. **Cons:** Turns `_harvest` toward a log viewer and competes with its bounded state/replay design. **Context:** #343 uses a 250 ms coalesced snapshot and existing replay rather than per-file events. **Depends on / blocked by:** A documented operator workflow current state cannot support.
- **What:** Consider an application-owned diagnostics dashboard or alert only when an adopter identifies a durable operational owner and service objective. **Why:** The package should expose proof, not become a telemetry backend. **Pros:** Could support sustained production operations when there is an owner. **Cons:** Adds hosting, retention, alerting, and operational cost unrelated to #343. **Context:** The observatory remains source-backed and in-process. **Depends on / blocked by:** An adopter-defined owner, objective, and operational design.

## Coverage-proof follow-ups (#674)

- **What:** Add a synthetic multi-project or multi-target packaged-consumer graph to the semantic coverage proof. **Why:** The owned `Smoke.Tests` sentinel will catch exact-project collection and raw-to-merged loss, but not every graph-specific regression. **Pros:** Covers multiple application assemblies, fan-in shape, and framework-specific output paths. **Cons:** Extends the fixture and release runtime beyond the smallest #674 guard. **Context:** Land only after the canonical manifest-bound sentinel has run reliably; use a demonstrated regression or adopter graph to choose the shape. **Depends on / blocked by:** The #674 semantic proof and observed need. 
- **What:** Extract or formalize a shared runner/integration classifier only if evidence shows the CLI and diagnostic classification need the same contract. **Why:** A separate external classifier can drift from the VSTest-only CLI contract. **Pros:** One tested meaning for runner, direct coverlet package identity, and compatibility errors. **Cons:** Can expand coverage-engine scope or introduce a cross-project dependency without a real fault. **Context:** #674 keeps the external classifier diagnostic-only and does not change VSTest/MTP support. **Depends on / blocked by:** A current classified reproduction showing a CLI contract mismatch.
