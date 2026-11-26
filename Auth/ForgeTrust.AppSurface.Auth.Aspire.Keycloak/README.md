# ForgeTrust.AppSurface.Auth.Aspire.Keycloak

`ForgeTrust.AppSurface.Auth.Aspire.Keycloak` adds an AppHost-only local Keycloak proof for AppSurface OpenID Connect authentication. It builds on the official Aspire Keycloak hosting integration and keeps Keycloak, containers, and Aspire hosting dependencies out of runtime web app packages.

Use this package when you want a five-minute real OIDC login proof without signing up for SaaS, hand-configuring Keycloak, or copying fragile container setup between sample apps.

Use the [AppSurface Auth adoption ladder](../../start-here/auth-adoption-ladder.md) when deciding whether this local real-provider proof, DevAuth, OIDC, Auth.Testing, or host-owned ASP.NET Core authentication is the right rung.

Do not use this package for production Keycloak administration, tenant authority, user provisioning, social identity providers, confidential-client secret lifecycle, app-user mapping, token storage, or provider SDK abstractions. Use `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` for fake local personas and `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` when a web host already chose its OIDC provider.

## Identity Credential Bindings

The #888 candidate declaration APIs separate [immutable identity configuration](../../docs/identity-configuration.md)
from typed execution bindings. `AppSurfaceKeycloakOptions.CreateDesiredConfiguration()` freezes client mode, exact
issuer/callback/origin declarations, mappers, Google-provider declarations and explicit removals. Snapshot creation
validates these fields without resolving credentials. The default remains a public client with loopback callbacks.

`AppSurfaceKeycloakCredentialBindings.ClientSecret` declares the confidential client's separate secret
`IResourceBuilder<ParameterResource>`. `IdentityProviderSecrets` maps exactly the declared enabled provider aliases
to secret parameters. Each parameter must belong to the same AppHost and must differ from administrator parameters;
a provider binding must also differ from the client binding. Missing, extra, nonsecret and cross-host bindings fail
with `ASKEYC027` before value resolution. Public client mode has no client-secret binding. Neither options nor the
[web configuration projection](AppSurfaceKeycloakConfigurationProjection.cs) contains credential values.

Normal public registration and supported owned lifetime integration for these declarations remain pending the
[lifecycle gate](../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results). These candidate declarations do not make
the current public registration overload a configured reconciler. The internal feasibility registration remains
qualified separately; see [Approach B](../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results).

The pure [credential reference](../../docs/client-credential-reference.md) describes one captured run/client identity.
Its reader has the documented macOS ARM64, expiry, filesystem and trusted-ancestor constraints. Do not replace it
with a credential in ordinary configuration or infer another platform's support from portable assembly metadata.

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

## Supervised identity-readiness candidate

The local #888 candidate adds public PKCE and typed confidential-client modes with `SupervisedExecution` to the normal
`AddAppSurfaceKeycloak` entry point. Its [registration and recovery guide](../../docs/supervised-keycloak-execution.md)
explains copied container seed/web payloads, the explicit `SupervisedRun.RunAsync` lifetime, stable persistent ownership
and verification-only recovery. Aspire exposes completion/diagnostic handles while the package supervises Docker
execution. This candidate is qualified only by its recorded local proofs; the [AppHost-loss checkpoint](../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results)
records the heartbeat fence and launch uncertainty controls. Complete acceptance, API commitment, publication and
downstream Skoolit adoption remain separate gates. The legacy host-project seed API
below does not inherit this candidate's lifecycle claims.

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

### Realm-ready worker package payload

The [package build](ForgeTrust.AppSurface.Auth.Aspire.Keycloak.csproj) publishes the existing finite worker as a
framework-dependent `net10.0` DLL, with no apphost, self-contained runtime or single-file bundling. Its project now references only the pure Keycloak library; the existing assembly name is retained for resolver compatibility. It packages the
complete publish directory under `tools/net10.0/any/realm-ready/`, including dependency assemblies, satellite
resources and runtime-specific subdirectories. The worker host must provide a compatible `Microsoft.NETCore.App` 10 runtime; see [.NET framework-dependent deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/).
The `any` directory describes the portable payload layout, and does not establish support on every platform.

The [build-transitive target](buildTransitive/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.targets) copies that tree
to `$(OutDir)appsurface-keycloak-realm-ready/`, preserving relative paths. It runs after `CopyFilesToOutputDirectory`
and fails with `ASKEYC022` before copying if the payload is empty or the worker DLL, dependency manifest or runtime
configuration is missing. Packing also rejects those missing inputs. Preserve the entire directory when moving an
AppHost output; copying only the worker DLL or the previous four-file payload can omit a dependency needed later.
The resolver's three-file existence check is not a complete dependency integrity check. Runtime-host failures such
as malformed configuration or an unavailable framework occur before worker entry and have .NET host diagnostics.
Framework-dependent `dotnet publish` output carries the same recursive worker directory beside the AppHost.
Keep it as an external directory when packaging an AppHost as a single file.

`BuildRealmReadyWorker` restores the worker and publishes into a fresh configuration/TFM-specific intermediate
directory. The parent pack build supplies the pure-library output first; the nested worker publish disables
project-reference builds to reuse that matching output. The worker no longer has a reference back to its adapter. Use a normal build-enabled pack after source
changes. A `--no-build` pack requires matching, previously built project-reference outputs.

The [#888 closure history](../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results) records the earlier
adapter-referencing payload. The [Approach B integration checkpoint](../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results)
records the current Aspire-free worker, moved baseline verifier, and internal configured adapter seam. The public
`RealmReady()` default remains the read-only public-client baseline. The internal seam validates typed same-resource
administrator/client bindings and stages them only after ownership at `OnBeforeStart`; it is not a committed public
configuration API. The default public registration's broader Run-policy/import migration remains an open gate.
The checkpoint includes separate real-server default-readiness controls for the extracted and consumer-copied
worker, alongside configured reconciliation/readback and required-seed lifecycle evidence. Both use empty runtime
caches and isolated payloads; these local proofs do not establish published-package adoption or complete default
registration migration.

The worker's optional `APPSURFACE_KEYCLOAK_CA_CERTIFICATE_FILE` is a nonsecret path to the exact expected server
certificate. The measured local self-signed profile pins SHA-256 with hostname and validity checks; without it,
platform TLS validation applies. It grants no remote-admin authority or general trust bypass. Configured mode uses
`--appsurface-keycloak-reconcile <desired.json> <private-bindings.json> <result.json>` internally; secret bytes stay
out of argv/environment and the safe OIDC projection. Worker result paths/codes and private-file checks are internal
and remain subject to the checkpoint's custody/platform/API proof limits. The later
[owned-adapter checkpoint](../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results) adds an internal same-daemon/
volume lease, declared resource binding, shared capture and recovery-only experiment. Its constructor/registration
remain effect-free; acquisition precedes private values and workloads. This macOS ARM64 stamped-volume profile
requires trusted ancestors and does not establish the proposed public ownership, migration or recovery contract.

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

Both registration overloads are local Run-only APIs and accept `Development`, `Test`, or `Testing` by default.
Registration now fails with `ASKEYC023` before writing the realm import or adding graph resources when Aspire is
publishing or the environment is outside that allowlist. This is a policy change for hosts that previously registered
the default proof in other operations or environments. Keep the call behind the host's Aspire Run-operation check;
publish hosts should configure their external identity dependency separately. A genuinely local custom environment can
be enabled with `options.AdditionalLocalEnvironmentNames.Add("LocalProof")`; that opt-in does not enable Publish.

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
| `ASKEYC022` | The finite realm-ready worker cannot be resolved, or its required package files are missing before copying. | Restore the complete AppHost package and rebuild. See [worker payload requirements](#realm-ready-worker-package-payload); pre-entry .NET runtime-host errors have separate diagnostics. |
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

## Captured security recipes

The shared [Keycloak recipe package](../ForgeTrust.AppSurface.Auth.Keycloak/README.md) captures explicit source/runtime/material pins, executes supported alignment/runtime/archive operations and inspects actual immutable images. It has no Aspire dependency. AppHost projects retain the local theme loop described above.

`AppSurfaceKeycloakThemeReleaseEvidence` remains a declaration tuple. `VerifyObservedImage` accepts only evidence returned by `KeycloakSecurity.InspectAsync` and checks that its actual platform manifest, platform, exact named-theme inventory, and every packaged theme file hash match this tuple; it does not convert a declaration into inspected evidence. Include the complete packaged theme and its reviewed baseline in the captured recipe, then inspect the final host-composed image. Scan, CLI, browser, upgrade/restore and authorized publication remain separate [release gates](../ForgeTrust.AppSurface.Auth.Keycloak/docs/api-and-contracts.md).

`VerifyObservedImage` accepts either a theme-only or security recipe observation when the immutable image identity, platform, exact named-theme inventory, and every expected theme member hash match this release tuple. This comparison establishes this tuple’s theme-file facts; it does not transfer the security recipe’s policy authority or turn a theme-only tuple into security release approval.

Release evidence publication uses the same internal create-if-absent filesystem implementation as the [captured recipe SDK](../ForgeTrust.AppSurface.Auth.Keycloak/docs/api-and-contracts.md). Concurrent writers to one absent path produce exactly one complete winner; every later writer receives a build-contract diagnostic. Staging and destination must be on the same local filesystem with hard-link support on Unix. Existing evidence remains immutable.

The [adversarial feasibility continuation](../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results) records the later
main-based dependency refresh, actual post-seed generic drift detection, and captured-file rejection at both
configured worker and real web startup readers. Internal `WorkerBoundaryFailure` results report a safe boundary
stage, exception type and elapsed seconds, with exit `70`; raw exception messages and captured bytes are excluded.
This diagnostic protocol remains internal. No new public seed sandbox or runtime reconciliation controller is added.

The later [neutral and pinned Skoolit configuration proof](../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results)
extends internal configured registration with optional typed provider-secret bindings. Registration snapshots and
validates same-AppHost secret parameters without resolving them; ownership precedes private staging and worker
execution. Public-client mode has no application-client capture. Confidential mode retains one protected capture
shared by the worker and real web OIDC startup reader. Exact callbacks/origins and generic pre-readiness mapper,
theme and provider ownership are explicit migration deltas for the Skoolit configuration fixture; this does not
establish actual Skoolit application adoption, real Google login or deployed tunnel acceptance.

The [real browser feasibility continuation](../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results) adds opt-in
owned Chromium login/logout acceptance for those isolated configuration fixtures. It retains empty-cache extracted
runtime closure, independent stop-before-clean observation and the separate 30-second worker/stop bounds. It
qualifies local browser behavior with a scoped certificate exception and locally routed public origins; real
provider, deployed tunnel, actual consumer adoption and coordinated publication remain separate acceptance gates.
