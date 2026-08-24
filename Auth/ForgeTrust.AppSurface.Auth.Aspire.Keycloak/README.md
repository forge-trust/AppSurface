# ForgeTrust.AppSurface.Auth.Aspire.Keycloak

`ForgeTrust.AppSurface.Auth.Aspire.Keycloak` adds an AppHost-only local Keycloak proof for AppSurface OpenID Connect authentication. It builds on the official Aspire Keycloak hosting integration and keeps Keycloak, containers, and Aspire hosting dependencies out of runtime web app packages.

Use this package when you want a five-minute real OIDC login proof without signing up for SaaS, hand-configuring Keycloak, or copying fragile container setup between sample apps.

Use the [AppSurface Auth adoption ladder](../../start-here/auth-adoption-ladder.md) when deciding whether this local real-provider proof, DevAuth, OIDC, Auth.Testing, or host-owned ASP.NET Core authentication is the right rung.

Do not use this package for production Keycloak administration, tenant authority, user provisioning, social identity providers, confidential-client secret lifecycle, app-user mapping, token storage, or provider SDK abstractions. Use `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` for fake local personas and `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` when a web host already chose its OIDC provider.

## Five-Minute Real Login

The focused repository proof uses a Keycloak AppHost and a paired web app:

```bash
AUTH_ASPIRE_KEYCLOAK_ENABLE_LOCAL_SEEDS=true aspire run --project examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- local
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
AUTH_ASPIRE_KEYCLOAK_ENABLE_LOCAL_SEEDS=true aspire run --project examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- verify
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

## Ordered Local Seed Projects

Use `RealmReady()` and `WithLocalSeed(...)` when a local AppHost needs a small number of finite, consumer-owned
startup stages after the generated Keycloak baseline is genuinely usable. Typical stages create a local identity
provider alias, write an application-owned identity map, or load an application fixture. The package establishes only
the completion graph and safe bindings; each consumer project owns its Keycloak client, administration calls, retries,
mutations, idempotence, state store, output hygiene, timeout, and exit code.

```csharp
var adminUsername = builder.AddParameter("keycloak-admin-username", "admin", secret: false);
var adminPassword = builder.AddParameter("keycloak-admin-password", secret: true);

var keycloak = builder.AddAppSurfaceKeycloak("auth", adminUsername, adminPassword, options =>
{
    options.UsePersistentDataVolume = true;
});

var identity = keycloak.WithLocalSeed(
    "identity-bootstrap",
    seed => builder.AddProject<Projects.IdentityBootstrap>(seed.ResourceName)
        .WithEnvironment("LOCAL_SEED_ADMIN_USERNAME", adminUsername)
        .WithEnvironment("LOCAL_SEED_STORE_PATH", ".appsurface/local-identity.json"),
    options => options.WithRequiredSecretParameter("LOCAL_SEED_ADMIN_PASSWORD", adminPassword));

var fixture = keycloak.WithLocalSeed(
    "candidate-fixture",
    seed => builder.AddProject<Projects.CandidateFixture>(seed.ResourceName)
        .WithEnvironment("LOCAL_SEED_STORE_PATH", ".appsurface/local-identity.json"),
    options => options.After(identity));

var web = builder.AddProject<Projects.Web>("web")
    .WaitForCompletion(fixture.Resource);
```

`RealmReady()` is lazy and cached per `AppSurfaceKeycloakResource`. It adds one finite package-owned executable that
waits for Keycloak health, then verifies metadata, the generated realm baseline, and the public authorization
challenge. The worker receives only safe expected values, never administrator credentials or seeded-user passwords.
Every first seed waits for that handle; every later seed must call `After(...)` with the immediately previous seed from
the same wrapper. The resulting graph is intentionally linear:

```text
Keycloak healthy -> RealmReady -> identity-bootstrap -> candidate-fixture -> web
```

### Local-only policy and opt-out

Seed registration is fail-closed. It is legal only for Aspire `Run` in `Development`, `Test`, or `Testing` by default,
matched case-insensitively. `Publish` and every other operation are denied before the factory, realm-ready resource, or
consumer project is materialized—even if an allowed-environment list contains a deployment-looking value. An explicit
local host may extend `AllowedEnvironmentNames`, but that never permits publish.

Do not model an optional integration as an optional secret. Instead, use an explicit nonsecret local option and call
`WithLocalSeed` only when the integration should exist. When it is off, no seed is registered, no consumer mutation is
attempted, and the application should wait on the last required predecessor rather than a missing stage.

The factory must return the one `ProjectResource` named by `seed.ResourceName`; returning `null`, a project from another
AppHost, or a differently named resource fails registration. The project must be finite: exit `0` only after it has
completed its own idempotent work, and exit nonzero for capability, validation, or mutation failure. A hung project has
no completion signal, so dependent resources stay blocked until the AppHost is canceled.

### Safe context and required secrets

`AppSurfaceKeycloakLocalSeedContext` contains only `ResourceName`, `Authority`, `RealmName`, and `PublicClientId`.
It deliberately excludes external subjects, claims, tokens, provider responses, user passwords, administrator
credentials, and application records. A seed can bind a required credential only with
`WithRequiredSecretParameter(environmentVariableName, parameter)`. The parameter must be a secret `ParameterResource`
from the same AppHost; binding names and parameters cannot be duplicated or reused by another seed in that wrapper.
AppSurface validates metadata and applies Aspire's typed environment reference to that returned project only. It never
calls `Value`, `GetValueAsync`, logs, serializes, or otherwise resolves the secret value.

Generated manifests can contain the framework-required parameter name/reference, but never its value. AppSurface's
projection, diagnostics, and evidence likewise contain no seed secret. Consumer projects are still responsible for
their own stdout, stderr, dashboard logs, and provider-client logs; never print credentials, tokens, or remote response
bodies from a seed worker.

The original `AddAppSurfaceKeycloak(name, configure)` overload remains the smallest option for the five-minute login
proof and intentionally has no administrator parameter resources. Choose the explicit-parameter overload only when a
consumer-owned local seed genuinely needs the Keycloak Admin API. Its password parameter must be typed as
`secret: true`; a non-secret password parameter is rejected with `ASKEYC001` before the Keycloak resource is added.

### Do not confuse this with DevAuth personas

Local seeds run once during AppHost startup and gate resource launch. They are for finite convergence work against a
real local provider and application-owned state. They are not request-time authentication, persona selection, or a
replacement for browser sign-in. For visible fake personas selected while handling local requests, use
[`ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth`](../ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md) instead.
For real web authentication, continue to configure
[`ForgeTrust.AppSurface.Auth.AspNetCore.Oidc`](../ForgeTrust.AppSurface.Auth.AspNetCore.Oidc/README.md) in the web host.

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

The fresh output directory contains a source snapshot under `themes/<name>` without development-only assets, a `Containerfile`, and an `appsurface-keycloak-theme-manifest.json` evidence file containing both source and packaged manifests. `Digest` binds the registration, both manifests, platform, base image, and template baseline. The Containerfile labels the theme name, source and packaged manifests, build-contract digest, base image, and platform. `VerifyPackagedTheme` fails if content is missing, added, or changed. The package does not build or push the image, mutate a production realm, deploy infrastructure, or automate rollout/rollback; those remain application and operations responsibilities.

For a mutable source, recreate the build contract immediately before packaging. A source change after registration makes build-context materialization fail before it copies an unproven file into the image context.

## Release Evidence

After an application-owned CI build produces its digest-pinned theme image, bind it to the validated source and
packaged manifests before any realm selection:

```csharp
var releaseEvidence = AppSurfaceKeycloakThemeReleaseEvidence.Create(
    buildContract,
    "registry.example/appsurface-keycloak-theme:1.0@sha256:<64-lowercase-hex-digest>");
releaseEvidence.Write("artifacts/keycloak-theme-evidence.json");
```

The atomically written tuple records the theme name, source and packaged-manifest digests, build-contract digest,
pinned Keycloak base image, final image, platform, and optional template-baseline digest. It excludes realm JSON,
credentials, property values, source-machine paths, and image layers. Applications should publish the tuple with
their CI artifact, then select the matching realm theme only after the image is available. Call `Verify(...)` before
reusing a tuple to reject a changed image or build contract.

## Theme Verification

There are two distinct proofs. The local AppHost proof mounts mutable source read-only and disables only development
theme caches; it is for fast iteration and never release evidence. The immutable proof builds a fresh context,
checks the image labels and exact theme subtree, starts a disposable Linux/amd64 Keycloak realm, reads back
`loginTheme`, renders an authorization challenge, and hash-checks a declared same-origin resource. The package owns
the deterministic inputs and evidence format. Application CI owns Docker, image publication, and the disposable
runtime invocation.

An assets-only theme inherits Keycloak's templates and form semantics. A copied FreeMarker template remains subject
to the reviewed baseline policy, and a Keycloak-image change always requires the [upgrade procedure](docs/theme-upgrade.md).

## CI Evidence

The repository's required `keycloak-theme-evidence` job proves the checked-in sample against
`quay.io/keycloak/keycloak:26.6@sha256:22c8cf9e79af88d320499c93994bb2c319ca255092d8ae7c7a45dd46d68192e2` on
Linux/amd64. It uploads a `keycloak-theme-evidence` artifact with `pass`, `fail`, or `not-run` status, safe manifest
and build-contract digests, a candidate OCI manifest digest, and no credentials, realm import, source path, or image
layer. `not-run` and `fail` are release-gate failures. A consuming release must replace the candidate image digest
with its pushed immutable registry reference before calling `AppSurfaceKeycloakThemeReleaseEvidence.Create`.

This job is deliberately not production automation. It neither pushes the image nor mutates a production realm.
Use the compatible tuple and order in the [upgrade and rollback procedure](docs/theme-upgrade.md) for those
application-owned operations.

## Upgrade and Rollback

Treat the theme name, source manifest, packaged manifest, build-contract digest, final image digest, pinned
Keycloak base image, platform, and template baseline as one compatible tuple. Publish and health-check the new image
before selecting its theme. To roll back, restore the earlier verified image first, select the theme from its matching
tuple, read back the selection and declared resource, record rollback evidence, then retain the former image until
the rollback window closes. See the ordered [upgrade and rollback procedure](docs/theme-upgrade.md) for the full
checklist and template-baseline requirements.

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

## Persistent-data Recovery

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
| `ASKEYC015` | The pinned Keycloak archive layout is unsupported. | Stop and review the exact image layout before template compatibility work. |
| `ASKEYC016` | The live source changed after its manifest was generated. | Regenerate the manifest and build contract before packaging. |
| `ASKEYC017` | Build context or packaged content differs from the validated manifest. | Create a fresh build context and rebuild from the same source/image evidence tuple. |
| `ASKEYC018` | Image identity, labels, or packaged theme content differs from evidence. | Rebuild the matching image from the validated build contract. |
| `ASKEYC019` | The Linux/amd64 runtime proof was unavailable or timed out. | Run the named CI job; `not-run` is not release success. |
| `ASKEYC020` | Disposable-realm readback did not select the expected login theme. | Reset only disposable proof data and rerun the matching tuple. |
| `ASKEYC021` | A required login resource was missing, cross-origin, redirected, oversized, or hash-mismatched. | Repair the declared same-origin resource and rebuild the image. |
| `ASKEYC022` | The finite package-owned realm-ready worker could not be resolved. | Restore the AppHost package and use the supported .NET SDK. |
| `ASKEYC023` | Local seed registration was attempted outside permitted local execution. | Use Aspire `Run` in an explicitly allowed local environment; publish is always denied. |
| `ASKEYC024` | A local seed name, predecessor, project factory, or typed secret binding is invalid. | Use one correctly named finite project, name the immediate predecessor, and bind each required secret parameter once. |

If the Aspire CLI is missing, install it before running the AppHost. If local development certificates block startup, run `aspire certs trust` or `dotnet dev-certs https --trust` from an interactive shell.

## Escape Hatches

Use the underlying Aspire resource for ordinary local customization, a consumer-owned Containerfile for an
application-owned image pipeline, or an assets-only theme for the smallest supported themed path. Non-amd64 and
alternative runtimes may run deterministic source tests, but they cannot replace the named Linux/amd64 release proof.
Production Keycloak, image publication, realm mutation, rollout, and rollback remain operator-owned and unsupported
by this package.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

This AppHost-oriented package follows the coordinated AppSurface release policy.
Before using a prerelease build in an AppHost, development, or test environment,
check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md) for publication status,
compatibility guidance, and readiness.
<!-- appsurface-release-guidance: end -->

---

[Back to Auth List](../ForgeTrust.AppSurface.Auth/README.md) | [Back to Root](../../README.md)
