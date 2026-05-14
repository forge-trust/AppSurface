# AppSurface

> ⚠️ **Under Construction:** This library is actively being developed and is not intended for production use yet.
> Monorepo for the ForgeTrust.AppSurface projects

ForgeTrust.AppSurface is a collection of .NET libraries designed to provide a lightweight, modular startup pipeline for both console and web applications.

If you are deciding which package to install first, start with the [AppSurface v0.1 package chooser](./packages/README.md).

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
- Prefer one cache boundary per data snapshot. In RazorDocs, `DocAggregator` owns both docs aggregation and search-index payload caching so downstream controllers consume one shared snapshot.


## Project Structure

### [Packages](./packages/README.md)

- [**AppSurface v0.1 package chooser**](./packages/README.md) - the generated install map for direct-install packages, support/runtime packages, and proof-host surfaces.

### [Core](./ForgeTrust.AppSurface.Core/README.md)

- [**ForgeTrust.AppSurface.Core**](./ForgeTrust.AppSurface.Core/README.md) – Core abstractions for defining modules and starting an application via `AppSurfaceStartup` and `StartupContext`.

### [Console](./Console/README.md)

- [**ForgeTrust.AppSurface.Console**](./Console/ForgeTrust.AppSurface.Console/README.md) – Helpers for building command line apps with [CliFx](https://github.com/Tyrrrz/CliFx) including a `CriticalService`-based command runner and helpers for configuring services.

### [Web](./Web/README.md)

- [**ForgeTrust.AppSurface.Web**](./Web/ForgeTrust.AppSurface.Web/README.md) – Bootstraps ASP.NET Core apps, lets modules register middleware and endpoints, and includes conventional browser status pages plus opt-in production 500 pages.
- [**ForgeTrust.AppSurface.Web.OpenApi**](./Web/ForgeTrust.AppSurface.Web.OpenApi/README.md) – Optional module that adds OpenAPI generation using `AddEndpointsApiExplorer` and `WithOpenApi`.
- [**ForgeTrust.RazorWire**](./Web/ForgeTrust.RazorWire/README.md) – Adds reactive Razor-based streaming, islands, and CDN-default export tooling for server-rendered web apps.
- [**ForgeTrust.AppSurface.Docs**](./Web/ForgeTrust.AppSurface.Docs/README.md) – Reusable Razor Class Library package that serves harvested source docs with section-first landing, sidebar, search, built-in trust plus contributor-provenance details, and optional published-version archive surfaces.
- [**ForgeTrust.AppSurface.Docs.Standalone**](./Web/ForgeTrust.AppSurface.Docs.Standalone/README.md) – Thin export host for exporting or serving RazorDocs as an application.
- [**ForgeTrust.AppSurface.Web.Scalar**](./Web/ForgeTrust.AppSurface.Web.Scalar/README.md) – Optional module that serves the Scalar API reference UI and depends on the OpenAPI module.

### [CLI](./Cli/ForgeTrust.AppSurface.Cli/README.md)

- [**ForgeTrust.AppSurface.Cli**](./Cli/ForgeTrust.AppSurface.Cli/README.md) – Public `appsurface` command-line tool, including the `appsurface docs` RazorDocs preview workflow.

### [Dependency](./Dependency/README.md)

- [**ForgeTrust.AppSurface.Dependency.Autofac**](./Dependency/ForgeTrust.AppSurface.Dependency.Autofac/README.md) – Optional integration with the Autofac IoC container so modules can participate in Autofac service registration.

### [Aspire](./Aspire/README.md)

- [**ForgeTrust.AppSurface.Aspire**](./Aspire/ForgeTrust.AppSurface.Aspire/README.md) – Integration with .NET Aspire to provide a modular approach to defining distributed applications and service defaults.

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

If you are evaluating packages from your own app project rather than running this repo, start with the generated package chooser in [packages/README.md](./packages/README.md). Use the package matrix to pick the module your app actually needs, then run the matching install command from your app project, for example:

```bash
dotnet add package ForgeTrust.AppSurface.Web --project <path-to-your-app.csproj>
```

Add optional modules only when the generated chooser points you to them.

For contributor verification, build the solution:

```bash
dotnet build
dotnet test --no-build
```

Run merged solution coverage (product assemblies only):

```bash
./scripts/coverage-solution.sh
```

This command:
- Runs each solution test project.
- Collects coverage only for `ForgeTrust.AppSurface.*` modules.
- Excludes test modules (`*.Tests` and `*.IntegrationTests`) from coverage.
- Produces one merged Cobertura file at `TestResults/coverage-merged/coverage.cobertura.xml`.
- Writes a summary to `TestResults/coverage-merged/summary.txt`.

Check out the examples to see how modules are composed in practice:

```bash
dotnet run --project examples/console-app
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

- [Console app example](examples/console-app/README.md) – builds a simple command line
  application using [CliFx](https://github.com/Tyrrrz/CliFx) for command
  definitions.
- [Web app example](examples/web-app/README.md) – shows a minimal ASP.NET Core app that
  composes middleware and endpoints from modules.
- [Config validation example](examples/config-validation/README.md) – shows scalar
  validation on a strongly typed config wrapper and the startup failure shape.

## License

AppSurface is licensed under the [Polyform Small Business License 1.0.0](./licensing.md).

Free for individuals and businesses with fewer than 100 people and under
$1,000,000 USD prior-year revenue (inflation-adjusted). Larger companies
require a commercial license.
