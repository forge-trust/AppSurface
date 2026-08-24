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
aspire run --apphost examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- local
```

Open `http://localhost:5059`, choose sign in, and use one of the local-only seeded users:

| User | Password | Expected result |
| --- | --- | --- |
| `admin` | `appsurface-admin-local-only` | The proof page shows the `admin` AppSurface proof role. |
| `viewer` | `appsurface-viewer-local-only` | The proof page shows the `viewer` AppSurface proof role. |

Noninteractive verification:

```bash
aspire run --non-interactive --apphost examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- verify
```

The verifier checks Keycloak metadata, generated realm/client evidence, forbidden secret markers in the realm evidence,
the authorization challenge, `/auth/proof/status`, and `/auth/proof/protected`.

## #782 Completion-Gate Feasibility Spike

The AppHost now inserts the finite `auth-aspire-keycloak-readiness-gate` project between Keycloak health and the web
proof:

```text
Keycloak healthy
    -> readiness gate runs AppSurface metadata + realm-evidence + public-client checks once
        -> gate exits 0
            -> finite lifecycle worker exits 0
                -> web proof starts
```

The gate is an implementation-only feasibility artifact, not a package API. It receives only non-secret local proof
metadata, reconstructs the existing [`AppSurfaceKeycloakReadinessProbe`](../../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#apphost-shape), and exits nonzero with a named `ASKEYC` code on failure. It never receives an
administrator credential, performs Keycloak administration, or prints raw exception/provider output.

This establishes the documented Aspire `WaitFor` plus `WaitForCompletion` path for both the baseline gate and a
consumer-style finite project. The lifecycle worker accepts only the private feasibility modes `success`, `failure`,
`timeout`, and `hang` through its AppHost environment so the spike can record native terminal and blocked-dependent
behavior; it does not administer Keycloak or register a public seed. This AppHost does **not** ship the proposed
`RealmReady` or `WithLocalSeed` package surface; those remain gated by the approved
[#782 design](../../docs/designs/auth-aspire-keycloak-local-seeds.md) and its
[test plan](../../docs/designs/auth-aspire-keycloak-local-seeds-test-plan.md).

For fast, container-free verification of the sample gate contract, run:

```bash
dotnet test Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj --no-restore
```

## Theme Lifecycle

The default sample intentionally stays on the no-theme path so a real OIDC login remains a five-minute proof. To activate its checked-in assets-only `appsurface-sample` theme, set an immutable Keycloak image reference before running the same local profile:

```bash
export AUTH_ASPIRE_KEYCLOAK_THEME_IMAGE='quay.io/keycloak/keycloak:26.6@sha256:22c8cf9e79af88d320499c93994bb2c319ca255092d8ae7c7a45dd46d68192e2'
aspire run --apphost examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- local
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
| Browser warns about local certificates | Development certificates are missing or untrusted. | Run `aspire certs trust` or `dotnet dev-certs https --trust` from an interactive shell. |
| Sign-in says invalid username or password | You used a production credential or stale persisted realm. | Use the seeded local-only users above, or delete the persistent Keycloak data volume and rerun. |
| Login fails after enabling persistent data | A persisted realm/admin state is stale. | Delete the Keycloak data volume and rerun with disposable defaults. |

This is not a production Keycloak administration sample. It does not teach social IdPs, confidential clients, tenant mapping, app-user provisioning, or provider lifecycle.
