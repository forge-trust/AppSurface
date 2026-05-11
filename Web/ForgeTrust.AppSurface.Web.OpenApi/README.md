# ForgeTrust.AppSurface.Web.OpenApi

This package provides a modular integration for OpenAPI (Swagger) document generation in AppSurface web applications.

## Overview

The `AppSurfaceWebOpenApiModule` simplifies the configuration of OpenAPI by:
- Registering the necessary services via `AddOpenApi`.
- Configuring the `EndpointsApiExplorer`.
- Automatically mapping the OpenAPI documentation endpoints.
- Providing default document and operation transformers to clean up and brand the generated documentation based on the `StartupContext`.

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
