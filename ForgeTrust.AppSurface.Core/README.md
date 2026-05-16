# ForgeTrust.AppSurface.Core

The foundation of the AppSurface ecosystem. This package defines the core abstractions, the startup pipeline, and the module system that powers all other AppSurface libraries.

## Overview

The Core library is designed to be lightweight and implementation-agnostic. It provides the infrastructure to:
- Define **Modules** (`IAppSurfaceModule`, `IAppSurfaceHostModule`) that encapsulate logic.
- Manage **Dependency Graphs** between modules.
- Provide a consistent **Startup Pipeline** (`AppSurfaceStartup`) that sits on top of the .NET Generic Host.

## Key Concepts

- **`IAppSurfaceModule`**: The base interface for any unit of functionality that needs to register services or configure the application.
- **`StartupContext`**: Provides metadata about the running application, including the user-facing application label, assembly-backed host identity, application discovery assembly, environment, and startup-level console output mode.
- **`ConsoleOutputMode`**: Shared core enum that lets console-oriented packages describe whether command output should remain host-centric or command-first.
- **`AppSurfaceStartup`**: The base class that orchestrates the host building and service registration process.
- **`AppSurfaceStartup.RegisterDependencies`**: A protected seam for specialized startup types that need the module graph prepared before they build the Generic Host.

## Application labels and host identity

`StartupContext.ApplicationName` is a display label. Use it for generated documentation titles, command output, OpenAPI branding, and other user-facing product surfaces.

`StartupContext.HostApplicationName` is the assembly-backed identity assigned to `IHostEnvironment.ApplicationName` and the Generic Host `applicationName` setting. It defaults to the process entry assembly when one is available so cross-assembly hosts still resolve the correct static web asset manifest. When `StartupContext.OverrideEntryPointAssembly` is set, it uses that override assembly name instead. If no entry assembly is available, AppSurface falls back to the root module assembly as a defensive last resort.

`StartupContext.EntryPointAssembly` is the assembly AppSurface scans for application-owned commands, MVC application parts, Aspire components, and similar extensibility points. It defaults to the root module assembly so test runners and shared outer hosts do not accidentally scan the xUnit/VSTest process entry assembly. When `StartupContext.OverrideEntryPointAssembly` is set, that override applies to both discovery and host manifest identity.

Keep these values separate. ASP.NET static web assets use the host application name to find runtime manifests. Passing a custom display label such as `CustomDocsHost` into the host environment can make static asset requests resolve against a manifest that does not exist. When a test or custom host needs a different manifest identity, set `StartupContext.OverrideEntryPointAssembly` instead of overloading `ApplicationName`.

## Environment resolution

`StartupContext.EnvironmentProvider` defaults to `DefaultEnvironmentProvider`, which keeps AppSurface module decisions aligned with the Generic Host arguments. When startup receives `--environment Development` or `--environment=Development`, `StartupContext.IsDevelopment` reports `true` before module hooks run.

If no command-line environment is supplied, AppSurface falls back to `ASPNETCORE_ENVIRONMENT`, then `DOTNET_ENVIRONMENT`, then `Production`. Pass a custom `IEnvironmentProvider` to `StartupContext` when a test, embedded host, or specialized runner needs a different source of truth. Blank `--environment` values are ignored so normal process environment fallback still works.

Pitfalls:

- Do not read only `IHostEnvironment` when writing module startup decisions. Module hooks receive `StartupContext` before the built host exists.
- Do not pass `--environment` only to the Generic Host if an AppSurface module also needs the same value. Put it in `StartupContext.Args`, or pass a matching custom `IEnvironmentProvider`.

## Startup dependency graph

`AppSurfaceStartup` registers framework dependencies and root-module dependencies exactly once per `StartupContext`. Standard hosts do not need to call anything directly: the registration happens during host-builder creation before module hooks and service registration run.

Specialized startup types can call the protected `RegisterDependencies(StartupContext context)` seam earlier when they need module-derived options before `IHostBuilder.Build()`. AppSurface Web uses this to resolve `WebOptions.StartupTimeout` before arming its startup watchdog around host creation and startup.

Call `RegisterDependencies` before reading `StartupContext.GetDependencies()` for startup-shaping decisions. Repeated calls with the same context are no-ops, but module registration is still part of startup composition, so avoid calling it from request-time code or from parallel threads.

## Logging in Static Utilities

Core static utilities stay host-agnostic: they do not reach into a global logger, service provider, or ambient startup state. When a public static helper has useful diagnostics, expose an additive overload with an explicit non-null `ILogger` parameter and keep the existing no-logger overload silent. Private shared implementations may accept `ILogger?` only to avoid duplicating logic between the silent and diagnostic paths.

Use this pattern when a helper performs fallback behavior that callers may want to audit. For example, `PathUtils.FindRepositoryRoot(startPath, logger)` logs a warning when `startPath` does not exist and repository-root discovery has to continue from the nearest existing ancestor.

Prefer ordinary dependency injection for services, modules, hosted services, and application-owned classes. The optional logger pattern is only for static helpers where injecting a service instance would make the API harder to use or force unrelated callers to construct infrastructure.

Define static helper log messages with the source-generated `[LoggerMessage]` attribute on private static partial methods. Give each message a stable event ID, level, and template. Do not use ad hoc `logger.Log...` calls for new Core diagnostics when the message is part of an intentional API behavior.

Pitfalls:

- Do not create a static `LoggerFactory` inside a utility. That couples Core to a console/logging policy the host did not choose.
- Do not throw only because a fallback warning was logged. Keep the documented return behavior unless the input contract itself is invalid.
- Do not swallow cleanup failures silently when a logger is available. Log them at `Debug` when they are intentionally suppressed to preserve the primary exception or cancellation path.

## Usage

Most users will use a more specialized package like `ForgeTrust.AppSurface.Web` or `ForgeTrust.AppSurface.Console`, which inherit from the abstractions provided here.

---
[🏠 Back to Root](../README.md)
