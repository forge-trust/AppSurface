# ForgeTrust.AppSurface.Durable.Templates

```text
dotnet new install ForgeTrust.AppSurface.Durable.Templates@0.2.0-preview.13
dotnet new appsurface-durable-worker -n FirstDurableWorker
dotnet test FirstDurableWorker --filter FullyQualifiedName~FirstDurableWork --logger "console;verbosity=detailed"
```

This public-preview .NET template package creates an application-owned .NET 10 ASP.NET Core host and test project for externally activated, PostgreSQL-backed Durable Work. It uses the native `dotnet new` engine, the stable short name `appsurface-durable-worker`, and source name `AppSurfaceDurableWorker`. It is installed with `dotnet new install`; do not add the template package as a runtime `PackageReference`.

Start with the [canonical Durable worker guide](../../start-here/durable-worker.md) for prerequisites, the four required proof checkpoints, route/authentication contracts, exact settings and limits, local-run setup, timing clocks, recovery, and support boundaries. This package README is the installation and shape reference; it does not replace the [external activation API contract](../external-activation-v1.md), [typed Work guide](../migrations/typed-work-definitions-v1.md), [schema and role operations](../heartbeat-retention-operations.md), or the [generated shape v1 upgrade guide](../../releases/durable-worker-template-shape-v1.md).

## Package and generated-project contract

- Requires the .NET 10 SDK. The `FirstDurableWork` test also requires a running Docker daemon that can run Linux containers and obtain the pinned PostgreSQL 16.5 image and package dependencies.
- The generated root contains the solution, `appsettings.json`, central `Directory.Packages.props`, `Directory.Build.props` and `Directory.Build.targets`, formatting and ignore files, host and test projects, README, and sample replacement guide. Native template name substitution changes solution/project names, references, assembly names, and namespaces consistently.
- The host directly references the coordinated `ForgeTrust.AppSurface.Durable`, `ForgeTrust.AppSurface.Durable.Provider`, `ForgeTrust.AppSurface.Durable.PostgreSql`, `ForgeTrust.AppSurface.Observability`, and `ForgeTrust.AppSurface.Workers` packages. It also references Npgsql and the OpenTelemetry SDK/OTLP hosting packages. It does not directly reference `ForgeTrust.AppSurface.Core`.
- The test project directly references the coordinated Durable, Provider, PostgreSQL, and Durable.Testing packages. Its pinned test graph includes the .NET test SDK, xUnit/VSTest, ASP.NET Core test hosting, TimeProvider testing, Testcontainers for PostgreSQL, Npgsql, OpenTelemetry, and coverage collection. The generated test project references the generated host project only; it has no AppSurface source-project or repository-test-project reference. AppSurface versions are coordinated; third-party dependencies are pinned.
- Generated projects restore from published/candidate package artifacts and contain no AppSurface source-project or repository-test-project reference. The template pack project itself has no runtime dependency on the Durable framework packages.
- The host remains passive and Work-only. It accepts typed Work through the separate producer path and exposes an authenticated empty wake. Normal startup validates authentication and read-only store compatibility; it applies no DDL or grants, initializes no epoch, and starts no background worker.
- The proof fixture owns its disposable schema, role, epoch, container, SDK exporter, and cleanup. A missing Docker prerequisite is a test failure, not a skipped success.

The current authored package graph and the exact v1 generated path inventory are recorded in the [versioned shape guide](../../releases/durable-worker-template-shape-v1.md). The template manifest is package metadata and is not copied into the generated application.

The command block is pinned to the current authored coordinated baseline `0.2.0-preview.13`, including the prerelease suffix. Install only a package version present in the selected feed; release verification must use the exact candidate template and matching generated package versions.

The four checkpoint lines are expected contract output, not captured output from this documentation change:

```text
[first-work] authorized activation accepted
[first-work] Work reached terminal completion
[first-work] readiness: NotStarted -> Healthy
[first-work] exported appsurface.durable.runtime.activation
```

`Completed` from the activation service does not prove all Work succeeded. The generated proof inspects persisted terminal Work, the Healthy readiness transition, and an activity actually exported after SDK flush. See the canonical guide for the full four-checkpoint meaning and for evidence that remains independently required.

For read-only checks of an already provisioned store, use the [Durable runtime doctor guide](../runtime-doctor.md) with a coordinated CLI/provider version that includes the command. Follow it with the application's composition verifier; a clean doctor result does not establish the four first-Work checkpoints.

## Generated source ownership

The generated files are application-owned after creation. An installed template update affects future projects only; it does not patch existing source. The output allowlist and exact v1 inventory are recorded in the [shape guide](../../releases/durable-worker-template-shape-v1.md). For new Work, replace the typed definition/executor, binding, and producer request in the three documented locations; retain the activation, authentication, and host plumbing.

For a continuously hosted worker use the PostgreSQL provider's explicit [`AddWorkerHost()` path](../ForgeTrust.AppSurface.Durable.PostgreSql/README.md#run-a-worker-host). For an existing custom HTTP host, compose the [provider-neutral external activation service](../external-activation-v1.md) directly. For provider or deployment operations, use the [Provider SPI](../ForgeTrust.AppSurface.Durable.Provider/README.md) and the canonical [role/preflight guide](../heartbeat-retention-operations.md). The template does not include the experimental ASP.NET adapter, production identity-provider configuration, a migration tool, or an installer outside `dotnet new`.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->
