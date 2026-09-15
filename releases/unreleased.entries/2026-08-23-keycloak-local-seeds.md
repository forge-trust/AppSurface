<!-- appsurface:unreleased-entry section="included" -->
### Ordered local Keycloak seeds

- [`ForgeTrust.AppSurface.Auth.Aspire.Keycloak`](../../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#ordered-local-seed-projects)
  now exposes an AppHost-only, strictly ordered `RealmReady()` and `WithLocalSeed(...)` lifecycle for finite
  consumer-owned local projects. It keeps Keycloak Admin API clients, credentials, retries, mutation policy, and
  idempotent application state in the consumer worker while AppSurface supplies only the baseline proof, dependency
  graph, typed secret-reference validation, and redacted diagnostics.
- Start with the [two-worker Keycloak AppHost sample](../../examples/auth-aspire-keycloak-apphost/README.md#ordered-local-seed-proof).
  It demonstrates a persistent, rerun-safe broker/identity/fixture convergence chain and distinguishes startup-time
  local seeding from request-time [DevAuth personas](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md).
