# Start here: a Durable external-activation worker

Use the [Durable worker template](../Durable/ForgeTrust.AppSurface.Durable.Templates/README.md) when a new .NET 10 application needs a small ASP.NET Core host that accepts typed Durable Work separately and exposes an authenticated, payload-free HTTP wake. The generated host uses the PostgreSQL provider and remains passive: it does not start a worker loop, apply schema changes, or grant database privileges.

This is a public-preview path for a PostgreSQL-backed, externally activated, Work-only host. It is not a general workflow platform or production-support commitment. For a continuously hosted worker, use the explicit [`AddWorkerHost()` path](../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#run-a-worker-host). For an existing host, follow the [external activation contract](../Durable/external-activation-v1.md) and [reference host](../examples/durable-external-activation/README.md). For operator-owned schema and role work, start with the [Durable operations guide](../Durable/heartbeat-retention-operations.md).

## Install, create, and prove

Run these commands from a directory outside the AppSurface source checkout:

```console
dotnet new install ForgeTrust.AppSurface.Durable.Templates@0.2.0-preview.13
dotnet new appsurface-durable-worker -n FirstDurableWorker
dotnet test FirstDurableWorker --filter FullyQualifiedName~FirstDurableWork --logger "console;verbosity=detailed"
```

The install command uses the coordinated package version shown in this authored source baseline, including its prerelease suffix. Install a version that is available in the feed you selected and keep the template, generated host, and generated test dependencies on the same coordinated Durable preview version. The template is installed with `dotnet new install`; it is not a runtime `PackageReference`.

The first and second commands install and generate the template. The third runs a real PostgreSQL-backed test. The test fixture creates and owns a disposable database, roles, schema, and runtime epoch; it uses the pinned PostgreSQL 16.5 container image and the packaged `configure-postgresql-roles.sql` bytes. You do not provision a database for this proof. The proof requires the .NET 10 SDK and a running Docker daemon with permission to use Linux containers. A missing Docker daemon is a failed prerequisite, never a skipped test or successful proof. Start Docker and rerun the same test command.

Four checkpoint lines are expected, and each is emitted only after its assertion succeeds:

```text
[first-work] authorized activation accepted
[first-work] Work reached terminal completion
[first-work] readiness: NotStarted -> Healthy
[first-work] exported appsurface.durable.runtime.activation
```

These lines describe the required output, not a run captured from this documentation change. The test must first accept typed Work, authorize an empty wake, inspect the persisted terminal Work result, observe a real readiness transition, flush the SDK, and find the exported activity. A successful HTTP response, an accepted `Ready` record, a `Completed` pump aggregate, or an `ActivityListener` callback alone does not satisfy the proof.

## What the generated host owns

The generated solution is application-owned source. It has no reference to AppSurface source projects or repository test projects, and it can be restored and built outside this checkout using the pinned published package graph. Creating a project copies the current template shape; installing or updating the template never edits an already generated application.

The template keeps these responsibilities visible:

| Responsibility | Owner |
| --- | --- |
| Typed Work name/version, codecs, executor, retry and provider-safety choices, acceptance, and domain effects | Your application |
| HTTP routes, `ActivationAuthorization`, identity-provider scheme, claim and permission meaning, ingress limits, deployment enablement, and production health policy | Your host and deployment |
| PostgreSQL schema, StoreId, runtime epoch, migration-owner and retention operations, and the complete role manifest | Your operator/deployment process |
| Authoritative discovery, claims, leases, fences, execution state, and terminal bookkeeping | The configured Durable provider |
| Disposable database setup, proof credentials, lifecycle, export assertion, and cleanup | The generated integration-test fixture |

Normal host startup validates authentication composition and settings before creating restricted dispatcher/runtime data sources. It performs read-only schema, StoreId, and epoch validation before listening. It never applies DDL, changes grants or roles, initializes the epoch, or receives migration-owner or retention credentials. The dispatcher and runtime use separate restricted connections; the `work_only` role manifest provides database-enforced lane scope. Two connection strings alone do not create that isolation: follow the [canonical role recipe](../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#role-recipe-contract) and [schema-11 preflight](../Durable/heartbeat-retention-operations.md#complete-runtime-set-preflight-and-proof-checklist).

The wake route carries no Work, scope, lane, or effect identity in its body. Accept typed Work through the separate application-owned producer path, retain the acceptance receipt and authorized scope, then use the empty authenticated wake to ask the provider to attempt one Work-only pass. `Completed` describes a completed pump invocation and aggregate, not success of every Work item. Inspect persisted Work and effect evidence before deciding whether a retry is safe; Durable does not guarantee exactly-once external effects.

## Host routes and authorization

| Route | Contract |
| --- | --- |
| `GET /live` | Returns process liveness without querying PostgreSQL, including during a store outage. |
| `GET /compatibility` | Returns HTTP 200 only when the canonical activation assessment allows an attempt. A compatible `NotStarted` state may qualify. |
| `GET /ready` | Returns HTTP 200 only when the assessment is `Healthy` and `IsReady` is true. `NotStarted`, `Stale`, `Draining`, `Incompatible`, and `Unavailable` remain not ready. |
| `POST /private/durable/activate` | Requires authentication and the named `ActivationAuthorization` policy, then accepts an empty wake and calls `IDurableExternalActivationService` once with `DurableRuntimeSurface.Work`. |

Authentication and authorization run before body handling, health observation, or service resolution. The endpoint requires an authenticated principal even if the named policy is misconfigured as permissive. Production composition must register the policy and every selected or applicable default authentication scheme before the host listens. Missing policy or scheme fails startup before any database access. This presence check does not validate an identity provider or define the meaning of the application's claims and permissions. Startup validates settings, token environment, policy, and schemes first; it then acquires the restricted data sources, performs read-only schema/StoreId/epoch validation, and composes the passive provider, activation service, telemetry, and listener. The ten-second startup bound includes reaching the listening state. Partial source acquisitions are disposed on any earlier failure.

The built-in bearer scheme is only for the `Development` environment. It requires `APPSURFACE_TEMPLATE_ACTIVATION_TOKEN`, has no default, accepts a nonblank value of at most 256 UTF-8 bytes, and rejects malformed, repeated, missing, or over-limit token input. It is rejected in every non-Development environment, even when a production scheme is configured. Never commit a token or put it in generated `appsettings.json`; inject a fresh local value through a process or secret provider. The proof fixture uses per-builder configuration and does not mutate the parent process environment.

The wake body must be empty. After authorization, a known nonempty body is rejected without reading it. For an unknown-length body, the host reads at most one byte: timely end-of-stream is an empty wake, while any byte is rejected. `DurableActivation:BodyReadBudgetSeconds` bounds that pre-service read. If this deadline wins, the host returns the bounded `WakeBodyReadTimeout`/504 response and makes no Durable health, admission, or activation-service call. Caller abort and transport read failure retain their separate host-failure semantics; the host does not invent a response after an aborted transport.

The canonical [external activation reference](../Durable/external-activation-v1.md#closed-outcomes-and-host-status-mapping) owns all ten service outcomes, their DTO fields, cancellation classification, and recovery rules. The generated HTTP layer maps those outcomes to its documented status and safe response projection. Its service budget is cooperative and does not preempt an arbitrary executor or include terminal bookkeeping. The pump discovery budget, service request budget, HTTP ingress/body deadline, execution, and provider terminal cleanup are distinct clocks.

## Settings and defaults

The activation settings belong to the generated host and become one application-owned seam; they do not add a configuration API to AppSurface. Invalid values fail before listening. The following keys use invariant integer seconds or counts:

| Setting | Default | Valid value | Meaning |
| --- | ---: | --- | --- |
| `DurableActivation:PumpMaximumItems` | 32 | Integer 1–10,000 | Maximum items selected in one pump pass. The request cannot choose this value. |
| `DurableActivation:PumpDiscoveryBudgetSeconds` | 2 | Integer 1–300 | Budget for provider discovery, independent of the service request budget. |
| `DurableActivation:RequestBudgetSeconds` | 10 | Positive integer through 4,294,967 | Cooperative budget for the activation service call. It does not promise executor preemption or cap provider terminal bookkeeping. |
| `DurableActivation:BodyReadBudgetSeconds` | 5 | Integer 1–300 | Separate total deadline for the authorized unknown-length empty-body check; it is disposed before service invocation. |
| `APPSURFACE_TEMPLATE_ACTIVATION_TOKEN` | None | Development only; 1–256 UTF-8 bytes | Development bearer credential. It is never a production fallback. |

The generated `appsettings.json` exposes these application-owned connection, database identity, and exporter settings:

| Key | Default | Meaning |
| --- | --- | --- |
| `Durable:DispatcherConnectionString` | Empty | Restricted Work-only dispatcher connection used for payload-free discovery. |
| `Durable:RuntimeConnectionString` | Empty | Separate restricted runtime connection for provider mutations and execution. |
| `Durable:StoreId` | Empty | Identity of the explicitly provisioned Durable store. |
| `Durable:RuntimeEpoch` | Empty | Nonempty active epoch initialized by the operator before host startup. |
| `Durable:MaximumPoolSize` | 10 | Maximum connections in each owned restricted pool. Overrides must remain positive and bounded. |
| `Durable:ConnectionTimeoutSeconds` | 5 | Connection timeout in seconds; overrides must remain positive and bounded. |
| `Durable:CommandTimeoutSeconds` | 5 | Command timeout in seconds; overrides must remain positive and bounded. |
| `OpenTelemetry:OtlpEndpoint` | Empty | Optional host-owned OTLP endpoint. The first-Work test uses an in-process SDK exporter. |
| `urls` | `http://127.0.0.1:5080` | Local HTTP listen URL; configure a deployment-appropriate address when hosting outside local development. |

Any connection-setting override belongs to the application/operator that owns the connection. Supply separate dispatcher and runtime credentials, the provisioned StoreId, and a nonempty active runtime epoch. Do not supply provisioning credentials to the host. The checked-in settings file contains empty connection/epoch values and no working credential or bearer token. Use a secret store or process environment for credentials rather than committing them.

The reference defaults of 32 items, a two-second discovery budget, and a ten-second service request budget come from the public activation contract. The body-read setting is a host-owned ingress control; changing it affects only the pre-service read phase, never discovery or service budgets. The service has no internal retry loop, second execution semaphore, distributed lock, readiness cache, or queue. Per-request budgets and connection-pool limits do not guarantee transport-level overload protection; production adopters own ingress concurrency and capacity.

## Choose an alternative when it fits better

- Prefer the [typed Work definition path](../Durable/migrations/typed-work-definitions-v1.md) for reusable Work whose name/version, codecs, provider safety, and retry policy should be shared by registration and acceptance. A low-level `DurableWorkRequest` is appropriate when an application deliberately owns and validates those contract facts at each call site.
- Call `IDurableRuntimePump` directly for a scheduled or application-controlled pass when the caller already owns health observation, admission policy, cancellation, and result handling. Use `IDurableExternalActivationService` when the shared health/admission checks, cooperative request budget, closed result family, and standard activity are the needed boundary.
- Keep an existing custom route when the host already owns authentication, body policy, status/DTO mapping, ingress limits, and deployment enablement. Compose the external activation service behind that route; do not place Work selection or caller-supplied pump limits in its payload.
- Keep the host's existing OpenTelemetry SDK/exporter when it owns collection and delivery. The generated host supports OTLP configuration, while the proof uses an isolated in-process exporter so a collector is not a hidden prerequisite.
- Choose the explicit PostgreSQL `AddWorkerHost()` route for continuous polling. Choose the [operator and schema guides](../Durable/README.md#slice-7-discovery-and-reconciliation) for migrations, role setup, preflight, and deployment rather than putting those responsibilities in an application template.

## Run the host outside the proof fixture

The three-command test owns disposable provisioning. A normal generated host does not. To run the host itself:

1. Follow the [PostgreSQL schema, role, and epoch procedure](../Durable/heartbeat-retention-operations.md) for a database you control. Install the generated application's exact matching Durable/PostgreSQL preview packages, apply reviewed migrations and the complete role manifest out of band, and record StoreId and active runtime epoch.
2. Configure only the distinct restricted dispatcher/runtime connections, StoreId, and epoch named in the generated `appsettings.json`. Do not use the migration-owner or retention-operator identities for runtime access.
3. In local `Development`, set a fresh `APPSURFACE_TEMPLATE_ACTIVATION_TOKEN` outside the repository. Configure the host's own exporter settings if you want OTLP delivery; the three-command test uses an in-process SDK exporter and needs no collector.
4. Run the generated host with `dotnet run --project src/FirstDurableWorker/FirstDurableWorker.csproj`. Check `/live`, `/compatibility`, and `/ready` separately. Stop and dispose the host before changing schema, roles, or epoch.

The exact connection-setting names and exporter configuration are part of the generated source and `appsettings.json`; do not infer them from the test fixture. A production deployment must replace Development authentication with its real registered scheme and policy, keep the sample token unset, and use its own transport, deployment gate, and operator runbook. See the [reference host configuration](../examples/durable-external-activation/README.md#configuration-contract) for the related public contracts.

## Bounds for correctness and timing evidence

These are observation limits for test and evidence workflows, not claims about measured performance:

| Phase | Bound |
| --- | ---: |
| Docker availability check | 5 seconds |
| Pull a missing pinned image in a cold run | 300 seconds |
| Readiness, schema, migrations, epoch, role setup, and verification after acquisition | 40 seconds |
| Ordinary host startup | 10 seconds |
| First-Work lifecycle and terminal observation | 25 seconds, polling the single record every 100 ms |
| SDK flush | 5 seconds |
| All independently owned cleanup | 20 seconds total; child-tree termination is bounded to 10 seconds within it |
| Exact-package install/create/format/test commands | 180 seconds |
| Exact-package restore/build commands | 300 seconds |
| Ordinary native PostgreSQL initialization and provisioning on each OS | 90 seconds |
| Native local-host observation | 5 seconds |

The blocking primed timing workload is exactly the three commands above. Verify the pinned image and hashed pinned package closure before starting; do not purge shared caches. Five independent serial samples use unique projects/databases and the .NET 10 SDK, Docker, pinned image, and required NuGet artifacts already available. The install/create/restore/build group is capped at 55 seconds. The root clock includes install, creation, restore/build, disposable setup, lifecycle/export proof, command completion, and cleanup; every sample must finish in less than 180 seconds and the median must be less than 120 seconds. Retain every attempt; one failed or timed-out sample invalidates that full timing series.

The cold diagnostic uses five independent serial runs with an isolated workflow daemon where the PostgreSQL 16.5 image is absent and a fresh local NuGet cache. Report cache provenance, all durations, median, and nearest-rank p95. Its root watchdog is 900 seconds, after which the sample is failed and bounded cleanup still runs. For five samples, median is sorted sample 3 and nearest-rank p95 is sorted sample 5 (the maximum). There is no cold product SLO. Do not relabel a warm cache as cold or discard a failed attempt.

Correctness on the candidate/local feed is separate from replay against the promoted public package. Native PostgreSQL setup for the ordinary generated executable smoke on Linux, macOS, and Windows is also separate from the unchanged Docker-backed `FirstDurableWork` proof. A native smoke cannot satisfy its container-pinned recipe-byte and lifecycle assertions.

## Recovery and release evidence

- **Docker unavailable:** `FirstDurableWork` fails its prerequisite and emits no successful checkpoint. Start a Docker daemon with Linux containers and rerun the exact third command above.
- **Authentication composition invalid:** startup fails before connecting to PostgreSQL or listening. Register the named `ActivationAuthorization` policy and its real scheme/default in the application composition seam, then rerun the test command. Use the Development token path only in local Development.
- **Packaged role SQL missing:** the generated proof fails instead of reconstructing SQL. Confirm the generated test directly references the matching PostgreSQL package and that `configure-postgresql-roles.sql` is present in its output; rerun the third command. If the exact package still omits the asset, treat it as a package defect and retain the failed proof.

These are recovery instructions, not captured runtime messages. A successful local test is not a publication certificate. The expected schema-v1 receipt records source revision; exact template/provider IDs, versions, and hashes; SDK, OS, and image inputs; cache and clock mode; command identifiers; per-phase elapsed durations; exit/result status; all four checkpoint assertion booleans; and owned cleanup completion. This describes the receipt expected from implementation and is not a receipt from a completed run. Release evidence must bind the generated graph/content identity as well. Missing, failed, truncated, stale, or mismatched evidence withholds success.

The package-consumer proof owns unique CLI-home, package-cache, generated-output, and evidence roots outside the source checkout. It uninstalls the exact-package candidate and proves the expected short name is absent before testing installation through its isolated feed. Cleanup applies only to those positively owned roots; it never clears shared user caches or deletes the source checkout. A preinstalled template, parent NuGet configuration, remote feed, or project reference cannot satisfy a missing artifact.

The ordinary executable smoke has a separate private native PostgreSQL cluster on each required CI operating system. Ubuntu resolves one matching PostgreSQL 16 tool directory under `/usr/lib/postgresql/16/bin`. macOS installs/resolves the `postgresql@16` Homebrew keg in the disposable job without starting a Homebrew service or using its default data directory. Windows resolves the runner's native `PGBIN` tools without starting the preconfigured service or using `PGDATA`. Each job verifies that `initdb`, `postgres`, `pg_ctl`, and `psql` come from the same installation and records the actual server version, which must be PostgreSQL 16 or newer. Missing tools or fixture setup fail the required smoke; there is no fake-provider fallback. This native-cluster smoke still cannot replace the pinned-container first-Work proof.

Keep the following obligations independent:

- The original outside-checkout human trial runs after exact-package generation and the real first-Work proof. The developer receives no missing steps or live coaching; record start/end, package and cache provenance, phases, checkpoint output, first misunderstood instruction, and any help supplied. Owner/scheduling coordination for this trial and the separate adopter certificate remains pending; neither result is claimed here.
- The representative downstream Skoolit adopter certificate is separate from template correctness.
- [#801](https://github.com/forge-trust/AppSurface/issues/801) remains open for the Durable doctor-output cross-link. This guide records the canonical URL and obligation; the #801 owner must land the link or record an explicit case-owner agreement before #806 closes. The portable verification/retention work tracked by [#798](https://github.com/forge-trust/AppSurface/issues/798) is a separate disposition; a generated test cannot satisfy either item.
- Five-run timing evidence and the all-OS native local-run matrix are separate gates. No timing, human, adopter, doctor, candidate, or OS result is claimed by this guide.

For later generated projects, use the [shape v1 inventory and manual upgrade checklist](../releases/durable-worker-template-shape-v1.md). For existing Durable consumers, start at the [package chooser](../packages/README.md), the [typed Work guide](../Durable/migrations/typed-work-definitions-v1.md), the [Durable package family guide](../Durable/README.md), and the [Durable diagnostics catalog](../troubleshooting/durable-diagnostics.md).
