# ForgeTrust.AppSurface.Aspire

.NET Aspire integration for the AppSurface ecosystem.

## Overview

`ForgeTrust.AppSurface.Aspire` provides a modular way to define distributed applications using .NET Aspire. It allows you to encapsulate service defaults and resource registrations into modules.

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
