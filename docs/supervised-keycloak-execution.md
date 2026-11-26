# Supervised local Keycloak execution candidate

This page describes the current local candidate for running a required seed and one web workload with AppSurface Keycloak. The candidate entry point is the normal `AddAppSurfaceKeycloak` overload with typed credentials and `SupervisedExecution`; it is not a publication or supported-platform declaration. Both `ForgeTrust.AppSurface.Auth.Keycloak` and `ForgeTrust.AppSurface.Auth.Aspire.Keycloak` remain publication-held pending explicit coordinated release authorization. No real downstream consumer adoption, external identity-provider/broker adoption, or tunnel adoption is established by this profile.

The approved [#888 identity-readiness plan](plans/issue-888-keycloak-identity-readiness.md) and the [owner-lifetime custody and supervised-composition checkpoint](plans/issue-888-keycloak-identity-readiness.md#feasibility-results) remain the decision and evidence records. The owner-lifetime checkpoint covers its recorded composition proof; it does not establish the outcome of the separate normal adapter and persistent-volume matrix. The [normal adapter checkpoint](plans/issue-888-keycloak-identity-readiness.md#feasibility-results) records its final eleven-case result on **November 17, 2026** and the remaining parent-crash and acceptance gates. The [supervised-boundary amendment](plans/issue-888-keycloak-identity-readiness.md#feasibility-results) authorizes exploring this execution boundary, not publication or downstream adoption.

## Candidate boundary

The current profile is specifically a **macOS ARM64 AppHost with a Linux ARM64 Docker environment**. The .NET runtime and Keycloak images are digest-pinned. Other host operating systems, host architectures, Docker architectures, remote daemons, and image revisions are outside this candidate’s qualification boundary. A source-level macOS check does not establish ARM64 support by itself.

Treat this as a provisional qualification profile for this supervised fixture only. It does not change the existing local default policy or qualify legacy registration paths. The default remains the disposable public-client local proof; confidential identity, persistent volume ownership, required identity seeding, or supervised composition must be selected explicitly. The [Auth adoption ladder](../start-here/auth-adoption-ladder.md) helps choose between this candidate, DevAuth, Auth.Testing, and host-configured OIDC.

Aspire still has explicit-start resource handles for Keycloak readiness, seed completion, and web diagnostics. The package publishes their observed states into the Aspire resource graph. It creates and supervises the actual containers through the selected local Docker socket; DCP is not the executor for these workloads. The placeholder executable resources are handles for ordering and observation, not commands for DCP to launch as the workload.

The profile supports public PKCE and confidential Keycloak clients. Set `RequireIdentitySeed = true` and clear package-provisioned seeded users. Register one or more finite required seed workloads ahead of exactly one web workload. The implementation permits at most eight seed/web workload handles in total. Use the typed supervised overload and keep extra host `ProjectResource`, container, and executable workloads out of this AppHost.

## Register and run

The supervised overload is declared on [`AppSurfaceKeycloakHostingExtensions`](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/AppSurfaceKeycloakHostingExtensions.cs):

```csharp
AddAppSurfaceKeycloak(
    string name,
    IResourceBuilder<ParameterResource> adminUsername,
    IResourceBuilder<ParameterResource> adminPassword,
    AppSurfaceKeycloakCredentialBindings credentials,
    Action<AppSurfaceKeycloakOptions> configure)
```

It returns the ordinary [`AppSurfaceKeycloakResource`](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/AppSurfaceKeycloakResource.cs). Its `SupervisedRun` property exposes the `AppSurfaceKeycloakSupervisedRun` helper for this candidate. Registration snapshots declarations and typed parameter identities; it does not resolve credential values or create run directories, imports, containers, or volumes. Credential values are resolved only after the run has acquired its owner lease.

This example assumes `builder`, `payloadDirectory`, and `volumeName` are supplied by the local AppHost. The payload directory must contain the complete portable publish closure for both assemblies. The seed and web arguments shown are nonsecret paths understood by those consumer programs.

```csharp
using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak;

var adminUsername = builder.AddParameter("keycloak-admin-user", "local-admin");
var adminPassword = builder.AddParameter("keycloak-admin-password", secret: true);
var clientSecret = builder.AddParameter("keycloak-client-secret", secret: true);
var seedPassword = builder.AddParameter("fixture-user-password", secret: true);

var execution = new AppSurfaceKeycloakSupervisedOptions();
var keycloak = builder.AddAppSurfaceKeycloak(
    "identity",
    adminUsername,
    adminPassword,
    new AppSurfaceKeycloakCredentialBindings { ClientSecret = clientSecret },
    options =>
    {
        options.Realm = "local-proof";
        options.ClientId = "local-web";
        options.ClientMode = KeycloakClientMode.Confidential;
        options.RequireIdentitySeed = true;
        options.SeededUsers.Clear();
        options.PersistentVolumeName = volumeName;
        options.SupervisedExecution = execution;
    });

var run = keycloak.SupervisedRun!;
run.WithPublicInputs(new Dictionary<string, string>
{
    ["FixtureLabel"] = "local"
});

run.WithSeed(new AppSurfaceKeycloakSupervisedWorkload(
    "identity-seed", payloadDirectory, "IdentitySeed.dll",
    ["/proof/public/config.json", "/proof/private/input.json"])
{
    PrivateInputs = new Dictionary<string, IResourceBuilder<ParameterResource>>
    {
        ["AdminUsername"] = adminUsername,
        ["AdminPassword"] = adminPassword,
        ["UserPassword"] = seedPassword
    }.ToImmutableDictionary()
});

run.WithWeb(new AppSurfaceKeycloakSupervisedWorkload(
    "web-proof", payloadDirectory, "WebProof.dll",
    ["/proof/public/config.json"]));

var app = builder.Build();
var result = await run.RunAsync(app, cancellationToken);
return result.ExitCode;
```

`WithPublicInputs`, `WithSeed`, and `WithWeb` return the supervised helper or an Aspire executable resource handle. Register all inputs and workloads before the AppHost starts. `WithSeed` adds a required finite workload to the completion chain; each seed must exit successfully before the next starts. `WithWeb` adds the single web workload after the last seed. The web receives public configuration and the public certificate/reference files, but it cannot receive `PrivateInputs`. Seed credentials belong in same-AppHost typed parameter bindings, never in public inputs, command arguments, or ordinary environment settings. Keep `Environment` values nonsecret as well.

Call `RunAsync` with the `DistributedApplication` built from the same builder and return its `ExitCode` from the AppHost entry point. It starts the app and remains active until cancellation, host shutdown, or a required failure. It then owns teardown and returns a process-level result. Do not replace this call with `app.RunAsync()` when the caller needs the supervised teardown result. `ActiveRetained` reports whether the active owner marker remains; `OwnerId` identifies the run but does not by itself authorize recovery.

## Defaults and constraints

[`AppSurfaceKeycloakSupervisedOptions`](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/AppSurfaceKeycloakSupervisedOptions.cs) supplies these defaults:

| Option | Default | Constraint or purpose |
| --- | --- | --- |
| `LeaseRoot` | `LocalApplicationData/AppSurface/Keycloak/leases` | Shared private owner state. Keep the resolved path stable across sibling worktrees and recovery attempts. |
| `RunRoot` | `Path.GetTempPath()/appsurface-keycloak-runs` | Private parent for per-owner directories at `RunRoot/OwnerId`. Keep it stable across worktrees and recovery attempts. |
| `DockerEndpoint` | `unix://<UserProfile>/.docker/run/docker.sock` | Local Unix socket. The candidate accepts this user socket or `unix:///var/run/docker.sock`; it does not accept an arbitrary remote endpoint. |
| `RuntimeImage` | `mcr.microsoft.com/dotnet/sdk@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b` | Pinned Linux ARM64 runtime image. |
| `KeycloakImage` | `quay.io/keycloak/keycloak@sha256:831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669` | Pinned Keycloak image for this profile. |
| `TransportReadyTimeout` | 120 seconds | Must be greater than zero and at most 10 minutes. |
| `LocalSeedTimeout` | 120 seconds per seed | Must be greater than zero and at most 10 minutes. |

Both paths must be fully qualified and cannot contain control characters or commas. Both image references must use a lowercase 64-character SHA-256 digest. The Docker endpoint, lease root, run root, image digests, and exact physical volume name are part of the owner/recovery context; keep them unchanged when recovering a retained owner.

Each [`AppSurfaceKeycloakSupervisedWorkload`](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/AppSurfaceKeycloakSupervisedOptions.cs) uses an absolute payload directory and a simple `.dll` assembly filename. Payload files are copied after ownership acquisition; symlinks/reparse points are rejected and the copy is bounded to 4,096 entries. Arguments are nonsecret, bounded to 32 entries, and each must be at most 4,096 characters. Nonsecret environment has bounded names and values. Workload names must be unique lowercase Docker-style names from 3 to 63 characters. `PrivateInputs` are typed parameters from the same AppHost; secret parameters must be secret, except the same registered admin username may be bound under `AdminUsername`.

`WithSeed` and `WithWeb` reject the reserved names `anchor`, `keycloak`, `worker`, `observation`, `initialization`, `initialize`, and `observe-` followed by a 32-digit hexadecimal GUID. These names belong to package-owned containers or their recorded roles. Use a workload-specific name such as `required-seed` or `app-web`.

`WithPublicInputs` accepts at most 32 string fields, with keys up to 128 characters and values up to 4,096 characters. It cannot replace fields generated for identity, paths, or transport policy. Before publishing these fields, the owner rejects keys or values containing any resolved administrator password, client secret, or provider secret. This guard does not classify arbitrary consumer text or replace the consumer's input-hygiene obligations.

The required seed and secret administrator password apply to both client modes. For `Public`, leave `ClientSecret` unset in the typed bindings. The generated public workload configuration has `ClientMode` set to `"Public"` and `ReferencePath` set to null. Configure the web OIDC handler for PKCE without a client secret. For `Confidential`, supply a same-AppHost typed client-secret parameter. The configuration has `ClientMode` set to `"Confidential"` and a nonsecret `ReferencePath` to the sealed credential reference shared by the worker and web. In either mode, enabled identity-provider aliases require exactly matching same-AppHost typed secret bindings.

The registration overload is allowed only during Aspire `Run` in `Development`, `Test`, or `Testing` by default. Additional local environment names must be explicitly configured. This is a local proof path; it is not a production host or unattended deployment mode.

Required seeds must preserve the generic realm, client, mapper and broker settings established by the readiness
worker. Consumer acceptance can compare those settings after the seeds and before releasing the web workload.
The normal adapter fixture exercises that comparison against Keycloak's Admin API using a separate read-only
observer. The proof measures state at those two points and does not grant authority to repair seed changes.
See the [post-seed and recovery checkpoint](plans/issue-888-keycloak-identity-readiness.md#feasibility-results).
The [normal adapter browser checkpoint](plans/issue-888-keycloak-identity-readiness.md#feasibility-results) exercises this ordering
with the neutral public client and the confidential Skoolit configuration on loopback.

## Legacy volume migration

The supervised typed overload supports `UsePersistentDataVolume = true` with no explicit name only when the exact
volume produced by Aspire's `WithDataVolume()` naming logic already exists. Aspire includes the AppHost path in
that name. Check the old app's exact volume before moving the AppHost. If the generated volume cannot be established,
startup fails with `ASKEYC028` before bootstrap or credential resolution. Set `PersistentVolumeName` to the existing
name to keep it after a move. Set a new explicit name when you intend to create a volume. Both paths use the same
daemon-and-volume lease. Keep persistent data and use [guarded recovery](#shutdown-and-guarded-recovery) for a retained owner.

For the pinned Keycloak 26.7.2 image, a read-only observer checks the volume root and the existing H2 data
file under the volume lease. The adapter uses `1000:0` when the root and database have that owner, or when
the root has that owner and the database is absent. The volume root can have the image’s UID even when the
host UID created the database. That case uses the host UID so Keycloak can reopen the existing data file.
The adapter leaves existing ownership and modes on disk alone. Only Keycloak
receives the read-only server input mount. Its files are readable by the container UID, and an owner-only
host run directory protects them on the host. The active record binds this server identity before create so
that normal stop and guarded recovery verify the same identity. The caller must supply the existing realm's
working typed administrator credentials. This path applies to the qualified pinned image and root ownership
pair. See the [volume-root identity contract](local-keycloak-volume-identity.md).

## Local theme edits and approved HTTPS issuers

To try a live theme, set [`LoginTheme`](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/AppSurfaceKeycloakThemeOptions.cs)
to a validated source with `Platform = "linux/arm64"` and the supervised Keycloak image's exact repository and
digest. Use a live source directory the operator can maintain throughout the run. The adapter mounts it read-only
and disables Keycloak's theme template, theme, and static-resource caches. The source is validated at registration
and remains editable during the run. The initial manifest digest identifies the validated source snapshot. The
existing [exact-image theme release proof](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#theme-quickstart)
keeps its own validation rules.

For a public-looking local OIDC authority, add its exact HTTPS origin to `ApprovedHttpsOrigins`, set `Authority`
to its `/realms/<realm>` URI, and declare the corresponding exact callback, signed-out callback, and web origins.
Keycloak advertises the configured browser issuer. The reconciler and administrator probes use the owned
`https://localhost:<KeycloakPort>` transport with the generated leaf pin. Configure the web workload and browser
with the local route and trust needed to reach the exact issuer. Origin approval is a URI declaration and supplies
neither a route nor a trust certificate. See the [combined fixture proof](plans/issue-888-keycloak-identity-readiness.md#feasibility-results).

## Shutdown and guarded recovery

Normal shutdown through `RunAsync` uses one fixed **30-second shared deadline** for stopping/fencing the owned namespace, waiting for bounded work to end, verifying every recorded container and (when configured) the persistent volume identity, removing verified stopped containers and private-input directories, and clearing the active marker last. The timer is shared across these phases; one slow phase does not receive a fresh 30 seconds. A successful stop command alone is not proof that owned work is stopped.

Private-directory cleanup checks the deadline before listing the directories and before each deletion. A synchronous filesystem call already in progress can outlast the timer. The owner lease stays held through the call, and an overrun fails shutdown with the active marker retained.

The adapter creates each owner-specific run directory exclusively. A path that already exists stays intact. If the directory acquired by the current run is replaced before cleanup, shutdown fails with `ASKEYC034` and retains the active marker for inspection.

The AppHost preserves the first failure reported before shutdown begins. Caller cancellation during preparation returns `ASKEYC032` after verified cleanup. If owned shutdown cannot be verified, `RunAsync` returns `ASKEYC034` in place of the earlier failure code.

The candidate now binds the anchor to one AppHost-owned TCP heartbeat connection, published only on an exact
loopback port. A nonsecret listener-ready file precedes connection setup. The initial accept plus exact GUID frame
share an eight-second deadline, and no second owner connection is accepted. A confidential anchor publishes the
sealed capture before acknowledging. A public anchor runs the same fence without a client input mount or credential
reference. AppHost records `OwnerFenceEstablished` only after receiving the acknowledgement. It sends a
33-byte run-id frame every second. EOF, invalid frames or a missing frame for eight seconds causes the anchor to exit,
which terminates members of its PID namespace, including detached session children. A twelve-second initial timer
covers pre-acknowledgement capture/publication; an atomic transition arbitrates timer expiry versus protocol handoff.
These bounds start at worker entry/listener setup; the external proof measures whole namespace stop from AppHost loss.
See the [AppHost-loss checkpoint](plans/issue-888-keycloak-identity-readiness.md#feasibility-results) for actual results and remaining
qualification limits. Do not infer that a retained marker means the containers have already stopped.

`LaunchUncertain` is persisted before create/start daemon commands and cleared only after an acknowledged response
and a successful marker update. An interrupted response or failed marker update leaves the AppHost using the prior
ownership snapshot. It can still fence a known anchor using the observed container identity. Neither controlled cleanup
nor recovery clears that reservation from an instantaneous absent/stopped census: the daemon-side operation may still
commit later. Marker schema 3 also
requires the established owner fence when an anchor is recorded; historical schemas are rejected conservatively.
Unknown in-flight operations and unsupported failure windows retain state for further investigation rather than
granting a manual clear or replay. The no-reconnect heartbeat assumes the qualified host and daemon; it is not
an authentication mechanism against either principal or protection from a frozen/failed Docker VM.

If stop, inventory, volume identity, cleanup, or the deadline is uncertain, `RunAsync` returns nonzero and retains the active marker. Do not delete that marker or its lock files by hand. The retained record blocks another run from taking the same owner key. On a persistent-volume run, the exact Docker volume name and returned `OwnerId` are inputs to guarded recovery.

Run [`AppSurfaceKeycloakSupervisedRun.VerifyStoppedAndClearAsync`](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/AppSurfaceKeycloakSupervisedRun.cs) in a separate recovery-only branch **before creating the normal Aspire builder or registering resources**:

```csharp
var recovery = await AppSurfaceKeycloakSupervisedRun.VerifyStoppedAndClearAsync(
    execution,
    exactRecordedVolumeName,
    exactExpectedOwnerId,
    cancellationToken);

if (recovery.Kind != "RecoveryOnlyCompleted" || recovery.WorkloadsStarted || !recovery.Cleared)
    return 1;

return 0; // Exit. Remove the one-shot recovery request before a normal run.
```

The call needs the same Docker socket, shared `LeaseRoot`, stable `RunRoot`, pinned runtime profile, exact physical volume name, and exact expected owner ID as the retained record. The run directory must still pass private-directory validation. Recovery validates the recorded AppHost process incarnation and container roster before observing the volume. Malformed active records fail with `ASKEYC033` and remain available for investigation. Recovery has its own bounded 30-second verification-and-clear attempt. It does not resolve admin, client, provider, or seed credentials and it never continues into ordinary startup. Its success result has `Kind == "RecoveryOnlyCompleted"`, `WorkloadsStarted == false`, and `Cleared == true`; a stale request, identity mismatch, unavailable inventory, cancellation, or timeout fails without authorizing a clear.

Recovery performs metadata-only Docker inventory and may launch a short-lived package observer container to read the mounted persistent-volume root stamp. That observer uses a read-only mount at `/volume`; the [`volume-root identity contract`](local-keycloak-volume-identity.md) describes the `statx` stamp and its caller-side requirements. Recovery does **not** issue application/workload `start`, `stop`, or `kill` operations. If observation cannot establish the exact stopped owner and unchanged volume identity, it leaves the marker active. Recovery clears only the marker after verification; it is not a general stale-container cleanup or data-reset command, and it cannot stop containers still running after an unqualified failure or uncertain daemon operation.

Recovery journals the observer's launch under the held lease. After verifying the observer stopped and removing it,
recovery restores the original active record bytes before deciding whether to clear. If observer completion is
uncertain, its intent remains in the record. A missing or replaced volume blocks clear. The lease rechecks both the
active file identity and its bytes before clear, so an edit during verification or an identical-byte file replacement
can invalidate the verified record. Keep the active marker and the volume in place after a refusal.

Marker clearance and volume availability are separate results. A fresh run checks the volume's attachments,
including stopped containers, and rejects an unexpected attachment. Retain the recorded ownership details for
diagnosing a blocked restart after recovery.

## Trust and qualification limits

The lease and Docker observations assume a trusted same-user account, host root/administrators, and selected Docker daemon. They are not isolation from those principals. Process, container, mount, and metadata checks are bounded observations of a changing local system; they cannot revoke a value already read or prevent a trusted principal from changing state after an observation. The volume-root stamp is supporting replacement evidence, not a clock-derived generation label or anti-root guarantee. It must be combined with exact daemon, volume, mountpoint, read-only observer mount, and lease checks.

This remains a local candidate only. The proof described by the linked records does not authorize package publication or establish actual Skoolit adoption. The legacy `AddAppSurfaceKeycloak` paths and their existing host-owned seed/resource arrangements are **not qualified by this supervised-execution proof**. Keep their evidence and behavior separate; do not transfer this candidate’s stop, recovery, or platform claims to them. See the package [README](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md) for the established legacy local proof and seed APIs.
