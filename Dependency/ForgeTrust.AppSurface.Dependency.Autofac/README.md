# ForgeTrust.AppSurface.Dependency.Autofac

Autofac IoC container integration for AppSurface modules.

## Overview

This package allows modules to participate in Autofac service registration, enabling advanced DI features not available in the default .NET service collection.

## Release Guidance

AppSurface is preparing the first coordinated `v0.1.0` release. Before installing this package from a prerelease feed, read the [v0.1 release preview](../../releases/v0.1-preview.md) for current release risk, provisional migration guidance, and the finalization path to the tagged release note.

## Usage

Inherit from `AppSurfaceAutofacModule` instead of `IAppSurfaceModule` if you need to use Autofac-specific registrations.

```csharp
public class MyAutofacModule : AppSurfaceAutofacModule
{
    protected override void Load(ContainerBuilder builder)
    {
        // Custom Autofac registrations
    }
}
```

---
[📂 Back to Dependency List](../README.md) | [🏠 Back to Root](../../README.md)
