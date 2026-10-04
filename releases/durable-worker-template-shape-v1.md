# Durable worker template generated shape v1

This is the initial versioned baseline for projects created by `appsurface-durable-worker`. It records the exact generated file inventory and the manual review needed when a later template release changes that shape. There is no earlier template shape to migrate from. The v1 inventory is the predecessor for every later before/after guide.

The generated application is owned by its adopter. Installing or updating the template affects future projects only; it does not change any file in an existing generated directory.

## Package compatibility

The following coordinated version is the authored baseline in this source tree. A released template and all direct Durable package references must use one exact coordinated preview version; keep prerelease labels intact.

| Consumer | Package | Version in authored baseline |
| --- | --- | --- |
| `dotnet new` template | `ForgeTrust.AppSurface.Durable.Templates` | `0.2.0-preview.13` |
| Generated host | `ForgeTrust.AppSurface.Durable` | `0.2.0-preview.13` |
| Generated host | `ForgeTrust.AppSurface.Durable.Provider` | `0.2.0-preview.13` |
| Generated host | `ForgeTrust.AppSurface.Durable.PostgreSql` | `0.2.0-preview.13` |
| Generated host | `ForgeTrust.AppSurface.Observability` | `0.2.0-preview.13` |
| Generated host | `ForgeTrust.AppSurface.Workers` | `0.2.0-preview.13` |
| Generated test project | `ForgeTrust.AppSurface.Durable` | `0.2.0-preview.13` |
| Generated test project | `ForgeTrust.AppSurface.Durable.Provider` | `0.2.0-preview.13` |
| Generated test project | `ForgeTrust.AppSurface.Durable.PostgreSql` | `0.2.0-preview.13` |
| Generated test project | `ForgeTrust.AppSurface.Durable.Testing` | `0.2.0-preview.13` |

The generated application uses published or exact candidate package artifacts and a pinned third-party graph. The authored host directly references Durable, Provider, PostgreSql, Observability, and Workers; it does not directly reference Core. The generated test project directly references Durable, Provider, PostgreSql, and Durable.Testing, plus the generated host project. Neither generated project references an AppSurface source project or repository test project. The template package itself is installed through the .NET template engine and is not a runtime package reference.

The authored central file pins Npgsql 10.0.3; OpenTelemetry, OpenTelemetry.Exporter.OpenTelemetryProtocol, and OpenTelemetry.Extensions.Hosting 1.15.3; Microsoft.AspNetCore.Mvc.Testing and Microsoft.AspNetCore.TestHost 10.0.2; Microsoft.Extensions.TimeProvider.Testing 10.9.0; Microsoft.NET.Test.Sdk 18.5.1; Testcontainers.PostgreSql 4.13.0; xunit 2.9.3; and xunit.runner.visualstudio 3.1.5. The generated test project also pins coverlet.collector 10.0.1. These are the authored baseline pins in the central props and test project, not a claim that a candidate artifact has been restored or tested.

## Exact v1 generated inventory

The native template manifest is package metadata, not application content. With the documented command using `-n FirstDurableWorker`, the authored template content maps to these application-owned paths (with names substituted by the native template engine):

```text
FirstDurableWorker.slnx
.editorconfig
.gitignore
appsettings.json
Directory.Packages.props
Directory.Build.props
Directory.Build.targets
src/FirstDurableWorker/FirstDurableWorker.csproj
src/FirstDurableWorker/Program.cs
src/FirstDurableWorker/WorkerApplication.cs
src/FirstDurableWorker/AssemblyInfo.cs
src/FirstDurableWorker/Work/SampleWork.cs
src/FirstDurableWorker/Work/SampleWorkExecutor.cs
src/FirstDurableWorker/Work/WorkRegistration.cs
src/FirstDurableWorker/Work/SampleWorkProducer.cs
src/FirstDurableWorker/Hosting/ActivationEndpoints.cs
src/FirstDurableWorker/Hosting/DevelopmentBearerHandler.cs
src/FirstDurableWorker/Hosting/Telemetry.cs
tests/FirstDurableWorker.Tests/FirstDurableWorker.Tests.csproj
tests/FirstDurableWorker.Tests/ActivityExporter.cs
tests/FirstDurableWorker.Tests/BoundedProcessRunner.cs
tests/FirstDurableWorker.Tests/FirstDurableWorkTests.cs
tests/FirstDurableWorker.Tests/HostContractTests.cs
tests/FirstDurableWorker.Tests/PostgreSqlTestContainerImage.cs
tests/FirstDurableWorker.Tests/PostgreSqlFixture.cs
tests/FirstDurableWorker.Tests/FixtureBudgets.cs
tests/FirstDurableWorker.Tests/SetupOperationLifetime.cs
tests/FirstDurableWorker.Tests/SetupOperationLifetimeTests.cs
tests/FirstDurableWorker.Tests/xunit.runner.json
tests/FirstDurableWorker.Tests/NativePostgreSqlSmokeTests.cs
tests/FirstDurableWorker.Tests/TypedWorkScenarioTests.cs
README.md
docs/replace-sample.md
```

Native template metadata uses short name `appsurface-durable-worker`, source name `AppSurfaceDurableWorker`, C# language/project tags, Durable/worker/PostgreSQL classifications, and `preferNameDirectory: true`. Native replacement changes solution and project names, project references, assembly names, and namespaces together. The root settings and central MSBuild/NuGet files are included in the generated project; `AssemblyInfo.cs` grants the generated test assembly access to the host assembly's intentional test surface. There are no post-actions, install-time version selectors, switches that disable authentication or broaden SQL permissions, startup migrations, packaged build-output assemblies, or experimental ASP.NET adapter references.

The package content allowlist includes the manifest and intended dotfiles explicitly. It excludes `bin`, `obj`, test results, local settings, repository references, and packaging-only files. The generated root `appsettings.json` contains no secret: connection strings, StoreId, and runtime epoch start empty and must be supplied through application-owned configuration before startup.

| Generated group | Owner and contract |
| --- | --- |
| Solution, root `Directory.*` files, `.editorconfig`, `.gitignore` | Standalone solution/build entry, centrally pinned package versions and shared build settings, formatting, and local exclusions; usable outside the repository. |
| Host project, `Program.cs`, `WorkerApplication.cs` | .NET 10 ASP.NET Core executable, one application-owned composition seam, pre-listen validation, passive provider registration, restricted data-source ownership, and disposal. |
| `Work/*` | Application-owned typed definition/input/result/codecs, executor, safety/retry decisions, one registration binding, and separate acceptance/request construction. |
| `Hosting/*` | Application-owned direct route mapping, Development-only auth scheme, and SDK/export activation. |
| Root `appsettings.json` | Non-secret bounded values and empty connection/StoreId/epoch defaults that the adopter supplies before startup. No credential or bearer token. |
| Test project and host friend declaration | Published-package-only test project references only the generated host; `AssemblyInfo.cs` grants its named test assembly access to intentional internal host seams. |
| Fixture helpers | `ActivityExporter.cs`, `BoundedProcessRunner.cs`, `FixtureBudgets.cs`, `PostgreSqlFixture.cs`, `PostgreSqlTestContainerImage.cs`, `SetupOperationLifetime.cs`, `SetupOperationLifetimeTests.cs`, and `xunit.runner.json` own activity capture, bounded child/fixture setup, pinned image selection, awaited and observed setup-operation lifetime, and test-runner configuration. |
| Test scenarios | `FirstDurableWorkTests.cs` proves the exact Docker-backed first-Work flow and its four checkpoints; `HostContractTests.cs` checks host/auth/API composition; `NativePostgreSqlSmokeTests.cs` verifies separate ordinary read-only startup against the explicitly owned native cluster; `TypedWorkScenarioTests.cs` covers typed Work scenarios. The first-Work proof still requires actual candidate execution with persisted terminal Work, readiness, SDK export, and bounded cleanup. |
| `README.md`, `docs/replace-sample.md` | First proof, prerequisites, local run, host ownership, three-location replacement, and manual upgrades. |

This source inventory is not itself a generated-artifact or runtime result. Exact-package archive inspection and clean-hive generation must verify that the packed template emits the listed shape. In particular, the package-consumer proof must find and execute the named `FirstDurableWork` test; an empty filter result is not evidence for any checkpoint.

## Manual upgrade checklist

For every later template shape, the release must provide a versioned before/after guide, exact template/generated-package compatibility table, and a manual file-by-file checklist. The adopter should:

1. Record the current template package version and compare the direct Durable package graph with the new compatibility table.
2. Generate a clean project with the new template beside the existing application. Do not install over or regenerate the existing directory.
3. Compare the new file inventory with this guide and the current generated `README.md`; identify added, removed, renamed, and behaviorally changed files.
4. Review `Program.cs`, `WorkerApplication.cs`, `Hosting/*`, settings, routes, auth policy, and provider wiring manually. Preserve application-owned production authentication, health policy, secrets, and transport limits.
5. Compare the typed Work definition, codec versions, executor/reconciler, registration, and producer separately. Decide how persisted old Work and results remain readable and executable; never rewrite already accepted records by copying new sample files.
6. Apply database migrations, role changes, StoreId/epoch operations, and deployment changes only through their operator runbooks. A template update does not perform any of them.
7. Build and run non-Docker contract tests, then run the Docker-backed `FirstDurableWork` proof with the exact coordinated packages. Re-run the adopter's own production-auth and deployment tests before release.

If only the sample Work changes, use the three locations in the generated [`docs/replace-sample.md`](../Durable/ForgeTrust.AppSurface.Durable.Templates/content/durable-worker/docs/replace-sample.md). Keep the empty authenticated wake and application/provider boundary intact.

## Evidence and operational limits

The normative user entry is the [Durable worker start guide](../start-here/durable-worker.md). Its four checkpoints are separate assertions: authorized wake, persisted terminal Work, `NotStarted` to `Healthy`, and actual SDK-exported activation activity. The checkpoint block in a README or receipt-format example is expected shape only; it is not evidence that any run passed.

The generated Docker proof, ordinary executable smoke on Ubuntu/macOS/Windows using an explicitly owned native PostgreSQL cluster, candidate/public artifact replay, timing series, outside-checkout human trial, representative Skoolit adopter certificate, and doctor-output link are independent obligations. A native PostgreSQL smoke cannot substitute for the Docker proof. A candidate/local-feed run is not a public NuGet replay. No generated, all-OS, timing, human, adopter, or doctor result is claimed by this guide.

The following bounds are part of the v1 package-consumer proof implementation. Exact limits pass; inputs one unit over the relevant limit fail closed.

| Evidence boundary | Limit |
| --- | ---: |
| Compressed template archive | 32 MiB |
| Archive entries | 256 |
| Total streamed inflated content | 16 MiB |
| One archive entry | 2 MiB |
| Normalized path length | 1,024 UTF-8 bytes |
| Packaged role SQL entry and consumer output | 1 MiB each |
| Receipt/manifest JSON | 1 MiB and maximum depth 16 |
| Captured stdout or stderr per child | 4 MiB each; excess is drained and marks evidence truncated |
| Safe correctness report | 64 KiB |
| Command records per correctness run | 32 |
| Retained scrubbed evidence per run | 64 MiB |

The verifier rejects rooted, traversal, backslash-alias, duplicate, case-colliding, unexpected, or missing required paths; malformed or duplicate JSON; symlink/reparse escapes; and ambiguous ownership of a workspace or evidence root. It counts actual streamed bytes. Commands use structured arguments. Provisioning credentials are confined to the child/container environment needed by `psql`; they are not command arguments, parent-process environment changes, retained logs, exceptions, reports, or telemetry. Truncated command output cannot produce a successful receipt.

Fixture observation bounds are 5 seconds for Docker availability, 300 seconds to pull a missing image, 40 seconds for readiness/provisioning/schema/epoch/roles/verification after acquisition, 10 seconds for ordinary host startup, 25 seconds for lifecycle and terminal observation with 100 ms single-record polling, 5 seconds for SDK flush, and 20 seconds total for independent cleanup. Child-tree termination is capped at 10 seconds within cleanup. Correctness commands have 180-second install/create/format/test and 300-second restore/build bounds. The separate native PostgreSQL smoke allows 90 seconds for initialization/provisioning and 5 seconds for live-host observation. These are failure bounds, not speed measurements.

The primed timing workload is exactly the install/new/test sequence in the [canonical start guide](../start-here/durable-worker.md). Its install/create/restore/build group has a 55-second cap. The root timed run includes disposable setup, first-Work lifecycle/export proof, command completion, and cleanup; five independent serial samples are required, the median must be under 120 seconds, and every sample must finish under 180 seconds. A failed or timed-out sample invalidates the entire series; retain every attempted sample and publish no passing summary for that series.

The cold path is diagnostic only: use an isolated workflow daemon whose PostgreSQL 16.5 image is absent and a fresh local NuGet cache, then report all five samples, median, and nearest-rank p95. The root watchdog is 900 seconds; reaching it fails the sample and still requires bounded cleanup. With five observations, nearest-rank p95 is sample 5 (the maximum). There is no cold product SLO. Never relabel a warm cache as cold, purge shared caches, or discard a failed attempt. Human onboarding timing and first misunderstanding are separately observed and do not share these automated clocks.
