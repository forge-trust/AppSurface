# Supervised real web and seed fixtures

These are isolated local proof fixtures for the supervised candidate documented in
[`docs/supervised-keycloak-execution.md`](../../../docs/supervised-keycloak-execution.md). Their pinned
macOS ARM64 AppHost/Linux ARM64 Docker profile is provisional and fixture-scoped. The neutral and Skoolit-shaped
configurations are synthetic replay inputs: they do not show a real consumer, real external provider, or tunnel
adopting the package. Both Keycloak packages remain publication-held.

These standalone programs exercise the existing AppSurface Keycloak credential reference and AppSurface OIDC adapter
against the pinned local Keycloak 26.7.2 realm. The parent composition copies each `Program.cs` outside the checkout,
adds extracted package assemblies, and compiles against the .NET shared framework with an empty package cache and no
package feeds. These directories intentionally have no project files.

The public configuration is a PascalCase JSON object shared by both programs. The web accepts one argument, the
configuration path. The seed accepts three arguments in order: public configuration path, private input path, and
sanitized receipt path. The configuration contains `Authority`, `ClientId`, `Realm`, `Username`, `ExpectedRunId`,
`KeycloakHost`, `KeycloakPort`, `CertificatePath`, `ReferencePath`, `WebBaseAddress`, `ListenUrl`, `SeedReceiptPath`,
`WorkerReceiptPath`, `SeedMode`, `ClientMode`, `CallbackPath`, and `SignedOutCallbackPath`. The worker receipt
defaults to `/proof/state/worker-result.json`. The seed mode defaults to `success`; failure and drift modes belong
to the separate adversarial proof cases. Each fixture permits only its `https://localhost:<KeycloakPort>/realms/<realm>`
issuer and a synthetic `seeded-user`.

| Fixture | Realm and client | Mode | Callbacks |
| --- | --- | --- | --- |
| Composition | `sample`, `sample-web` | Confidential | `/signin-oidc`, `/signout-callback-oidc` |
| Neutral | `appsurface-dev`, `appsurface-web` | Public | `/signin-appsurface-oidc`, `/signout-callback-appsurface-oidc` |
| Skoolit configuration | `skoolit`, `skoolit-web` | Confidential | `/signin-oidc`, `/signout-callback-oidc` |

The private seed input carries `AdminUsername`, `AdminPassword`, and `UserPassword`. The synthetic username is fixed
in the fixture. Older fixture transports may include the same username explicitly. The input is read from
the seed-only mount and is never copied to argv, environment variables, receipts, or output. A seed requires a real
worker receipt whose `Ready` field is true, either at the top level or under `result`. In `fail` mode it writes
`{"createdOrPreserved":false,"expectedSubject":null,"code":"InjectedRequiredSeedFailure"}` and exits 31 before
requesting an Admin API token or looking up or mutating users. In `success` mode it creates the synthetic user when
absent, preserves only the exact synthetic fixture user on a retry, sets the private password, idempotently assigns
the `viewer` realm role, verifies the resulting user and role, writes a lowercamelcase receipt, and exits 0. Invalid
arguments return 64, cancellation returns 124, and other failures return 32 with only a generic stage and exception
type in diagnostics.

The Skoolit seed also declares the admin-only `skoolit_app_user_id` profile attribute, reads its value back, and
records the expected synthetic application subject. The web verifies the validated issuer and Keycloak subject
against the seed receipt and checks the projected `appsurface.subject_id` against that synthetic subject.

Each web validates the seed receipt. The confidential web also validates the metadata-only
`KeycloakClientCredentialReference` against the configured run, issuer, and client. During startup the web reads the
credential into memory for `OpenIdConnectOptions.ClientSecret`. The public web requires a null reference path and
uses PKCE without a client secret. Both use the AppSurface cookie and OpenID Connect handlers with authorization
code flow and `SaveTokens` disabled.

The web exposes anonymous `GET /state` with `authenticated`, `subject`, `roles`, `name`, and the application-subject
match boolean. `GET /login` starts
the actual OIDC challenge; `GET /logout` signs out through the cookie and OIDC handlers; `/` is a small browser page;
and `/proof` requires authentication and checks the seeded subject. The seed and OIDC backchannels disable proxies and
redirects, accept only the exact configured `localhost:<KeycloakPort>` target, connect through the shared loopback
network namespace, retain the logical issuer hostname for TLS validation, and require the mounted leaf certificate's
exact SHA-256 pin, valid dates, and matching hostname. SIGTERM/SIGINT stop both fixtures so the supervisor can own
the bounded 30-second teardown.

The [combined #888 replay](../../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results) also exercises the
exact local Skoolit tunnel-shaped origins. `PublicWebOrigin` selects the fixture's HTTPS application origin.
The copied browser harness maps that exact host and the declared authority to run-owned loopback ports through a
local CONNECT proxy. It terminates application TLS with its own temporary certificate and pins both fixture
certificates in Chromium. The web and seed keep separate transport rules. OIDC backchannel requests preserve the
declared issuer and SNI over loopback, and the seed always addresses `https://localhost:<KeycloakPort>` for
administration. The test-only web accepts the public application host from the proxy, with its loopback HTTP
`/state` endpoint available to the startup observer.

`ThemeCssPath` is a host-side browser harness input for the isolated theme fixture. The browser finds the
stylesheet linked on the exact authority, reads it before and after the edit, and records only the assertions.
The driver independently verifies the read-only mount and container identity across the edit.
