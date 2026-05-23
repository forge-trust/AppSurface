# ForgeTrust.AppSurface.Web.OpenApi

This package provides a modular integration for OpenAPI (Swagger) document generation in AppSurface web applications.

## Overview

The `AppSurfaceWebOpenApiModule` simplifies the configuration of OpenAPI by:
- Registering the necessary services via `AddOpenApi`.
- Configuring the `EndpointsApiExplorer`.
- Automatically mapping the OpenAPI documentation endpoints.
- Providing default document and operation transformers to clean up and brand the generated documentation based on the `StartupContext`.

## Release Guidance

AppSurface is preparing the first coordinated `v0.1.0` release. Before installing this package from a prerelease feed, read the [v0.1 release preview](../../releases/v0.1-preview.md) for current release risk, provisional migration guidance, and the finalization path to the tagged release note.

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
- **Automatic Endpoint Mapping**: Calls `endpoints.MapOpenApi()` for you during the endpoint configuration phase.

---
[📂 Back to Web List](../README.md) | [🏠 Back to Root](../../README.md)
