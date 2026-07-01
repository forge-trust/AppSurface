# ForgeTrust.AppSurface.Web.Scalar

This package integrates the [Scalar](https://scalar.com/) API Reference UI into AppSurface web applications.

## Overview

The `AppSurfaceWebScalarModule` provides a modern, interactive API documentation interface. It depends on `ForgeTrust.AppSurface.Web.OpenApi` and maps the Scalar UI when Scalar exposure options and the AppSurface-owned OpenAPI endpoint exposure both allow the active environment.

## Release Guidance

AppSurface publishes the coordinated `v0.1.0` release as one package-facing story. Before installing this package from a package feed, read the [v0.1.0 release note](../../releases/v0.1.0.md) for current release risk, migration guidance, and package readiness.

## Usage

Simply add the `AppSurfaceWebScalarModule` to your module dependencies:

```csharp
public class MyModule : IAppSurfaceWebModule
{
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceWebScalarModule>();
    }

    // ...
}
```

## Features

- **Built-in Dependency**: Automatically registers the `AppSurfaceWebOpenApiModule`.
- **Controlled UI Mapping**: Maps the Scalar API reference endpoint using `MapScalarApiReference()` only when exposure options allow it.
- **OpenAPI-Aware Gate**: Requires the AppSurface-owned OpenAPI endpoint to be exposed in the same environment. Scalar never calls `MapOpenApi()` itself.
- **Production-Safe Default**: Maps Scalar in Development and hides it in non-development environments unless the host opts in.

## Endpoint Exposure

`AppSurfaceWebScalarOptions` binds from the `AppSurfaceWebScalar` configuration section.

```json
{
  "AppSurfaceWebScalar": {
    "ExposeEndpoint": "DevelopmentOnly"
  }
}
```

`ExposeEndpoint` uses `AppSurfaceApiDocumentationEndpointExposure` from `ForgeTrust.AppSurface.Web.OpenApi`:

- `DevelopmentOnly` (`0`): maps Scalar only when `StartupContext.IsDevelopment` is `true`. This is the default.
- `Always` (`1`): allows Scalar in every environment, subject to OpenAPI exposure also being allowed.
- `Never` (`2`): never maps Scalar, including in Development.

For production exposure, opt into both Scalar and OpenAPI:

```json
{
  "AppSurfaceWebOpenApi": {
    "ExposeEndpoint": "Always"
  },
  "AppSurfaceWebScalar": {
    "ExposeEndpoint": "Always"
  }
}
```

If Scalar is `Always` but OpenAPI remains `DevelopmentOnly` in Production, neither the Scalar UI nor the AppSurface-owned OpenAPI endpoint is available through the default module composition. If OpenAPI is `Always` but Scalar is `DevelopmentOnly`, the OpenAPI document is available and Scalar stays hidden.

Use `Always` only when the host intentionally exposes API documentation and protects it with host-owned controls such as authorization, private networking, or a reverse proxy policy. AppSurface only decides whether to map endpoints; it does not add authentication or authorization.

Code-first configuration is also supported:

```csharp
services.Configure<AppSurfaceWebOpenApiOptions>(options =>
{
    options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Always;
});

services.Configure<AppSurfaceWebScalarOptions>(options =>
{
    options.ExposeEndpoint = AppSurfaceApiDocumentationEndpointExposure.Always;
});
```

Invalid enum values fail options validation at startup when the options are read.

---
[📂 Back to Web List](../README.md) | [🏠 Back to Root](../../README.md)
