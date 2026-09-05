# AppSurface Auth Aspire Keycloak AppHost

This AppHost proves `ForgeTrust.AppSurface.Auth.Aspire.Keycloak` with real local Keycloak and a paired ASP.NET Core web app that uses `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc`.

## Five-Minute Real Login

Prerequisites:

- .NET 10 SDK
- Aspire CLI
- Docker or another Aspire-supported container runtime
- ports `8080` and `5059` available

Run the local graph:

```bash
AUTH_ASPIRE_KEYCLOAK_ENABLE_LOCAL_SEEDS=true aspire run --project examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- local
```

Open `http://localhost:5059`, choose sign in, and use one of the local-only seeded users:

| User | Password | Expected result |
| --- | --- | --- |
| `admin` | `appsurface-admin-local-only` | The proof page shows the `admin` AppSurface proof role. |
| `viewer` | `appsurface-viewer-local-only` | The proof page shows the `viewer` AppSurface proof role. |

Noninteractive verification:

```bash
AUTH_ASPIRE_KEYCLOAK_ENABLE_LOCAL_SEEDS=true aspire run --project examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- verify
```

The verifier checks Keycloak metadata, generated realm/client evidence, forbidden secret markers in the realm evidence,
the authorization challenge, `/auth/proof/status`, `/auth/proof/protected`, and the exact consumer-owned result of the
two local seed projects.

## Ordered Local Seed Proof

This sample demonstrates the public ordered-local-seed extension points introduced for [issue #782](../../docs/designs/auth-aspire-keycloak-local-seeds.md).
The package owns only the readiness and completion graph. The sample owns the Keycloak Admin API call, the credentials,
the durable local state, idempotence, and the fixture mutation:

The sample requires `AUTH_ASPIRE_KEYCLOAK_ENABLE_LOCAL_SEEDS=true` for every command that registers the mutating
workers. That is an intentional sample-level opt-in for a non-default AppHost environment; without it, the package's
Development/Test/Testing policy fails closed before either worker is materialized. Aspire `Publish` remains denied even
when this flag is present.

```text
Keycloak healthy
    -> package-owned RealmReady verifies metadata + realm evidence + public-client challenge
        -> auth-aspire-keycloak-seed-identity-bootstrap upserts local-broker and founder -> subject-founder-001
            -> auth-aspire-keycloak-seed-candidate-fixture upserts candidate:founder
                -> web proof and verifier start
```

The `identity-bootstrap` project alone receives the typed secret administrator-password parameter. The package never
resolves that value, and neither the web proof nor fixture project receives its parameter reference. The workers write
only non-secret records to `.appsurface/auth-aspire-keycloak-local-seed-store.json` below the AppHost directory.
Before the identity worker sends its administrator-password grant, it accepts only the AppHost-projected HTTPS
`localhost` or `127.0.0.1` authority whose path is exactly `/realms/{realm}`. A remote host, HTTP authority, encoded
path separator, or realm mismatch fails locally before any administrator credential reaches transport.

The AppHost uses persistent Keycloak data deliberately. Each worker converges by a natural key, so a normal rerun
leaves exactly one `local-broker` alias, one `founder -> subject-founder-001` mapping, and one `candidate:founder`
fixture. To prove safe partial recovery, inject the fixture failure after the first worker has converged:

```bash
AUTH_ASPIRE_KEYCLOAK_INJECT_FIXTURE_FAILURE=true \
  AUTH_ASPIRE_KEYCLOAK_ENABLE_LOCAL_SEEDS=true aspire run --project examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- verify
```

The candidate fixture ends nonzero; Aspire leaves the web proof blocked and no fixture record is written. Stop that
intentional failed run, then run the normal `verify` command again. The idempotent first worker repairs or preserves its
records, the fixture is added once, and the verifier proves the final counts. A hung consumer has no successful
completion signal either, so its dependents stay blocked until the AppHost is canceled.

For focused, container-free API and graph coverage, run:

```bash
dotnet test Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj -p:UseSharedCompilation=false
```

This is startup-time local convergence against a real provider, not request-time persona selection. Use
[`ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth`](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md)
for visible fake personas during local requests, and use the paired
[`ForgeTrust.AppSurface.Auth.AspNetCore.Oidc`](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc/README.md) package
for the web application's real OIDC registration.

## Theme Lifecycle

The default sample intentionally stays on the no-theme path so a real OIDC login remains a five-minute proof. To activate its checked-in assets-only `appsurface-sample` theme, set an immutable Keycloak image reference before running the same local profile:

```bash
export AUTH_ASPIRE_KEYCLOAK_THEME_IMAGE='quay.io/keycloak/keycloak:26.6@sha256:22c8cf9e79af88d320499c93994bb2c319ca255092d8ae7c7a45dd46d68192e2'
AUTH_ASPIRE_KEYCLOAK_ENABLE_LOCAL_SEEDS=true aspire run --project examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- local
```

The package handles local read-only mounting, development-only cache switches, realm `loginTheme` selection, source manifest generation, and an immutable-image-ready build contract. See the [package theme quickstart](../../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#theme-quickstart), [build contract](../../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#build-contract), [CI evidence](../../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#ci-evidence), and [upgrade/rollback procedure](../../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/docs/theme-upgrade.md).

Keep production image build/push, realm mutation, rollout, and rollback in application CI and operator runbooks. A bind mount and disabled theme caches are local-development behavior only.

### Sample-theme acceptance

The checked-in appsurface-sample theme is intentionally assets-only: Keycloak retains its login templates and
semantic form behavior. Before treating a pinned-image run as release evidence, record these manual checks against
the local login page:

- keyboard-only sign-in and recovery reach visible focus indicators in logical tab order;
- invalid credentials, required actions, and expired-action recovery retain labels and Keycloak error association;
- browser zoom at 200%, a narrow viewport, and the no-CSS fallback leave the sign-in form usable;
- body/control contrast, screen-reader names for the logo, and reduced-motion behavior are acceptable;
- locale, RTL, dark-palette, and third-party-provider pages remain Keycloak/application-owned unless the consumer
  explicitly supplies and reviews those assets.

This checklist verifies the user-facing inherited states that manifest hashes cannot prove. Record the final image,
source and packaged-manifest digests, build-contract digest, pinned base image, reviewer/date, and the matching
`keycloak-theme-evidence` artifact alongside the result. The CI job runs the disposable-realm readback and same-origin
stylesheet hash check; it never replaces this manual accessibility review.

### State ownership matrix

The sample is an assets-only theme with an application-owned light palette. It inherits Keycloak templates, form
semantics, and its no-CSS fallback. Its stylesheet has no logo or provider label, so application identity remains
optional rather than impersonating an identity provider.

| Login state | Owner | Release evidence |
| --- | --- | --- |
| Initial sign-in and loading/submission | Keycloak template; sample stylesheet | CI renders the authorization challenge; manual keyboard, focus, and reduced-motion review. |
| Invalid credentials, locked account, required action, and expired action | Keycloak template and error associations | Manual review confirms labels, errors, recovery task, and persistent focus. |
| Session restart, invalid client, redirect failure, and Keycloak server error | Keycloak | Existing OIDC proof and manual recovery review; the sample does not replace provider error copy. |
| Locale selection, fallback, and long/RTL text | Keycloak realm | Test the consumer's configured locales and fallback. RTL is inherited, not separately styled by this sample. |
| Declared light/dark appearance | Sample stylesheet | Light-only is explicit and remains legible with an explicit background and foreground; verify it under a dark browser preference. |
| Optional stylesheet failure | Keycloak template | Disable CSS in the browser: the inherited form remains the usable authentication task. |
| Third-party provider actions | Keycloak or consumer | Not styled or relabeled by the sample; consumers own any provider-specific presentation. |

## Recovery

| What you see | Likely cause | Fix |
| --- | --- | --- |
| `aspire: command not found` | Aspire CLI is not installed or not on `PATH`. | Install the Aspire CLI and rerun the command. |
| Container startup fails | Docker/container runtime is unavailable. | Start Docker or your configured container runtime, then rerun. |
| `ASKEYC002` | Port `8080` or `5059` is occupied. | Stop the other process or override the matching option in the AppHost. |
| `ASKEYC003` | Keycloak metadata did not become reachable. | Inspect container logs and confirm port/container runtime health. |
| `ASKEYC004`–`ASKEYC005` | Keycloak metadata issuer or generated realm evidence differs from the local proof contract. | Recreate the disposable local realm/import and rerun; inspect only the named safe diagnostic. |
| `ASKEYC006` | Client id or redirect URI does not match imported realm state. | Reset stale Keycloak data or keep callback path and web proof port aligned. |
| `ASKEYC010`–`ASKEYC014` | Theme name, image, source, property, resource, or template-baseline declaration is invalid. | Read the package source-policy guidance, then rebuild the manifest. |
| `ASKEYC015`–`ASKEYC016` | The pinned archive layout is unsupported or source changed after validation. | Review the exact image layout, then regenerate the manifest and build contract. |
| `ASKEYC017` | Materialized or packaged theme content differs from the manifest. | Recreate the build context from a fresh source snapshot and rebuild the image. |
| `ASKEYC018`–`ASKEYC021` | The built image, exact runtime proof, realm selection, or required resource is invalid. | Re-run `keycloak-theme-evidence`; resolve the named image/readback/resource failure before release. |
| `ASKEYC022` | The package-owned finite realm-ready worker could not be resolved. | Restore the AppHost package build output and use the supported .NET SDK. |
| `ASKEYC023` | A seed was registered outside a local Aspire `Run` operation or permitted environment. | Use an explicitly allowed Development/Test/Testing AppHost run; publish is never permitted. |
| `ASKEYC024` | A local seed name, predecessor, factory result, or typed secret binding is invalid. | Keep the stages finite and linear, return the exact named project, and bind each typed secret to one seed only. |
| Browser warns about local certificates | Development certificates are missing or untrusted. | Run `aspire certs trust` or `dotnet dev-certs https --trust` from an interactive shell. |
| Sign-in says invalid username or password | You used a production credential or stale persisted realm. | Use the seeded local-only users above, or delete the persistent Keycloak data volume and rerun. |
| Login fails after enabling persistent data | A persisted realm/admin state is stale. | Delete the Keycloak data volume and rerun with disposable defaults. |

This is not a production Keycloak administration sample. It does not teach social IdPs, confidential clients, tenant mapping, app-user provisioning, or provider lifecycle.
