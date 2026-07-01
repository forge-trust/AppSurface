# ForgeTrust.AppSurface.Web.OpenApi

This package provides a modular integration for OpenAPI (Swagger) document generation in AppSurface web applications.

## Overview

The `AppSurfaceWebOpenApiModule` simplifies the configuration of OpenAPI by:
- Registering the necessary services via `AddOpenApi`.
- Configuring the `EndpointsApiExplorer`.
- Mapping the OpenAPI documentation endpoint when endpoint exposure options allow it.
- Providing default document and operation transformers to clean up and brand the generated documentation based on the `StartupContext`.

## Release Guidance

AppSurface publishes the coordinated `v0.1.0` release as one package-facing story. Before installing this package from a package feed, read the [v0.1.0 release note](../../releases/v0.1.0.md) for current release risk, migration guidance, and package readiness.

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
