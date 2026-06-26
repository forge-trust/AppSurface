# AppSurface CLI Authenticated Command Design

Issue `#425` defines the design contract for future AppSurface CLI authentication. It does not add auth commands yet.

The design starts from authenticated command execution, not from generic `login` plumbing. A future `appsurface auth login` flow only matters when it makes a protected CLI workflow safer and easier for package consumers.

## Design Premises

- `ForgeTrust.AppSurface.Auth` stays passive and surface-neutral. It describes users, sessions, prompts, audit events, and auth results; it does not authenticate users, store tokens, evaluate command permissions, or talk to identity providers.
- CLI auth must not depend on ASP.NET Core, browser sessions, cookies, RazorWire, web middleware, `ForgeTrust.AppSurface.Auth.AspNetCore`, or `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc`.
- OAuth Device Authorization Grant is required for headless and browser-constrained terminals, but it is one method in the CLI auth matrix, not the only method.
- Interactive desktop terminals should prefer browser or loopback PKCE when the environment supports it.
- CI and automation must never prompt unless a caller explicitly opts into an interactive flow.
- Token caching needs a CLI token-cache contract. The contract can mirror LocalSecrets' fail-closed diagnostics discipline, but LocalSecrets is not the token-cache abstraction.

## Protected Command Wedge

The v0 design uses a representative protected command to force concrete resource, scope, tenant, profile, and diagnostic decisions:

| Wedge | Why this wedge | Resource | Tenant/profile behavior | Required auth | Failure responses |
| --- | --- | --- | --- | --- | --- |
| `appsurface docs publish --archive ./dist/docs --site <site>` | Extends existing local `docs export` and `docs verify-archive` workflows into a remote action that package consumers can understand. | AppSurface docs-host publishing API or equivalent hosted archive endpoint. | Requires active profile and tenant/site binding. `--tenant`, `--profile`, and `--site` must be explicit when cache state is ambiguous. | Browser/loopback PKCE by default for interactive desktop use, device flow for headless terminals, non-interactive token only when explicitly configured. | `not_logged_in`, `tenant_required`, `profile_ambiguous`, `scope_missing`, `token_expired`, `cache_unavailable`, `ci_prompt_blocked`, `publish_resource_denied`. |

This wedge is a design forcing function, not a commitment to implement `docs publish` in `#425`.

## Auth Method Matrix

```text
AUTH METHOD SELECTION
interactive terminal + browser available -> browser/loopback PKCE
interactive terminal + browser unavailable -> device flow
headless/SSH/browser constrained       -> device flow
CI/non-interactive                     -> configured non-interactive token only
CI/non-interactive + missing token     -> fail closed, no prompt
```

`appsurface auth login` must choose an auth method explicitly or through safe environment detection. Device-flow polling must follow RFC 8628 semantics, including `authorization_pending`, `slow_down`, `access_denied`, `expired_token`, configured polling intervals, cancellation, and timeout backoff.

Refresh tokens for public clients require provider-supported rotation or sender constraints. When that is not available, the design should prefer short-lived sessions or explicit re-login over silent long-lived token storage.

## Package Boundary

The design must decide CLI-auth ownership before implementation begins:

- `ForgeTrust.AppSurface.Auth` may receive only genuinely surface-neutral vocabulary after the CLI contract proves reusable value.
- For v0, the CLI-auth boundary lives inside `ForgeTrust.AppSurface.Cli` under an internal namespace such as `ForgeTrust.AppSurface.Cli.Auth`. That boundary owns active CLI auth contracts, token cache contracts, command-gate contracts, diagnostic codes, and auth-method selection. Promote those contracts to a future package such as `ForgeTrust.AppSurface.Auth.Cli` only after the command-gate, token-cache, diagnostics, and auth-method selection contracts prove reusable outside the CLI package.
- `ForgeTrust.AppSurface.Cli` owns command wiring, help text, stdout/stderr behavior, exit codes, docs, and packed-tool smoke tests.
- Dependency guards must prove CLI auth does not pull ASP.NET Core auth, OIDC web handlers, RazorWire UI/runtime assets, provider SDKs, EF Core, Aspire, Keycloak, Auth0, Okta, Microsoft.Identity.Web, or persistence packages.

## Command Gate

Protected commands must pass through a neutral command-gate contract before projecting any outcome into `AppSurfaceAuthResult`.

```text
COMMAND GATE
load profile -> resolve tenant/site -> load cache entry -> validate expiry/skew
  -> refresh if allowed -> map scopes/resource -> execute command
  -> otherwise fail with deterministic diagnostic + exit code
```

The command gate must describe command requirements, resource binding, tenant/profile selection, required scopes, provider evidence or introspection strategy, deterministic exit codes, and safe metadata. Tenant metadata is context, not permission truth.

## Token Cache Threat Model

The token-cache contract must specify:

- Key shape: normalized issuer/authority, client id, audience/resource, scopes, subject/account, tenant, profile, token type, and schema version.
- Entry metadata: issued/expires timestamps, expiry skew, refresh-token rotation state, provider evidence, cache health, and redacted diagnostic metadata.
- Mutation safety: cross-process locking or documented single-writer assumptions, atomic writes, compare-and-swap refresh-token rotation, corruption handling, and migration/version behavior.
- Storage posture: OS credential store by default, in-memory test seam, explicit unavailable/locked/unsupported states, and no plaintext fallback for refresh tokens.

## Command Map

| Purpose | Canonical command | Alias posture | Next hint |
| --- | --- | --- | --- |
| Check auth/cache health | `appsurface auth status` | no top-level alias in v0 | `Next: appsurface auth login --profile default` |
| Start interactive auth | `appsurface auth login` | no top-level alias in v0 | `Next: appsurface auth whoami` |
| Print safe identity | `appsurface auth whoami` | avoid bare `whoami` until CLI has global auth affordances | `Next: appsurface docs publish ...` |
| Refresh session | `appsurface auth refresh` | none | `Next: appsurface auth status` |
| Remove local session | `appsurface auth logout` | none | show whether remote revocation happened |
| Switch profile | `appsurface auth profile switch` | avoid ambiguous `profile switch` root command | `Next: appsurface auth status` |
| Select tenant | `appsurface auth tenant select` | avoid ambiguous `tenant select` root command | `Next: appsurface auth status` |

`logout` must separate local cache deletion, refresh-token revocation, access-token expiry, provider session sign-out, and tenant/profile cleanup into separate outcomes.

## First Five Minutes

The implementation design must define an exact fresh-consumer path before any auth commands ship:

```bash
dotnet new tool-manifest
dotnet tool install ForgeTrust.AppSurface.Cli --prerelease
dotnet tool run appsurface --version
dotnet tool run appsurface auth status
dotnet tool run appsurface auth login --profile default --tenant demo
dotnet tool run appsurface auth whoami
dotnet tool run appsurface docs publish --archive ./dist/docs --site demo --dry-run
```

Expected first-run output contract:

| Step | Expected stream | Success marker | Failure marker |
| --- | --- | --- | --- |
| `dotnet tool run appsurface --version` | stdout | `appsurface <semver>` | standard .NET tool failure |
| `dotnet tool run appsurface auth status` | stdout for cache/profile summary; stderr only for diagnostics | `ASCLI100 auth_status_ready` with profile, tenant, issuer, cache health, expiry, and next action | `ASCLI101 not_logged_in` when no usable token exists |
| `dotnet tool run appsurface auth login --profile default --tenant demo` | stdout for user-action instructions; stderr only for diagnostics | `ASCLI110 login_complete` with profile, tenant, issuer, auth method, cache health, and next action | `ASCLI102 ci_prompt_blocked`, `ASCLI103 cache_unavailable`, provider denial, expiry, timeout, or cancellation diagnostic |
| `dotnet tool run appsurface auth whoami` | stdout | `ASCLI120 identity_ready` with display-safe subject, tenant, profile, scopes, expiry, and auth method | `ASCLI101 not_logged_in`, `ASCLI104 scope_missing`, or `ASCLI107 profile_ambiguous` |
| `dotnet tool run appsurface docs publish --archive ./dist/docs --site demo --dry-run` | stdout for dry-run plan; stderr only for diagnostics | `ASCLI130 command_authorized` followed by dry-run publish summary | `ASCLI101 not_logged_in`, `ASCLI104 scope_missing`, `ASCLI105 tenant_required`, `ASCLI106 publish_resource_denied`, `ASCLI107 profile_ambiguous`, or `ASCLI102 ci_prompt_blocked` |

Sample safe status output:

```text
ASCLI100 auth_status_ready
Profile: default
Tenant: demo
Issuer: https://auth.example.test
Subject: acct_1234
Scopes: docs.publish
Cache: secure-store:available
Expires: <future-utc-expiry>
Next: appsurface docs publish --archive ./dist/docs --site demo --dry-run
```

Sample unauthenticated output:

```text
ASCLI101 not_logged_in
Problem: This command requires AppSurface CLI authentication.
Cause: No usable token was found for profile 'default' and tenant 'demo'.
Fix: Run `appsurface auth login --profile default --tenant demo`, or configure non-interactive credentials for CI.
Docs: https://forge-trust.com/docs/appsurface-cli/auth#ascli101
Retryable: yes
```

Target time to hello world:

- under 5 minutes for a prepared sandbox or documented provider
- under 2 minutes for local unauthenticated failure/status discovery

If no public sandbox provider exists for v0, the first package-ready proof must say so plainly and focus on safe status output, unauthenticated failure, CI no-prompt failure, token-cache contract behavior, and redacted diagnostics instead of pretending real provider success is available.

## CI And Non-Interactive Auth

V0 CI design should be narrow:

- Never prompt when `CI=true`, stdin is non-interactive, or `--non-interactive` is set.
- Accept only explicitly configured non-interactive credentials for v0, such as an environment token or profile-scoped token file if the threat model permits it.
- Define precedence between command flags, environment variables, active profile, and cache.
- Defer provider-specific workload identity and service-principal helpers to follow-up issues unless the design can specify them without provider SDK dependencies.

Required GitHub Actions shape:

```yaml
- uses: actions/checkout@v5
  with:
    persist-credentials: false
- uses: actions/setup-dotnet@v5
  with:
    dotnet-version: 10.0.x
- run: dotnet tool restore
- run: dotnet tool run appsurface auth status --non-interactive
  env:
    APPSURFACE_CLI_TOKEN: ${{ secrets.APPSURFACE_CLI_TOKEN }}
- run: dotnet tool run appsurface docs publish --archive ./dist/docs --site demo --non-interactive
  env:
    APPSURFACE_CLI_TOKEN: ${{ secrets.APPSURFACE_CLI_TOKEN }}
```

Accepted credential precedence:

1. explicit command flags such as `--profile`, `--tenant`, and future `--token-file`
2. `APPSURFACE_CLI_TOKEN` for non-interactive CI only
3. active profile and tenant cache entry
4. interactive login flow when prompting is allowed

When credentials are missing in CI or `--non-interactive` mode, the command must fail with `ASCLI102 ci_prompt_blocked` on stderr and exit code `12`.

## Diagnostics

Auth diagnostics must use deterministic `ASCLI1xx` codes and follow the existing AppSurface CLI diagnostic style: problem, cause, fix, docs, retryability, safe metadata, and deterministic exit code.

| Code | Name | Stream | Exit code | Retryable | Safe metadata keys |
| --- | --- | --- | ---: | --- | --- |
| `ASCLI100` | `auth_status_ready` | stdout | 0 | no | `profile`, `tenant`, `issuer`, `scopes`, `cache_status`, `expires_at`, `next_action` |
| `ASCLI101` | `not_logged_in` | stderr | 10 | yes | `profile`, `tenant`, `resource`, `next_action` |
| `ASCLI102` | `ci_prompt_blocked` | stderr | 12 | yes | `profile`, `tenant`, `resource`, `non_interactive_reason` |
| `ASCLI103` | `cache_unavailable` | stderr | 13 | yes | `profile`, `tenant`, `store_kind`, `store_status`, `platform` |
| `ASCLI104` | `scope_missing` | stderr | 14 | no | `profile`, `tenant`, `resource`, `required_scopes` |
| `ASCLI105` | `tenant_required` | stderr | 15 | yes | `profile`, `candidate_tenants`, `resource` |
| `ASCLI106` | `publish_resource_denied` | stderr | 16 | no | `profile`, `tenant`, `site`, `resource` |
| `ASCLI107` | `profile_ambiguous` | stderr | 17 | yes | `candidate_profiles`, `tenant`, `resource`, `next_action` |
| `ASCLI110` | `login_complete` | stdout | 0 | no | `profile`, `tenant`, `issuer`, `auth_method`, `cache_status`, `next_action` |
| `ASCLI120` | `identity_ready` | stdout | 0 | no | `profile`, `tenant`, `issuer`, `subject_kind`, `scopes`, `expires_at` |
| `ASCLI130` | `command_authorized` | stdout | 0 | no | `profile`, `tenant`, `resource`, `scopes`, `dry_run` |

Auth status and success markers write to stdout. Problems, causes, fixes, docs links, and provider-denied outcomes write to stderr. Token values, refresh-token state, raw provider payloads, email, display name, and unredacted subject claims must never appear on either stream.

```text
ASCLI101 not_logged_in
Problem: This command requires AppSurface CLI authentication.
Cause: No usable token was found for profile 'default' and tenant 'demo'.
Fix: Run `appsurface auth login --profile default --tenant demo`, or configure non-interactive credentials for CI.
Docs: https://forge-trust.com/docs/appsurface-cli/auth#ascli101
Retryable: yes
```

```text
ASCLI102 ci_prompt_blocked
Problem: AppSurface CLI authentication cannot prompt in this environment.
Cause: CI or --non-interactive mode is active, but no non-interactive credential was configured.
Fix: Set the documented token input or run the command locally after `appsurface auth login`.
Docs: https://forge-trust.com/docs/appsurface-cli/auth#ascli102
Retryable: yes
```

```text
ASCLI103 cache_unavailable
Problem: The secure token cache is unavailable.
Cause: The OS credential store is locked, unsupported, or unreachable.
Fix: Unlock the credential store, choose a supported platform, or use an explicit non-interactive credential in CI. AppSurface will not fall back to plaintext refresh-token storage.
Docs: https://forge-trust.com/docs/appsurface-cli/auth#ascli103
Retryable: yes
```

```text
ASCLI107 profile_ambiguous
Problem: AppSurface CLI could not choose an active profile.
Cause: Multiple profiles can satisfy this command and no `--profile` value was supplied.
Fix: Re-run with `--profile <name>` or set an active profile with `appsurface auth profile switch`.
Docs: https://forge-trust.com/docs/appsurface-cli/auth#ascli107
Retryable: yes
```

Provider errors map into AppSurface diagnostics without raw provider payloads:

| Provider condition | AppSurface diagnostic |
| --- | --- |
| `authorization_pending` | no diagnostic until polling timeout; keep waiting |
| `slow_down` | no diagnostic; increase polling interval |
| `access_denied` | `ASCLI101 not_logged_in` with denial cause redacted |
| `expired_token` | `ASCLI101 not_logged_in` with re-login fix |
| token endpoint unavailable | `ASCLI103 cache_unavailable` only when cache/store is the cause; otherwise provider-unavailable follow-up diagnostic in the same `ASCLI1xx` range |

Redaction tests must scan stdout, stderr, logs, diagnostics, and exception paths for access tokens, refresh tokens, subject claim values, email, display name, and raw provider payload leaks.

## Required State Machines

### Auth Method Selection

```text
START
  -> CI=true or --non-interactive
       -> APPSURFACE_CLI_TOKEN present      -> NON_INTERACTIVE_TOKEN
       -> APPSURFACE_CLI_TOKEN missing      -> ASCLI102 ci_prompt_blocked
  -> --auth-method device
       -> DEVICE_FLOW
  -> interactive terminal + browser usable
       -> BROWSER_LOOPBACK_PKCE
  -> interactive terminal + no browser
       -> DEVICE_FLOW
  -> SSH/headless/browser constrained
       -> DEVICE_FLOW
  -> otherwise
       -> ASCLI102 ci_prompt_blocked
```

### Device-Flow Polling

```text
START_DEVICE_FLOW
  -> request device code
  -> display verification URI + user code on stdout
  -> poll after provider interval
       -> authorization_pending -> wait interval
       -> slow_down             -> increase interval, wait
       -> access_denied         -> ASCLI101 not_logged_in
       -> expired_token         -> ASCLI101 not_logged_in
       -> cancellation          -> ASCLI101 not_logged_in
       -> timeout               -> ASCLI101 not_logged_in
       -> malformed response    -> provider diagnostic in ASCLI1xx
       -> token response        -> CACHE_WRITE
```

### Token Refresh Lifecycle

```text
LOAD_CACHE
  -> cache missing                         -> ASCLI101 not_logged_in
  -> cache locked/unsupported/corrupt       -> ASCLI103 cache_unavailable
  -> access token valid after expiry skew   -> COMMAND_GATE
  -> refresh token missing                  -> ASCLI101 not_logged_in
  -> refresh allowed
       -> compare-and-swap refresh succeeds -> COMMAND_GATE
       -> refresh denied/expired            -> ASCLI101 not_logged_in
       -> concurrent rotation conflict      -> reload cache once, then retry or ASCLI103
```

### Profile And Tenant Selection

```text
START
  -> --profile supplied           -> use supplied profile
  -> one active profile           -> use active profile
  -> multiple active profiles     -> ASCLI107 profile_ambiguous
  -> --tenant supplied            -> use supplied tenant
  -> one tenant bound to profile  -> use bound tenant
  -> no tenant or ambiguous tenant -> ASCLI105 tenant_required
```

### Command Gate Outcomes

```text
LOAD_PROFILE
  -> RESOLVE_TENANT
  -> LOAD_CACHE
  -> VALIDATE_EXPIRY_AND_SKEW
  -> MAP_RESOURCE_AND_SCOPES
       -> missing scope          -> ASCLI104 scope_missing
       -> resource denied        -> ASCLI106 publish_resource_denied
       -> allowed                -> ASCLI130 command_authorized -> execute command
```

### Logout Outcomes

```text
SELECT_PROFILE_TENANT
  -> delete local cache entry
       -> local delete succeeds  -> report local session removed
       -> cache unavailable      -> ASCLI103 cache_unavailable
  -> attempt refresh-token revocation when configured
       -> revocation succeeds    -> report revoked
       -> revocation unsupported -> report local-only logout
       -> revocation fails       -> report local deletion separately from remote failure
  -> never imply provider browser session sign-out unless provider confirms it
```

### Cache Corruption And Migration

```text
READ_CACHE_ENTRY
  -> schema current + parse succeeds -> use entry
  -> old schema + migration succeeds -> atomic write new schema -> use entry
  -> old schema + migration fails    -> ASCLI103 cache_unavailable
  -> corrupt entry                   -> quarantine redacted copy, ASCLI103 cache_unavailable
  -> unsupported secure store        -> ASCLI103 cache_unavailable
```

### Cancellation After User-Code Display

```text
USER_CODE_DISPLAYED
  -> Ctrl+C before token granted     -> stop polling, ASCLI101 not_logged_in, no cache write
  -> process signal during cache write -> finish atomic write or roll back temp file
  -> cancellation after token cached -> ASCLI110 login_complete already emitted, preserve cache
```

## Test And Readiness Requirements

Follow-up implementation issues must require:

- CLI help contract tests for `auth`, `auth login`, `auth status`, `auth whoami`, `auth refresh`, `auth logout`, `auth tenant select`, `auth profile switch`, and the protected command wedge.
- Auth-method selection tests for interactive desktop, browser-unavailable terminal, SSH/headless terminal, explicit device-flow override, CI/non-interactive mode, and prompt-forbidden environments.
- Device-flow tests for success, pending, slow-down, denial, expiry, timeout backoff, cancellation, malformed provider responses, and provider interval changes.
- Cache tests for key-shape isolation, wrong-account reuse prevention, secure-store available/unavailable, no plaintext fallback, redacted diagnostics, corruption, migration, locked-store behavior, unsupported-store behavior, profile switching, tenant isolation, and test-only in-memory cache.
- Concurrency tests for simultaneous `login`, `logout`, `refresh`, `status`, and protected command execution over the same profile.
- Output leak scans across stdout, stderr, logs, diagnostics, and exception paths.
- Dependency guard tests for package references and loaded assemblies.
- Packed-tool smoke tests from a clean consumer fixture: `auth --help`, status without login, CI no-prompt failure, protected command unauthenticated failure, safe `whoami`, and package version output.
- Package-readiness verification: package index updates, CLI README/help contract tests, release-note entry, dependency payload inventory, `dotnet pack`, and `verify-packages --package-version 0.0.0-ci.local` or its auth-specific successor.

## Follow-Up Issue Slices

1. Auth-method matrix and protected command wedge.
2. CLI token-cache contract and threat model.
3. Command-gate contract and tenant/resource binding.
4. CI/non-interactive auth posture.
5. Logout, revocation, status, and diagnostics.
6. Docs, package readiness, dependency guards, and packed-tool consumer proof.
