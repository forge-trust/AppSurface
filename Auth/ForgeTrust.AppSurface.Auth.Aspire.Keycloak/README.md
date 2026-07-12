# ForgeTrust.AppSurface.Auth.Aspire.Keycloak

`ForgeTrust.AppSurface.Auth.Aspire.Keycloak` adds an AppHost-only local Keycloak proof for AppSurface OpenID Connect authentication. It builds on the official Aspire Keycloak hosting integration and keeps Keycloak, containers, and Aspire hosting dependencies out of runtime web app packages.

Use this package when you want a five-minute real OIDC login proof without signing up for SaaS, hand-configuring Keycloak, or copying fragile container setup between sample apps.

Use the [AppSurface Auth adoption ladder](../../start-here/auth-adoption-ladder.md) when deciding whether this local real-provider proof, DevAuth, OIDC, Auth.Testing, or host-owned ASP.NET Core authentication is the right rung.

Do not use this package for production Keycloak administration, tenant authority, user provisioning, social identity providers, confidential-client secret lifecycle, app-user mapping, token storage, or provider SDK abstractions. Use `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` for fake local personas and `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` when a web host already chose its OIDC provider.

## Five-Minute Real Login

The focused repository proof uses a Keycloak AppHost and a paired web app:

```bash
aspire run --apphost examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- local
```

The web proof listens on `http://localhost:5059`. Sign in with one of the local-only seeded users:

| User | Password | Purpose |
| --- | --- | --- |
| `admin` | `appsurface-admin-local-only` | Shows the admin AppSurface proof role. |
| `viewer` | `appsurface-viewer-local-only` | Shows the viewer AppSurface proof role. |

Run the noninteractive proof when a local container runtime is available:

```bash
aspire run --non-interactive --apphost examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- verify
```

## AppHost Shape

```csharp
var keycloak = builder.AddAppSurfaceKeycloak();

var web = builder.AddProject<Projects.AuthAspireKeycloakWeb>("web")
    .WithHttpEndpoint(targetPort: AppSurfaceKeycloakDefaults.WebProofPort, env: "ASPNETCORE_HTTP_PORTS");

keycloak.Configuration.ApplyTo(web)
    .WithReference(keycloak.Resource)
    .WaitFor(keycloak.Resource);
```

`AddAppSurfaceKeycloak(...)` generates a deterministic realm import, calls the official Aspire `AddKeycloak(...)` API, mounts the realm import directory with `WithRealmImport(...)`, and returns an `AppSurfaceKeycloakResource` wrapper whose `Resource` property exposes the underlying `IResourceBuilder<KeycloakResource>` for normal Aspire APIs.

## Defaults

| Setting | Default |
| --- | --- |
| Keycloak resource | `keycloak` |
| Realm | `appsurface-dev` |
| Public client id | `appsurface-web` |
| Keycloak port | `8080` |
| Web proof port | `5059` |
| Callback path | `/signin-appsurface-oidc` |
| Signed-out callback path | `/signout-callback-appsurface-oidc` |
| Keycloak data | disposable by default |

The paired web app receives only:

```json
{
  "Authentication:Oidc:Authority": "http://localhost:8080/realms/appsurface-dev",
  "Authentication:Oidc:ClientId": "appsurface-web",
  "Authentication:Oidc:CallbackPath": "/signin-appsurface-oidc",
  "Authentication:Oidc:SignedOutCallbackPath": "/signout-callback-appsurface-oidc",
  "Authentication:Oidc:RequireClientSecret": "false"
}
```

Admin credentials, seeded user passwords, raw realm JSON, tokens, client secrets, provider response bodies, and raw claims are never projected into runtime app configuration.

## Persistent Data Pitfall

Disposable data is the default because Keycloak startup realm import is deterministic on repeat runs. `UsePersistentDataVolume = true` keeps Keycloak data outside the container lifecycle; it also preserves admin credentials and imported realm state. If you change users, redirect URIs, or admin credentials while persistent data is enabled, delete the volume before expecting startup import to recreate the realm.

## Diagnostics

Diagnostics use `ASKEYC001+` codes and follow Problem/Cause/Fix/Docs wording. Common failures:

| Code | Problem | Fix |
| --- | --- | --- |
| `ASKEYC001` | Invalid local realm, client, user, path, URI, or port option. | Use lowercase deterministic local proof values or override the matching option. |
| `ASKEYC002` | Fixed local port is occupied. | Stop the other process or override `KeycloakPort` / `WebProofPort`. |
| `ASKEYC003` | OpenID metadata is unavailable. | Confirm Docker/container runtime, port availability, and Keycloak startup logs. |
| `ASKEYC004` | Metadata issuer does not match the expected realm. | Verify realm import and authority configuration. |
| `ASKEYC005` | Generated realm evidence is missing expected client, redirect, or users. | Regenerate realm import and reset stale persistent data. |
| `ASKEYC006` | Authorization endpoint rejected the configured client or redirect URI. | Reset stale data or update callback path and web proof port together. |

If the Aspire CLI is missing, install it before running the AppHost. If local development certificates block startup, run `aspire certs trust` or `dotnet dev-certs https --trust` from an interactive shell.

## Release Guidance

Use the [stable package chooser](../../packages/README.md) to compare this AppHost-only package with the runtime Auth packages. Use the [release hub](../../releases/README.md) for coordinated AppSurface versioning and package publication evidence.

---
[Back to Auth List](../README.md) | [Back to Root](../../README.md)
