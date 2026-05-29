# ForgeTrust.AppSurface.Aspire

.NET Aspire integration for the AppSurface ecosystem.

## Overview

`ForgeTrust.AppSurface.Aspire` provides a modular way to define distributed applications using .NET Aspire. It allows you to encapsulate service defaults and resource registrations into modules.

## Release Guidance

AppSurface has cut the first coordinated `v0.1.0` release candidate. Before installing this package from a prerelease feed, read the [v0.1.0 RC 1 release note](../../releases/v0.1.0-rc.1.md) for current release risk, migration guidance, and package readiness.

## Installation

```bash
dotnet add package ForgeTrust.AppSurface.Aspire
```

## Usage

Use `AspireApp` to start your Aspire AppHost:

```csharp
await AspireApp<MyHostModule>.RunAsync(args);
```

---
[📂 Back to Aspire List](../README.md) | [🏠 Back to Root](../../README.md)
