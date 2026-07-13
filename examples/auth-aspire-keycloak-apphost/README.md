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

## Recovery

| What you see | Likely cause | Fix |
| --- | --- | --- |
| `aspire: command not found` | Aspire CLI is not installed or not on `PATH`. | Install the Aspire CLI and rerun the command. |
| Container startup fails | Docker/container runtime is unavailable. | Start Docker or your configured container runtime, then rerun. |
| `ASKEYC002` | Port `8080` or `5059` is occupied. | Stop the other process or override the matching option in the AppHost. |
| `ASKEYC003` | Keycloak metadata did not become reachable. | Inspect container logs and confirm port/container runtime health. |
| `ASKEYC006` | Client id or redirect URI does not match imported realm state. | Reset stale Keycloak data or keep callback path and web proof port aligned. |
| Browser warns about local certificates | Development certificates are missing or untrusted. | Run `aspire certs trust` or `dotnet dev-certs https --trust` from an interactive shell. |
| Sign-in says invalid username or password | You used a production credential or stale persisted realm. | Use the seeded local-only users above, or delete the persistent Keycloak data volume and rerun. |
| Login fails after enabling persistent data | A persisted realm/admin state is stale. | Delete the Keycloak data volume and rerun with disposable defaults. |

This is not a production Keycloak administration sample. It does not teach social IdPs, confidential clients, tenant mapping, app-user provisioning, or provider lifecycle.
