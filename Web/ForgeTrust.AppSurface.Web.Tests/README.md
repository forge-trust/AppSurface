# ForgeTrust.AppSurface.Web.Tests

Unit and integration tests for the `ForgeTrust.AppSurface.Web` package.

## Overview

This project ensures the reliability of the web bootstrapping logic, including:
- CORS policy configuration and application.
- Web options handling.
- Module lifecycle hooks in a web context.

## Running Tests

You can run the tests using the .NET CLI from the root of the repository:

```bash
dotnet test
```

## Theme-pair proof

Run the focused Web contract after restoring packages:

```bash
dotnet test Web/ForgeTrust.AppSurface.Web.Tests/ForgeTrust.AppSurface.Web.Tests.csproj --filter FullyQualifiedName~AppSurfaceThemeWebIntegrationTests
```

The proof covers System/Light/Dark document output, deterministic nonce-free payloads, CSP nonce attachment, host `color-scheme` conflict preservation, forced-colors semantic variables, and the generated-RazorWire-error selector boundary. Pair it with the [Docs theme tests](../ForgeTrust.AppSurface.Docs.Tests/README.md), the [RazorWire integration tests](../ForgeTrust.RazorWire.IntegrationTests/README.md), and static export coverage before release.

---
[📂 Back to Web List](../README.md) | [🏠 Back to Root](../../README.md)
