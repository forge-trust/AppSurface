# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.5`. It stays provisional until the next tag is cut.

## What is taking shape

- [`appsurface canary poll`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-canary-poll) now turns one existing protected named-canary evaluation into a read-only, caller-owned deployment proof. It validates an application base URL and environment-only marker/credential sources before dispatch, preserves path bases, disables redirects and hidden client timeouts, parses the preview compatibility core by property name, and gives `pending` plus recoverable transport failures an explicit bounded polling lifecycle. Safe text and JSON outcomes expose only the canary name, attempts, elapsed time, diagnostic, bounded reason/summary, next action, and documentation URL; raw credentials, markers, headers, URLs, and response bodies never render. `pass` alone exits `0`; semantic canary failures, protocol failures, transient exhaustion, deadlines, and cancellation use stable nonzero exits. The tool remains a caller rail, not a canary trigger, readiness probe, deployment controller, identity broker, or composite Action.
- `ForgeTrust.AppSurface.Theming` and the explicit `ForgeTrust.AppSurface.Web` Razor adapter now provide immutable semantic theme pairs with native System/Light/Dark browser output. The Web opt-in can retain a presentation-only Light/Dark choice in browser-local storage while preserving one canonical URL and HTML tree: it has deterministic first paint, nonce/hash CSP support, session-only fallback when storage is blocked, no cookie/account/cache/SEO behavior, and a headless native-radio contract. Docs consumer proof and versioned published trees share that origin-scoped behavior; protected Markdown downloads remain raw private `text/markdown` attachments. Docs still preserves its default `AppSurfaceDark` path, public density, chrome, Graphite compatibility, short-hex overrides, static published-tree output, and internal token ownership. RazorWire styling remains limited to generated form-error nodes and uses the nonce-bearing critical stylesheet under strict CSP. Package discovery, quickstart, diagnostics, Docs migration, test commands, and deliberate non-goals ship with the feature; tenant selection, shared Graphite, remote packs, and adoption telemetry remain separate policy work.
- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- The [release tool](../tools/ForgeTrust.AppSurface.Release/README.md) now treats checked-in versioned sidecars as explicitly `prepared`, then derives a transient `tagged` projection only after an annotated tag binds the prepared sidecar, manifest, and evidence digests. The new `tag-message` and `inspect` commands validate the tag object, tagger, base-branch reachability, V1/V2 evidence, package surface, and preparation commit before publishing. Docs publication uses the validated projection only in a disposable checkout and retains its inspect proof; prepared source files never acquire tag or GitHub Release claims.
- AppSurface Docs adds an opt-in protected Markdown browser download. Hosts set `AppSurfaceDocs:MarkdownDownload:Enabled=true`, provide a host-owned named ASP.NET Core reader policy, and may bound the aggregate exact-source snapshot with `MaxSnapshotBytes` (default `8,388,608`, range `1..33,554,432`). Pages require exact inline `download_markdown: true`; sidecars, aliases, generated pages, noncanonical routes, and archives do not grant access. Successful canonical `GET` responses are private, no-store `text/markdown` attachments containing the original valid UTF-8 bytes; `HEAD` returns the matching attachment metadata without a body. The feature is disabled by default and is browser-only v1; it adds no API, batch, vendor, or automatic synchronization integration. See the [five-minute setup](../Web/ForgeTrust.AppSurface.Docs/use-appsurface-docs.md#five-minute-protected-markdown-download) and [package reference](../Web/ForgeTrust.AppSurface.Docs/README.md#protected-markdown-download).
- [`appsurface coverage gate`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate) now applies a configurable `--tolerance` grace margin to overall and patch thresholds. The default `0.5` percentage point tolerance reduces rounding-related flakiness, `0` preserves strict enforcement, effective thresholds never fall below `0`, invalid values fail before evaluation, and console plus Markdown and JSON reports show the effective thresholds they enforce while retaining configured thresholds for automation.
- [`appsurface coverage gate`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate) now supports `--patch-line-mode measurable|codecov`. The backwards-compatible `measurable` default counts a mapped changed line when it has one or more hits; `codecov` also requires complete condition coverage when branch data is present, while continuing to exclude unmapped lines. The repository coverage wrapper uses `codecov` mode for its patch gate so local checks reproduce Codecov's partial-condition treatment before CI.
- `ForgeTrust.AppSurface.Web.OpenApi` now uses `Microsoft.AspNetCore.OpenApi` 10.0.9 and directly requires `Microsoft.OpenApi` in the range `[2.7.5, 3.0.0)`, keeping .NET 10 consumers on the supported 2.x line above the range affected by [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc) while preserving existing OpenAPI and Scalar APIs and endpoint behavior.
- [`appsurface coverage run`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) can now start long-running non-exclusive test projects earlier with `--schedule longest-first`. It reuses prior `timings.json` data when available, preserves integration and Playwright projects as exclusive barriers, supports explicit priority projects, fails invalid explicit timing or priority input before tests run, warns and preserves input order for unmeasured projects when inferred prior timings are missing or unusable, and keeps artifact names stable.
- [`appsurface coverage run`](../Cli/ForgeTrust.AppSurface.Cli/README.md#exclude-discovered-test-projects) now supports repeatable `--exclude-test-project` segment globs for solution-discovered tests. Exclusions are normalized and case-insensitive, reject stale or malformed patterns before side effects, remain visible in list and dry-run output, preserve solution compilation, and are proven through the packaged CLI consumer with an excluded failing sentinel project.
- The [ASP.NET Core DevAuth example](../examples/auth-aspnetcore-dev-auth/README.md#what-the-verifier-proves) now has deterministic, staged startup proof: synchronous build failures stop immediately, child exits and Kestrel readiness are observed separately, a child-owned listening record gates the real-loopback HTTP workflow, and cleanup targets only the recorded child. A child-scoped standard .NET host setting avoids configuration-reload stalls in restricted file-watcher environments without changing normal example or consumer behavior. Focused in-process host coverage complements rather than replaces the real-socket verifier, and failures preserve only bounded, sanitized, allowlisted evidence. This is a contributor-experience correction; it adds no package API, package or production-host runtime behavior, package version, or release implication.
- The coordinated package graph now addresses
  [GHSA-pgww-w46g-26qg](https://github.com/advisories/GHSA-pgww-w46g-26qg) by pinning AppSurface Docs to exact
  `AngleSharp` `[1.5.2]`, `HtmlSanitizer` `[9.1.949-beta]`, and `AngleSharp.Css` `[1.0.0-beta.216]`
  dependencies. This is a dependency-only upgrade: AppSurface Docs public APIs, registration, configuration, and consumer
  usage are unchanged. The beta sanitizer/CSS pair is intentional only for preview releases; stable package verification
  rejects either prerelease dependency until
  [issue #682](https://github.com/forge-trust/AppSurface/issues/682) selects compatible stable versions. The
  [Docs security boundary](../Web/ForgeTrust.AppSurface.Docs/README.md#dependency-security-boundary) remains narrow:
  sanitization covers rendered package-documentation fragments, not general UGC or host CSP. The RazorWire CLI also
  carries the coordinated parser upgrade in its proof-only bundled tool graph, but remains excluded with
  `publish_decision: do_not_publish`; see its
  [installation and publication boundary](../Web/ForgeTrust.RazorWire.Cli/README.md#installation). The sanitizer regression
  proof passed all four Chromium variants: `text/html` and `application/xhtml+xml`, each exercised through `title` and
  `style` RCDATA handling.
- [`ForgeTrust.AppSurface.Web` named canary evaluation](../Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints) is now available in preview: applications register typed, application-owned proof
  evaluators and explicitly map one fixed protected route family. Completed evaluations add required `name`, `ready`, and
  `status` fields plus optional typed evidence, a marker fingerprint, and up to 16 registration-declared bounded details.
  Existing `AppSurfaceCanaryResult(status)` construction remains source-compatible. Consumers must tolerate optional
  omissions, unknown fields, and property reordering; the contract remains preview until the
  [#625 caller](https://github.com/forge-trust/AppSurface/issues/625) proves polling and operator actions.
  The canonical guide includes a complete forwarding evaluator, a contrasting migration fixture, copyable
  `System.Text.Json` and `jq` consumers, the #623-to-#624 upgrade contract, and separate under-5-minute authenticated-host
  and under-15-minute cold-path onboarding targets.
  The package emits fixed completion event `62401` with typed evaluation and host facts only; marker, reason, summary,
  correlation, and custom detail values remain response-only. Bounds and declarations constrain shape but do not classify
  or redact application-authored text. The default adapter still returns `200` only for `pass` and `503` for completed
  non-pass states; authenticated diagnostic consumers can opt into status-preserving `AlwaysOk`. Authorization remains
  host-owned and fail-closed, and triggering, retries, polling, aggregation, health-check adaptation, and `/ready` behavior
  remain outside this primitive.
- [`ForgeTrust.AppSurface.Web` health and readiness probes](../Web/ForgeTrust.AppSurface.Web/README.md#health-and-readiness-probes) are now opt-in. New hosts avoid ASP.NET Core health-check registration and `/health` plus `/ready` endpoint mapping unless `WebOptions.Health.Enabled` is explicitly set to `true`; enabled probes also avoid general route-handler binding during startup. Hosts whose deployment or monitoring infrastructure consumes those probes must enable the shared flag; paths, readiness tags, response semantics, validation, and authorization behavior are unchanged.
- [`ForgeTrust.RazorWire`](../Web/ForgeTrust.RazorWire/README.md#choose-who-supplies-turbo) upgrades its package-owned Turbo UMD payload from 8.0.12 to 8.0.23 while preserving the existing `Bundled`, same-origin `CustomPath`, and `HostManaged` runtime-source contract. Static CDN and hybrid exports continue to materialize the exact bundled runtime.
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
- [`appsurface secrets transfer`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-secrets-transfer) now supports
  version-2, one-way Google Secret Manager-to-LocalSecrets materialization for an IAM-authorized developer's local
  integration testing. Each source is a full numeric version resource, plans and receipts remain value-free, existing
  local values require guarded `--replace` plus exact confirmation, and prepared local writes can resume only after
  an in-memory equality check. The [local-testing guide](../Config/ForgeTrust.AppSurface.Config.LocalSecrets/docs/materialize-remote-secrets-for-local-testing.md)
  explains prerequisites, recovery, and the explicit runtime posture required for a `Production`-named local namespace.
- [`ForgeTrust.AppSurface.Web` named canaries](../Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints)
  now include a bounded protected aggregate snapshot at `GET /_appsurface/canaries`. Operators can select registered
  canaries by exact name or durable tag, receive ordinal partial outcomes under explicit concurrency and deadline caps,
  and parse a privacy-safe envelope with fixed telemetry. The feature does not add triggers, retries, polling, readiness
  effects, or authorization-policy ownership; hosts retain those decisions.
- [`ForgeTrust.AppSurface.Config.LocalSecrets`](../Config/ForgeTrust.AppSurface.Config.LocalSecrets/README.md) now
  uses an entitlement-free macOS `SecItem` v2 Keychain namespace for cross-process LocalSecrets parity. Readable
  retained v1 records surface a terminal migration diagnostic instead of being silently consumed; operators can use
  [`appsurface secrets migrate`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-secrets) to copy them safely
  without exposing values. The [migration guide](../Config/ForgeTrust.AppSurface.Config.LocalSecrets/docs/macos-keychain-v2-migration.md)
  covers namespace matching, resumable recovery, canonical-v2 precedence, and the interactive three-key smoke.
- The PostgreSQL durable schema adds the Schedule ledger, payload-free dispatch leases, forced-RLS history partitions, and a reviewed role recipe. Operators can use the [migration and role setup guidance](../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#explicit-schema-and-epoch-deployment) before enabling the manual processor.
- The PostgreSQL durable source preview now provides explicit worker-host composition. Passive registration resolves the
  bounded runtime pump, typed health and drain control, and authorized Work and scope control clients without starting a
  worker or applying DDL; `AddWorkerHost()` is the separate opt-in for one polling host with metadata-only PostgreSQL
  wake hints. The host records payload-free worker liveness and drain state through `0005_runtime_heartbeat.sql`, fails
  closed on incompatible schema or epoch state, and leaves application authorization, external activation, dashboards,
  and trace instrumentation outside the package boundary. Follow the [worker-host quickstart](../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#run-a-worker-host) before enabling it.
- [`ForgeTrust.AppSurface.Durable`](../Durable/README.md) now preserves bounded W3C causal evidence for PostgreSQL Flows without treating waits or process gaps as live spans. Command acceptance and every committed event, timer, Activity Work, or evaluation transition retain only the validated `traceparent`, optional bounded `tracestate`, a runtime-generated correlation token, and a fixed cause kind. The next real execution becomes a short-lived Activity linked to that committed cause; baggage, Flow payloads, scopes, caller identities, and raw trace headers are neither persisted nor emitted. Start with the [Durable Flow trace-context guide](../Durable/flow-trace-context-v1.md) for source registration, tags, diagnostics, migration order, and the under-five-minute local proof. The current TestHost proof verifies crash/restart causality under the explicitly opted-in hosted-runtime boundary.
- Coordinated package documentation now follows the release that was current when that documentation tree was published: current docs use the stable [`releases/current.md`](./current.md) pointer, while historical trees retain their original versioned release notes. The [release tool](../tools/ForgeTrust.AppSurface.Release/README.md) records and validates that contract through [versioned manifest and evidence V2 artifacts](./README.md#release-evidence-bundle), preserving V1 evidence compatibility and rejecting incomplete, conflicting, or unknown package release-link declarations.
- Add release-facing changes here.

## Migration watch

- Apply `0004_schedule_protocol.sql` with the migration-owner workflow before constructing Schedule clients or processors. Runtime credentials must remain distinct non-owner, non-`BYPASSRLS` dispatcher and scoped-runtime roles; use [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) rather than granting table access directly.
- Apply `0005_runtime_heartbeat.sql` and rerun the role recipe before enabling `AddWorkerHost()`. Initialize or rotate the active runtime epoch through the migration-owner workflow first; application startup intentionally performs no DDL.
- Apply `0006_flow_trace_context.sql` only after `0005_runtime_heartbeat.sql`, then rerun [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql). The scoped runtime receives the reviewed trace-context relation grants under forced RLS; the payload-free dispatcher receives no access.
- Schedule history partitions cover the current and following UTC months. Before the boundary is crossed, an operator must run `appsurface_durable.ensure_schedule_history_partitions()` as the migration owner; a missing partition fails writes visibly rather than routing data elsewhere.
- The repository-only `scripts/coverage-solution.sh` wrapper now has one no-argument run-and-gate path over the public [`appsurface coverage` commands](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run). Its former group, filter, build, output, and merge compatibility inputs now fail before work starts with an exit-2 command-specific migration message; select projects, own assembly filters, or merge shards through the package-consumer CLI instead. The legacy `ForgeTrust.AppSurface.CoverageRunner` implementation and test project have been removed.
- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.
