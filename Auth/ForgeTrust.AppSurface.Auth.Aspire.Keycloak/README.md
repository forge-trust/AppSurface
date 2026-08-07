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

The generated local users include a deterministic non-deliverable email address and surname. This satisfies Keycloak's
default profile requirements, so the proof redirects directly after password sign-in instead of asking developers to
complete a profile. These fields exist only in the disposable local realm and are not runtime configuration.

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

## Theme Quickstart

The original no-theme path stays the fastest proof. Opt into a local application-owned login theme only after pinning the Keycloak image you intend to test:

```csharp
var themeSource = Path.GetFullPath("../Identity/KeycloakTheme", AppContext.BaseDirectory);
var loginTheme = AppSurfaceKeycloakThemeOptions.Login(
    name: "application",
    sourceDirectory: themeSource,
    baseImage: AppSurfaceKeycloakImageReference.Parse(
        "quay.io/keycloak/keycloak:26.6@sha256:<64-lowercase-hex-digest>"));
loginTheme.RequiredThemeProperties.Add("parent");
loginTheme.RequiredResourcePaths.Add("login/resources/css/site.css");

var keycloak = builder.AddAppSurfaceKeycloak("auth", options => options.LoginTheme = loginTheme);
```

Relative theme paths are resolved against the AppHost process base directory. Resolve them explicitly, as shown, or copy
the theme into the AppHost output tree before registration.

The source root contains `login/theme.properties` and is mounted read-only at `/opt/keycloak/themes/application`. The local AppHost disables only Keycloak's theme and template caches. The mount intentionally reflects source edits for local development, while `keycloak.Theme` evidence captures the registration-time manifest only. It selects `loginTheme` in the generated disposable realm import and returns safe evidence containing the name, image reference, platform, and source-manifest digest. That evidence never exposes the source-machine path, property values, realm JSON, bootstrap credentials, or token material.

`LoginTheme` is optional. When it is absent, resource image configuration, realm JSON, and the existing five-minute local login behavior remain unchanged. The initial exact-image proof platform is `linux/amd64`; a different value is rejected instead of being silently presented as release evidence.

## Source Policy

A theme source is a bounded regular-file tree rooted at `login/theme.properties`. The validator rejects missing roots, symbolic links/reparse points, traversal, unsupported files, more than 256 files or directories, individual files larger than 1 MiB, and trees larger than 8 MiB. Allowed files are Keycloak theme resources: `.properties`, `.ftl`, CSS, JavaScript, standard image formats, and common font formats.

The manifest sorts slash-normalized relative paths ordinally and records each file's byte length and SHA-256 digest; its digest is independent of machine-local source paths and file enumeration order. `RequiredThemeProperties` checks only property names, never serializes their values. `RequiredResourcePaths` and `DevelopmentOnlyResourcePaths` must resolve inside the manifest. The complete source manifest retains development-only assets as local evidence, while `AppSurfaceKeycloakThemeBuildContract.PackagedManifest` excludes them from the immutable image context and packaged-content proof. `login/theme.properties` cannot be development-only because a packaged Keycloak theme requires it.

## Template Overrides

An assets-only theme needs no template baseline. A theme that copies a FreeMarker (`.ftl`) file must set `TemplateBaselineDirectory` to a reviewed, bounded upstream baseline containing the same slash-relative template paths. The registration records the baseline digest alongside the image and source-manifest digests, and rejects any copied template that is not present in that baseline. This detects unexpected template overrides before packaging; review and refresh the baseline whenever the pinned Keycloak image changes.

The baseline is evidence, not automatic migration. AppSurface never edits a FreeMarker template, changes Keycloak-owned form semantics, or claims a newer Keycloak image is compatible without a reviewed baseline and exact-image verification.

## Build Contract

Use `AppSurfaceKeycloakThemeBuildContract` to create the consumer-owned immutable image context:

```csharp
var buildContract = AppSurfaceKeycloakThemeBuildContract.Create(loginTheme);
var buildContext = buildContract.Write("artifacts/keycloak-theme");

// Application or CI owns this command, image tag, registry, and rollout.
// docker build --file "$buildContext/Containerfile" --tag application-keycloak:local "$buildContext"

buildContract.VerifyPackagedTheme(Path.Join(buildContext, "themes", "application"));
```

The fresh output directory contains a source snapshot under `themes/<name>` without development-only assets, a `Containerfile`, and an `appsurface-keycloak-theme-manifest.json` evidence file containing both source and packaged manifests. The Containerfile uses the immutable image reference and labels the theme name, platform, and packaged-manifest digest. `VerifyPackagedTheme` fails if content is missing, added, or changed. The package does not build or push the image, mutate a production realm, deploy infrastructure, or automate rollout/rollback; those remain application and operations responsibilities.

For a mutable source, recreate the build contract immediately before packaging. A source change after registration makes build-context materialization fail before it copies an unproven file into the image context.

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
  "Authentication:Oidc:Authority": "https://localhost:8080/realms/appsurface-dev",
  "Authentication:Oidc:ClientId": "appsurface-web",
  "Authentication:Oidc:CallbackPath": "/signin-appsurface-oidc",
  "Authentication:Oidc:SignedOutCallbackPath": "/signout-callback-appsurface-oidc",
  "Authentication:Oidc:RequireClientSecret": "false"
}
```

The local Keycloak authority is HTTPS because Aspire publishes its browser-facing Keycloak endpoint over TLS, while the
paired web proof remains HTTP on its fixed loopback port.

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
| `ASKEYC010` | Login-theme name, immutable image reference, platform, or declared path is invalid. | Use an explicit lower-case name, a digest-pinned image, `linux/amd64`, and theme-relative paths. |
| `ASKEYC011` | Theme source is missing, unsafe, unsupported, or outside deterministic bounds. | Restore a regular source tree rooted at `login/theme.properties`. |
| `ASKEYC012` | Theme source entries collide after slash or case normalization. | Rename one of the colliding files so the theme is portable across supported hosts. |
| `ASKEYC013` | A required theme property name or resource is absent. | Correct the declaration or source without adding sensitive values to evidence. |
| `ASKEYC014` | A copied FreeMarker template has no reviewed upstream baseline. | Record the matching baseline for the pinned Keycloak image before packaging. |
| `ASKEYC017` | Build context or packaged content differs from the validated manifest. | Create a fresh build context and rebuild from the same source/image evidence tuple. |

If the Aspire CLI is missing, install it before running the AppHost. If local development certificates block startup, run `aspire certs trust` or `dotnet dev-certs https --trust` from an interactive shell.

## Release Guidance

Use the [stable package chooser](../../packages/README.md) to compare this AppHost-only package with the runtime Auth packages. Use the [release hub](../../releases/README.md) for coordinated AppSurface versioning and package publication evidence.

---
[Back to Auth List](../README.md) | [Back to Root](../../README.md)
