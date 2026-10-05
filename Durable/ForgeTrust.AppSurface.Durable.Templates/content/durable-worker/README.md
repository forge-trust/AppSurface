# FirstDurableWorker

This generated .NET 10 solution is an application-owned, public-preview example of a PostgreSQL-backed Durable Work host with an authenticated external wake. It contains no AppSurface source checkout or repository-test-project dependency. You own the generated host, Work contracts, authentication, deployment, and future upgrades.

## Install, create, and run the first proof

The template package version below is the authored coordinated baseline in this source tree. Use the exact compatible version published to your selected feed, retaining the prerelease suffix:

```console
dotnet new install ForgeTrust.AppSurface.Durable.Templates@0.2.0-preview.13
dotnet new appsurface-durable-worker -n FirstDurableWorker
dotnet test FirstDurableWorker --filter FullyQualifiedName~FirstDurableWork --logger "console;verbosity=detailed"
```

The first command installs the native .NET template. The second creates this solution. The third runs the real PostgreSQL-backed proof. It requires the .NET 10 SDK and a running Docker daemon with Linux-container support. The test itself owns a disposable PostgreSQL 16.5 container, schema, role manifest, runtime epoch, SDK exporter, and cleanup; no manual database provisioning or host-installed `psql` is required for these commands. If Docker is unavailable, the test fails without emitting a successful checkpoint. Start Docker and rerun the third command.

Expected checkpoint lines are shown below. They are assertions required by the proof, not a transcript of a run already performed:

```text
[first-work] authorized activation accepted
[first-work] Work reached terminal completion
[first-work] readiness: NotStarted -> Healthy
[first-work] exported appsurface.durable.runtime.activation
```

The proof distinguishes request acceptance, persisted terminal success, readiness, and actual SDK export. `Completed` means the pump call returned; it does not establish that every Work item succeeded. The test reads the persisted Work/result and flushes the SDK before it reports success.

For the canonical settings and API reference, recovery instructions, independent verification gates, and public-preview limits, see the [Durable worker start guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/durable-worker.md). To replace the sample safely, follow [docs/replace-sample.md](docs/replace-sample.md).

The proof test namespace is fixed as `DurableWorkerTemplate.Tests`, independently of the generated application name. This keeps the documented `FullyQualifiedName~FirstDurableWork` filter limited to its Docker proof even when the project is named `FirstDurableWorker`; the separately configured native smoke is excluded. The friend-test assembly name still follows your application name.

## Run the host separately

The test fixture provisions only its disposable proof database. A normal run of this host never creates schema or roles and never initializes an epoch.

Every built host gets a new privacy-safe `template:<random GUID>` worker identity for its claims and heartbeat. Concurrent hosts have independent identities even within one process; a restart creates a new identity. The identity is runtime ownership metadata, not a credential or a stable application identifier. Existing heartbeat retention remains operator-owned; see the [Durable operations guide](https://github.com/forge-trust/AppSurface/blob/main/Durable/heartbeat-retention-operations.md).

1. Provision and verify a PostgreSQL database you control using the released [schema, role, and epoch procedure](https://github.com/forge-trust/AppSurface/blob/main/Durable/heartbeat-retention-operations.md). Follow the complete reviewed role manifest and the `work_only` dispatcher/runtime boundary.
2. Configure the host with `Durable:DispatcherConnectionString` and `Durable:RuntimeConnectionString` using separate restricted identities, plus `Durable:StoreId` and the nonempty active `Durable:RuntimeEpoch`. The `Durable:MaximumPoolSize`, `Durable:ConnectionTimeoutSeconds`, and `Durable:CommandTimeoutSeconds` defaults are 10, 5, and 5; overrides must remain positive and bounded. These keys are in the generated `appsettings.json`. Keep migration-owner and retention credentials out of the host.
3. For local development, run in `Development` and supply a fresh `APPSURFACE_TEMPLATE_ACTIVATION_TOKEN` through the process environment or a secret provider. This token has no default and is rejected outside Development. Production must configure a real ASP.NET authentication scheme and the named `ActivationAuthorization` policy; it must leave the sample token unset.
4. Configure `OpenTelemetry:OtlpEndpoint` when an external OTLP collector is available. Its default is empty. The automated first proof uses an in-process SDK exporter and does not require a collector. The default listen URL is `http://127.0.0.1:5080`; configure an appropriate address for the environment.
5. Start the app with `dotnet run --project src/FirstDurableWorker/FirstDurableWorker.csproj`. Check `/live`, `/compatibility`, and `/ready` independently. Stop the host before changing schema, grants, roles, or epoch.

The proof's 5-second default body-read deadline is independent of the 2-second pump discovery and 10-second cooperative service request budgets. See the start guide for all accepted bounds, body semantics, health status rules, production ownership, and the difference between this Docker proof and the all-OS native PostgreSQL smoke. A successful template test is not a production deployment certificate.

## Manual upgrades

Installing a newer template never changes this generated directory. Compare your application with the versioned [generated shape v1 inventory and manual upgrade checklist](https://github.com/forge-trust/AppSurface/blob/main/releases/durable-worker-template-shape-v1.md). Preserve application-owned edits, merge reviewed host or configuration changes manually, and keep all direct AppSurface package versions coordinated.

Changes to Work name/version, codecs, persisted results, provider safety, or retry policy are application data and rollout decisions. Do not rewrite records already accepted under an earlier Work identity. Review the [typed Work contract](https://github.com/forge-trust/AppSurface/blob/main/Durable/migrations/typed-work-definitions-v1.md) and [Work protocol](https://github.com/forge-trust/AppSurface/blob/main/Durable/work-protocol-v1.md) before changing them.
