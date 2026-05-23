# ForgeTrust.AppSurface.Web.Scalar

This package integrates the [Scalar](https://scalar.com/) API Reference UI into AppSurface web applications.

## Overview

The `AppSurfaceWebScalarModule` provides a modern, interactive API documentation interface. It depends on `ForgeTrust.AppSurface.Web.OpenApi` and automatically configures everything needed to serve the Scalar UI.

## Release Guidance

AppSurface is preparing the first coordinated `v0.1.0` release. Before installing this package from a prerelease feed, read the [v0.1 release preview](../../releases/v0.1-preview.md) for current release risk, provisional migration guidance, and the finalization path to the tagged release note.

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
- **Automatic UI Mapping**: Maps the Scalar API reference endpoint using `MapScalarApiReference()`.
- **Zero Config**: Works out of the box with the default AppSurface startup pipeline.

---
[📂 Back to Web List](../README.md) | [🏠 Back to Root](../../README.md)
