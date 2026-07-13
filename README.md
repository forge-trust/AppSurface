# AppSurface

[![Build](https://github.com/forge-trust/AppSurface/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/forge-trust/AppSurface/actions/workflows/build.yml)
[![Package Gate](https://github.com/forge-trust/AppSurface/actions/workflows/package-gate.yml/badge.svg?branch=main)](https://github.com/forge-trust/AppSurface/actions/workflows/package-gate.yml)
[![Package Artifacts](https://github.com/forge-trust/AppSurface/actions/workflows/package-artifacts.yml/badge.svg)](https://github.com/forge-trust/AppSurface/actions/workflows/package-artifacts.yml)
[![Code Quality](https://github.com/forge-trust/AppSurface/actions/workflows/code-quality.yml/badge.svg?branch=main)](https://github.com/forge-trust/AppSurface/actions/workflows/code-quality.yml)
[![CodeQL](https://img.shields.io/badge/CodeQL-enabled-0f6fff?logo=github&logoColor=white)](https://github.com/forge-trust/AppSurface/security/code-scanning)
[![Codecov](https://codecov.io/gh/forge-trust/AppSurface/branch/main/graph/badge.svg)](https://codecov.io/gh/forge-trust/AppSurface)
[![NuGet Packages](https://img.shields.io/badge/NuGet-package%20chooser-004880?logo=nuget&logoColor=white)](./packages/README.md)
[![Dependabot](https://img.shields.io/badge/dependabot-enabled-025e8c?logo=dependabot&logoColor=white)](./.github/dependabot.yml)
[![Security Policy](https://img.shields.io/badge/security-private%20reporting-2ea44f?logo=github)](./SECURITY.md)
[![License](https://img.shields.io/badge/license-Polyform%20Small%20Business-blue)](./licensing.md)

> ⚠️ **Under Construction:** This library is actively being developed and is not intended for production use yet.
> Monorepo for the ForgeTrust.AppSurface projects

ForgeTrust.AppSurface is a collection of .NET libraries designed to provide a lightweight, modular startup pipeline for both console and web applications.

If you are deciding which package to install first, start with the [AppSurface v0.1 package chooser](./packages/README.md). If you are choosing among Auth packages, use the [AppSurface Auth adoption ladder](./start-here/auth-adoption-ladder.md) before installing optional auth adapters.

## Vision

The primary vision of AppSurface is to simplify application bootstrapping by encouraging **composition through small, focused modules**. Instead of monolithic startup classes or scattered configuration logic, AppSurface allows developers to encapsulate features into reusable modules that handle:

-   Dependency Injection (DI) registration
-   Host configuration
-   Application-specific startup logic

This approach aims to:
-   **Share cross-cutting concerns** between different application types (e.g., sharing logging or database setup between a Web API and a background Console worker).
-   **Keep applications minimal**, with infrastructure heavily decoupled from business logic.
-   **Provide consistency** in how applications are initialized and configured, regardless of whether they are web or console apps.

## Key Design Goals

1.  **Modularity**: Everything should be a module that does one thing well. Take what you need and don't get burdened by what you don't.
2.  **Consistency**: A unified `AppSurfaceStartup` pipeline for different project types.
3.  **Flexibility**: Open for integration with external libraries (Autofac, OpenApi, etc.) and stick to framework provided abstractions where possible.
4.  **Performance**: Designed to have minimal overhead on the application startup and execution.
5.  **Ease of Use**: Simple APIs and clear patterns to make getting started frictionless.
6.  **Convention over Configuration**: Sensible defaults are provided so only minimal configuration is required.
7.  **Secure By Default**: Security best practices are applied automatically where appropriate.

## Caching Conventions

- Use `IMemo` for application and service-layer caching (for example, web modules and domain services).
- Use direct `IMemoryCache` only inside caching infrastructure (the `ForgeTrust.AppSurface.Caching` package) or framework integration points where `IMemo` cannot be injected.
- If a module depends on `AppSurfaceCachingModule`, do not call `AddMemoryCache()` again in that module.
- Prefer one cache boundary per data snapshot. In AppSurface Docs, `DocAggregator` owns both docs aggregation and search-index payload caching so downstream controllers consume one shared snapshot.


## Project Structure

### [Packages](./packages/README.md)

- [**AppSurface v0.1 package chooser**](./packages/README.md) - the generated install map for direct-install packages, support/runtime packages, and proof-host surfaces.

### [Core](./ForgeTrust.AppSurface.Core/README.md)

- [**ForgeTrust.AppSurface.Core**](./ForgeTrust.AppSurface.Core/README.md) – Core abstractions for defining modules, starting an application via `AppSurfaceStartup` and `StartupContext`, and running AppSurface-owned process workflows through a CliWrap-backed policy surface.

### [Auth](./Auth/ForgeTrust.AppSurface.Auth/README.md)

- [**AppSurface Auth adoption ladder**](./start-here/auth-adoption-ladder.md) - Start here when you need to choose between host-owned ASP.NET Core auth, AppSurface Auth contracts, Auth.AspNetCore, DevAuth, OIDC, Auth.Testing, and RazorWire-facing proof surfaces.
- [**ForgeTrust.AppSurface.Auth**](./Auth/ForgeTrust.AppSurface.Auth/README.md) – Surface-neutral auth vocabulary for AppSurface modules, including user/session/context contracts, auth outcome results, durable external-subject to app-user-id mapping contracts, passive login/logout prompts, passive audit event descriptions, and no runtime request or identity-provider behavior.
- [**ForgeTrust.AppSurface.Auth.AspNetCore**](./Auth/ForgeTrust.AppSurface.Auth.AspNetCore/README.md) – ASP.NET Core adapter that maps existing host request auth context and named policies into AppSurface auth results without owning schemes, middleware, challenges, forbids, redirects, or identity-provider setup. Run the [Auth Web/RazorWire proof](./examples/auth-web-razorwire-proof/README.md) to see one host policy drive both API and rendered UI state.
- [**ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth**](./Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md) – Development-by-default selectable persona auth for local/proof AppSurface policy proofs, with a visible control page, embeddable state overlay, named fake scheme, startup guard, explicit environment opt-in, and no production identity-provider behavior.
- [**ForgeTrust.AppSurface.Auth.AspNetCore.Oidc**](./Auth/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc/README.md) – ASP.NET Core cookie + OIDC convenience registration with explicit AppSurface scheme names, conservative token defaults, passive prompt helpers, and safe diagnostics without silent default-scheme takeover or identity-provider ownership.
- [**ForgeTrust.AppSurface.Auth.Aspire.Keycloak**](./Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md) – AppHost-only local Keycloak proof for real AppSurface OIDC sign-in, with deterministic realm import, secret-safe web projection, readiness probes, fixed-port diagnostics, and no runtime web dependency on Keycloak packages.
- [**ForgeTrust.AppSurface.Auth.Testing**](./Auth/ForgeTrust.AppSurface.Auth.Testing/README.md) – Test-only ASP.NET Core harness for deterministic AppSurface auth personas, WebApplicationFactory integration tests, canonical auth result assertions, and ProblemDetails checks without becoming production authentication or Dev Auth.

### [Intelligence](./Intelligence/ForgeTrust.AppSurface.Intelligence/README.md)

- [**ForgeTrust.AppSurface.Intelligence**](./Intelligence/ForgeTrust.AppSurface.Intelligence/README.md) - Product-intelligence event contracts, lifecycle metadata, privacy validation, and host-owned sink hooks for forwarding sanitized AppSurface product events to systems such as PostHog without taking a vendor dependency.

### [Observability](./Observability/README.md)

- [**ForgeTrust.AppSurface.Observability**](./Observability/ForgeTrust.AppSurface.Observability/README.md) – Application-side OpenTelemetry logging, tracing, and metrics registration for Aspire or another OTLP collector.

### [Flow](./Flow/README.md)

- [**ForgeTrust.AppSurface.Flow**](./Flow/ForgeTrust.AppSurface.Flow/README.md) – Typed long-running process contracts, generated-case authoring, graph validation, definition registry, and an in-memory runner for local tests and hello-world flows.
- [**ForgeTrust.AppSurface.Flow.DurableTask**](./Flow/ForgeTrust.AppSurface.Flow.DurableTask/README.md) – Durable Task adapter boundary with runner/client services, resume-event authorization, timeout, late-event and retry behavior, and context serialization validation.

### [Console](./Console/README.md)

- [**ForgeTrust.AppSurface.Console**](./Console/ForgeTrust.AppSurface.Console/README.md) – Helpers for building command line apps with [CliFx](https://github.com/Tyrrrz/CliFx), source-generated command descriptors, a `CriticalService`-based command runner, and helpers for configuring services.

### [Web](./Web/README.md)

- [**ForgeTrust.AppSurface.Web**](./Web/ForgeTrust.AppSurface.Web/README.md) – Bootstraps ASP.NET Core apps, lets modules register pre-routing middleware, endpoint-aware middleware, and endpoints, and includes conventional browser status pages plus opt-in production 500 pages.
- [**ForgeTrust.AppSurface.Web.OpenApi**](./Web/ForgeTrust.AppSurface.Web.OpenApi/README.md) – Optional module that adds OpenAPI generation with development-only endpoint exposure by default.
- [**ForgeTrust.RazorWire**](./Web/ForgeTrust.RazorWire/README.md) – Adds reactive Razor-based streaming, islands, and CDN-default export tooling for server-rendered web apps.
- [**ForgeTrust.RazorWire.Auth.AspNetCore**](./Web/ForgeTrust.RazorWire.Auth.AspNetCore/README.md) – ASP.NET Core adapter for RazorWire auth projection helpers, delegating rendered UI state to host-owned AppSurface policy evaluation without adding auth schemes, redirects, or endpoint enforcement.
- [**ForgeTrust.AppSurface.Docs**](./Web/ForgeTrust.AppSurface.Docs/README.md) – Reusable Razor Class Library package that serves harvested source docs with section-first landing, sidebar, search, built-in trust plus contributor-provenance details, and optional published-version archive surfaces.
- [**ForgeTrust.AppSurface.Docs.Standalone**](./Web/ForgeTrust.AppSurface.Docs.Standalone/README.md) – Thin export host for exporting or serving AppSurface Docs as an application.
- [**ForgeTrust.AppSurface.Web.Scalar**](./Web/ForgeTrust.AppSurface.Web.Scalar/README.md) – Optional module that serves the Scalar API reference UI when both Scalar and OpenAPI exposure gates allow it.

### [CLI](./Cli/ForgeTrust.AppSurface.Cli/README.md)

- [**ForgeTrust.AppSurface.Cli**](./Cli/ForgeTrust.AppSurface.Cli/README.md) – Public `appsurface` command-line tool, including `appsurface docs` preview/export workflows, `appsurface secrets` local-secret diagnostics, `appsurface coverage run` private test orchestration, `appsurface coverage merge` Cobertura fan-in, and `appsurface coverage gate` local threshold enforcement.

### [Dependency](./Dependency/README.md)

- [**ForgeTrust.AppSurface.Dependency.Autofac**](./Dependency/ForgeTrust.AppSurface.Dependency.Autofac/README.md) – Optional integration with the Autofac IoC container so modules can participate in Autofac service registration.

### [Aspire](./Aspire/README.md)

- [**ForgeTrust.AppSurface.Aspire**](./Aspire/ForgeTrust.AppSurface.Aspire/README.md) – Local .NET Aspire AppHost composition with AppSurface modules, CLI-selectable profiles, reusable Aspire components, and native publish/verification integration for explicitly annotated resources.

### [Deployment](./Deployment/README.md)

- [**ForgeTrust.AppSurface.Deployment**](./Deployment/ForgeTrust.AppSurface.Deployment/README.md) – Portable, schema-versioned deployment intent, validation, diagnostics, deterministic serialization, and provider contracts with no Aspire or cloud dependency.
- [**ForgeTrust.AppSurface.Deployment.GcpCloudRun**](./Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/README.md) – Deterministic Cloud Run migration-job Terraform/evidence compilation and read-only shadow or owned parity verification.

These packages are designed to work together so that features can be shared
across different application types while maintaining a consistent startup
approach.

## Getting started

If you want to see value first, run the web hello world:

```bash
dotnet run --project examples/web-app -- --port 5055
```

Then, from another terminal, prove the running endpoint:

```bash
curl http://127.0.0.1:5055
```

Expected response:

```text
Hello World from the root!
```

That example is the smallest concrete path through `ForgeTrust.AppSurface.Web`: a root module, one mapped endpoint, and the AppSurface startup pipeline doing the hosting work.

To verify the browser/API error-page contract instead, run the focused proof:

```bash
bash examples/web-error-pages/verify.sh
```

That proof starts a local production-mode web app, checks conventional browser `401`, `403`, `404`, and `500` pages, and verifies API requests do not receive surprise browser HTML.

If you are evaluating packages from your own app project rather than running this repo, use the [package-first path](./start-here/first-success-path.md#package-first-path) to create a fresh ASP.NET Core app, install `ForgeTrust.AppSurface.Web`, and verify the first route. Run the package command from the app project directory, or pass the project path explicitly. The generated package chooser in [packages/README.md](./packages/README.md) is the install map for picking optional modules after that first proof.

```bash
dotnet package add ForgeTrust.AppSurface.Web
```

Add optional modules only when the generated chooser points you to them.

If you need a composed product-shaped proof instead of a single package slice, run the [product-readiness lab](./examples/product-readiness-lab/README.md):

```bash
dotnet run --project examples/product-readiness-lab/ProductReadinessLab.csproj -- --report
```

That fast command emits a no-infrastructure readiness report. To exercise every row the local lab can prove, including Postgres-backed product-state persistence, run the AppHost verifier:

```bash
aspire run --non-interactive --apphost examples/product-readiness-lab-apphost/ProductReadinessLabAppHost.csproj -- verify
```

The lab emits a readiness report with `proven-locally`, `host-owned`, `deferred`, `unsafe-to-copy`, and `blocked` rows. Its in-process host proof shows where `IDurableTaskFlowRunner<TContext>` and `IDurableTaskFlowClient<TContext>` fit, while keeping Durable Task worker/client hosting and storage provider setup explicitly host-owned.

For contributor verification, build the solution:

```bash
dotnet build
dotnet test --no-build
```

Run the repository's full AppSurface coverage-and-gate lane:

```bash
./scripts/coverage-solution.sh
```

For a local checkout, the default patch comparison is `origin/main`. The CI workflow overrides that
value with `HEAD^1` for its pull-request merge checkout, so its patch gate evaluates exactly the
tested merge tree. Set `COVERAGE_GATE_DIFF_BASE=` to run only the aggregate gate, as CI does for
baseline builds.

This command:
- Runs each solution test project.
- Collects coverage only for `ForgeTrust.AppSurface.*` modules.
- Excludes test modules (`*.Tests` and `*.IntegrationTests`) from coverage.
- Produces one merged Cobertura file at `TestResults/coverage-merged/coverage.cobertura.xml`.
- Produces AppSurface-managed JUnit files as `TestResults/coverage-merged/junit-coverage-<index>-<project-name-hash>.xml`.
- Writes a summary to `TestResults/coverage-merged/summary.txt`.
- Writes machine-readable timing data to `TestResults/coverage-merged/timings.json`.
- Writes slow-test diagnostics to `TestResults/coverage-merged/slow-test-diagnostics.md` and
  `TestResults/coverage-merged/slow-test-diagnostics.json`, including diagnostic aggregation
  overhead in seconds and as a percent of elapsed runner time at diagnostics generation.
- Uses the source AppSurface CLI and its package-owned ReportGenerator dependency for the default
  full-solution lane.
- Gates at 95% line coverage and 85% branch coverage, plus 95% line and 85% branch coverage for
  the selected patch when a diff base is configured.

Private package-consuming repositories should use the public CLI runner instead of this repository's script:

```bash
dotnet tool run appsurface coverage run --solution ./MyApp.slnx
dotnet tool run appsurface coverage gate --coverage ./TestResults/coverage-merged/coverage.cobertura.xml --min-line 85 --min-branch 75
```

The `appsurface coverage run` command discovers `.sln`/`.slnx` test projects or accepts repeated `--test-project` values, runs Coverlet-instrumented projects, writes private local artifacts under `TestResults/coverage-merged`, and merges Cobertura through the CLI package's ReportGenerator dependency without reading the consumer repo's tool manifest. No separate merge command is required for ordinary package consumers: `coverage run` produces `TestResults/coverage-merged/coverage.cobertura.xml` directly. Managed test results are opt-in with `--test-results junit`; this requires selected test projects to reference `JunitXml.TestLogger`. `--slow-test-diagnostics` implies managed JUnit results and writes `slow-test-diagnostics.md` and `.json` beside the merged coverage file. Use `appsurface coverage merge --source ./TestResults/coverage-shards --output ./TestResults/coverage-merged` when a matrix job or custom test workflow already produced shard files named `coverage.cobertura.xml`. The optional `appsurface coverage gate` command evaluates that merged Cobertura file locally, writes `coverage-gate.json` and `coverage-gate.md`, appends the Markdown report to `$GITHUB_STEP_SUMMARY` when GitHub Actions provides it, and fails with `ASCOV020` when line, branch, or configured patch coverage is below threshold. Patch coverage accepts exactly one source: `--diff-base` for local Git history, `--diff-file` for a CI-produced unified diff artifact, or `--diff-stdin` for piped unified diff text. External diff artifacts are private local inputs, are bounded at 20 MiB, and fail closed when non-empty content is not unified diff text. The coverage commands are intentionally private-by-default: they do not upload coverage, call GitHub APIs, or store trends.

The legacy grouped script modes remain coverage-only for bounded local or CI experiments; use the
public [`appsurface coverage gate`](./Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate)
command separately when a grouped artifact needs gating:

```bash
BUILD_CONFIGURATION=Release BUILD_SOLUTION=false ./scripts/coverage-solution.sh --group web --output TestResults/coverage-groups/web
./scripts/coverage-solution.sh --list-groups
./scripts/coverage-solution.sh --merge-only TestResults/coverage-groups --output TestResults/coverage-merged
```

Available bounded groups are `core`, `tools`, `web`, `docs`, `razorwire`, and `integration`.

Default PR validation keeps solution coverage in one lane until measured group runs prove they reduce total GitHub Actions minutes, not just wall-clock time.

The current CI critical-path policy and timing baseline live in [eng/ci-critical-path.md](./eng/ci-critical-path.md).

When tests need to launch child processes, prefer CliWrap-backed helpers over raw `System.Diagnostics.Process` setup. Keep process-output capture in the helper result so CI failures include stdout and stderr, and reserve raw `Process` usage for tests that intentionally exercise process-wrapper behavior.

Check out the examples to see how modules are composed in practice:

```bash
dotnet run --project examples/auth-aspnetcore-bridge
dotnet run --project examples/auth-web-razorwire-proof/AuthWebRazorWireProofExample.csproj
dotnet run --project examples/console-app
dotnet run --project examples/flow-approval-local/FlowApprovalLocalExample.csproj
dotnet run --project examples/product-readiness-lab/ProductReadinessLab.csproj -- --report
aspire run --non-interactive --apphost examples/product-readiness-lab-apphost/ProductReadinessLabAppHost.csproj -- verify
dotnet run --project examples/web-app
dotnet run --project examples/razorwire-mvc/RazorWireWebExample.csproj
```

For the intentional validation-failure shape, run `dotnet run --project examples/config-validation`.

The RazorWire MVC example includes a failed-form UX page at `/Reactivity/FormFailures` that shows server-handled validation, development anti-forgery diagnostics, default fallback rendering, and consumer styling hooks.

## Release notes and upgrade policy

AppSurface is preparing to release the entire monorepo in unison. The public release contract now lives in the repository so teams can see what is queued for the next version, how pre-1.0 changes are handled, and where future migration notes will live.

- [Package chooser](./packages/README.md) - the generated first-install map for web, console, Aspire, and optional package add-ons.
- [Release hub](./releases/README.md) - start here for the narrative release surface.
- [Unreleased proof artifact](./releases/unreleased.md) - the living notes for the next coordinated version.
- [Changelog](./CHANGELOG.md) - the compact ledger for tagged and in-flight changes.
- [Pre-1.0 upgrade policy](./releases/upgrade-policy.md) - the stability and migration contract before `v1.0.0`.
- [Contribution and release entry rules](./CONTRIBUTING.md) - how PR titles and unreleased entries feed the release surface.

## Feedback and contributing

AppSurface uses GitHub issue forms to keep bug reports, feature requests, and docs/developer-experience feedback concrete enough to reproduce or evaluate. If an example, README, quickstart, or package API leaves you stuck, start with the [contribution guide](./CONTRIBUTING.md), [choose an issue template](https://github.com/forge-trust/AppSurface/issues/new/choose), and file the form that matches the problem.

Use docs/DX feedback for confusing guidance, missing concepts, broken links, snippet drift, or first-run friction. Use feature requests for focused product capabilities, API shapes, workflows, or examples. Use bug reports when runtime behavior, generated output, or package APIs do something unexpected.
Do not file suspected vulnerabilities, leaked secrets, or exploit details in public issues; follow the [security policy](./SECURITY.md) instead.

## Examples

The [examples](examples/README.md) directory contains sample applications that demonstrate
how to use this project.

- [Auth ASP.NET Core bridge example](examples/auth-aspnetcore-bridge/README.md) – proves an ASP.NET Core host-owned auth stack can flow named policy results into AppSurface auth contracts.
- [Auth Web/RazorWire proof](examples/auth-web-razorwire-proof/README.md) – shows one host-owned ASP.NET Core policy driving both a Minimal API response and RazorWire-rendered auth state.
- [Console app example](examples/console-app/README.md) – builds a simple command line
  application using [CliFx](https://github.com/Tyrrrz/CliFx) source-generated command
  descriptors.
- [Aspire AppHost example](examples/aspire-apphost/README.md) – shows local Aspire AppHost
  composition with AppSurface profiles and reusable Aspire components.
- [Auth Aspire Keycloak AppHost proof](examples/auth-aspire-keycloak-apphost/README.md) – starts local Keycloak, imports a deterministic AppSurface realm, runs the OIDC web proof, and provides a noninteractive verifier for the real-provider flow.
- [Web app example](examples/web-app/README.md) – shows a minimal ASP.NET Core app that
  composes middleware and endpoints from modules.
- [Web error-page proof](examples/web-error-pages/README.md) – runs a one-command verifier
  for AppSurface Web browser status pages, production exception pages, and API-friendly
  non-HTML behavior.
- [Config validation example](examples/config-validation/README.md) – shows scalar
  validation on a strongly typed config wrapper and the startup failure shape.
- [Local secrets example](examples/local-secrets/README.md) – shows OS-backed local
  secret posture, CLI setup, provider precedence, and paste-safe diagnostics for
  solo development before a remote vault exists.
- [Flow approval local example](examples/flow-approval-local/README.md) – shows a typed
  flow that waits for an approval event and resumes through the in-memory runner.
- [Product readiness lab](examples/product-readiness-lab/README.md) – runs a composed
  local evaluator with AppSurface Web, Auth.AspNetCore, Flow, DurableTask-facing
  host-shape guidance, and Postgres product-state proof. Use its AppHost `verify`
  profile when the report should exercise Postgres and require that row to be
  `proven-locally`.

## License

AppSurface is licensed under the [Polyform Small Business License 1.0.0](./licensing.md).

Free for individuals and businesses with fewer than 100 people and under
$1,000,000 USD prior-year revenue (inflation-adjusted). Larger companies
require a commercial license.
