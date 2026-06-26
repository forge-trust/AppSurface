# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.4`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.AppSurface.Auth` now defines durable external-subject to app-user-id mapping contracts without taking on user-store, ASP.NET Core, OIDC, EF Core, Aspire, or tenant-authority responsibilities.
- `ForgeTrust.AppSurface.Auth.AspNetCore` now includes AppSurface-shaped Minimal API policy helpers: `AddAppSurfacePolicy(...)` keeps policy definition in ASP.NET Core, while `RequireSurfacePolicy(...)` evaluates the named host policy through the existing AppSurface evaluator and returns API-safe ProblemDetails JSON for challenge, forbid, missing-policy, missing-service, and missing-subject outcomes instead of triggering browser redirects.
- `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` adds a Development-only persona lab with a named fake auth scheme, protected persona cookie, loopback-only and same-origin mutation controls, status JSON, embeddable state marker overlay, and startup guards so package consumers can prove AppSurface auth flows without shipping fake identity.
- Add `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc`, a public preview ASP.NET Core cookie + OIDC convenience package with explicit AppSurface scheme names, `SaveTokens=false` by default, passive prompt helpers, safe diagnostics, event chaining, and package chooser/readiness coverage.
- Sanitized AppSurface Config audit diffs for comparing captured runtime configuration reports.
- AppSurface Observability package defaults for sending app-side logs, traces, and metrics to Aspire or another OTLP collector.
- LocalSecrets platform-index self-healing so `appsurface secrets list` no longer surfaces stale names whose stored
  values are missing.
- The AppSurface CLI now carries the design contract for future authenticated command execution. The design keeps `ForgeTrust.AppSurface.Auth` passive, uses `appsurface docs publish --archive ./dist/docs --site <site>` as the protected command wedge, treats RFC 8628 device flow as one auth method rather than the whole CLI story, and requires secure token-cache boundaries, CI no-prompt behavior, `ASCLI1xx` diagnostics, and packed-tool readiness proof before auth commands ship.
- LocalSecrets Linux `secret-tool` resolution now uses trusted system candidates or an explicit absolute override instead
  of executing the first `secret-tool` discovered on `PATH`.
- `ForgeTrust.AppSurface.Web` now rejects the literal CORS origin wildcard `*` outside Development when AppSurface owns
  the CORS policy, so permissive production APIs must either name explicit browser origins or register host-owned
  ASP.NET Core CORS.

## Included in the next coordinated version

### Release and docs surface

- `examples/auth-web-razorwire-proof` now gives package adopters a five-minute browser proof for `ForgeTrust.AppSurface.Auth.AspNetCore`: one host-owned `OperatorsOnly` policy drives both a Minimal API response and a RazorWire-facing rendered state while all fake auth and persona switching stays sample-local.
- Added `ExternalSubject`, `AppUserId`, `IAppSurfaceUserIdentityResolver`, `AppSurfaceUserIdentityResolutionContext`, `AppSurfaceUserIdentityResult`, and `AppSurfaceUserIdentityStatus`, with README guidance for uniqueness, idempotency, cancellation, concurrency, PII-safe diagnostics, and ASP.NET Core adapter integration planning.
- `ForgeTrust.AppSurface.Auth.AspNetCore` documents the new Minimal API policy helper flow, package chooser metadata, safe ProblemDetails failure shape, and when native ASP.NET Core `RequireAuthorization(...)` remains the better choice.
- `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` documents the five-minute local persona flow, persistent in-app marker overlay, marker skinning options, same-origin POST protection, DevAuth versus real host auth/OIDC/test harness boundaries, stable `ASDEV###` diagnostics, and removal guidance before deployment.
- Document the AppSurface OIDC package boundary, including when to use raw ASP.NET Core OIDC or provider SDKs instead, and add a no-secret local registration proof example.
- AppSurface Config now exposes a sanitized config audit diff surface. `ConfigAuditReportDiffer` compares two existing `ConfigAuditReport` snapshots without re-resolving providers, `ConfigAuditDiffTextRenderer` renders deterministic same-host or captured-snapshot evidence with redaction uncertainty called out, and `ConfigAuditDiffCommandRunner` gives apps command-framework-agnostic same-host and captured JSON workflows with display-safe problem/cause/fix/docs-link failures.
- `ForgeTrust.AppSurface.Config.LocalSecrets` hardens the explicit file fallback path. Unix fallback directories are created with `0700` mode bits when missing, existing loose parent directories fail closed instead of being modified in place, and JSON files are written or repaired with `0600` mode bits during `set`, `delete`, and `doctor`; reads reject symbolic-link paths and non-canonical mode bits before returning a secret value. `appsurface secrets doctor --store-file` now treats `ready`, `repaired`, and `degraded` posture diagnostics as doctor-style success while keeping `unsupported` path shapes terminal. This is Unix mode-bit hardening, not Windows ACL hardening or a universal POSIX ACL proof; OS-backed LocalSecrets stores remain the recommended local-development path.
- AppSurface Observability adds `ForgeTrust.AppSurface.Observability` with module-first OpenTelemetry logging, tracing, and metrics registration, endpoint-driven OTLP exporter setup, service identity resource metadata, and docs for Aspire and non-Aspire adoption paths.
- AppSurface Docs adds a trusted maintainer harvest rebuild loop: `_health` can render a `Rebuild docs` action, `POST {DocsRootPath}/_harvest/rebuild` starts or queues a full source-backed harvest through `AppSurfaceDocsHarvestCoordinator.RequestRebuildAsync(...)`, and `{DocsRootPath}/_harvest` shows the live observatory before returning to the validated docs/search context.
- RazorWire hybrid islands now reject inline `data:` module specifiers from both `client-module`/`data-rw-module` and `window.RazorWireIslandModules`, and also reject protocol-relative `//...` module URLs. Move any prototype inline module such as `data:text/javascript,...` into a served module like `/js/my-island.js` that exports `mount(root, props)`.
- RazorWire export now owns HTTP redirect handling for artifact-producing fetches, including crawled routes and conventional `404.html` staging. Same-origin redirects remain supported, while redirects outside the configured export origin and base path fail with `RWEXPORT008` before response content is read or written; routes that intentionally point to a different host or app path should be modeled as external references instead of exporter-managed artifacts.
- RazorWire stream authorization can now return `AppSurfaceAuthResult` through `IRazorWireStreamAuthorizer`, preserving legacy bool authorizers while mapping challenge, forbid, setup failure, unsafe navigation, and stale session outcomes before SSE starts.
- AppSurface CLI export and docs export now configure the shared RazorWire `ExportEngine` HTTP client with automatic redirects disabled, so `RWEXPORT008` redirect-boundary checks run before artifact response bodies are read or written.
- RazorWire export now guards generated artifact materialization and AppSurface Docs release archive traversal with `RWEXPORT009`. HTML, CSS, binary assets, `404.html`, docs partials, redirect alias HTML, `_redirects`, frozen route manifests, and release manifests are validated before parent creation, final writes, archive enumeration, metadata reads, or hashing, so symlinks, junctions, reparse points, and lexical output-root escapes are rejected without following them.
- RazorWire form interactions now give server-rendered apps stable local mechanics for conditional form targets and one-dimensional ASP.NET Core model-bound collections. Use raw `data-rw-*` attributes or the matching TagHelpers to reveal and disable app-owned fields, add rows from an app-authored `__index__` template, duplicate editable values without copying identity/delete/concurrency fields, and choose between physical remove or explicit mark-for-removal.
- AppSurface LocalSecrets platform-backed stores now validate indexed names against live stored values during
  `appsurface secrets list`. Missing values are pruned from the index when validation and repair succeed, and
  `appsurface secrets delete KEY` repairs a stale indexed name when the value is already gone while preserving
  `local-secret-missing` for keys that never existed.
- `Cli/ForgeTrust.AppSurface.Cli/docs/authenticated-command-design.md` documents the future CLI auth ladder, including browser/loopback PKCE, RFC 8628 device flow, non-interactive CI tokens, command-gate state, token-cache threat modeling, deterministic auth diagnostics, and follow-up issue slices.
- AppSurface LocalSecrets now hardens Linux Secret Service command selection. Linux uses `/usr/bin/secret-tool`, then
  `/bin/secret-tool`, or an explicit trusted absolute path through `AppSurfaceLocalSecretsOptions.LinuxSecretToolPath`
  and `appsurface secrets --secret-tool-path`. PATH matches are reported only as ignored diagnostic context, invalid
  overrides fail before command launch, and `--secret-tool-path` cannot be combined with `--store-file`.
- AppSurface Web CORS startup validation now fails closed before policy registration when non-development
  `CorsOptions.AllowedOrigins` includes the exact literal `*`, while preserving Development all-origin convenience and
  wildcard subdomain origins such as `https://*.example.com`.

### AppSurface Flow

- Reduce internal `InMemoryFlowRunner<TContext>` routing overhead by using prevalidated `FlowDefinition<TContext>` execution metadata while keeping public Flow APIs unchanged.
- Reduce synchronous in-memory runner allocations by making `FlowExecutionContext<TContext>` an immutable value-type snapshot passed into each node execution.
- Expand the Flow benchmark suite with runner-shape, generated-authoring, and outcome-allocation lanes so future performance work can attribute remaining overhead before changing public APIs.

## Migration watch

- `AppSurfaceUser.Id` remains a host-owned subject identifier in the existing ASP.NET Core adapter. Consumers that need durable app-owned users should resolve that subject through the new identity resolver contract instead of treating the mapped subject claim as an app user id.
- Production AppSurface-managed CORS no longer accepts `AllowedOrigins = ["*"]`. Replace the literal wildcard with
  explicit origins such as `["https://app.example.com"]`; keep local permissive behavior behind
  `EnableAllOriginsInDevelopment`; use wildcard subdomains such as `["https://*.example.com"]` only when matching
  subdomains; and register/apply host-owned ASP.NET Core CORS when an API is intentionally public to every browser
  origin.
- Hosts adopting `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` must still call `UseAuthentication()` and `UseAuthorization()` in ASP.NET Core order and must explicitly configure default schemes if they want host-wide defaults.
- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
- `FlowExecutionContext<TContext>` is now a readonly record struct instead of a sealed record class. Most node implementations continue to read `FlowId`, `Version`, `NodeId`, `State`, and `ResumeEvent` the same way, but code that depended on reference identity, nullable context parameters, or `context is null` checks should switch to checking the populated members it requires.
- RazorWire no longer treats `data:text/javascript,...` values in `window.RazorWireIslandModules` as importable modules. Use a relative, root-relative, same-origin, explicit HTTPS, or bare import-map module specifier instead.
