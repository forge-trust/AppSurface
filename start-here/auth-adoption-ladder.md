# AppSurface Auth Adoption Ladder

Use this guide when you already have, or are about to add, authentication to an ASP.NET Core app and need to decide where AppSurface fits.

AppSurface Auth does not replace ASP.NET Core authentication, ASP.NET Core authorization, ASP.NET Identity, Auth0, Entra, Keycloak, provider SDKs, production middleware, policy definitions, user stores, or app-user provisioning. Host apps own identity providers, schemes/middleware, policies/enforcement, user stores, and production behavior. AppSurface gives modules a shared auth vocabulary, maps host-owned policy results into AppSurface-shaped results, and provides local or test-only proof tools.

## Start Here In Five Minutes

If you are choosing a package, do this first:

1. Pick the row that matches your situation.
2. Install only the package in that row, if the row has an install command.
3. Run the linked proof or example.
4. Read the "does not own" column before copying code into production.

Target timings:

| Task | Target |
| --- | ---: |
| Choose the correct auth package | 2 minutes |
| Run the local DevAuth proof | 5 minutes |
| Run the local real-Keycloak proof | 5 minutes after container runtime is ready |
| Wire an existing ASP.NET Core policy to `RequireSurfacePolicy(...)` | 7 minutes |
| Add deterministic Auth.Testing coverage | 10 minutes |

## Package Ladder

| Situation | Package | Install | Owns | Does not own | Proof | Production exit criteria | Escape hatch / host-owned override |
| --- | --- | --- | --- | --- | --- | --- | --- |
| I need normal production authentication and no AppSurface auth contract yet. | Raw ASP.NET Core auth | No AppSurface Auth package | Authentication handlers, middleware, policies, challenge/forbid behavior, redirects, provider setup, user stores. | AppSurface result mapping, AppSurface auth vocabulary, DevAuth, Auth.Testing. | Follow ASP.NET Core and provider docs. | Keep using raw ASP.NET Core when AppSurface modules do not need shared auth results. | Host owns everything. |
| I author reusable AppSurface modules and need passive auth vocabulary. | `ForgeTrust.AppSurface.Auth` | `dotnet package add ForgeTrust.AppSurface.Auth` | Surface-neutral users, sessions, auth outcomes, prompts, audit event descriptions, and external-subject to app-user-id contracts. | Authentication schemes or handlers, cookies, JWT/OIDC, middleware, endpoint filters, runtime request accessors, user stores, provisioning, database schema, challenges, forbids, redirects, audit sinks, UI. | Read the [Auth core README](../Auth/ForgeTrust.AppSurface.Auth/README.md). | Implement durable `ExternalSubject` to `AppUserId` resolution in the app when domain records need app-owned identity. | Keep persistence and provisioning policy in the host app. |
| I have an ASP.NET Core app with existing auth policies and need AppSurface-shaped results. | `ForgeTrust.AppSurface.Auth.AspNetCore` | `dotnet package add ForgeTrust.AppSurface.Auth.AspNetCore` | Request auth context mapping, named host-policy evaluation, `AppSurfaceAuthResult`, `RequireSurfacePolicy(...)`, and ProblemDetails auth failures for API endpoints. | Authentication schemes or handlers, cookies, JWT/OIDC, ASP.NET Identity, policy definitions, middleware insertion, authorization middleware replacement, browser challenge/forbid execution, redirects, audit sinks, RazorWire UI, MVC/controller helpers. | Run the [Auth Web/RazorWire proof](../examples/auth-web-razorwire-proof/README.md) or the lower-level [ASP.NET Core bridge example](../examples/auth-aspnetcore-bridge/README.md). | Keep host auth middleware and policies in normal ASP.NET Core. Add AppSurface only where modules or APIs need AppSurface-shaped outcomes. | Use raw `IAppSurfaceAspNetCorePolicyEvaluator` when an endpoint needs a custom resource or response. |
| I need fake local personas to manually prove AppSurface policy behavior. | `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` | `dotnet package add ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` | Development-by-default fake persona scheme, explicit local/proof environment opt-in, local control page, selected-persona cookie, a marker that stays fixed on desktop and flows with narrow layouts, safe local status JSON, and ASDEV diagnostics. | Production authentication, OIDC, JWT validation, ASP.NET Identity, password handling, durable app-user mapping, user stores, tenant authority, audit sinks, deployment auth, automatic host HTML injection, RazorWire auth UI components, or Auth.Testing integration-test assertions. | Run the [DevAuth example](../examples/auth-aspnetcore-dev-auth/README.md). | Remove DevAuth package references, `AddAppSurfaceDevAuth(...)`, `MapAppSurfaceDevAuth()`, and DevAuth scheme references before production. | Override scheme name, path prefix, cookie name, marker CSS, allowed environment names, and default-scheme behavior only for local proof hosts. |
| I already chose a real OIDC provider and want AppSurface-named scheme conventions. | `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` | `dotnet package add ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` | Named cookie/OIDC scheme registration, conservative token defaults, passive local prompt helpers, OIDC subject mapping handoff, event chaining, and ASOIDC diagnostics. | User stores, ASP.NET Identity replacement, identity-provider hosting, OAuth/OIDC server behavior, provider SDK ownership, EF Core, persistence, middleware insertion, active redirects, challenge execution, sign-in/sign-out execution, silent default-scheme takeover, token storage by default, Aspire Keycloak, DevAuth, app-user provisioning. | Run the [OIDC example](../examples/auth-aspnetcore-oidc/README.md). | Keep provider registration, secrets, token policy, user mapping, and production sign-in/out behavior host-owned. | Configure scheme names, `SaveTokens`, client-secret requirements, return-url policy, and OpenID Connect events. |
| I need a real local OIDC provider proof without signing up for SaaS or teaching Keycloak administration. | `ForgeTrust.AppSurface.Auth.Aspire.Keycloak` | `dotnet package add ForgeTrust.AppSurface.Auth.Aspire.Keycloak` in an AppHost project | AppHost-only Keycloak resource setup, deterministic local realm/client/users, secret-safe OIDC projection, readiness probes, fixed-port diagnostics, disposable-data default, and ASKEYC diagnostics. | Runtime web auth registration, production Keycloak administration, social IdPs, confidential-client secret lifecycle, provider SDK ownership, tenant mapping, user provisioning, app-user mapping, token storage, or web/runtime dependency on Keycloak packages. | Run the [Auth Aspire Keycloak AppHost proof](../examples/auth-aspire-keycloak-apphost/README.md). | Keep the package in AppHost/dev/test projects; pair runtime web apps with `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` directly. | Override ports, callback paths, seeded users, and persistent data only for local proof needs. |
| I need deterministic integration tests for AppSurface auth results. | `ForgeTrust.AppSurface.Auth.Testing` | `dotnet package add ForgeTrust.AppSurface.Auth.Testing` | Test personas, WebApplicationFactory setup, persona clients, request-level persona selection, explicit scheme modes, production-environment guard, stable test diagnostics, and AppSurface auth result / ProblemDetails assertions. | Production authentication, cookies, JWT/OIDC, ASP.NET Identity, identity providers, authorization policy ownership, middleware replacement, user stores, session freshness simulation, DevAuth runtime personas, browser login flows, policy bypasses. | Run the Auth.Testing flow in the [Auth Web/RazorWire proof tests](../examples/auth-web-razorwire-proof.tests/AuthWebRazorWireProofExampleTests.cs). | Keep this package in test projects and test hosts only. Use DevAuth for manual local persona switching. | Choose `DefaultScheme`, `NamedScheme`, or `NoDefault` to prove exactly how much the test host owns. |
| I need RazorWire UI to reflect auth state. | `ForgeTrust.RazorWire.Auth.AspNetCore` with `ForgeTrust.RazorWire` and `ForgeTrust.AppSurface.Auth.AspNetCore` | `dotnet package add ForgeTrust.RazorWire.Auth.AspNetCore` | RazorWire auth projection helpers and an ASP.NET Core adapter over host-owned AppSurface policy results. | Authentication schemes or handlers, cookies, JWT/OIDC, ASP.NET Identity, authorization policy definitions, middleware insertion, browser challenge/forbid execution, redirects, DevAuth persona controls, endpoint authorization, or RazorWire core UI primitives by itself. | Run the [Auth Web/RazorWire proof](../examples/auth-web-razorwire-proof/README.md) and read the [RazorWire auth adapter README](../Web/ForgeTrust.RazorWire.Auth.AspNetCore/README.md). | Keep RazorWire projection passive: UI may reflect host policy results, but endpoints still need ASP.NET Core/AppSurface enforcement. | Keep enforcement in ASP.NET Core policies and AppSurface endpoint filters. |

## Adopt Without Changing Enforcement

For an existing ASP.NET Core app, the safest adoption path is:

1. Leave authentication handlers, middleware order, policy definitions, identity provider setup, user stores, and production behavior in the host app.
2. Add `ForgeTrust.AppSurface.Auth.AspNetCore`.
3. Map the subject claim that the host already validates.
4. Put `RequireSurfacePolicy(...)` on one Minimal API endpoint where AppSurface-shaped ProblemDetails are useful.
5. Run the proof for allowed, unauthenticated, forbidden, and setup-failure outcomes.
6. Add `ForgeTrust.AppSurface.Auth.Testing` in an integration test project for deterministic personas.
7. Add DevAuth only to Development hosts, or explicit local/proof environments, where a visible fake-persona proof helps manual testing.
8. Add `ForgeTrust.AppSurface.Auth.Aspire.Keycloak` only to AppHost/dev/test projects when real local OIDC proof is needed.
9. Remove DevAuth and local Keycloak proof packages from production startup and keep real challenge, forbid, redirect, and sign-in/out behavior host-owned.

## Copy This / Do Not Copy This

Copy:

- Package install commands for shipped packages in the ladder.
- The linked examples' command lines and expected outcomes.
- The host-owned boundary language into app docs when your app has its own auth package guide.
- DevAuth marker rendering in local/proof pages where fake auth must stay visible; the renderer self-suppresses outside allowed environments.
- Auth Aspire Keycloak AppHost commands and local-only seeded credentials when a real provider proof is needed.
- Auth.Testing helpers into integration test projects.

Do not copy:

- DevAuth into production startup.
- Test personas into production auth.
- OIDC sample secrets, placeholder authority values, or token settings into real deployments without provider review.
- Auth Aspire Keycloak package references into runtime web apps or production deployment paths.
- RazorWire proof wording as a production enforcement promise.
- `RequireSurfacePolicy(...)` and native `RequireAuthorization(...)` on the same Minimal API endpoint.

## If It Fails

| Failure | Problem | Cause | Fix | Docs |
| --- | --- | --- | --- | --- |
| Missing policy | AppSurface cannot evaluate the named host policy. | The policy name on `RequireSurfacePolicy(...)` does not exist in ASP.NET Core authorization options. | Register the policy with `AddAppSurfacePolicy(...)` or normal ASP.NET Core `AddPolicy(...)`, then keep the name identical. | [Auth.AspNetCore policy results](../Auth/ForgeTrust.AppSurface.Auth.AspNetCore/README.md#policy-results) |
| Missing services | AppSurface cannot resolve required ASP.NET Core auth services. | Authentication or authorization services/handlers were not registered for the policy being evaluated. | Register normal ASP.NET Core authentication and authorization services before using AppSurface mapping. | [Auth.AspNetCore diagnostics](../Auth/ForgeTrust.AppSurface.Auth.AspNetCore/README.md#diagnostics) |
| Missing subject | The authenticated principal did not contain the configured subject claim. | `MapSubjectClaim(...)` points at a claim the host did not issue. | Map the claim your host really validates, or issue the expected claim in the host auth scheme. | [Auth.AspNetCore subject mapping](../Auth/ForgeTrust.AppSurface.Auth.AspNetCore/README.md#durable-app-user-mapping-boundary) |
| Middleware order surprise | Browser redirects or challenges happen before AppSurface ProblemDetails. | Native authorization middleware owns the endpoint response shape. | Use `RequireSurfacePolicy(...)` for API endpoints where AppSurface should return ProblemDetails; keep native authorization metadata on endpoints where middleware should own browser behavior. | [Endpoint policy helpers](../Auth/ForgeTrust.AppSurface.Auth.AspNetCore/README.md#endpoint-policy-helpers) |
| DevAuth startup failure | Fake local auth refuses to start. | DevAuth detected an environment outside `AllowedEnvironmentNames`, real scheme conflicts, missing personas, bad path prefix, or invalid persona ids. | Read the ASDEV code and keep DevAuth Development-by-default, explicitly opted in only for local/proof environments, and visibly fake. | [DevAuth diagnostics](../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics) |
| OIDC validation failure | OIDC setup fails fast. | Required authority, client id, client secret, subject claim, or token policy is missing or unsafe for the configured mode. | Configure provider-owned OIDC options and keep token/persistence choices explicit. | [OIDC README](../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc/README.md) |
| Keycloak proof failure | The real local OIDC proof cannot start or verify. | Aspire CLI, container runtime, fixed ports, stale persistent data, realm import, client id, or redirect URI does not match the proof contract. | Read the ASKEYC code, keep the package AppHost-only, reset stale data, and keep callback paths and ports aligned. | [Auth Aspire Keycloak README](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md) |
| Auth.Testing setup failure | A test persona or assertion fails before the request proves the expected outcome. | The persona, subject claim, scheme mode, environment, or expected ProblemDetails contract does not match the test host. | Fix the test host setup or assertion; do not treat Auth.Testing as production auth. | [Auth.Testing troubleshooting](../Auth/ForgeTrust.AppSurface.Auth.Testing/README.md#troubleshooting) |

## What AppSurface Auth Competes With

Use native ASP.NET Core authentication and authorization directly when you only need a production auth stack. Use ASP.NET Identity, Auth0, Entra, Keycloak, or another provider SDK directly when the work is sign-in, users, tokens, profile storage, federation, or provider lifecycle.

Use AppSurface Auth when AppSurface modules need common contracts, AppSurface-shaped policy outcomes, local proof personas, deterministic integration-test personas, or safe package-specific diagnostics around an auth stack your host still owns.

## Read Next

- [AppSurface package chooser](../packages/README.md)
- [ForgeTrust.AppSurface.Auth](../Auth/ForgeTrust.AppSurface.Auth/README.md)
- [ForgeTrust.AppSurface.Auth.AspNetCore](../Auth/ForgeTrust.AppSurface.Auth.AspNetCore/README.md)
- [ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth](../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md)
- [ForgeTrust.AppSurface.Auth.AspNetCore.Oidc](../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc/README.md)
- [ForgeTrust.AppSurface.Auth.Aspire.Keycloak](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md)
- [ForgeTrust.AppSurface.Auth.Testing](../Auth/ForgeTrust.AppSurface.Auth.Testing/README.md)
