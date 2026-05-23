# ForgeTrust.AppSurface.Aspire

.NET Aspire integration for the AppSurface ecosystem.

## Overview

`ForgeTrust.AppSurface.Aspire` provides a modular way to define distributed applications using .NET Aspire. It allows you to encapsulate service defaults and resource registrations into modules.

## Release Guidance

AppSurface is preparing the first coordinated `v0.1.0` release. Before installing this package from a prerelease feed, read the [v0.1 release preview](../../releases/v0.1-preview.md) for current release risk, provisional migration guidance, and the finalization path to the tagged release note.

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
