# ForgeTrust.AppSurface.Web.OpenApi

This package provides a modular integration for OpenAPI (Swagger) document generation in AppSurface web applications.

## Overview

The `AppSurfaceWebOpenApiModule` simplifies the configuration of OpenAPI by:
- Registering the necessary services via `AddOpenApi`.
- Configuring the `EndpointsApiExplorer`.
- Mapping the OpenAPI documentation endpoint when endpoint exposure options allow it.
- Providing default document and operation transformers to clean up and brand the generated documentation based on the `StartupContext`.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

## OpenAPI.NET Dependency Floor

This package directly requires `Microsoft.OpenApi` 2.7.5 or later to avoid the process-termination vulnerability described by [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc). The direct dependency is intentional: `Microsoft.AspNetCore.OpenApi` 10.0.9 still declares a 2.0.0 minimum, which allows NuGet to select a vulnerable version unless the consuming package supplies a safer floor.

AppSurface itself restores and verifies `Microsoft.OpenApi` 2.7.5 on the 2.x line. The published NuGet dependency is a minimum with no upper bound, so downstream dependency resolution may select a later version. That open-ended range is not a claim that AppSurface has tested or guarantees compatibility with `Microsoft.OpenApi` 3.x; consumers overriding the coordinated dependency graph must run their own OpenAPI generation and endpoint tests.

## Usage

To enable OpenAPI support in your application, add the `AppSurfaceWebOpenApiModule` as a dependency in your root module:

```csharp
public class MyModule : IAppSurfaceWebModule
{
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceWebOpenApiModule>();
    }
    
    // ...
}
```

## Features

- **Document Transformation**: Automatically updates the API title and tags based on the application name defined in the `StartupContext`.
- **Controlled Endpoint Mapping**: Calls `endpoints.MapOpenApi()` during endpoint configuration only when `AppSurfaceWebOpenApiOptions.ExposeEndpoint` allows the active environment.
- **Production-Safe Default**: Maps `/openapi/{documentName}.json` in Development and hides it in non-development environments unless the host opts in.

## Endpoint Exposure

`AppSurfaceWebOpenApiOptions` binds from the `AppSurfaceWebOpenApi` configuration section.

```json
{
  "AppSurfaceWebOpenApi": {
    "ExposeEndpoint": "DevelopmentOnly"
  }
}
```

`ExposeEndpoint` uses `AppSurfaceApiDocumentationEndpointExposure`:

- `DevelopmentOnly` (`0`): maps the OpenAPI endpoint only when `StartupContext.IsDevelopment` is `true`. This is the default.
- `Always` (`1`): maps the endpoint in every environment.
- `Never` (`2`): never maps the endpoint, including in Development.

Use `Always` only when the host intentionally exposes API metadata and protects it with host-owned controls such as authorization, private networking, or a reverse proxy policy. AppSurface only decides whether to map the endpoint; it does not add authentication or authorization.

Code-first configuration is also supported:

```csharp
services.Configure<AppSurfaceWebOpenApiOptions>(options =>
{
    options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Always;
});
```

Invalid enum values fail options validation at startup when the options are read.

---
[📂 Back to Web List](../README.md) | [🏠 Back to Root](../../README.md)
